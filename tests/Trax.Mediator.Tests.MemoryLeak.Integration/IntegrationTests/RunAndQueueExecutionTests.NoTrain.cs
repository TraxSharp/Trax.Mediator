using FluentAssertions;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Trax.Core.Exceptions;
using Trax.Mediator.Services.TrainBus;

namespace Trax.Mediator.Tests.MemoryLeak.Integration.IntegrationTests;

public partial class RunAndQueueExecutionTests
{
    [Test]
    public async Task TrainBus_InputWithNoTrain_TellsTheHostWhereItLookedAndHowToFixIt()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<ITrainBus>();

        var act = async () => await bus.RunAsync<Unit>(new InputWithNoTrain());

        var thrown = (await act.Should().ThrowAsync<TrainException>()).Which;
        thrown.Message.Should().Contain(typeof(InputWithNoTrain).FullName!);
        thrown.Message.Should().Contain(typeof(RunAndQueueExecutionTests).Assembly.GetName().Name);
        thrown.Message.Should().Contain("ScanAssemblies(");
    }

    /// <summary>No train in any scanned assembly takes this.</summary>
    public record InputWithNoTrain;
}
