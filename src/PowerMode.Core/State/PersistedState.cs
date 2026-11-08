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

    public Dictionary<LeverKind, LeverResult> LastResults { get; set; } = [];

    public ModeOptions OptionsFor(PowerModeKind mode)
    {
        if (!Options.TryGetValue(mode, out var options))
        {
            options = new ModeOptions();
            Options[mode] = options;
        }

        return options;
    }
}
