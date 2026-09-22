using FluentAssertions;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Trax.Core.Exceptions;
using Trax.Core.Junction;
using Trax.Effect.Extensions;
using Trax.Effect.Services.ServiceTrain;
using Trax.Mediator.Configuration;
using Trax.Mediator.Services.ChainVerification;
using Trax.Mediator.Services.TrainDiscovery;

namespace Trax.Mediator.Tests.MemoryLeak.Integration.UnitTests;

/// <summary>
/// The host reads every registered train's chain before it serves traffic.
///
/// <para>A chain names junction types, so three things are decidable at startup: that it can be
/// read, that every junction it names can be built, and that every junction's input reaches
/// Memory before it is needed. Each of those otherwise waits for something to run the train, which
/// for a rarely-taken train can be a long way from deployment.</para>
/// </summary>
[TestFixture]
public class TrainChainStartupValidatorTests
{
    /// <summary>
    /// Starts a validator that sees exactly one train. Discovery is substituted rather than
    /// scanned so each case is judged on its own train and not on the others in this assembly.
    /// </summary>
    private static async Task<Exception?> Start<TService, TTrain>(bool skip = false)
        where TService : class
        where TTrain : class, TService
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrax(trax => trax.AddEffects(effects => effects));
        services.AddScopedTraxRoute<TService, TTrain>();

        await using var provider = services.BuildServiceProvider();

        var discovery = Substitute.For<ITrainDiscoveryService>();
        discovery.DiscoverTrains().Returns([Registration<TService, TTrain>()]);

        var validator = new TrainChainStartupValidator(
            discovery,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MediatorConfiguration { SkipChainVerification = skip }
        );

        try
        {
            await validator.StartAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static TrainRegistration Registration<TService, TTrain>() =>
        new()
        {
            ServiceType = typeof(TService),
            ImplementationType = typeof(TTrain),
            InputType = typeof(ChainProbeInput),
            OutputType = typeof(bool),
            Lifetime = ServiceLifetime.Scoped,
            ServiceTypeName = typeof(TService).Name,
            ImplementationTypeName = typeof(TTrain).Name,
            InputTypeName = nameof(ChainProbeInput),
            OutputTypeName = nameof(Boolean),
            HasAllowAnonymousAttribute = true,
            RequiredPolicies = [],
            RequiredRoles = [],
            IsQuery = false,
            IsMutation = false,
            IsRemote = false,
            IsBroadcastEnabled = false,
            GraphQLOperations = 0,
        };

    [Test]
    public async Task Startup_WhenEveryChainLinesUp_Starts() =>
        (await Start<IWellFormedTrain, WellFormedTrain>()).Should().BeNull();

    [Test]
    public async Task Startup_WhenAJunctionsInputNeverReachesMemory_RefusesToStart()
    {
        var failure = await Start<IBrokenFlowTrain, BrokenFlowTrain>();

        failure.Should().BeOfType<TrainException>();
        failure!
            .Message.Should()
            .Contain(nameof(IBrokenFlowTrain))
            .And.Contain("Chain a junction that produces it first");
    }

    [Test]
    public async Task Startup_WhenAChainReadsItsInput_RefusesToStart()
    {
        var failure = await Start<IReadsInputTrain, ReadsInputTrain>();

        failure.Should().BeOfType<TrainException>();
        failure!.Message.Should().Contain("TrainInput");
    }

    [Test]
    public async Task Startup_WhenVerificationIsSkipped_StartsAnyway() =>
        (await Start<IBrokenFlowTrain, BrokenFlowTrain>(skip: true))
            .Should()
            .BeNull("the opt-out exists for the declared-versus-concrete blind spot");

    /// <summary>
    /// A dedicated input type. These trains are discovered by assembly scan like any other, so a
    /// shared input type such as string would register a train for it and change what other
    /// tests in this assembly see.
    /// </summary>
    public record ChainProbeInput(string Value);

    private class TextToNumber : Junction<ChainProbeInput, int>
    {
        public override Task<int> Run(ChainProbeInput input) => Task.FromResult(input.Value.Length);
    }

    private class NumberToFlag : Junction<int, bool>
    {
        public override Task<bool> Run(int input) => Task.FromResult(input > 0);
    }

    public interface IWellFormedTrain : IServiceTrain<ChainProbeInput, bool>;

    public class WellFormedTrain : ServiceTrain<ChainProbeInput, bool>, IWellFormedTrain
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<TextToNumber>().Chain<NumberToFlag>().Resolve();
    }

    public interface IBrokenFlowTrain : IServiceTrain<ChainProbeInput, bool>;

    public class BrokenFlowTrain : ServiceTrain<ChainProbeInput, bool>, IBrokenFlowTrain
    {
        // NumberToFlag needs an int and nothing before it produces one.
        protected override Task<Either<Exception, bool>> Junctions() =>
            Chain<NumberToFlag>().Resolve();
    }

    public interface IReadsInputTrain : IServiceTrain<ChainProbeInput, bool>;

    public class ReadsInputTrain : ServiceTrain<ChainProbeInput, bool>, IReadsInputTrain
    {
        protected override Task<Either<Exception, bool>> Junctions() =>
            TrainInput.Value.Length > 0
                ? Chain<TextToNumber>().Chain<NumberToFlag>().Resolve()
                : Chain<TextToNumber>().Chain<NumberToFlag>().Resolve();
    }
}
