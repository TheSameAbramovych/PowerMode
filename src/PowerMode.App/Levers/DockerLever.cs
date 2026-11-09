using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Infrastructure;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Parsing;

namespace PowerMode.App.Levers;

/// <summary>
/// Docker Desktop: <c>docker desktop stop</c> in saving modes; Grid (or "Keep Docker running") starts the engine,
/// waits for <c>docker info</c> and starts every snapshot container that is not running.
/// </summary>
internal sealed class DockerLever : ILever
{
    public const string Tool = "docker";
    private const string ExecutableName = "docker.exe";
    private const string KnownLocation = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
    private const string NotInstalled = "Docker is not installed";
    private const string StatusUnavailable = "Docker Desktop status unavailable (docker desktop status and docker ps failed, or the engine is still starting)";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CaptureStartingWait = TimeSpan.FromSeconds(30);
    private static readonly string[] StatusArguments = ["desktop", "status", "--format", "json"];
    private static readonly string[] NamesArguments = ["ps", "--format", "{{.Names}}"];

    private readonly IProcessRunner _runner;
    private readonly UserLauncher _launcher;
    private readonly Func<AppConfig> _config;
    private readonly Lazy<string?> _docker = new(() => ExecutableLocator.Find(ExecutableName, KnownLocation));

    public DockerLever(IProcessRunner runner, UserLauncher launcher, Func<AppConfig> config)
    {
        _runner = runner;
        _launcher = launcher;
        _config = config;
    }

    public LeverKind Kind => LeverKind.Docker;

    public async Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_docker.Value is null)
        {
            // Not installed: legitimately no Docker part.
            return;
        }

        snapshot.Docker = await ReadAsync(waitWhileStarting: true, cancellationToken).ConfigureAwait(false)
            ?? throw new LeverException(StatusUnavailable);
    }

    /// <summary>Engine state and running container count for the live column; null when Docker is missing or cannot be read.</summary>
    public async Task<(bool Running, int Containers)?> ReadLiveAsync(CancellationToken cancellationToken)
    {
        if (_docker.Value is null)
        {
            return null;
        }

        return await ReadAsync(waitWhileStarting: false, cancellationToken).ConfigureAwait(false) is { } docker
            ? (docker.EngineRunning, docker.RunningContainers.Count)
            : null;
    }

    /// <summary>
    /// The engine and its running containers, or null when that cannot be told: the status call failed and
    /// <c>docker ps</c> does not answer either, the engine is still starting, or it runs but its containers cannot be listed.
    /// </summary>
    private async Task<DockerSnapshot?> ReadAsync(bool waitWhileStarting, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        if (waitWhileStarting && status == DockerEngineStatus.Starting)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(_config().Docker.EngineWaitSeconds, CaptureStartingWait.TotalSeconds));
            while (status == DockerEngineStatus.Starting && DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                status = await StatusAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return status switch
        {
            DockerEngineStatus.Stopped => new DockerSnapshot(false, []),
            DockerEngineStatus.Starting => null,

            // Running, or Unknown (status failed or timed out): docker ps answers only while the engine runs.
            _ => await RunningContainersAsync(cancellationToken).ConfigureAwait(false) is { } names ? new DockerSnapshot(true, names) : null,
        };
    }

    public async Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.Docker is not { } target)
        {
            return LeverPlan.None;
        }

        if (_docker.Value is null)
        {
            return LeverPlan.Fail(NotInstalled, permanent: true);
        }

        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        var engineUp = status is DockerEngineStatus.Running or DockerEngineStatus.Starting;
        var actions = new List<LeverAction>();
        if (!target.Running)
        {
            // Unknown means the status call failed (a busy or hung backend), not that the engine is down. Stopping is
            // safe to repeat and fails visibly when the backend really hangs.
            if (status != DockerEngineStatus.Stopped)
            {
                actions.Add(new LeverAction("Stop Docker Desktop", ct => RunCheckedAsync(CommandTimeout, ["desktop", "stop"], ct)));
            }

            return LeverPlan.Of(actions);
        }

        if (!engineUp)
        {
            actions.Add(new LeverAction("Start Docker Desktop and wait for the engine", StartEngineAsync));
        }

        var missing = status == DockerEngineStatus.Running
            ? target.ContainersToStart.Except(await RunningContainersAsync(cancellationToken).ConfigureAwait(false) ?? [], StringComparer.Ordinal).ToList()
            : [.. target.ContainersToStart];
        if (missing.Count > 0)
        {
            actions.Add(new LeverAction($"Start containers: {string.Join(", ", missing)}", ct => StartContainersAsync(target.ContainersToStart, ct)));
        }

        return LeverPlan.Of(actions);
    }

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        if (Elevation.IsElevated)
        {
            // An elevated "docker desktop start" would run Docker Desktop elevated; let the shell start it as the user.
            _launcher.Launch(_docker.Value!, "desktop start", hidden: true);
        }
        else
        {
            await RunCheckedAsync(CommandTimeout, ["desktop", "start"], cancellationToken).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_config().Docker.EngineWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var info = await _runner.RunAsync(_docker.Value!, ["info"], QueryTimeout, cancellationToken).ConfigureAwait(false);
            if (info.Succeeded)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new LeverException($"Docker engine did not start within {_config().Docker.EngineWaitSeconds} s");
    }

    private async Task StartContainersAsync(IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        var running = await RunningContainersAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var missing = wanted.Except(running, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            await RunCheckedAsync(CommandTimeout, ["start", .. missing], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DockerEngineStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_docker.Value!, StatusArguments, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? DockerParser.ParseDesktopStatus(result.StandardOutput) : DockerEngineStatus.Unknown;
    }

    /// <summary>Names of the running containers, or null when <c>docker ps</c> fails (never an empty list for "unknown").</summary>
    private async Task<IReadOnlyList<string>?> RunningContainersAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_docker.Value!, NamesArguments, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? DockerParser.ParseNames(result.StandardOutput) : null;
    }

    private async Task RunCheckedAsync(TimeSpan timeout, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_docker.Value!, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new LeverException(result.Describe(Tool));
        }
    }
}
