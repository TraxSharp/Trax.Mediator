using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Data.Services.WorkQueuePromotion;
using Trax.Effect.Models.Metadata;
using Trax.Effect.Models.WorkQueue;
using Trax.Effect.Models.WorkQueue.DTOs;
using Trax.Effect.Services.ServiceTrain;
using Trax.Mediator.Services.TrainExecution;
using Trax.Mediator.Tests.Postgres.Integration.Fixtures;

namespace Trax.Mediator.Tests.Postgres.Integration.IntegrationTests;

/// <summary>
/// Covers two-phase enqueue: a train may hold its work queue entry unconfirmed until its
/// <c>OnQueue</c> hook has committed, so that a crash between the two leaves a recoverable entry
/// rather than a side-effect nothing will consume.
///
/// <para>
/// Immediate promotion is the default. Deferral is opt-in, because it costs an extra round trip and
/// only earns anything when the hook writes somewhere Trax's own transaction cannot reach.
/// </para>
/// </summary>
[TestFixture]
public class DeferredPromotionTests : TestSetup
{
    private ITrainExecutionService Execution =>
        Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();

    private IWorkQueuePromotion Promotion =>
        Scope.ServiceProvider.GetRequiredService<IWorkQueuePromotion>();

    private async Task<WorkQueue?> EntryAsync(long id)
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        return await context.WorkQueues.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
    }

    private async Task<long> InsertUnconfirmedAsync(DateTime createdAt)
    {
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        var entry = WorkQueue.Create(
            new CreateWorkQueue
            {
                TrainName = "DeferTx.Manual",
                Input = "{}",
                InputTypeName = typeof(DeferInput).FullName,
                DeferPromotion = true,
            }
        );
        entry.CreatedAt = createdAt;
        await context.Track(entry);
        await context.SaveChanges(CancellationToken.None);
        return entry.Id;
    }

    [SetUp]
    public async Task ResetAsync()
    {
        Observed.Clear();
        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        await context
            .WorkQueues.Where(w =>
                w.TrainName!.Contains("DeferTx") || w.TrainName!.Contains("DeferredPromotionTests")
            )
            .ExecuteDeleteAsync();
    }

    // ── default: immediate promotion ────────────────────────────────

    [Test]
    public async Task A_train_without_a_hook_is_confirmed_immediately()
    {
        var result = await Execution.QueueAsync(typeof(IPlainTrain).FullName!, "{\"Value\":\"x\"}");

        (await EntryAsync(result.WorkQueueId))!
            .ConfirmedAt.Should()
            .NotBeNull("immediate promotion is the default — nothing is staged");
    }

    [Test]
    public async Task A_hook_that_does_not_defer_is_confirmed_immediately()
    {
        var result = await Execution.QueueAsync(
            typeof(IImmediateHookTrain).FullName!,
            "{\"Value\":\"x\"}"
        );

        (await EntryAsync(result.WorkQueueId))!
            .ConfirmedAt.Should()
            .NotBeNull(
                "overriding OnQueue alone must not change when the entry becomes dispatchable"
            );
    }

    // ── opt-in: deferred promotion ──────────────────────────────────

    [Test]
    public async Task A_deferring_train_stages_its_entry_unconfirmed_while_the_hook_runs()
    {
        var result = await Execution.QueueAsync(
            typeof(IDeferringTrain).FullName!,
            "{\"Value\":\"x\"}"
        );

        Observed
            .ConfirmedAtDuringHook.Should()
            .ContainSingle()
            .Which.Should()
            .BeNull(
                "the entry is committed unconfirmed BEFORE the hook runs — that is what makes a "
                    + "crash during the hook recoverable rather than invisible"
            );

        (await EntryAsync(result.WorkQueueId))!
            .ConfirmedAt.Should()
            .NotBeNull("a successful hook is followed by promotion");
    }

    [Test]
    public async Task A_deferring_train_whose_hook_throws_leaves_no_entry()
    {
        var act = async () =>
            await Execution.QueueAsync(typeof(IDeferringThrowTrain).FullName!, "{\"Value\":\"x\"}");

        (await act.Should().ThrowAsync<Exception>()).WithMessage("*hook rejected*");

        var factory = Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>();
        using var context = await factory.CreateDbContextAsync(CancellationToken.None);
        (await context.WorkQueues.CountAsync(w => w.TrainName!.Contains("DeferringThrowTrain")))
            .Should()
            .Be(0, "a rejected mutation leaves no trace — the staged entry is removed");
    }

    // ── promotion service ───────────────────────────────────────────

    [Test]
    public async Task PromoteAsync_confirms_an_unconfirmed_entry()
    {
        var id = await InsertUnconfirmedAsync(DateTime.UtcNow);

        (await Promotion.PromoteAsync(id, CancellationToken.None)).Should().BeTrue();
        (await EntryAsync(id))!.ConfirmedAt.Should().NotBeNull();
    }

    [Test]
    public async Task PromoteAsync_returns_false_when_the_entry_is_already_confirmed()
    {
        var id = await InsertUnconfirmedAsync(DateTime.UtcNow);
        await Promotion.PromoteAsync(id, CancellationToken.None);

        (await Promotion.PromoteAsync(id, CancellationToken.None))
            .Should()
            .BeFalse("callers distinguish a recovery from a no-op");
    }

    [Test]
    public async Task PromoteAsync_returns_false_for_an_entry_that_does_not_exist()
    {
        (await Promotion.PromoteAsync(-1, CancellationToken.None)).Should().BeFalse();
    }

    [Test]
    public async Task PromoteStaleAsync_promotes_only_entries_older_than_the_window()
    {
        var stale = await InsertUnconfirmedAsync(DateTime.UtcNow.AddMinutes(-30));
        var fresh = await InsertUnconfirmedAsync(DateTime.UtcNow);

        var promoted = await Promotion.PromoteStaleAsync(
            TimeSpan.FromMinutes(10),
            CancellationToken.None
        );

        promoted.Should().Be(1);
        (await EntryAsync(stale))!.ConfirmedAt.Should().NotBeNull();
        (await EntryAsync(fresh))!
            .ConfirmedAt.Should()
            .BeNull("an entry still inside the window may simply have a slow hook in flight");
    }

    [Test]
    public async Task PromoteStaleAsync_ignores_entries_that_are_already_confirmed()
    {
        await InsertUnconfirmedAsync(DateTime.UtcNow.AddMinutes(-30));
        await Promotion.PromoteStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        (await Promotion.PromoteStaleAsync(TimeSpan.FromMinutes(10), CancellationToken.None))
            .Should()
            .Be(0);
    }

    // ── probes ──────────────────────────────────────────────────────

    /// <summary>What a hook saw about its own entry while it was running.</summary>
    public static class Observed
    {
        public static readonly List<DateTime?> ConfirmedAtDuringHook = [];

        public static void Clear() => ConfirmedAtDuringHook.Clear();
    }

    public record DeferInput
    {
        public string Value { get; init; } = string.Empty;
    }

    public record PlainInput
    {
        public string Value { get; init; } = string.Empty;
    }

    public record ImmediateInput
    {
        public string Value { get; init; } = string.Empty;
    }

    public record DeferThrowInput
    {
        public string Value { get; init; } = string.Empty;
    }

    public interface IPlainTrain : IServiceTrain<PlainInput, Unit>;

    public class PlainTrain : ServiceTrain<PlainInput, Unit>, IPlainTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IImmediateHookTrain : IServiceTrain<ImmediateInput, Unit>;

    public class ImmediateHookTrain : ServiceTrain<ImmediateInput, Unit>, IImmediateHookTrain
    {
        protected override Task OnQueue(Metadata metadata, CancellationToken ct) =>
            Task.CompletedTask;

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IDeferringTrain : IServiceTrain<DeferInput, Unit>;

    public class DeferringTrain(IDataContextProviderFactory factory)
        : ServiceTrain<DeferInput, Unit>,
            IDeferringTrain
    {
        protected override bool DeferQueuePromotion => true;

        protected override async Task OnQueue(Metadata metadata, CancellationToken ct)
        {
            // Read the entry back on a fresh connection: the staging commit has landed, so the
            // row exists and must still be unconfirmed at this point.
            using var context = await factory.CreateDbContextAsync(ct);
            var row = await context
                .WorkQueues.AsNoTracking()
                .FirstOrDefaultAsync(w => w.ExternalId == metadata.ExternalId, ct);

            Observed.ConfirmedAtDuringHook.Add(row?.ConfirmedAt);
        }

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public interface IDeferringThrowTrain : IServiceTrain<DeferThrowInput, Unit>;

    public class DeferringThrowTrain : ServiceTrain<DeferThrowInput, Unit>, IDeferringThrowTrain
    {
        protected override bool DeferQueuePromotion => true;

        protected override Task OnQueue(Metadata metadata, CancellationToken ct) =>
            throw new InvalidOperationException("hook rejected the mutation");

        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }
}
