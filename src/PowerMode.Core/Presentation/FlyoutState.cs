using PowerMode.Core.Levers;

namespace PowerMode.Core.Presentation;

/// <summary>One monitor right now: its name (EDID friendly name), brightness, its display's refresh rate, and whether it is on.</summary>
public sealed record MonitorLive(string Name, int? BrightnessPercent, int? RefreshHz, bool Active = true);

/// <summary>
/// Values measured right now. Anything not measurable stays null and is not shown. <see cref="BrightnessPercent"/>
/// and <see cref="RefreshHz"/> are the primary monitor; <see cref="Monitors"/> lists every monitor, primary first,
/// when more than one is known.
/// <see cref="BrightnessKnown"/> is false until the monitor brightness has been read once (it is assumed until then).
/// </summary>
public sealed record LiveValues(
    double? CpuGhz = null,
    double? CpuUtilityPercent = null,
    double? GpuWatts = null,
    int? BrightnessPercent = null,
    int? RefreshHz = null,
    bool? DockerEngineRunning = null,
    int? DockerContainersRunning = null,
    IReadOnlyList<MonitorLive>? Monitors = null,
    bool BrightnessKnown = true)
{
    public static LiveValues Empty { get; } = new();

    /// <summary>Every monitor, primary first; a single unnamed one built from the primary values when none are listed.</summary>
    public IReadOnlyList<MonitorLive> AllMonitors() =>
        Monitors is { Count: > 0 } monitors ? monitors : [new MonitorLive(string.Empty, BrightnessPercent, RefreshHz)];

    /// <summary>The inputs of the power estimate: every monitor that is on.</summary>
    public Power.PowerInputs ToPowerInputs() =>
        new(CpuUtilityPercent, CpuGhz, GpuWatts, [.. AllMonitors().Where(m => m.Active).Select(m => new Power.MonitorInput(m.BrightnessPercent, m.RefreshHz))]);
}

public enum RowGlyph
{
    None,
    Ok,
    Applying,
    Failed,
}

public enum StatusTone
{
    Normal,
    Warning,
}

/// <summary>One row of the lever table: name · what the mode set (one fact per line) · live value + glyph.</summary>
public sealed record RowState(
    LeverRow Row,
    string Title,
    IReadOnlyList<string> Lines,
    string? Live,
    RowGlyph Glyph,
    string? FailureReason,
    bool ShowKeepDocker,
    bool KeepDocker);

/// <summary>Everything the flyout shows; the WPF view only binds to this, so renders and tests share it.</summary>
public sealed record FlyoutState(
    PowerModeKind Mode,
    bool IsApplying,
    string StatusLine,
    StatusTone StatusTone,
    string? Banner,
    IReadOnlyList<RowState> Rows,
    string Footer,
    bool StartWithWindows,
    Power.PowerCard? Power = null);
