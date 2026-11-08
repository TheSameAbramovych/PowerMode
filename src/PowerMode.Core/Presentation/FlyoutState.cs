using PowerMode.Core.Levers;

namespace PowerMode.Core.Presentation;

/// <summary>Values measured right now. Anything not measurable stays null and is not shown.</summary>
public sealed record LiveValues(
    double? CpuGhz = null,
    double? CpuUtilityPercent = null,
    double? GpuWatts = null,
    int? BrightnessPercent = null,
    int? RefreshHz = null,
    bool? DockerEngineRunning = null,
    int? DockerContainersRunning = null)
{
    public static LiveValues Empty { get; } = new();
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
    bool StartWithWindows);
