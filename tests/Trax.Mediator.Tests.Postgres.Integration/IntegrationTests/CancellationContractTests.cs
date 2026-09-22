using FluentAssertions;
using LanguageExt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.Services.DataContext;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Services.ServiceTrain;
using Trax.Mediator.Services.TrainExecution;
using Trax.Mediator.Tests.Postgres.Integration.Fixtures;

namespace Trax.Mediator.Tests.Postgres.Integration.IntegrationTests;

/// <summary>
/// What the caller's cancellation token reaches on the synchronous execution path — the token
/// a GraphQL resolver supplies as <c>ctx.RequestAborted</c>, so these are the semantics a
/// disconnecting or timing-out HTTP client gets.
///
/// <para>Cancellation here is cooperative: it reaches the train as
/// <see cref="Train{TIn,TOut}.CancellationToken"/> and stops only the work that observes it.
/// What the execution record then says is Trax.Effect's contract, pinned by
/// <c>CancelledOutcomePersistenceTests</c> there rather than duplicated here.</para>
/// </summary>
public class CancellationContractTests : TestSetup
{
    public override async Task TestSetUp()
    {
        await base.TestSetUp();
        CancelProbe.Reset();
    }

    [Test]
    public async Task Run_WithAnAlreadyCancelledToken_NeverStartsTheTrainAndRecordsNothing()
    {
        var execution = Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var run = async () =>
            await execution.RunAsync(typeof(ICooperativeTrain).FullName!, "{}", cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();

        CancelProbe.Started.Task.IsCompleted.Should().BeFalse();

        using var context = NewContext();
        var rows = await context.Metadatas.AsNoTracking().ToListAsync();
        rows.Should().BeEmpty("nothing ran, so there is no execution to account for");
    }

    [Test]
    public async Task Run_WhenTheCallerCancels_StopsATrainThatObservesTheToken()
    {
        var execution = Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();
        using var cts = new CancellationTokenSource();

        var runTask = execution.RunAsync(typeof(ICooperativeTrain).FullName!, "{}", cts.Token);
        await CancelProbe.Started.Task;

        await cts.CancelAsync();

        var awaiting = async () => await runTask;
        await awaiting.Should().ThrowAsync<OperationCanceledException>();

        CancelProbe
            .RanToCompletion.Should()
            .BeFalse("a train that passes the token to its downstream call is genuinely stopped");
    }

    [Test]
    public async Task Run_WhenTheTrainIgnoresTheToken_TheDownstreamWorkCompletesAnyway()
    {
        var execution = Scope.ServiceProvider.GetRequiredService<ITrainExecutionService>();
        using var cts = new CancellationTokenSource();

        var runTask = execution.RunAsync(typeof(IIndifferentTrain).FullName!, "{}", cts.Token);
        await CancelProbe.Started.Task;

        await cts.CancelAsync();

        runTask
            .IsCompleted.Should()
            .BeFalse("cancelling the caller's token does not interrupt work that ignores it");

        // The downstream system answers after the caller has already given up.
        CancelProbe.Downstream.SetResult();

        // Whether the call then throws or returns the result is Trax.Effect's to decide, and it
        // changed when the terminal write stopped running on the caller's token — see
        // CancelledOutcomePersistenceTests there. What this repo owns is the fact above it: the
        // work was not interruptible, so it ran to completion after the caller had given up.
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }

        CancelProbe
            .RanToCompletion.Should()
            .BeTrue("the write landed downstream after the caller stopped waiting for it");
    }

    private IDataContext NewContext() =>
        (IDataContext)
            Scope.ServiceProvider.GetRequiredService<IDataContextProviderFactory>().Create();

    internal static class CancelProbe
    {
        public static TaskCompletionSource Started { get; private set; } = Fresh();

        public static TaskCompletionSource Downstream { get; private set; } = Fresh();

        public static bool RanToCompletion { get; set; }

        public static void Reset()
        {
            Started = Fresh();
            Downstream = Fresh();
            RanToCompletion = false;
        }

        private static TaskCompletionSource Fresh() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A train that hands the caller's token to its downstream call.</summary>
    internal class CooperativeTrain : ServiceTrain<CooperativeInput, Unit>, ICooperativeTrain
    {
        protected override async Task<Either<Exception, Unit>> RunInternal(CooperativeInput input)
        {
            var parked = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            await using var registration = CancellationToken.Register(() =>
                parked.TrySetCanceled()
            );

            CancelProbe.Started.TrySetResult();
            await parked.Task;

            CancelProbe.RanToCompletion = true;
            return Activate(input, Unit.Default).Resolve();
        }
    }

    /// <summary>A train whose downstream call takes no token, as an un-cancellable SDK would.</summary>
    internal class IndifferentTrain : ServiceTrain<IndifferentInput, Unit>, IIndifferentTrain
    {
        protected override async Task<Either<Exception, Unit>> RunInternal(IndifferentInput input)
        {
            CancelProbe.Started.TrySetResult();
            await CancelProbe.Downstream.Task;

            CancelProbe.RanToCompletion = true;
            return Activate(input, Unit.Default).Resolve();
        }
    }

    internal record CooperativeInput;

    internal record IndifferentInput;

    internal interface ICooperativeTrain : IServiceTrain<CooperativeInput, Unit> { }

    internal interface IIndifferentTrain : IServiceTrain<IndifferentInput, Unit> { }
}
