namespace PowerMode.Core.Power;

/// <summary>
/// One sample: when, the total, and whether every input was measured (<see cref="Complete"/>; false while the monitor
/// brightness was still assumed, so such a sample never becomes the Grid reference).
/// </summary>
public sealed record PowerPoint(DateTimeOffset At, double TotalW, bool Complete = true);

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

    /// <summary>The plotted range spans at least this many watts, so a steady load draws flat.</summary>
    public const double MinRangeW = 40;

    private readonly Queue<PowerPoint> _points = new();
    private PowerPoint[]? _snapshot;

    /// <summary>The points, oldest first; the same list until the next <see cref="Add"/> (so a view redraws only on change).</summary>
    public IReadOnlyList<PowerPoint> Points => _snapshot ??= [.. _points];

    /// <summary>When the last mode switch started in this run (null before the first one).</summary>
    public DateTimeOffset? SwitchedAt { get; private set; }

    public void Add(DateTimeOffset at, double totalW, bool complete = true)
    {
        _snapshot = null;
        _points.Enqueue(new PowerPoint(at, totalW, complete));
        while (_points.Count > Capacity || (_points.Count > 0 && at - _points.Peek().At > Window))
        {
            _points.Dequeue();
        }
    }

    public void MarkSwitch(DateTimeOffset at) => SwitchedAt = at;

    /// <summary>
    /// Mean of the points of the last <paramref name="window"/> (not before <paramref name="notBefore"/>; with
    /// <paramref name="completeOnly"/>, only <see cref="PowerPoint.Complete"/> ones); null without points.
    /// </summary>
    public double? Mean(DateTimeOffset now, TimeSpan window, DateTimeOffset? notBefore = null, bool completeOnly = false)
    {
        var from = now - window;
        if (notBefore is { } floor && floor > from)
        {
            from = floor;
        }

        var points = _points.Where(p => p.At >= from && p.At <= now && (p.Complete || !completeOnly)).ToList();
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

    /// <summary>
    /// The watts the sparkline spans bottom to top: the lowest and highest point, widened to <see cref="MinRangeW"/>
    /// around their middle when they are closer, and moved up so it never goes below 0 W.
    /// </summary>
    public static (double Min, double Max) PlotRange(IReadOnlyList<PowerPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return (0, MinRangeW);
        }

        var min = points.Min(p => p.TotalW);
        var max = points.Max(p => p.TotalW);
        if (max - min >= MinRangeW)
        {
            return (min, max);
        }

        var middle = (max + min) / 2;
        var low = middle - MinRangeW / 2;
        return low < 0 ? (0, MinRangeW) : (low, middle + MinRangeW / 2);
    }
}
