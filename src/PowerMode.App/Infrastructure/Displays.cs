using System.Globalization;
using PowerMode.App.Native;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Infrastructure;

/// <summary>A connected monitor: its EDID name, the GDI display it shows (null while it is off), whether it is on and primary.</summary>
internal sealed record DisplayInfo(string Name, string? Device, bool Active, bool Primary);

/// <summary>
/// The connected monitors and the display topology through the CCD API: names for the rows, how many are connected
/// and on, the active configuration to save, "primary only", and putting a saved configuration back.
/// </summary>
internal static class Displays
{
    /// <summary>Every connected monitor, primary first, then the others that are on, then those that are off.</summary>
    public static List<DisplayInfo> All()
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        var seen = new HashSet<(long, uint)>();
        var list = new List<DisplayInfo>();
        var (activePaths, activeCount, _, _) = DisplayConfig.Query(DisplayConfig.QdcOnlyActivePaths);
        for (var i = 0; i < activeCount; i++)
        {
            if (seen.Add(DisplayConfig.Target(activePaths, i)))
            {
                var device = DisplayConfig.SourceName(activePaths, i);
                list.Add(new DisplayInfo(Name(activePaths, i, list.Count), device, true, string.Equals(device, primary, StringComparison.OrdinalIgnoreCase)));
            }
        }

        var (allPaths, allCount, _, _) = DisplayConfig.Query(DisplayConfig.QdcAllPaths);
        for (var i = 0; i < allCount; i++)
        {
            if (DisplayConfig.TargetAvailable(allPaths, i) && seen.Add(DisplayConfig.Target(allPaths, i)))
            {
                list.Add(new DisplayInfo(Name(allPaths, i, list.Count), null, false, false));
            }
        }

        return [.. list.OrderByDescending(d => d.Primary).ThenByDescending(d => d.Active)];
    }

    /// <summary>The active paths and modes, to put back later; how many monitors were on.</summary>
    public static DisplayTopology Capture()
    {
        var (paths, pathCount, modes, modeCount) = DisplayConfig.Query(DisplayConfig.QdcOnlyActivePaths);
        if (pathCount == 0)
        {
            throw new LeverException("Cannot read the display configuration");
        }

        var active = Enumerable.Range(0, pathCount).Select(i => DisplayConfig.Target(paths, i)).Distinct().Count();
        var data = new byte[8 + paths.Length + modes.Length];
        BitConverter.GetBytes(pathCount).CopyTo(data, 0);
        BitConverter.GetBytes(modeCount).CopyTo(data, 4);
        paths.CopyTo(data, 8);
        modes.CopyTo(data, 8 + paths.Length);
        return new DisplayTopology(active, Convert.ToBase64String(data));
    }

    /// <summary>Only the primary display on (SDC_TOPOLOGY_INTERNAL).</summary>
    public static bool PrimaryOnly()
    {
        var code = DisplayConfig.Set(null, 0, null, 0, DisplayConfig.SdcTopologyInternal | DisplayConfig.SdcApply);
        return code == DisplayConfig.ErrorSuccess ? true : throw new LeverException($"Windows refused to turn the other displays off (code {code})");
    }

    /// <summary>Put the saved configuration back; when Windows refuses it, extend the desktop to every display instead.</summary>
    public static string Restore(DisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var data = Convert.FromBase64String(topology.Data);
        var pathCount = BitConverter.ToInt32(data, 0);
        var modeCount = BitConverter.ToInt32(data, 4);
        var paths = data[8..(8 + pathCount * DisplayConfig.PathSize)];
        var modes = data[(8 + pathCount * DisplayConfig.PathSize)..];
        var code = DisplayConfig.Set(paths, pathCount, modes, modeCount,
            DisplayConfig.SdcApply | DisplayConfig.SdcUseSuppliedDisplayConfig | DisplayConfig.SdcSaveToDatabase | DisplayConfig.SdcAllowChanges);
        if (code == DisplayConfig.ErrorSuccess)
        {
            return "saved layout restored";
        }

        var extend = DisplayConfig.Set(null, 0, null, 0, DisplayConfig.SdcTopologyExtend | DisplayConfig.SdcApply);
        return extend == DisplayConfig.ErrorSuccess
            ? string.Create(CultureInfo.InvariantCulture, $"saved layout refused (code {code}), desktop extended to every display")
            : throw new LeverException(string.Create(CultureInfo.InvariantCulture, $"Windows refused to turn the other displays on (codes {code}, {extend})"));
    }

    private static string Name(byte[] paths, int index, int position) =>
        DisplayConfig.TargetName(paths, index) ?? string.Create(CultureInfo.InvariantCulture, $"Display {position + 1}");
}
