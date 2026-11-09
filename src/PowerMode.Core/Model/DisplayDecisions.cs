namespace PowerMode.Core.Model;

public enum TopologyAction
{
    /// <summary>Nothing to do: already as wanted.</summary>
    Done,

    /// <summary>Only one display is connected: skipped, not failed.</summary>
    SkipOneDisplay,

    /// <summary>Switch to the primary display only (its paths applied alone).</summary>
    PrimaryOnly,

    /// <summary>Put the saved topology back (secondary displays on again).</summary>
    Restore,
}

/// <summary>The decisions of the multi-monitor levers, apart from the Windows calls.</summary>
public static class DisplayDecisions
{
    /// <summary>A display that lacks the requested rate falls back to its lowest rate at or above this.</summary>
    public const int MinFallbackHz = 50;

    /// <summary>
    /// What the secondary-displays lever does, from the target, the connected displays and the active ones.
    /// Restoring is needed only while fewer displays are on than in the saved topology.
    /// </summary>
    public static TopologyAction Decide(TopologyTarget target, int connectedDisplays, int activeDisplays)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (connectedDisplays <= 1)
        {
            return TopologyAction.SkipOneDisplay;
        }

        if (target.PrimaryOnly)
        {
            return activeDisplays <= 1 ? TopologyAction.Done : TopologyAction.PrimaryOnly;
        }

        return target.Restore is { } saved && activeDisplays < saved.ActiveDisplays ? TopologyAction.Restore : TopologyAction.Done;
    }

    /// <summary>
    /// The monitors that must answer over DDC/CI but did not: those Grid restores, or in a saving mode those of the
    /// baseline, that are connected now (<paramref name="enumerated"/>) and gave no reading (<paramref name="answered"/>).
    /// A monitor that is not connected (unplugged, off) or never answered at capture (no DDC/CI, a TV) is not counted.
    /// </summary>
    public static IReadOnlyList<string> SilentMonitors(MonitorTarget target, IEnumerable<string> enumerated, IEnumerable<string> answered)
    {
        ArgumentNullException.ThrowIfNull(target);
        var owed = target.BrightnessPercent is null ? target.Restore.Select(m => m.Id) : target.Baseline ?? [];
        var connected = enumerated.ToHashSet(StringComparer.Ordinal);
        var read = answered.ToHashSet(StringComparer.Ordinal);
        return [.. owed.Distinct(StringComparer.Ordinal).Where(id => connected.Contains(id) && !read.Contains(id))];
    }

    /// <summary>A saving mode dims a monitor only when Grid can put it back (it is in the baseline, or no baseline is known).</summary>
    public static bool Dims(MonitorTarget target, string id)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Baseline is null || target.Baseline.Contains(id, StringComparer.Ordinal);
    }

    /// <summary>
    /// The levels of two reads one after the other, per monitor the first value that came back (so a monitor that
    /// answered only once is still in the baseline).
    /// </summary>
    public static List<MonitorLevel> MergeReads(IEnumerable<MonitorLevel> first, IEnumerable<MonitorLevel> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return [.. first.Concat(second).DistinctBy(m => m.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The rate to set on one display for <paramref name="wanted"/> Hz: the offered rate within a hertz of it, otherwise
    /// the lowest offered rate at or above 50 Hz (<c>Fallback</c> = true, to be logged); null when it offers neither.
    /// </summary>
    public static (int Hz, bool Fallback)? ChooseRefresh(IEnumerable<int> offered, int wanted)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var rates = offered.Distinct().ToList();
        if (RefreshRates.Pick(rates, wanted) is { } exact)
        {
            return (exact, false);
        }

        var lowest = rates.Where(hz => hz >= MinFallbackHz).Order().ToList();
        return lowest.Count > 0 ? (lowest[0], true) : null;
    }
}
