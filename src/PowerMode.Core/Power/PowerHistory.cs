namespace PowerMode.Core.Power;

public sealed record PowerPoint(DateTimeOffset At, double TotalW);

/// <summary>
/// The last 10 minutes of total-power estimates (one point per 2 s sample, 300 points), in memory only, plus the
/// moment of the last mode switch. Used from one thread (the UI's).
/// </summary>
public sealed class PowerHistory
{
    public const int Capacity = 300;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The Grid reference and the saving mode's value are means over this long.</summary>
    public static readonly TimeSpan MeanWindow = TimeSpan.FromSeconds(60);

    /// <summary>A saving mode shows the difference to Grid once its samples span this long after the switch.</summary>
    public static readonly TimeSpan MinDeltaSpan = TimeSpan.FromSeconds(30);

    private readonly Queue<PowerPoint> _points = new();

    public IReadOnlyList<PowerPoint> Points => [.. _points];

    /// <summary>When the last mode switch started in this run (null before the first one).</summary>
    public DateTimeOffset? SwitchedAt { get; private set; }

    public void Add(DateTimeOffset at, double totalW)
    {
        _points.Enqueue(new PowerPoint(at, totalW));
        while (_points.Count > Capacity || (_points.Count > 0 && at - _points.Peek().At > Window))
        {
            _points.Dequeue();
        }
    }

    public void MarkSwitch(DateTimeOffset at) => SwitchedAt = at;

    /// <summary>Mean of the points of the last <paramref name="window"/> (not before <paramref name="notBefore"/>); null without points.</summary>
    public double? Mean(DateTimeOffset now, TimeSpan window, DateTimeOffset? notBefore = null)
    {
        var from = now - window;
        if (notBefore is { } floor && floor > from)
        {
            from = floor;
        }

        var points = _points.Where(p => p.At >= from && p.At <= now).ToList();
        return points.Count == 0 ? null : points.Average(p => p.TotalW);
    }

    /// <summary>
    /// Saving mode minus the Grid reference: the mean of the last 60 s after the switch, once the samples after the
    /// switch span 30 s. Null in Grid, without a reference, or too early.
    /// </summary>
    public double? DeltaVsGrid(PowerModeKind mode, double? gridReferenceW, DateTimeOffset? switchedAt, DateTimeOffset now)
    {
        if (mode == PowerModeKind.Grid || gridReferenceW is not { } reference || switchedAt is not { } since)
        {
            return null;
        }

        var after = _points.Where(p => p.At >= since && p.At <= now).ToList();
        if (after.Count == 0 || after[^1].At - after[0].At < MinDeltaSpan)
        {
            return null;
        }

        return Mean(now, MeanWindow, since) - reference;
    }
}
