using System.Runtime.InteropServices;

namespace PowerMode.App.Native;

/// <summary>
/// user32 CCD API (QueryDisplayConfig / SetDisplayConfig / DisplayConfigGetDeviceInfo). Paths and modes are kept as
/// raw bytes (DISPLAYCONFIG_PATH_INFO is 72 bytes, DISPLAYCONFIG_MODE_INFO 64), so a saved topology can be handed
/// back to SetDisplayConfig unchanged; only the few fields PowerMode reads are decoded.
/// </summary>
internal static class DisplayConfig
{
    public const int PathSize = 72;
    public const int ModeSize = 64;

    public const uint QdcAllPaths = 0x1;
    public const uint QdcOnlyActivePaths = 0x2;

    public const uint SdcTopologyInternal = 0x1;
    public const uint SdcTopologyExtend = 0x4;
    public const uint SdcUseSuppliedDisplayConfig = 0x20;
    public const uint SdcApply = 0x80;
    public const uint SdcSaveToDatabase = 0x200;
    public const uint SdcAllowChanges = 0x400;

    public const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int GetAdapterName = 4;
    private const int SourceNameSize = 84;
    private const int TargetNameSize = 420;
    private const int AdapterNameSize = HeaderSize + AdapterPathChars * 2;
    private const int AdapterPathChars = 128;
    private const int HeaderSize = 20;
    private const int TargetFriendlyNameOffset = HeaderSize + 16;
    private const int TargetFriendlyNameChars = 64;
    private const int SourceGdiNameChars = 32;

    /// <summary>Offsets in DISPLAYCONFIG_PATH_INFO: source adapter LUID + id, target adapter LUID + id, target available.</summary>
    private const int SourceIdOffset = 8;
    private const int SourceModeIndexOffset = 12;
    private const int TargetAdapterOffset = 20;
    private const int TargetIdOffset = 28;
    private const int OutputTechnologyOffset = 36;
    private const int TargetAvailableOffset = 60;

    /// <summary>DISPLAYCONFIG_MODE_INFO: infoType (1 = source), and the source mode's desktop position.</summary>
    private const int ModeInfoTypeSource = 1;
    private const int SourcePositionXOffset = 28;
    private const int SourcePositionYOffset = 32;
    private const uint NoModeIndex = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] byte[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, byte[]? paths, uint modeCount, byte[]? modes, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] packet);

    /// <summary>The paths and modes Windows reports; empty on failure.</summary>
    public static (byte[] Paths, int PathCount, byte[] Modes, int ModeCount) Query(uint flags)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != ErrorSuccess)
            {
                break;
            }

            var paths = new byte[pathCount * PathSize];
            var modes = new byte[modeCount * ModeSize];
            var code = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (code == ErrorSuccess)
            {
                return (paths[..(int)(pathCount * PathSize)], (int)pathCount, modes[..(int)(modeCount * ModeSize)], (int)modeCount);
            }

            if (code != ErrorInsufficientBuffer)
            {
                break;
            }
        }

        return ([], 0, [], 0);
    }

    public static int Set(byte[]? paths, int pathCount, byte[]? modes, int modeCount, uint flags) =>
        SetDisplayConfig((uint)pathCount, paths, (uint)modeCount, modes, flags);

    /// <summary>A target (monitor) of a path: adapter LUID + target id, unique per connected monitor.</summary>
    public static (long Adapter, uint Id) Target(byte[] paths, int index) =>
        (BitConverter.ToInt64(paths, index * PathSize + TargetAdapterOffset), BitConverter.ToUInt32(paths, index * PathSize + TargetIdOffset));

    public static bool TargetAvailable(byte[] paths, int index) => BitConverter.ToInt32(paths, index * PathSize + TargetAvailableOffset) != 0;

    /// <summary>The GDI name of a path's source, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public static string? SourceName(byte[] paths, int index)
    {
        var packet = Header(GetSourceName, SourceNameSize, paths, index * PathSize, BitConverter.ToUInt32(paths, index * PathSize + SourceIdOffset));
        return DisplayConfigGetDeviceInfo(packet) == ErrorSuccess ? Text(packet, HeaderSize, SourceGdiNameChars) : null;
    }

    /// <summary>The monitor's friendly name from its EDID, e.g. <c>M27Q2</c>; null when it has none.</summary>
    public static string? TargetName(byte[] paths, int index)
    {
        var (adapter, id) = Target(paths, index);
        var packet = new byte[TargetNameSize];
        BitConverter.GetBytes(GetTargetName).CopyTo(packet, 0);
        BitConverter.GetBytes(TargetNameSize).CopyTo(packet, 4);
        BitConverter.GetBytes(adapter).CopyTo(packet, 8);
        BitConverter.GetBytes(id).CopyTo(packet, 16);
        return DisplayConfigGetDeviceInfo(packet) == ErrorSuccess && Text(packet, TargetFriendlyNameOffset, TargetFriendlyNameChars) is { Length: > 0 } name ? name : null;
    }

    /// <summary>The path's monitor is a built-in panel (its output technology is internal or embedded).</summary>
    public static bool TargetIsInternal(byte[] paths, int index) =>
        Core.Model.TopologyRules.IsInternalOutput(BitConverter.ToUInt32(paths, index * PathSize + OutputTechnologyOffset));

    /// <summary>The path's source mode sits at the desktop origin (0,0): that source is the primary display.</summary>
    public static bool SourceAtOrigin(byte[] paths, int index, byte[] modes, int modeCount)
    {
        var mode = BitConverter.ToUInt32(paths, index * PathSize + SourceModeIndexOffset);
        if (mode == NoModeIndex || mode >= modeCount)
        {
            return false;
        }

        var offset = (int)mode * ModeSize;
        return BitConverter.ToInt32(modes, offset) == ModeInfoTypeSource
            && BitConverter.ToInt32(modes, offset + SourcePositionXOffset) == 0
            && BitConverter.ToInt32(modes, offset + SourcePositionYOffset) == 0;
    }

    /// <summary>The paths at the given indices, back to back, for SetDisplayConfig.</summary>
    public static byte[] SelectPaths(byte[] paths, IReadOnlyList<int> indices)
    {
        var selected = new byte[indices.Count * PathSize];
        for (var i = 0; i < indices.Count; i++)
        {
            Array.Copy(paths, indices[i] * PathSize, selected, i * PathSize, PathSize);
        }

        return selected;
    }

    /// <summary>An adapter's device path, stable across boots (unlike its LUID); null when Windows does not say.</summary>
    public static string? AdapterName(long adapter)
    {
        var packet = new byte[AdapterNameSize];
        BitConverter.GetBytes(GetAdapterName).CopyTo(packet, 0);
        BitConverter.GetBytes(AdapterNameSize).CopyTo(packet, 4);
        BitConverter.GetBytes(adapter).CopyTo(packet, 8);
        return DisplayConfigGetDeviceInfo(packet) == ErrorSuccess && Text(packet, HeaderSize, AdapterPathChars) is { Length: > 0 } name ? name : null;
    }

    private static byte[] Header(int type, int size, byte[] paths, int adapterOffset, uint id)
    {
        var packet = new byte[size];
        BitConverter.GetBytes(type).CopyTo(packet, 0);
        BitConverter.GetBytes(size).CopyTo(packet, 4);
        Array.Copy(paths, adapterOffset, packet, 8, 8);
        BitConverter.GetBytes(id).CopyTo(packet, 16);
        return packet;
    }

    private static string Text(byte[] packet, int offset, int chars)
    {
        var text = System.Text.Encoding.Unicode.GetString(packet, offset, chars * 2);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }
}
