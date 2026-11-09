namespace PowerMode.Core.Power;

/// <summary>
/// The running power estimate of the tray: the last inputs, the 10-minute history, the Grid reference taken when a
/// saving mode starts, and the card built from them. Used from the UI thread.
/// </summary>
public sealed class PowerTracker
{
    public PowerHistory History { get; } = new();

    /// <summary>The inputs of the last sample; null before the first one.</summary>
    public PowerInputs? LastInputs { get; private set; }

    /// <summary>The estimate for the last sample (with <paramref name="calibration"/>, or without it to calibrate).</summary>
    public PowerEstimate? Estimate(PowerConfig config, PowerCalibration? calibration) =>
        LastInputs is { } inputs ? PowerEstimator.Estimate(config, inputs, calibration) : null;

    /// <summary>One sample of the shared 2 s sampler.</summary>
    public PowerEstimate Add(PowerInputs inputs, DateTimeOffset now, PowerConfig config, PowerCalibration? calibration)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        LastInputs = inputs;
        var estimate = PowerEstimator.Estimate(config, inputs, calibration);
        History.Add(now, estimate.TotalW);
        return estimate;
    }

    /// <summary>
    /// A switch from <paramref name="from"/> to <paramref name="to"/> starts now: mark it, and when it leaves Grid for
    /// a saving mode return the Grid reference (the mean of the last minute; null without samples). Null otherwise,
    /// meaning "keep the stored reference". A re-apply of the same mode is not a switch.
    /// </summary>
    public (bool Leaves, double? ReferenceW) StartSwitch(PowerModeKind from, PowerModeKind to, DateTimeOffset now)
    {
        if (from == to)
        {
            return (false, null);
        }

        History.MarkSwitch(now);
        return from == PowerModeKind.Grid ? (true, History.Mean(now, PowerHistory.MeanWindow)) : (false, null);
    }

    /// <summary>The card: estimate, vs Grid (since the switch in this run, else since the mode started), history.</summary>
    public PowerCard Card(PowerConfig config, PowerCalibration? calibration, PowerModeKind mode, double? gridReferenceW, DateTimeOffset? modeSince, DateTimeOffset now)
    {
        var switchedAt = History.SwitchedAt ?? modeSince;
        return PowerCards.Build(
            Estimate(config, calibration),
            calibration,
            History.DeltaVsGrid(mode, gridReferenceW, switchedAt, now),
            History.Points,
            History.SwitchedAt,
            now);
    }
}
