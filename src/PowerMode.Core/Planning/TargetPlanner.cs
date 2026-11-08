using PowerMode.Core.Config;
using PowerMode.Core.Model;
using PowerMode.Core.State;

namespace PowerMode.Core.Planning;

/// <summary>
/// Pure function from (mode, config, snapshot, options) to the desired state of every lever.
/// Grid restores the snapshot; Station/Reserve set their values, and every item a saving mode does not
/// reduce itself is "as in the snapshot" (so Reserve→Station relaunches what only Reserve closed).
/// </summary>
public static class TargetPlanner
{
    public static TargetState Plan(PowerModeKind mode, AppConfig config, Snapshot? snapshot, ModeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var modeConfig = config.Modes.For(mode);
        return modeConfig is null
            ? PlanGrid(config, snapshot)
            : PlanSaving(mode, modeConfig, config, snapshot, options ?? new ModeOptions());
    }

    private static TargetState PlanGrid(AppConfig config, Snapshot? snapshot)
    {
        if (snapshot is null)
        {
            // Nothing was captured, so there is nothing to restore; only efficiency mode is cleared.
            return new TargetState { Mode = PowerModeKind.Grid };
        }

        return new TargetState
        {
            Mode = PowerModeKind.Grid,
            Scheme = snapshot.ActiveScheme is { } scheme && !PowerSchemes.IsOwn(scheme) ? new SchemeTarget(scheme, null) : null,
            Monitor = snapshot.Monitors.Count > 0 ? new MonitorTarget(null, snapshot.Monitors) : null,
            Refresh = snapshot.Displays.Count > 0 ? new RefreshTarget(null, snapshot.Displays) : null,
            Gpu = snapshot.Gpu is { } gpu ? new GpuTarget(null, gpu.LimitMilliwatts) : null,
            Wallpaper = snapshot.WallpaperEngineRunning ? WallpaperAction.Play : WallpaperAction.Leave,
            Docker = RestoreDocker(config, snapshot),
            Apps = new AppsTarget([], snapshot.Processes),
            Efficiency = EfficiencyTarget.ClearAll,
            Services = new ServicesTarget([], RunningServices(snapshot, [])),
        };
    }

    private static TargetState PlanSaving(PowerModeKind mode, ModeConfig m, AppConfig config, Snapshot? snapshot, ModeOptions options)
    {
        var stopDocker = m.StopDocker && !options.KeepDocker;
        return new TargetState
        {
            Mode = mode,
            Scheme = new SchemeTarget(m.Scheme.Id, m.Scheme),
            Monitor = m.BrightnessPercent is { } brightness ? new MonitorTarget(brightness, []) : null,
            Refresh = m.RefreshRateHz is { } hz ? new RefreshTarget(hz, []) : null,
            Gpu = m.GpuLimitWatts is { } watts ? new GpuTarget(watts, null) : null,
            Wallpaper = m.PauseWallpaperEngine ? WallpaperAction.Pause
                : snapshot?.WallpaperEngineRunning == true ? WallpaperAction.Play : WallpaperAction.Leave,
            Docker = stopDocker
                ? new DockerTarget(false, [], snapshot?.Docker?.RunningContainers.Count ?? 0)
                : snapshot is null ? null : RestoreDocker(config, snapshot),
            Apps = new AppsTarget(
                m.CloseApps,
                snapshot is null ? [] : [.. snapshot.Processes.Where(p => !Contains(m.CloseApps, p.Name))]),
            Efficiency = new EfficiencyTarget(m.EfficiencyApps),
            Services = new ServicesTarget(m.StopServices, snapshot is null ? [] : RunningServices(snapshot, m.StopServices)),
        };
    }

    private static DockerTarget? RestoreDocker(AppConfig config, Snapshot snapshot)
    {
        if (snapshot.Docker is not { EngineRunning: true } docker)
        {
            return null;
        }

        return new DockerTarget(
            true,
            [.. docker.RunningContainers.Where(c => !Contains(config.Docker.ExcludedContainers, c))],
            docker.RunningContainers.Count);
    }

    private static string[] RunningServices(Snapshot snapshot, IReadOnlyList<string> exclude) =>
        [.. snapshot.Services.Where(s => s.Value && !Contains(exclude, s.Key)).Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase)];

    internal static bool Contains(IEnumerable<string> names, string name) =>
        names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}
