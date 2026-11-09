using System.ComponentModel;
using System.Diagnostics;
using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Levers;

/// <summary>
/// Closes the apps of a mode's close list (window close first, then kill the tree) and relaunches what the
/// snapshot had running, as the normal user, with the configured relaunch arguments.
/// </summary>
internal sealed class ProcessLever : ILever
{
    private const string ChildProcessMarker = "--type=";
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly UserLauncher _launcher;
    private readonly Func<AppConfig> _config;

    public ProcessLever(UserLauncher launcher, Func<AppConfig> config)
    {
        _launcher = launcher;
        _config = config;
    }

    public LeverKind Kind => LeverKind.Apps;

    public Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        // Only names this baseline has not looked at yet (all of them for a fresh one): a name added to a close
        // list later is captured on its own, and what was captured before is never overwritten.
        var covered = snapshot.CoveredAppNames();
        var names = config.AllCloseApps().Where(n => !covered.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var captured = new List<CapturedProcess>();
        foreach (var name in names)
        {
            var processes = ProcessInfo.ByName(name);
            try
            {
                if (MainProcess(processes) is { } main)
                {
                    captured.Add(main);
                }
            }
            finally
            {
                ProcessInfo.DisposeAll(processes);
            }
        }

        snapshot.Processes = [.. snapshot.Processes, .. captured];
        snapshot.CoveredApps = [.. covered, .. names];
        return Task.CompletedTask;
    }

    /// <summary>The process to relaunch: the oldest one that is not a Chromium/Electron child (<c>--type=</c>).</summary>
    private static CapturedProcess? MainProcess(Process[] processes)
    {
        var candidates = processes
            .Select(p => (Process: p, Path: ProcessInfo.TryGetPath(p.Id), CommandLine: ProcessInfo.TryGetCommandLine(p.Id) ?? string.Empty, Start: ProcessInfo.TryGetStart(p)))
            .Where(c => c.Path is not null)
            .OrderBy(c => c.CommandLine.Contains(ChildProcessMarker, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.Start ?? DateTimeOffset.MaxValue)
            .ToList();
        return candidates.Count == 0
            ? null
            : new CapturedProcess(candidates[0].Process.ProcessName, candidates[0].Path!, ProcessInfo.ArgumentsOf(candidates[0].CommandLine));
    }

    public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Target.Apps;
        if (target.Close.Count == 0 && target.Relaunch.Count == 0)
        {
            return Task.FromResult(LeverPlan.None);
        }

        var closed = context.State.ClosedApps;
        var actions = new List<LeverAction>();
        foreach (var name in target.Close)
        {
            var count = Count(name);
            if (count > 0)
            {
                actions.Add(new LeverAction($"Close {FriendlyNames.Process(name)} ({count} {(count == 1 ? "process" : "processes")})", async ct =>
                {
                    await CloseAsync(name, ct).ConfigureAwait(false);
                    if (!closed.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        closed.Add(name);
                    }
                }));
            }
        }

        foreach (var app in target.Relaunch.Where(a => closed.Contains(a.Name, StringComparer.OrdinalIgnoreCase)))
        {
            if (Count(app.Name) == 0)
            {
                var arguments = _config().RelaunchArgumentsFor(app.Name) ?? app.Arguments;
                actions.Add(new LeverAction($"Relaunch {FriendlyNames.Process(app.Name)} {arguments}".TrimEnd(), async _ =>
                {
                    await Relaunch(app, arguments).ConfigureAwait(false);
                    closed.RemoveAll(n => string.Equals(n, app.Name, StringComparison.OrdinalIgnoreCase));
                }));
            }
        }

        return Task.FromResult(LeverPlan.Of(actions));
    }

    private static int Count(string name)
    {
        var processes = ProcessInfo.ByName(name);
        ProcessInfo.DisposeAll(processes);
        return processes.Length;
    }

    private static async Task CloseAsync(string name, CancellationToken cancellationToken)
    {
        var processes = ProcessInfo.ByName(name);
        try
        {
            foreach (var process in processes.Where(p => p.MainWindowHandle != IntPtr.Zero))
            {
                process.CloseMainWindow();
            }

            await Task.WhenAll(processes.Select(p => WaitAsync(p, GracefulWait, cancellationToken))).ConfigureAwait(false);
            var denied = 0;
            foreach (var process in processes.Where(p => !HasExited(p)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Win32Exception ex) when (Elevation.IsAccessDenied(ex))
                {
                    denied++;
                }
                catch (InvalidOperationException)
                {
                    // Exited meanwhile.
                }
            }

            await Task.WhenAll(processes.Select(p => WaitAsync(p, KillWait, cancellationToken))).ConfigureAwait(false);
            if (denied > 0)
            {
                throw new LeverException($"{FriendlyNames.Process(name)}: access denied", needsElevation: !Elevation.IsElevated);
            }

            if (processes.Any(p => !HasExited(p)))
            {
                throw new LeverException($"{FriendlyNames.Process(name)} did not exit");
            }
        }
        finally
        {
            ProcessInfo.DisposeAll(processes);
        }
    }

    private Task Relaunch(CapturedProcess app, string arguments)
    {
        if (!File.Exists(app.Path))
        {
            throw new LeverException($"{FriendlyNames.Process(app.Name)}: {app.Path} not found");
        }

        _launcher.Launch(app.Path, arguments, hidden: false);
        return Task.CompletedTask;
    }

    private static async Task WaitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Still running; the caller decides.
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // No access to wait on it; HasExited decides.
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
