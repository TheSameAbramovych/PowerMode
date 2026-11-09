using PowerMode.Core.Config;
using PowerMode.Core.Levers;

namespace PowerMode.Core.Model;

/// <summary>
/// Activate <see cref="Scheme"/>; when <see cref="Configure"/> is set, (re)create and configure it first. <see cref="IsFallback"/>:
/// Grid does not know the scheme that was active before (its read failed), so it activates the configured fallback.
/// </summary>
public sealed record SchemeTarget(Guid Scheme, SchemeConfig? Configure, bool IsFallback = false);

/// <summary>Either one brightness for every monitor, or restore each monitor from the snapshot.</summary>
public sealed record MonitorTarget(int? BrightnessPercent, IReadOnlyList<MonitorLevel> Restore);

/// <summary>Either one refresh rate for every display that offers it, or restore each display mode.</summary>
public sealed record RefreshTarget(int? RefreshHz, IReadOnlyList<DisplayModeInfo> Restore);

/// <summary>Either a limit in watts (clamped by the lever) or an exact milliwatt value to restore.</summary>
public sealed record GpuTarget(int? LimitWatts, uint? RestoreMilliwatts);

public enum WallpaperAction
{
    Leave,
    Pause,
    Play,
}

/// <summary>Engine stopped, or engine running with the listed containers started.</summary>
public sealed record DockerTarget(bool Running, IReadOnlyList<string> ContainersToStart, int SnapshotContainerCount);

public sealed record AppsTarget(IReadOnlyList<string> Close, IReadOnlyList<CapturedProcess> Relaunch)
{
    public static AppsTarget Empty { get; } = new([], []);
}

/// <summary>Efficiency mode on exactly these process names; cleared everywhere else it was set.</summary>
public sealed record EfficiencyTarget(IReadOnlyList<string> ProcessNames)
{
    public static EfficiencyTarget ClearAll { get; } = new([]);
}

public sealed record ServicesTarget(IReadOnlyList<string> Stop, IReadOnlyList<string> Start)
{
    public static ServicesTarget Empty { get; } = new([], []);
}

/// <summary>
/// The desired state of every lever for one mode. A <c>null</c> lever target means "leave it alone".
/// Produced by <see cref="Planning.TargetPlanner"/>, consumed by the levers.
/// </summary>
public sealed record TargetState
{
    public required PowerModeKind Mode { get; init; }

    public SchemeTarget? Scheme { get; init; }

    public MonitorTarget? Monitor { get; init; }

    public RefreshTarget? Refresh { get; init; }

    public GpuTarget? Gpu { get; init; }

    public WallpaperAction Wallpaper { get; init; }

    public DockerTarget? Docker { get; init; }

    /// <summary>The mode's config stops Docker, so "Keep Docker running" is a choice; otherwise Docker runs by config.</summary>
    public bool DockerStopConfigured { get; init; }

    public AppsTarget Apps { get; init; } = AppsTarget.Empty;

    public EfficiencyTarget Efficiency { get; init; } = EfficiencyTarget.ClearAll;

    public ServicesTarget Services { get; init; } = ServicesTarget.Empty;

    /// <summary>This target asks the lever to change something (as opposed to "leave it alone").</summary>
    public bool Sets(LeverKind kind) => kind switch
    {
        LeverKind.PowerScheme => Scheme is not null,
        LeverKind.DisplayMode => Refresh is not null,
        LeverKind.Brightness => Monitor is not null,
        LeverKind.Gpu => Gpu is not null,
        LeverKind.WallpaperEngine => Wallpaper != WallpaperAction.Leave,
        LeverKind.Docker => Docker is not null,
        LeverKind.Apps => Apps.Close.Count > 0 || Apps.Relaunch.Count > 0,
        LeverKind.Efficiency => Efficiency.ProcessNames.Count > 0,
        LeverKind.Services => Services.Stop.Count > 0 || Services.Start.Count > 0,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
