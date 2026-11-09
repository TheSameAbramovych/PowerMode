namespace PowerMode.Core.Power;

/// <summary>
/// The running power estimate of the tray: the last inputs, the 10-minute history, the Grid reference taken when a
/// saving mode starts, and the card built from them. Used from the UI thread. The history and the reference hold
/// uncalibrated totals, so a calibration made later never shifts "vs Grid"; the card shows the history with the
/// current calibration added.
/// </summary>
public sealed class PowerTracker
{
    private (IReadOnlyList<PowerPoint> Raw, double Offset, IReadOnlyList<PowerPoint> Shown)? _shown;

    /// <summary>Uncalibrated totals.</summary>
    public PowerHistory History { get; } = new();

    /// <summary>The inputs of the last sample; null before the first one.</summary>
    public PowerInputs? LastInputs { get; private set; }

    /// <summary>The estimate for the last sample (with <paramref name="calibration"/>, or without it to calibrate).</summary>
    public PowerEstimate? Estimate(PowerConfig config, PowerCalibration? calibration) =>
        LastInputs is { } inputs ? PowerEstimator.Estimate(config, inputs, calibration) : null;

    /// <summary>
    /// One sample of the shared 2 s sampler; returns the calibrated estimate. <paramref name="complete"/> is false
    /// while an input (the monitor brightness) is still assumed: such a sample is drawn but never becomes the Grid reference.
    /// </summary>
    public PowerEstimate Add(PowerInputs inputs, DateTimeOffset now, PowerConfig config, PowerCalibration? calibration, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        LastInputs = inputs;
        History.Add(now, PowerEstimator.Estimate(config, inputs).TotalW, complete);
        return PowerEstimator.Estimate(config, inputs, calibration);
    }

    /// <summary>
    /// A switch from <paramref name="from"/> to <paramref name="to"/> starts now: mark it, and when it leaves Grid for
    /// a saving mode return the uncalibrated Grid reference (the mean of the complete samples of the last minute; null
    /// without any). Null otherwise, meaning "keep the stored reference". A re-apply of the same mode is not a switch.
    /// </summary>
    public (bool Leaves, double? ReferenceW) StartSwitch(PowerModeKind from, PowerModeKind to, DateTimeOffset now)
    {
        if (from == to)
        {
            return (false, null);
        }

        History.MarkSwitch(now);
        return from == PowerModeKind.Grid ? (true, History.Mean(now, PowerHistory.MeanWindow, completeOnly: true)) : (false, null);
    }

    /// <summary>
    /// The card: estimate, vs Grid (since the switch in this run, else since the mode started; uncalibrated on both
    /// sides, so the offset cancels), and the history with the current calibration added.
    /// </summary>
    public PowerCard Card(PowerConfig config, PowerCalibration? calibration, PowerModeKind mode, double? gridReferenceRawW, DateTimeOffset? modeSince, DateTimeOffset now)
    {
        var switchedAt = History.SwitchedAt ?? modeSince;
        return PowerCards.Build(
            Estimate(config, calibration),
            calibration,
            History.DeltaVsGrid(mode, gridReferenceRawW, switchedAt, now),
            Shown(calibration?.OffsetW ?? 0),
            History.SwitchedAt,
            now);
    }

    /// <summary>The history in today's calibrated terms; the same list until a sample or the offset changes (a view redraws only on change).</summary>
    private IReadOnlyList<PowerPoint> Shown(double offset)
    {
        var raw = History.Points;
        if (_shown is { } cached && ReferenceEquals(cached.Raw, raw) && cached.Offset.Equals(offset))
        {
            return cached.Shown;
        }

        IReadOnlyList<PowerPoint> shown = offset == 0 ? raw : [.. raw.Select(p => p with { TotalW = p.TotalW + offset })];
        _shown = (raw, offset, shown);
        return shown;
    }
}
