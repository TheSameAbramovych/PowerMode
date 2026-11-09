using System.Runtime.InteropServices;

namespace PowerMode.App.Native;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayDevice
{
    public int Cb;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceString;

    public int StateFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string DeviceKey;

    public static DisplayDevice Create() => new() { Cb = Marshal.SizeOf<DisplayDevice>(), DeviceName = string.Empty, DeviceString = string.Empty, DeviceId = string.Empty, DeviceKey = string.Empty };
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DevMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    public short SpecVersion;
    public short DriverVersion;
    public short Size;
    public short DriverExtra;
    public int Fields;
    public int PositionX;
    public int PositionY;
    public int DisplayOrientation;
    public int DisplayFixedOutput;
    public short Color;
    public short Duplex;
    public short YResolution;
    public short TtOption;
    public short Collate;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string FormName;

    public short LogPixels;
    public int BitsPerPel;
    public int PelsWidth;
    public int PelsHeight;
    public int DisplayFlags;
    public int DisplayFrequency;
    public int IcmMethod;
    public int IcmIntent;
    public int MediaType;
    public int DitherType;
    public int Reserved1;
    public int Reserved2;
    public int PanningWidth;
    public int PanningHeight;

    public static DevMode Create() => new() { Size = (short)Marshal.SizeOf<DevMode>(), DeviceName = string.Empty, FormName = string.Empty };
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfoEx
{
    public int Size;
    public Rect Monitor;
    public Rect WorkArea;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;

    public static MonitorInfoEx Create() => new() { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal static class User32
{
    public const int EnumCurrentSettings = -1;
    public const int DisplayDeviceAttachedToDesktop = 0x1;
    public const int DmBitsPerPel = 0x40000;
    public const int DmPelsWidth = 0x80000;
    public const int DmPelsHeight = 0x100000;
    public const int DmDisplayFrequency = 0x400000;
    public const uint CdsUpdateRegistry = 0x1;
    public const uint CdsTest = 0x2;
    public const int DispChangeSuccessful = 0;
    public const int DispChangeRestart = 1;
    public const int WmHotkey = 0x0312;

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsEx(string device, int modeNumber, ref DevMode devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    public static extern int ChangeDisplaySettingsEx(string device, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    public const uint MonitorDefaultToPrimary = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    public static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    public const int AllowAnyProcess = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);
}

internal static class Kernel32
{
    public const uint AttachParentProcess = 0xFFFFFFFF;
    public const int ProcessPowerThrottling = 4;
    public const uint PowerThrottlingCurrentVersion = 1;
    public const uint PowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessSetInformation = 0x0200;

    public const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);

    /// <summary>APPLICATION_USER_MODEL_ID_MAX_LENGTH, including the terminating null.</summary>
    public const int AppUserModelIdMaxLength = 130;

    /// <summary>GetApplicationUserModelId returns this for a packaged process; APPMODEL_ERROR_NO_APPLICATION (15703) means unpackaged.</summary>
    public const int ErrorSuccess = 0;


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(Microsoft.Win32.SafeHandles.SafeProcessHandle process, ref uint length, [Out] char[] applicationUserModelId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, int size);
}

internal static class Dwmapi
{
    public const int WindowCornerPreference = 33;
    public const int BorderColor = 34;
    public const int CornerRound = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal static class Ntdll
{
    public const int ProcessCommandLineInformation = 60;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int informationClass, IntPtr information, int length, out int returnLength);
}

internal static class Shell32
{
    public const int SOk = 0;

    /// <summary>The path of a known folder; free <paramref name="path"/> with <see cref="Marshal.FreeCoTaskMem"/>.</summary>
    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
