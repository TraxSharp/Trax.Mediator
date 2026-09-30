using FluentAssertions;
using LanguageExt;
using Microsoft.Extensions.DependencyInjection;
using Trax.Effect.Data.InMemory.Extensions;
using Trax.Effect.Extensions;
using Trax.Effect.Services.ServiceTrain;
using Trax.Mediator.Extensions;
using Trax.Mediator.Services.TrainBus;
using Trax.Mediator.Services.TrainDiscovery;
using Trax.Mediator.Services.TrainRegistry;

namespace Trax.Mediator.Tests.MemoryLeak.Integration.UnitTests;

/// <summary>
/// A train is registered under its own interface, the one that derives from
/// <c>IServiceTrain&lt;,&gt;</c>, and not under whichever non-generic interface the CLR happens
/// to list first. A base class's interfaces are listed before the train's own, so a marker on a
/// shared base class used to become every derived train's service type.
/// </summary>
[TestFixture]
public class TrainServiceTypeSelectionTests
{
    [Test]
    public void Registry_TrainWhoseBaseClassImplementsAMarkerInterface_IsRegisteredUnderItsOwnInterface()
    {
        var registry = new TrainRegistry(typeof(TrainServiceTypeSelectionTests).Assembly);

        registry.InputTypeToTrain[typeof(MarkedInput)].Should().Be(typeof(IMarkedTrain));
        registry.InputTypeToTrain[typeof(OtherMarkedInput)].Should().Be(typeof(IOtherMarkedTrain));
    }

    [Test]
    public void Registry_TrainWithoutItsOwnInterface_IsRegisteredUnderTheClosedServiceTrain()
    {
        var registry = new TrainRegistry(typeof(TrainServiceTypeSelectionTests).Assembly);

        registry
            .InputTypeToTrain[typeof(UnmarkedInput)]
            .Should()
            .Be(typeof(IServiceTrain<UnmarkedInput, Unit>));
    }

    [Test]
    public void Registry_TrainWhoseInterfaceExtendsAnotherTrainInterface_IsRegisteredUnderTheMostDerived()
    {
        var registry = new TrainRegistry(typeof(TrainServiceTypeSelectionTests).Assembly);

        registry.InputTypeToTrain[typeof(LayeredInput)].Should().Be(typeof(ILayeredTrain));
    }

    [Test]
    public void TwoTrainsSharingAMarkedBaseClass_EachRunTheirOwnTrain()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrax(trax =>
            trax.AddEffects(effects => effects.UseInMemory())
                .AddMediator(assemblies: [typeof(TrainServiceTypeSelectionTests).Assembly])
        );
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<ITrainBus>();

        bus.InitializeTrain(new MarkedInput()).Should().BeOfType<MarkedTrain>();
        bus.InitializeTrain(new OtherMarkedInput()).Should().BeOfType<OtherMarkedTrain>();
    }

    [Test]
    public void Discovery_TrainWhoseBaseClassImplementsAMarkerInterface_IsListedUnderItsOwnInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTrax(trax =>
            trax.AddEffects(effects => effects.UseInMemory())
                .AddMediator(assemblies: [typeof(TrainServiceTypeSelectionTests).Assembly])
        );

        var registrations = new TrainDiscoveryService(services).DiscoverTrains();

        registrations
            .Should()
            .ContainSingle(r => r.ImplementationType == typeof(MarkedTrain))
            .Which.ServiceType.Should()
            .Be(typeof(IMarkedTrain));
    }

    [Test]
    public void RegisterServiceTrains_TrainWhoseBaseClassImplementsAMarkerInterface_IsRegisteredUnderItsOwnInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.RegisterServiceTrains(
            ServiceLifetime.Transient,
            typeof(TrainServiceTypeSelectionTests).Assembly
        );

        services.Should().Contain(d => d.ServiceType == typeof(IMarkedTrain));
        services.Should().Contain(d => d.ServiceType == typeof(IOtherMarkedTrain));
        services
            .Should()
            .NotContain(
                d => d.ServiceType == typeof(IAuditedTrain),
                "a marker on a shared base class is not a train's service type"
            );

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMarkedTrain>().Should().BeOfType<MarkedTrain>();
    }

    [Test]
    public void TrainServiceType_TrainWithTwoUnrelatedTrainInterfaces_IsRefusedNamingThem()
    {
        var act = () => TrainServiceType.Select(typeof(TwoFacedTrain));

        act.Should()
            .Throw<Trax.Core.Exceptions.TrainException>()
            .WithMessage($"*{nameof(TwoFacedTrain)}*{nameof(IFirstFace)}*{nameof(ISecondFace)}*");
    }

    #region Trains

    /// <summary>A marker a team might put on a shared base class for its trains.</summary>
    public interface IAuditedTrain;

    public abstract class AuditedTrainBase<TIn, TOut> : ServiceTrain<TIn, TOut>, IAuditedTrain;

    public record MarkedInput;

    public interface IMarkedTrain : IServiceTrain<MarkedInput, Unit>;

    public class MarkedTrain : AuditedTrainBase<MarkedInput, Unit>, IMarkedTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public record OtherMarkedInput;

    public interface IOtherMarkedTrain : IServiceTrain<OtherMarkedInput, Unit>;

    public class OtherMarkedTrain : AuditedTrainBase<OtherMarkedInput, Unit>, IOtherMarkedTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public record UnmarkedInput;

    public class UnmarkedTrain : AuditedTrainBase<UnmarkedInput, Unit>
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public record LayeredInput;

    public interface ILayeredBaseTrain : IServiceTrain<LayeredInput, Unit>;

    public interface ILayeredTrain : ILayeredBaseTrain;

    public class LayeredTrain : ServiceTrain<LayeredInput, Unit>, ILayeredTrain
    {
        protected override Task<Either<Exception, Unit>> Junctions() =>
            Task.FromResult<Either<Exception, Unit>>(Unit.Default);
    }

    public record TwoFacedInput;

    public interface IFirstFace : IServiceTrain<TwoFacedInput, Unit>;

    public interface ISecondFace : IServiceTrain<TwoFacedInput, Unit>;

    /// <summary>
    /// Abstract, so assembly scanning skips it and only the helper sees it: a concrete train of
    /// this shape would fail every scan of this test assembly.
    /// </summary>
    public abstract class TwoFacedTrain
        : ServiceTrain<TwoFacedInput, Unit>,
            IFirstFace,
            ISecondFace;

    #endregion
}
