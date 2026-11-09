using System.Globalization;

namespace PowerMode.Core.Power;

/// <summary>
/// Coefficients of the total-power estimate (<c>power</c> in config.json). Defaults fit this PC: Ryzen 7 2700
/// (≈16 W idle, ≈60 W all-core at 3.2 GHz, ≈85 W boosted at 4.0 GHz) and the Gigabyte M27Q2
/// (80 % → 36 W, 35 % → 19.5 W, 10 % → 12 W).
/// </summary>
public sealed class PowerConfig
{
    public double CpuIdleW { get; set; } = 15;
    public double CpuSpanW { get; set; } = 45;
    public double CpuBaseGhz { get; set; } = 3.2;
    public double CpuExponent { get; set; } = 2.0;
    public double MonitorBaseW { get; set; } = 9;
    public double MonitorPerPercentW { get; set; } = 0.30;
    public double HighRefreshExtraW { get; set; } = 3;

    /// <summary>Board, RAM, drives, fans, USB.</summary>
    public double OtherW { get; set; } = 40;

    /// <summary>Power supply efficiency: what the PC draws from the wall is the DC load divided by this.</summary>
    public double PsuEfficiency { get; set; } = 0.88;
}

/// <summary>What the estimate is computed from: the live CPU, GPU and monitor values.</summary>
/// <param name="CpuUtilityPercent">% Processor Utility (may exceed 100 when boosting).</param>
/// <param name="CpuGhz">Effective clock.</param>
/// <param name="GpuW">Measured board power (NVML).</param>
/// <param name="Monitors">Every monitor that is on: brightness and refresh rate (unknown values are assumed).</param>
public sealed record PowerInputs(double? CpuUtilityPercent, double? CpuGhz, double? GpuW, IReadOnlyList<MonitorInput> Monitors)
{
    /// <summary>One monitor.</summary>
    public PowerInputs(double? cpuUtilityPercent, double? cpuGhz, double? gpuW, int? brightnessPercent, int? refreshHz)
        : this(cpuUtilityPercent, cpuGhz, gpuW, [new MonitorInput(brightnessPercent, refreshHz)])
    {
    }
}

/// <summary>One monitor of the estimate: brightness (DDC/CI or the laptop panel) and the display's refresh rate.</summary>
public sealed record MonitorInput(int? BrightnessPercent, int? RefreshHz);

/// <summary>
/// One estimate. <see cref="GpuW"/> is measured (null when NVML does not answer); the rest is estimated.
/// <see cref="OtherW"/> is everything not CPU, GPU or monitor: the rest of the PC, power-supply losses and the
/// calibration offset, so the parts always add up to <see cref="TotalW"/>.
/// </summary>
public sealed record PowerEstimate(double CpuW, double? GpuW, double MonitorW, double OtherW, double TotalW);

/// <summary>A calibration against the power station's own display: the offset added to every estimate.</summary>
public sealed record PowerCalibration(double OffsetW, DateTimeOffset CalibratedAt);

/// <summary>
/// The total power of the PC, estimated: total watts are not measurable here (no kernel driver, no CPU package
/// power). The power station's display is the reference; one calibration aligns the estimate with it.
/// </summary>
public static class PowerEstimator
{
    public const int HighRefreshAboveHz = 120;
    public const double MaxUtility = 1.25;

    /// <summary>Brightness assumed while it has not been read yet.</summary>
    public const int UnknownBrightnessPercent = 50;

    public const double MinOffsetW = -100;
    public const double MaxOffsetW = 250;
    public const double MinReadingW = 20;
    public const double MaxReadingW = 2000;

    /// <summary><c>idle + span · min(util, 1.25) · (ghz / base)^exponent</c>; idle when the counters are not read yet.</summary>
    public static double CpuW(PowerConfig config, double? utilityPercent, double? ghz)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (utilityPercent is not { } percent || ghz is not { } clock || config.CpuBaseGhz <= 0)
        {
            return config.CpuIdleW;
        }

        var utility = Math.Clamp(percent / 100, 0, MaxUtility);
        return config.CpuIdleW + config.CpuSpanW * utility * Math.Pow(Math.Max(clock, 0) / config.CpuBaseGhz, config.CpuExponent);
    }

    /// <summary><c>base + perPercent · brightness (+ extra above 120 Hz)</c>.</summary>
    public static double MonitorW(PowerConfig config, int? brightnessPercent, int? refreshHz)
    {
        ArgumentNullException.ThrowIfNull(config);
        var brightness = Math.Clamp(brightnessPercent ?? UnknownBrightnessPercent, 0, 100);
        return config.MonitorBaseW + config.MonitorPerPercentW * brightness + (refreshHz > HighRefreshAboveHz ? config.HighRefreshExtraW : 0);
    }

    /// <summary><c>(cpu + gpu + other) / psuEfficiency + monitors + offset</c> (each monitor has its own supply).</summary>
    public static PowerEstimate Estimate(PowerConfig config, PowerInputs inputs, PowerCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(inputs);
        var cpu = CpuW(config, inputs.CpuUtilityPercent, inputs.CpuGhz);
        var gpu = inputs.GpuW is { } measured ? Math.Max(measured, 0) : (double?)null;
        var monitor = inputs.Monitors.Sum(m => MonitorW(config, m.BrightnessPercent, m.RefreshHz));
        var efficiency = config.PsuEfficiency > 0 ? config.PsuEfficiency : 1;
        var total = (cpu + (gpu ?? 0) + config.OtherW) / efficiency + monitor + (calibration?.OffsetW ?? 0);
        return new PowerEstimate(cpu, gpu, monitor, total - cpu - (gpu ?? 0) - monitor, total);
    }

    /// <summary>The offset that makes the estimate equal the station's reading (clamped to −100…+250 W).</summary>
    public static PowerCalibration Calibrate(double readingW, PowerEstimate uncalibrated, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(uncalibrated);
        return new PowerCalibration(Math.Clamp(readingW - uncalibrated.TotalW, MinOffsetW, MaxOffsetW), at);
    }

    /// <summary>What the user typed as the station's reading: a number of watts, 20–2000.</summary>
    public static bool TryParseReading(string? text, out double watts, out string? error)
    {
        var trimmed = (text ?? string.Empty).Trim().TrimEnd('W', 'w').Trim().Replace(',', '.');
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out watts))
        {
            error = "Type the watts the station shows";
            return false;
        }

        error = watts is < MinReadingW or > MaxReadingW ? "Between 20 and 2000 W" : null;
        return error is null;
    }
}
