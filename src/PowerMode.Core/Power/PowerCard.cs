using System.Globalization;

namespace PowerMode.Core.Power;

/// <summary>One part of the total: CPU, GPU, Monitor or Other. <see cref="Measured"/> parts are shown without "≈".</summary>
public sealed record PowerPart(string Name, double? Watts, bool Measured)
{
    /// <summary><c>CPU ≈ 24 W</c>, <c>GPU 21 W</c>, <c>GPU –</c> when unknown.</summary>
    public string Text => Watts is { } w
        ? string.Create(CultureInfo.InvariantCulture, $"{Name} {(Measured ? string.Empty : PowerCards.Approx + " ")}{w:0} W")
        : $"{Name} {PowerCards.Unknown}";

    /// <summary>The value alone, for the legend under the bar: <c>≈ 24 W</c>, <c>21 W</c>, <c>–</c>.</summary>
    public string ValueText => Watts is { } w
        ? string.Create(CultureInfo.InvariantCulture, $"{(Measured ? string.Empty : PowerCards.Approx + " ")}{w:0} W")
        : PowerCards.Unknown;

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
/// <param name="MinLabel">Lowest point of the history, e.g. <c>118</c>.</param>
/// <param name="MaxLabel">Highest point.</param>
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
        PowerPart[] parts =
        [
            new(Cpu, estimate?.CpuW, false),
            new(Gpu, estimate?.GpuW, true),
            new(Monitor, estimate?.MonitorW, false),
            new(Other, estimate?.OtherW, false),
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
            history.Count > 0 ? Watts(history.Min(p => p.TotalW)) : null,
            history.Count > 0 ? Watts(history.Max(p => p.TotalW)) : null);
    }

    /// <summary>Time of day today, otherwise with the date.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now) =>
        at.ToString(at.Date == now.Date ? "HH:mm" : "d MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Watts(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
