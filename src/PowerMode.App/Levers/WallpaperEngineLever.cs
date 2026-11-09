using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Infrastructure;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Levers;

/// <summary>
/// Pauses/resumes Wallpaper Engine with <c>wallpaper64.exe -control pause|play</c>. Only sent when Wallpaper
/// Engine is running (a control call would otherwise start it). Its pause state cannot be read, so a saving
/// mode always (re)sends pause.
/// </summary>
internal sealed class WallpaperEngineLever : ILever
{
    private const string ProcessName = "wallpaper64";
    private const string ControlFlag = "-control";
    private const string Tool = "Wallpaper Engine";
    private const string NotInstalled = "Wallpaper Engine not installed";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _runner;
    private readonly Func<AppConfig> _config;

    public WallpaperEngineLever(IProcessRunner runner, Func<AppConfig> config)
    {
        _runner = runner;
        _config = config;
    }

    public LeverKind Kind => LeverKind.WallpaperEngine;

    public Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.WallpaperEngineRunning = RunningPath() is not null;
        return Task.CompletedTask;
    }

    public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var action = context.Target.Wallpaper;
        if (action == WallpaperAction.Leave)
        {
            return Task.FromResult(LeverPlan.None);
        }

        if (RunningPath() is not { } running)
        {
            return Task.FromResult(File.Exists(_config().WallpaperEngine.Path) ? LeverPlan.Done : LeverPlan.Skip(NotInstalled));
        }

        var path = File.Exists(_config().WallpaperEngine.Path) ? _config().WallpaperEngine.Path : running;
        var verb = action == WallpaperAction.Pause ? "pause" : "play";
        return Task.FromResult(LeverPlan.Of(
        [
            new LeverAction($"{(action == WallpaperAction.Pause ? "Pause" : "Resume")} Wallpaper Engine", async ct =>
            {
                var result = await _runner.RunAsync(path, [ControlFlag, verb], Timeout, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new LeverException(result.Describe(Tool));
                }
            }),
        ]));
    }

    private static string? RunningPath()
    {
        var processes = ProcessInfo.ByName(ProcessName);
        try
        {
            return processes.Select(p => ProcessInfo.TryGetPath(p.Id)).FirstOrDefault(p => p is not null) ?? (processes.Length > 0 ? string.Empty : null);
        }
        finally
        {
            ProcessInfo.DisposeAll(processes);
        }
    }
}
