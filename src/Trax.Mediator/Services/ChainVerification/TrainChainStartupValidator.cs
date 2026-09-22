using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trax.Core.Exceptions;
using Trax.Core.Monad;
using Trax.Mediator.Configuration;
using Trax.Mediator.Services.TrainDiscovery;

namespace Trax.Mediator.Services.ChainVerification;

/// <summary>
/// Reads every registered train's chain at startup and refuses to start the host if one of them
/// cannot run.
/// </summary>
/// <remarks>
/// A chain is a declaration of junction types, so two things are decidable before any traffic
/// arrives: that the chain can be read at all, and that every junction's input reaches Memory
/// before the junction needs it. Left to runtime, each of those surfaces only on the path that
/// happens to hit it, which for a rarely-taken train can be a long way from deployment.
///
/// <para>Whether a junction can be built is deliberately not checked. A junction takes its
/// constructor arguments from Memory, which the chain fills as it runs, so answering that at
/// startup would mean replaying the resolution rather than the types. An approximation of it
/// rejected every train in the sample applications, all of which run.</para>
///
/// <para>Every train is checked before anything is reported, so one start tells you about all of
/// them rather than one per attempt. Opt out with
/// <c>AddMediator(m => m.SkipChainVerification())</c>, which is worth doing only for the blind
/// spot named in <c>ChainVerification</c>: a junction declaring an interface that the value
/// flowing in implements only incidentally.</para>
/// </remarks>
internal sealed class TrainChainStartupValidator(
    ITrainDiscoveryService discoveryService,
    IServiceScopeFactory scopeFactory,
    MediatorConfiguration configuration,
    ILogger<TrainChainStartupValidator>? logger = null
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.SkipChainVerification)
        {
            logger?.LogWarning(
                "Chain verification is off, so a train whose chain cannot run will not be found "
                    + "until something runs it."
            );

            return Task.CompletedTask;
        }

        using var scope = scopeFactory.CreateScope();
        var problems = new List<string>();
        var checkedTrains = 0;

        foreach (var registration in discoveryService.DiscoverTrains())
        {
            var problem = Check(scope.ServiceProvider, registration);

            checkedTrains++;

            if (problem is not null)
                problems.Add(problem);
        }

        if (problems.Count > 0)
            throw new TrainException(
                $"{problems.Count} of {checkedTrains} registered trains declare a chain that "
                    + "cannot run:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, problems.Select(p => "  - " + p))
            );

        logger?.LogDebug("Verified the chains of {TrainCount} trains.", checkedTrains);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string? Check(IServiceProvider services, TrainRegistration registration)
    {
        object train;

        try
        {
            train = services.GetRequiredService(registration.ServiceType);
        }
        catch (Exception ex)
        {
            return $"{registration.ServiceTypeName}: could not be constructed, so its chain "
                + $"could not be read ({ex.Message})";
        }

        ChainRecorder chain;

        try
        {
            // DeclaredChain is public on Train<,>, but the registration hands back the service
            // interface, so the concrete method is reached by name.
            chain = (ChainRecorder)
                train
                    .GetType()
                    .GetMethod(nameof(Core.Train.Train<,>.DeclaredChain))!
                    .Invoke(train, null)!;
        }
        catch (Exception ex) when (ex.InnerException is ChainDeclarationException declaration)
        {
            return $"{registration.ServiceTypeName}: {declaration.Message}";
        }
        catch (Exception ex)
        {
            return $"{registration.ServiceTypeName}: its chain could not be read ({ex.Message})";
        }

        var faults = Core.Monad.ChainVerification.Verify(
            chain,
            registration.InputType,
            registration.OutputType,
            type => services.GetService(type) is not null
        );

        return faults.Count == 0
            ? null
            : $"{registration.ServiceTypeName}: "
                + string.Join("; ", faults.Select(f => $"step {f.StepIndex} {f.Reason}"));
    }
}
