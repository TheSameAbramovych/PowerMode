namespace PowerMode.Core.Model;

/// <summary>One active display path as the "primary only" rule sees it: its source's GDI name and whether that source sits at the desktop origin.</summary>
public sealed record PathSource(string? Device, bool AtOrigin);

/// <summary>An adapter in a saved display topology: its boot-specific LUID and its stable device path.</summary>
public sealed record DisplayAdapter(long Luid, string DevicePath);

/// <summary>
/// The decisions on raw CCD data (DISPLAYCONFIG_PATH_INFO 72 bytes, DISPLAYCONFIG_MODE_INFO 64 bytes), apart from the
/// Windows calls: which paths "primary only" keeps, which output is a built-in panel, and moving a saved topology
/// onto the adapter LUIDs of the current boot.
/// </summary>
public static class TopologyRules
{
    public const int PathSize = 72;
    public const int ModeSize = 64;

    /// <summary>Offsets of the adapter LUIDs: path source and target, mode adapterId.</summary>
    private const int PathSourceAdapterOffset = 0;
    private const int PathTargetAdapterOffset = 20;
    private const int ModeAdapterOffset = 8;

    /// <summary>DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL, _DISPLAYPORT_EMBEDDED and _UDI_EMBEDDED.</summary>
    private const uint OutputInternal = 0x80000000u;
    private const uint OutputDisplayPortEmbedded = 11u;
    private const uint OutputUdiEmbedded = 13u;

    /// <summary>
    /// The paths "primary only" keeps: every path whose source is the primary display (so a cloned primary keeps its
    /// clones); when no source carries the primary's GDI name, the paths whose source is at the desktop origin (0,0),
    /// which is the primary by definition; empty when neither is found.
    /// </summary>
    public static IReadOnlyList<int> PrimaryPaths(IReadOnlyList<PathSource> paths, string? primary)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var byName = Indices(paths, p => primary is not null && string.Equals(p.Device, primary, StringComparison.OrdinalIgnoreCase));
        return byName.Count > 0 ? byName : Indices(paths, p => p.AtOrigin);
    }

    /// <summary>The output technology of a built-in panel (a laptop's own screen), not an external connector.</summary>
    public static bool IsInternalOutput(uint technology) =>
        technology is OutputInternal or OutputDisplayPortEmbedded or OutputUdiEmbedded;

    /// <summary>
    /// Old LUID to current LUID for every saved adapter, matched by device path; null when a saved adapter is not
    /// connected now (the saved layout cannot be put back as it was).
    /// </summary>
    public static Dictionary<long, long>? AdapterMap(IEnumerable<DisplayAdapter> saved, IReadOnlyDictionary<string, long> current)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var map = new Dictionary<long, long>();
        foreach (var adapter in saved)
        {
            if (!current.TryGetValue(adapter.DevicePath, out var now))
            {
                return null;
            }

            map[adapter.Luid] = now;
        }

        return map;
    }

    /// <summary>
    /// Rewrite every adapter LUID in the path and mode arrays through <paramref name="map"/>, in place. False (arrays
    /// unchanged) when one of them is not in the map.
    /// </summary>
    public static bool RemapAdapters(byte[] paths, int pathCount, byte[] modes, int modeCount, IReadOnlyDictionary<long, long> map)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(map);
        var offsets = new List<(byte[] Data, int Offset)>();
        for (var i = 0; i < pathCount; i++)
        {
            offsets.Add((paths, i * PathSize + PathSourceAdapterOffset));
            offsets.Add((paths, i * PathSize + PathTargetAdapterOffset));
        }

        for (var i = 0; i < modeCount; i++)
        {
            offsets.Add((modes, i * ModeSize + ModeAdapterOffset));
        }

        if (!offsets.TrueForAll(o => map.ContainsKey(BitConverter.ToInt64(o.Data, o.Offset))))
        {
            return false;
        }

        foreach (var (data, offset) in offsets)
        {
            BitConverter.GetBytes(map[BitConverter.ToInt64(data, offset)]).CopyTo(data, offset);
        }

        return true;
    }

    /// <summary>The distinct adapter LUIDs in the path and mode arrays.</summary>
    public static IReadOnlyList<long> Adapters(byte[] paths, int pathCount, byte[] modes, int modeCount)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(modes);
        var luids = new List<long>();
        for (var i = 0; i < pathCount; i++)
        {
            luids.Add(BitConverter.ToInt64(paths, i * PathSize + PathSourceAdapterOffset));
            luids.Add(BitConverter.ToInt64(paths, i * PathSize + PathTargetAdapterOffset));
        }

        for (var i = 0; i < modeCount; i++)
        {
            luids.Add(BitConverter.ToInt64(modes, i * ModeSize + ModeAdapterOffset));
        }

        return [.. luids.Distinct()];
    }

    private static List<int> Indices(IReadOnlyList<PathSource> paths, Func<PathSource, bool> match) =>
        [.. Enumerable.Range(0, paths.Count).Where(i => match(paths[i]))];
}
