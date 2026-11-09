using System.Globalization;
using PowerMode.App.Native;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// A connected monitor: its EDID name, the GDI display it shows (null while it is off), whether it is on and primary,
/// and whether it is a built-in panel (a laptop's own screen).
/// </summary>
internal sealed record DisplayInfo(string Name, string? Device, bool Active, bool Primary, bool Internal);

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
                list.Add(new DisplayInfo(Name(activePaths, i, list.Count), device, true, string.Equals(device, primary, StringComparison.OrdinalIgnoreCase), DisplayConfig.TargetIsInternal(activePaths, i)));
            }
        }

        var (allPaths, allCount, _, _) = DisplayConfig.Query(DisplayConfig.QdcAllPaths);
        for (var i = 0; i < allCount; i++)
        {
            if (DisplayConfig.TargetAvailable(allPaths, i) && seen.Add(DisplayConfig.Target(allPaths, i)))
            {
                list.Add(new DisplayInfo(Name(allPaths, i, list.Count), null, false, false, DisplayConfig.TargetIsInternal(allPaths, i)));
            }
        }

        return [.. list.OrderByDescending(d => d.Primary).ThenByDescending(d => d.Active)];
    }

    /// <summary>The active paths and modes, to put back later; how many monitors were on; the adapters' device paths.</summary>
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
        var adapters = TopologyRules.Adapters(paths, pathCount, modes, modeCount)
            .Select(luid => DisplayConfig.AdapterName(luid) is { } name ? new DisplayAdapter(luid, name) : null)
            .OfType<DisplayAdapter>()
            .ToList();
        return new DisplayTopology(active, Convert.ToBase64String(data), adapters);
    }

    /// <summary>
    /// Only the primary display on: the active paths whose source is the primary (its clones included) are applied as
    /// they are, so the display Windows calls primary is the one kept. When Windows refuses that and the kept display is
    /// a built-in panel, "PC screen only" (SDC_TOPOLOGY_INTERNAL) keeps the same panel; on a desktop it could keep
    /// another display, so the lever fails instead.
    /// </summary>
    public static bool PrimaryOnly()
    {
        var (paths, pathCount, modes, modeCount) = DisplayConfig.Query(DisplayConfig.QdcOnlyActivePaths);
        var sources = Enumerable.Range(0, pathCount)
            .Select(i => new PathSource(DisplayConfig.SourceName(paths, i), DisplayConfig.SourceAtOrigin(paths, i, modes, modeCount)))
            .ToList();
        var keep = TopologyRules.PrimaryPaths(sources, System.Windows.Forms.Screen.PrimaryScreen?.DeviceName);
        if (keep.Count == 0)
        {
            throw new LeverException("Cannot find the primary display");
        }

        if (keep.Count == pathCount)
        {
            return true;
        }

        var code = DisplayConfig.Set(DisplayConfig.SelectPaths(paths, keep), keep.Count, modes, modeCount,
            DisplayConfig.SdcApply | DisplayConfig.SdcUseSuppliedDisplayConfig | DisplayConfig.SdcAllowChanges);
        if (code == DisplayConfig.ErrorSuccess)
        {
            return true;
        }

        if (!keep.Any(i => DisplayConfig.TargetIsInternal(paths, i)))
        {
            throw new LeverException(string.Create(CultureInfo.InvariantCulture, $"Windows refused to turn the other displays off (code {code})"));
        }

        var internalOnly = DisplayConfig.Set(null, 0, null, 0, DisplayConfig.SdcTopologyInternal | DisplayConfig.SdcApply);
        return internalOnly == DisplayConfig.ErrorSuccess
            ? true
            : throw new LeverException(string.Create(CultureInfo.InvariantCulture, $"Windows refused to turn the other displays off (codes {code}, {internalOnly})"));
    }

    /// <summary>
    /// Put the saved configuration back, its adapter LUIDs moved to this boot's; when Windows refuses it (a saved
    /// display is gone), extend the desktop to every display instead.
    /// </summary>
    public static string Restore(DisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var data = Convert.FromBase64String(topology.Data);
        var pathCount = BitConverter.ToInt32(data, 0);
        var modeCount = BitConverter.ToInt32(data, 4);
        var paths = data[8..(8 + pathCount * DisplayConfig.PathSize)];
        var modes = data[(8 + pathCount * DisplayConfig.PathSize)..];
        if (topology.Adapters is { Count: > 0 } saved && TopologyRules.AdapterMap(saved, CurrentAdapters()) is { } map)
        {
            _ = TopologyRules.RemapAdapters(paths, pathCount, modes, modeCount, map);
        }

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

    /// <summary>Device path to LUID of every adapter Windows reports now (all paths, connected or not).</summary>
    private static Dictionary<string, long> CurrentAdapters()
    {
        var (paths, pathCount, modes, modeCount) = DisplayConfig.Query(DisplayConfig.QdcAllPaths);
        var current = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var luid in TopologyRules.Adapters(paths, pathCount, modes, modeCount))
        {
            if (DisplayConfig.AdapterName(luid) is { } name)
            {
                current.TryAdd(name, luid);
            }
        }

        return current;
    }

    private static string Name(byte[] paths, int index, int position) =>
        DisplayConfig.TargetName(paths, index) ?? string.Create(CultureInfo.InvariantCulture, $"Display {position + 1}");
}
