using System.Runtime.InteropServices;

namespace PowerMode.App.Native;

/// <summary>Performance counters through PDH with English counter paths (the OS UI is Ukrainian).</summary>
internal static class Pdh
{
    public const uint FormatDouble = 0x00000200;
    public const uint FormatNoCap100 = 0x00008000;
    public const int Success = 0;

    [StructLayout(LayoutKind.Explicit)]
    public struct FormattedValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    public static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    public static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    public static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FormattedValue value);

    [DllImport("pdh.dll")]
    public static extern int PdhCloseQuery(IntPtr query);
}
