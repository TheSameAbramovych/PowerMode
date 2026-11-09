using System.Globalization;

namespace PowerMode.Core.Power;

/// <summary>
/// One part of the total: CPU, GPU, Monitor or Other. <see cref="Measured"/> parts are shown without "≈".
/// <see cref="Shown"/> is the whole number shown: the parts are rounded so that they add up to the shown total.
/// </summary>
public sealed record PowerPart(string Name, double? Watts, bool Measured, int? Shown = null)
{
    /// <summary><c>CPU ≈ 24 W</c>, <c>GPU 21 W</c>, <c>GPU –</c> when unknown.</summary>
    public string Text => Amount is { } amount ? $"{Name} {(Measured ? string.Empty : PowerCards.Approx + " ")}{amount}" : $"{Name} {PowerCards.Unknown}";

    /// <summary>The value alone, for the legend under the bar: <c>≈ 24 W</c>, <c>21 W</c>, <c>–</c>.</summary>
    public string ValueText => Amount is { } amount ? $"{ApproxMark}{(Measured ? string.Empty : " ")}{amount}" : PowerCards.Unknown;

    /// <summary>The legend's fixed-width "≈" slot (empty for a measured part), so the numbers line up.</summary>
    public string ApproxMark => Measured || Watts is null ? string.Empty : PowerCards.Approx;

    /// <summary><c>24 W</c>; <c>–</c> when unknown.</summary>
    public string AmountText => Amount ?? PowerCards.Unknown;

    private string? Amount => (Shown ?? (Watts is { } w ? (int?)(int)Math.Round(w) : null)) is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"{value} W")
        : null;

    /// <summary>Share of the bar: never negative.</summary>
    public double BarWatts => Math.Max(Watts ?? 0, 0);
}

/// <summary>Everything the power card shows, as text and numbers; the WPF card only draws it.</summary>
/// <param name="TotalW">The estimate; null before the first sample.</param>
/// <param name="TotalText"><c>≈ 142</c> (the unit is drawn beside it).</param>
/// <param name="DeltaText"><c>▼ 61 W vs Grid</c>; null when not available (Grid, no reference, too early).</param>
/// <param name="DeltaIsSaving">Lower than Grid: shown in the "good" colour, otherwise muted (never red).</param>
/// <param name="CalibrationNote"><c>Estimate</c> or <c>Calibrated 10:31</c>.</param>
/// <param name="CalibrationAction"><c>Calibrate</c> or <c>Recalibrate</c>.</param>
/// <param name="IsCalibrated">A calibration is in use (Reset is offered).</param>
/// <param name="Parts">CPU, GPU, Monitor, Other, in this order.</param>
/// <param name="History">The last 10 minutes.</param>
/// <param name="SwitchMarker">The last mode switch, when inside the history.</param>
/// <param name="MinLabel">Bottom of the plotted range (see <see cref="PowerHistory.PlotRange"/>), e.g. <c>118</c>.</param>
/// <param name="MaxLabel">Top of the plotted range.</param>
public sealed record PowerCard(
    double? TotalW,
    string TotalText,
    string? DeltaText,
    bool DeltaIsSaving,
    string CalibrationNote,
    string CalibrationAction,
    bool IsCalibrated,
    IReadOnlyList<PowerPart> Parts,
    IReadOnlyList<PowerPoint> History,
    DateTimeOffset? SwitchMarker,
    string? MinLabel,
    string? MaxLabel)
{
    /// <summary><c>Total ≈ 142 W · ▼ 61 W vs Grid · Calibrated 10:31</c> for <c>--status</c>.</summary>
    public string SummaryLine => string.Join(PowerCards.Dot, new[] { $"Total {TotalText} W", DeltaText, CalibrationNote }.OfType<string>());

    /// <summary><c>CPU ≈ 24 W · GPU 21 W · Monitor ≈ 36 W · Other ≈ 61 W</c>.</summary>
    public string PartsLine => string.Join(PowerCards.Dot, Parts.Select(p => p.Text));
}

public static class PowerCards
{
    public const string Approx = "≈";
    public const string Unknown = "–";
    public const string Dot = " · ";
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Monitor = "Monitor";
    public const string Other = "Other";

    public static PowerCard Build(
        PowerEstimate? estimate,
        PowerCalibration? calibration,
        double? deltaVsGridW,
        IReadOnlyList<PowerPoint> history,
        DateTimeOffset? switchedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        var total = estimate?.TotalW;
        var totalText = total is { } t ? string.Create(CultureInfo.InvariantCulture, $"{Approx} {t:0}") : $"{Approx} {Unknown}";
        string? delta = deltaVsGridW switch
        {
            null => null,
            { } d when Math.Round(d) == 0 => "Same as Grid",
            { } d when d < 0 => string.Create(CultureInfo.InvariantCulture, $"▼ {-d:0} W vs Grid"),
            { } d => string.Create(CultureInfo.InvariantCulture, $"▲ {d:0} W vs Grid"),
        };
        var note = calibration is { } c ? $"Calibrated {When(c.CalibratedAt, now)}" : "Estimate";
        var shown = estimate is null ? null : Apportion(estimate);
        PowerPart[] parts =
        [
            new(Cpu, estimate?.CpuW, false, shown?[0]),
            new(Gpu, estimate?.GpuW, true, shown?[1]),
            new(Monitor, estimate?.MonitorW, false, shown?[2]),
            new(Other, estimate?.OtherW, false, shown?[3]),
        ];
        var marker = switchedAt is { } s && history.Count > 0 && s >= history[0].At ? s : (DateTimeOffset?)null;
        return new PowerCard(
            total,
            totalText,
            delta,
            deltaVsGridW < 0 && delta != "Same as Grid",
            note,
            calibration is null ? "Calibrate" : "Recalibrate",
            calibration is not null,
            parts,
            history,
            marker,
            history.Count > 0 ? Watts(PowerHistory.PlotRange(history).Min) : null,
            history.Count > 0 ? Watts(PowerHistory.PlotRange(history).Max) : null);
    }

    /// <summary>
    /// Whole watts for CPU, GPU, Monitor and Other that add up to the shown total: the measured GPU keeps its own
    /// rounding, the estimated parts share the rest by the largest remainder.
    /// </summary>
    public static int[] Apportion(PowerEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var total = (int)Math.Round(estimate.TotalW);
        var gpu = estimate.GpuW is { } measured ? (int)Math.Round(measured) : 0;
        double[] estimated = [estimate.CpuW, estimate.MonitorW, estimate.OtherW];
        var floors = estimated.Select(v => (int)Math.Floor(v)).ToArray();
        var left = total - gpu - floors.Sum();
        foreach (var index in Enumerable.Range(0, estimated.Length).OrderByDescending(i => estimated[i] - floors[i]).ThenBy(i => i))
        {
            if (left == 0)
            {
                break;
            }

            var step = Math.Sign(left);
            floors[index] += step;
            left -= step;
        }

        // More than one watt per part to move (a rounding never needs that): Other takes the rest.
        floors[2] += left;
        return [floors[0], gpu, floors[1], floors[2]];
    }

    /// <summary>Time of day today, otherwise with the date.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now) =>
        at.ToString(at.Date == now.Date ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Watts(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
