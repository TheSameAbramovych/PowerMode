using System.Globalization;
using System.Text.RegularExpressions;
using PowerMode.Core.Power;

namespace PowerMode.Core.Model;

/// <summary>An NVIDIA GPU as NVML reports it: name and the power limit range it accepts.</summary>
public sealed record GpuInfo(string Name, int MinLimitW, int MaxLimitW)
{
    public string RangeText => string.Create(CultureInfo.InvariantCulture, $"{MinLimitW}–{MaxLimitW} W");
}

/// <summary>
/// What this PC has, detected at start, so PowerMode adapts to any PC it is copied to: levers whose hardware or
/// software is missing are skipped and say so; the estimate is tuned to the CPU.
/// </summary>
/// <param name="CpuName">e.g. <c>AMD Ryzen 7 2700 Eight-Core Processor</c>.</param>
/// <param name="CpuMaxMhz">The CPU's rated clock (WMI MaxClockSpeed), when known.</param>
/// <param name="Gpu">The first NVIDIA GPU; null without NVML.</param>
/// <param name="Monitors">Monitor names, primary first.</param>
/// <param name="RefreshRates">Every refresh rate any display offers at its current resolution.</param>
/// <param name="DockerInstalled">The Docker CLI exists.</param>
/// <param name="WallpaperEngineInstalled">Wallpaper Engine exists at the configured path.</param>
/// <param name="IsElevated">Running as administrator.</param>
/// <param name="Version">PowerMode's version.</param>
public sealed record MachineInfo(
    string CpuName,
    int? CpuMaxMhz,
    GpuInfo? Gpu,
    IReadOnlyList<string> Monitors,
    IReadOnlyList<int> RefreshRates,
    bool DockerInstalled,
    bool WallpaperEngineInstalled,
    bool IsElevated,
    string Version)
{
    public const string NoNvidiaGpu = "No NVIDIA GPU";
    public const string DockerNotInstalled = "Docker Desktop not installed";

    /// <summary>Everything present: the render fixtures and tests that do not care.</summary>
    public static MachineInfo Assumed { get; } = new("CPU", null, new GpuInfo("NVIDIA GPU", 60, 132), [], [60, 144], true, true, true, "1.0.0");

    /// <summary>The <c>This PC</c> section of the Levers page: label and value, one fact per line.</summary>
    public IReadOnlyList<(string Label, string Value)> Facts() =>
    [
        ("CPU", CpuName),
        ("GPU", Gpu is { } gpu ? $"{gpu.Name} · {gpu.RangeText}" : "None (no NVIDIA GPU detected)"),
        ("Monitors", Monitors.Count > 0 ? string.Join(", ", Monitors) : "None detected"),
        ("Docker Desktop", DockerInstalled ? "Installed" : "Not installed"),
        ("Wallpaper Engine", WallpaperEngineInstalled ? "Installed" : "Not installed"),
        ("Rights", IsElevated ? "Administrator" : "Standard user"),
        ("Version", Version),
    ];
}

/// <summary>
/// A guess of the CPU's power from its name (no measurement exists), to tune the estimate on any PC: desktop Ryzen
/// non-X 65 W, Ryzen 5 X 95 W, Ryzen 7/9 X 105 W, Intel K 125 W, desktop non-K 65 W, laptop chips 15–55 W.
/// </summary>
public static partial class CpuProfiles
{
    public const int DefaultTdpW = 65;

    /// <summary>The defaults of <see cref="PowerConfig"/> fit a 65 W CPU (Ryzen 7 2700).</summary>
    private const double ReferenceTdpW = 65;

    public static int GuessTdpW(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName))
        {
            return DefaultTdpW;
        }

        if (RyzenPattern().Match(cpuName) is { Success: true } ryzen)
        {
            var tier = ryzen.Groups["tier"].Value;
            return Suffix(ryzen.Groups["suffix"].Value) ?? (ryzen.Groups["suffix"].Value.StartsWith('X') ? (tier == "5" ? 95 : 105) : DefaultTdpW);
        }

        if (IntelPattern().Match(cpuName) is { Success: true } intel)
        {
            var suffix = intel.Groups["suffix"].Value;
            return suffix.StartsWith('K') ? 125 : Suffix(suffix) ?? DefaultTdpW;
        }

        return DefaultTdpW;
    }

    /// <summary>The estimate's CPU part for this CPU: idle and span scale with the TDP; the base clock is the rated one.</summary>
    public static PowerConfig Tune(PowerConfig config, string? cpuName, int? maxMhz)
    {
        ArgumentNullException.ThrowIfNull(config);
        var scale = GuessTdpW(cpuName) / ReferenceTdpW;
        var defaults = new PowerConfig();
        config.CpuIdleW = Math.Round(defaults.CpuIdleW * scale, 1);
        config.CpuSpanW = Math.Round(defaults.CpuSpanW * scale, 1);
        if (maxMhz is > 500 and < 10_000)
        {
            config.CpuBaseGhz = Math.Round(maxMhz.Value / 1000.0, 2);
        }

        return config;
    }

    /// <summary>Laptop suffixes: U 15 W, P 28 W, T 35 W, HS 35 W, H 45 W, HX 55 W.</summary>
    private static int? Suffix(string suffix) => suffix switch
    {
        "U" => 15,
        "P" => 28,
        "T" => 35,
        "HS" => 35,
        "H" => 45,
        "HX" => 55,
        _ => null,
    };

    [GeneratedRegex(@"Ryzen\s+(?<tier>[3579])\s+(?:PRO\s+)?\d{4}(?<suffix>[A-Z0-9]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RyzenPattern();

    [GeneratedRegex(@"\bi[3579]-\d{4,5}(?<suffix>[A-Z]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex IntelPattern();
}
