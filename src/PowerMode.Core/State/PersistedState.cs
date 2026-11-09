using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.Core.State;

/// <summary>Options the user toggles per mode in the flyout.</summary>
public sealed class ModeOptions
{
    /// <summary>Do not stop Docker Desktop in this mode (the owner needs it for local CI sometimes).</summary>
    public bool KeepDocker { get; set; }
}

/// <summary>A process efficiency mode was set on, so Grid can clear exactly those.</summary>
public sealed record EfficiencyRecord(int ProcessId, string Name, DateTimeOffset ProcessStart, int PreviousPriority);

/// <summary>Everything in state.json.</summary>
public sealed class PersistedState
{
    public PowerModeKind Mode { get; set; } = PowerModeKind.Grid;

    /// <summary>When the current mode was entered.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>When a lever was last applied (switch or re-apply).</summary>
    public DateTimeOffset? LastApplied { get; set; }

    public Snapshot? Snapshot { get; set; }

    public Dictionary<PowerModeKind, ModeOptions> Options { get; set; } = [];

    public List<EfficiencyRecord> EfficiencyApplied { get; set; } = [];

    /// <summary>Process names PowerMode closed itself; only these are relaunched (an app the user quit stays quit).</summary>
    public List<string> ClosedApps { get; set; } = [];

    public Dictionary<LeverKind, LeverResult> LastResults { get; set; } = [];

    /// <summary>The flyout stays open when it loses focus (default); off = it hides when you click elsewhere.</summary>
    public bool FlyoutPinned { get; set; } = true;

    /// <summary>
    /// The exe path whose tray icon PowerMode has already promoted onto the taskbar (or told the user to drag there).
    /// Done once per path, so an icon the user later hides stays hidden.
    /// </summary>
    public string? TrayPromotedFor { get; set; }

    /// <summary>The options of a mode (defaults when never set). Read-only: change them with <see cref="WithOptions"/>.</summary>
    public ModeOptions OptionsFor(PowerModeKind mode) => Options.TryGetValue(mode, out var options) ? options : new ModeOptions();

    /// <summary>Replace the options of a mode copy-on-write, so readers on other threads never see a dictionary being changed.</summary>
    public void WithOptions(PowerModeKind mode, ModeOptions options) => Options = new Dictionary<PowerModeKind, ModeOptions>(Options) { [mode] = options };
}
