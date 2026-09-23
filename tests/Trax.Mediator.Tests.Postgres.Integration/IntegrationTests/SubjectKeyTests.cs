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
/// Covers the subject key a train may attach to its queue entry. Phase 1 only carries the value —
/// dispatch does not read it yet — so these tests are about where the key comes from and what
/// happens when it cannot be produced.
/// </summary>
[TestFixture]
public class SubjectKeyTests : TestSetup
{
    private ITrainExecutionService Execution =>
        Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();

    private async Task<WorkQueue?> EntryAsync(long id)
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        return await context.WorkQueues.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
    }

    [SetUp]
    public async Task ResetAsync()
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        await context
            .WorkQueues.Where(w => w.TrainName!.Contains("SubjectKeyTests"))
            .ExecuteDeleteAsync();
    }

    [Test]
    public async Task A_train_that_does_not_override_the_key_enqueues_with_null()
    {
        var result = await Execution.QueueAsync(
            typeof(IUnkeyedTrain).FullName!,
            "{\"customerId\":42}"
        );

        (await EntryAsync(result.WorkQueueId))!
            .SubjectKey.Should()
            .BeNull("no key means no serialization — every existing train must be untouched");
    }

    [Test]
    public async Task A_train_that_overrides_the_key_stamps_it_on_the_entry()
    {
        var result = await Execution.QueueAsync(
            typeof(IKeyedTrain).FullName!,
            "{\"customerId\":42}"
        );

        (await EntryAsync(result.WorkQueueId))!.SubjectKey.Should().Be("customer-42");
    }

    [Test]
    public async Task The_key_is_derived_from_the_input_not_fixed_per_train()
    {
        var first = await Execution.QueueAsync(typeof(IKeyedTrain).FullName!, "{\"customerId\":1}");
        var second = await Execution.QueueAsync(
            typeof(IKeyedTrain).FullName!,
            "{\"customerId\":2}"
        );

        (await EntryAsync(first.WorkQueueId))!.SubjectKey.Should().Be("customer-1");
        (await EntryAsync(second.WorkQueueId))!
            .SubjectKey.Should()
            .Be("customer-2", "the key identifies the record, so it varies per mutation");
    }

    [Test]
    public async Task A_key_that_cannot_be_computed_aborts_the_enqueue()
    {
        var act = async () =>
            await Execution.QueueAsync(typeof(IThrowingKeyTrain).FullName!, "{\"customerId\":42}");

        (await act.Should().ThrowAsync<Exception>()).WithMessage(
            "*cannot key this mutation*",
            "silently falling back to null would drop the serialization guarantee at exactly "
                + "the moment the caller expected it"
        );

        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        (await context.WorkQueues.CountAsync(w => w.TrainName!.Contains("ThrowingKeyTrain")))
            .Should()
            .Be(0, "an aborted enqueue leaves no entry");
    }

    [Test]
    public async Task A_null_returned_by_the_override_is_stored_as_null()
    {
        var result = await Execution.QueueAsync(
            typeof(INullKeyTrain).FullName!,
            "{\"customerId\":42}"
        );

        (await EntryAsync(result.WorkQueueId))!
            .SubjectKey.Should()
            .BeNull("returning null is a legitimate 'this one needs no serialization'");
    }

    // ── probes ──────────────────────────────────────────────────────

    public record UnkeyedInput
    {
        public int CustomerId { get; init; }
    }

    public record KeyedInput
    {
        public int CustomerId { get; init; }
    }

    public record ThrowingKeyInput
    {
        public int CustomerId { get; init; }
    }

    public record NullKeyInput
    {
        public int CustomerId { get; init; }
    }

    public interface IUnkeyedTrain : IServiceTrain<UnkeyedInput, Unit>;

    public class UnkeyedTrain : ServiceTrain<UnkeyedInput, Unit>, IUnkeyedTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IKeyedTrain : IServiceTrain<KeyedInput, Unit>;

    public class KeyedTrain : ServiceTrain<KeyedInput, Unit>, IKeyedTrain
    {
        protected override string? QueueSubjectKey(Metadata metadata) =>
            $"customer-{metadata.GetInput<KeyedInput>()!.CustomerId}";

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IThrowingKeyTrain : IServiceTrain<ThrowingKeyInput, Unit>;

    public class ThrowingKeyTrain : ServiceTrain<ThrowingKeyInput, Unit>, IThrowingKeyTrain
    {
        protected override string? QueueSubjectKey(Metadata metadata) =>
            throw new InvalidOperationException("cannot key this mutation");

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface INullKeyTrain : IServiceTrain<NullKeyInput, Unit>;

    public class NullKeyTrain : ServiceTrain<NullKeyInput, Unit>, INullKeyTrain
    {
        protected override string? QueueSubjectKey(Metadata metadata) => null;

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }
}
