using System.Reflection;
using Microsoft.Win32;
using PowerMode.App.Levers;
using PowerMode.Core.Config;
using PowerMode.Core.Model;

namespace PowerMode.App.Infrastructure;

/// <summary>Detects what this PC has (<see cref="MachineInfo"/>), so PowerMode works on any PC it is copied to. Read-only.</summary>
internal static class MachineProbe
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string CpuNameValue = "ProcessorNameString";
    private const string CpuMhzValue = "~MHz";

    /// <summary>PowerMode's version (the product version, without a build suffix), e.g. <c>1.0.0</c>.</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public static MachineInfo Read(AppConfig config, LeverSet levers)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(levers);
        using var cpu = Registry.LocalMachine.OpenSubKey(CpuKey);
        var rates = DisplayModeLever.CurrentModes()
            .SelectMany(DisplayModeLever.OfferedRates)
            .Where(hz => hz >= DisplayDecisions.MinFallbackHz)
            .Distinct()
            .Order()
            .ToList();
        return new MachineInfo(
            (cpu?.GetValue(CpuNameValue) as string)?.Trim() ?? "Unknown CPU",
            cpu?.GetValue(CpuMhzValue) as int?,
            levers.Nvml.Info(),
            [.. Displays.All().Select(d => d.Name)],
            rates,
            levers.Docker.IsInstalled,
            File.Exists(config.WallpaperEngine.Path),
            Elevation.IsElevated,
            Version);
    }
}
