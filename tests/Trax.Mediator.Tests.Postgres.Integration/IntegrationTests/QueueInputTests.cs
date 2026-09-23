using System.Text.Json;
using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.WorkQueue;
using Trax.Effect.Services.ServiceTrain;
using Trax.Mediator.Services.TrainExecution;
using Trax.Mediator.Tests.Postgres.Integration.Fixtures;

namespace Trax.Mediator.Tests.Postgres.Integration.IntegrationTests;

/// <summary>
/// What an enqueue does with what it is given: no input, a scheduled time in any kind, and
/// several enqueues sharing one scope. Each of these used to be accepted and then fail somewhere
/// the caller could not see, or fail for reasons unrelated to the caller.
/// </summary>
[TestFixture]
public class QueueInputTests : TestSetup
{
    private ITrainExecutionService Execution =>
        Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();

    private async Task<WorkQueue> EntryAsync(long id)
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        return await context.WorkQueues.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    [Test]
    public async Task No_input_is_stored_as_an_empty_object_the_runner_can_use()
    {
        var result = await Execution.QueueAsync(typeof(IOptionalInputTrain).FullName!, null);

        var stored = (await EntryAsync(result.WorkQueueId)).Input;

        stored
            .Should()
            .NotBeNull("the runner refuses an entry with no input, so null could only fail later");
        JsonSerializer.Deserialize<OptionalInput>(stored!)!.Label.Should().Be("default");
    }

    [Test]
    public async Task An_input_that_cannot_be_built_from_nothing_is_refused_at_enqueue()
    {
        var act = async () =>
            await Execution.QueueAsync(typeof(IRequiredInputTrain).FullName!, null);

        await act.Should()
            .ThrowAsync<JsonException>(
                "the caller is the one who can supply the missing value, so it fails for them"
            );
    }

    [Test]
    public async Task A_hook_receives_the_input_even_when_none_was_given()
    {
        HookProbe.Seen.Clear();

        await Execution.QueueAsync(typeof(IHookedOptionalInputTrain).FullName!, "");

        HookProbe.Seen.Should().ContainSingle().Which.Should().NotBeNull();
    }

    [Test]
    public async Task Whitespace_input_is_read_as_an_empty_object()
    {
        var result = await Execution.QueueAsync(typeof(IOptionalInputTrain).FullName!, " \t\r\n ");

        var stored = (await EntryAsync(result.WorkQueueId)).Input;

        stored.Should().NotBeNull("blank input is documented to be read as {}, the same as null");
        JsonSerializer.Deserialize<OptionalInput>(stored!)!.Label.Should().Be("default");
    }

    [Test]
    public async Task A_scheduled_time_in_the_past_is_stored_as_given_and_is_already_due()
    {
        var past = DateTime.UtcNow.AddHours(-3);

        var result = await Execution.QueueAsync(
            typeof(IOptionalInputTrain).FullName!,
            "{}",
            scheduledAt: past
        );

        var entry = await EntryAsync(result.WorkQueueId);

        entry
            .ScheduledAt.Should()
            .BeCloseTo(past, TimeSpan.FromSeconds(1), "the enqueue neither refuses nor clamps it");
        entry
            .ScheduledAt.Should()
            .BeOnOrBefore(
                DateTime.UtcNow,
                "it is the earliest dispatch time, so a past one is due as soon as a worker is free"
            );
        entry.ConfirmedAt.Should().NotBeNull("nothing stages it, so dispatch can claim it now");
    }

    [Test]
    public async Task A_local_scheduled_time_is_stored_as_utc()
    {
        var local = DateTime.SpecifyKind(DateTime.Now.AddHours(2), DateTimeKind.Local);

        var result = await Execution.QueueAsync(
            typeof(IOptionalInputTrain).FullName!,
            "{}",
            scheduledAt: local
        );

        (await EntryAsync(result.WorkQueueId))
            .ScheduledAt.Should()
            .BeCloseTo(local.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task An_unspecified_scheduled_time_is_taken_as_utc()
    {
        var unspecified = DateTime.SpecifyKind(
            DateTime.UtcNow.AddHours(2),
            DateTimeKind.Unspecified
        );

        var result = await Execution.QueueAsync(
            typeof(IOptionalInputTrain).FullName!,
            "{}",
            scheduledAt: unspecified
        );

        (await EntryAsync(result.WorkQueueId))
            .ScheduledAt.Should()
            .BeCloseTo(
                DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
                TimeSpan.FromSeconds(1),
                "a timestamp without an offset arrives from JSON as unspecified"
            );
    }

    [Test]
    public async Task Concurrent_enqueues_on_one_scope_do_not_interfere()
    {
        var execution = Execution;

        var act = async () =>
            await Task.WhenAll(
                Enumerable
                    .Range(0, 5)
                    .Select(_ =>
                        Task.Run(() =>
                            execution.QueueAsync(typeof(IHookedOptionalInputTrain).FullName!, "{}")
                        )
                    )
            );

        await act.Should()
            .NotThrowAsync("a Blazor circuit shares one scope across every enqueue it makes");
    }

    [Test]
    public async Task A_hook_can_enqueue_another_train()
    {
        var result = await Execution.QueueAsync(typeof(IChainingHookTrain).FullName!, "{}");

        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        (await context.WorkQueues.CountAsync(w => w.TrainName!.Contains("OptionalInputTrain")))
            .Should()
            .Be(1, "the hook's own enqueue gets its own context rather than refusing to nest");
        (await EntryAsync(result.WorkQueueId)).Should().NotBeNull();
    }

    // ── probes ──────────────────────────────────────────────────────

    public static class HookProbe
    {
        public static readonly System.Collections.Concurrent.ConcurrentBag<object?> Seen = [];
    }

    public record OptionalInput
    {
        public string Label { get; init; } = "default";
    }

    public record HookedOptionalInput
    {
        public string Label { get; init; } = "default";
    }

    public record RequiredInput
    {
        public required string Label { get; init; }
    }

    public record ChainingInput
    {
        public string Label { get; init; } = "default";
    }

    public interface IOptionalInputTrain : IServiceTrain<OptionalInput, Unit>;

    public class OptionalInputTrain : ServiceTrain<OptionalInput, Unit>, IOptionalInputTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() => Task.FromResult(Resolve());
    }

    public interface IHookedOptionalInputTrain : IServiceTrain<HookedOptionalInput, Unit>;

    public class HookedOptionalInputTrain
        : ServiceTrain<HookedOptionalInput, Unit>,
            IHookedOptionalInputTrain
    {
        protected override Task OnQueue(Metadata metadata, CancellationToken ct)
        {
            HookProbe.Seen.Add(metadata.GetInput<HookedOptionalInput>());
            return Task.CompletedTask;
        }

        protected override Task<Either<Exception, Unit>> Junctions() => Task.FromResult(Resolve());
    }

    public interface IRequiredInputTrain : IServiceTrain<RequiredInput, Unit>;

    public class RequiredInputTrain : ServiceTrain<RequiredInput, Unit>, IRequiredInputTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() => Task.FromResult(Resolve());
    }

    public interface IChainingHookTrain : IServiceTrain<ChainingInput, Unit>;

    public class ChainingHookTrain(ITrainExecutionService execution)
        : ServiceTrain<ChainingInput, Unit>,
            IChainingHookTrain
    {
        protected override Task OnQueue(Metadata metadata, CancellationToken ct) =>
            execution.QueueAsync(typeof(IOptionalInputTrain).FullName!, "{}", ct: ct);

        protected override Task<Either<Exception, Unit>> Junctions() => Task.FromResult(Resolve());
    }
}
