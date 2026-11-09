using System.ServiceProcess;
using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Levers;

/// <summary>Stops/starts Windows services (administrator). The start type is never touched.</summary>
internal sealed class ServiceLever : ILever
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public LeverKind Kind => LeverKind.Services;

    public Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        // Only names this baseline has not looked at yet; see ProcessLever.CaptureAsync.
        var covered = snapshot.CoveredServiceNames();
        var names = config.AllServices().Where(n => !covered.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var services = new Dictionary<string, bool>(snapshot.Services, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (Status(name) is { } status)
            {
                services[name] = status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            }
        }

        snapshot.Services = services;
        snapshot.CoveredServices = [.. covered, .. names];
        return Task.CompletedTask;
    }

    public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Target.Services;
        if (target.Stop.Count == 0 && target.Start.Count == 0)
        {
            return Task.FromResult(LeverPlan.None);
        }

        var actions = new List<LeverAction>();
        foreach (var name in target.Stop)
        {
            if (Status(name) is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.Paused)
            {
                actions.Add(new LeverAction($"Stop {FriendlyNames.Service(name)} ({name})", ct => ChangeAsync(name, start: false, ct), RequiresElevation: true));
            }
        }

        foreach (var name in target.Start)
        {
            if (Status(name) is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                actions.Add(new LeverAction($"Start {FriendlyNames.Service(name)} ({name})", ct => ChangeAsync(name, start: true, ct), RequiresElevation: true));
            }
        }

        return Task.FromResult(LeverPlan.Of(actions));
    }

    private static ServiceControllerStatus? Status(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Task<bool> ChangeAsync(string name, bool start, CancellationToken cancellationToken) =>
        NativeCall.RunAsync(
            () =>
            {
                using var service = new ServiceController(name);
                try
                {
                    if (start)
                    {
                        service.Start();
                    }
                    else
                    {
                        service.Stop();
                    }

                    service.WaitForStatus(start ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped, Timeout);
                    return true;
                }
                catch (InvalidOperationException ex) when (Elevation.IsAccessDenied(ex))
                {
                    throw new LeverException(LeverResult.NeedsAdministrator, needsElevation: true);
                }
                catch (InvalidOperationException ex)
                {
                    throw new LeverException($"{FriendlyNames.Service(name)}: {ex.Message}");
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                    throw new LeverException($"{FriendlyNames.Service(name)} did not {(start ? "start" : "stop")} in time");
                }
            },
            Timeout + TimeSpan.FromSeconds(5),
            FriendlyNames.Service(name),
            cancellationToken);
}
