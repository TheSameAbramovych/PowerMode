using System.Runtime.InteropServices;

namespace PowerMode.App.Native;

/// <summary>
/// nvml.dll (ships with the NVIDIA driver in System32), loaded dynamically so a PC without NVIDIA simply
/// reports "no NVIDIA GPU" instead of crashing. Initialised once and kept for live power readings.
/// </summary>
internal sealed unsafe class Nvml : IDisposable
{
    public const int Success = 0;
    public const int NoPermission = 4;

    /// <summary>NVML_ERROR_NOT_SUPPORTED: e.g. most laptop GeForce chips have no settable power limit.</summary>
    public const int NotSupported = 3;
    private const string LibraryName = "nvml.dll";

    private readonly IntPtr _library;
    private readonly delegate* unmanaged<int> _init;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<uint*, int> _getCount;
    private readonly delegate* unmanaged<uint, IntPtr*, int> _getHandle;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _getPowerUsage;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _getLimit;
    private readonly delegate* unmanaged<IntPtr, uint*, uint*, int> _getConstraints;
    private readonly delegate* unmanaged<IntPtr, uint*, int> _getDefaultLimit;
    private readonly delegate* unmanaged<IntPtr, uint, int> _setLimit;
    private readonly delegate* unmanaged<IntPtr, byte*, uint, int> _getName;
    private bool _disposed;

    private Nvml(IntPtr library)
    {
        _library = library;
        _init = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlInit_v2");
        _shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlShutdown");
        _getCount = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetCount_v2");
        _getHandle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetHandleByIndex_v2");
        _getPowerUsage = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetPowerUsage");
        _getLimit = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetPowerManagementLimit");
        _getConstraints = (delegate* unmanaged<IntPtr, uint*, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetPowerManagementLimitConstraints");
        _getDefaultLimit = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetPowerManagementDefaultLimit");
        _setLimit = (delegate* unmanaged<IntPtr, uint, int>)NativeLibrary.GetExport(library, "nvmlDeviceSetPowerManagementLimit");
        _getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetName");
    }

    /// <summary>Load and initialise NVML; null when there is no NVIDIA driver.</summary>
    public static Nvml? TryOpen(out string? error)
    {
        error = null;
        if (!NativeLibrary.TryLoad(LibraryName, typeof(Nvml).Assembly, DllImportSearchPath.System32, out var library))
        {
            error = "No NVIDIA driver (nvml.dll not found)";
            return null;
        }

        try
        {
            var nvml = new Nvml(library);
            var code = nvml._init();
            if (code != Success)
            {
                error = $"nvmlInit failed ({code})";
                NativeLibrary.Free(library);
                return null;
            }

            return nvml;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"nvml.dll is too old: {ex.Message}";
            NativeLibrary.Free(library);
            return null;
        }
    }

    public int DeviceCount()
    {
        uint count;
        return _getCount(&count) == Success ? (int)count : 0;
    }

    public IntPtr Device(int index)
    {
        IntPtr handle;
        Check(_getHandle((uint)index, &handle), "nvmlDeviceGetHandleByIndex");
        return handle;
    }

    public uint PowerUsageMilliwatts(IntPtr device)
    {
        uint value;
        Check(_getPowerUsage(device, &value), "nvmlDeviceGetPowerUsage");
        return value;
    }

    public uint DefaultLimitMilliwatts(IntPtr device)
    {
        uint value;
        Check(_getDefaultLimit(device, &value), "nvmlDeviceGetPowerManagementDefaultLimit");
        return value;
    }

    /// <summary>
    /// The power limit's range and current value; false when this GPU has no controllable power limit (NVML answers
    /// <see cref="NotSupported"/>). Any other error throws.
    /// </summary>
    public bool TryPowerLimit(IntPtr device, out (uint Min, uint Max, uint Current) limit)
    {
        uint min = 0;
        uint max = 0;
        uint current = 0;
        var code = _getConstraints(device, &min, &max);
        if (code != NotSupported)
        {
            Check(code, "nvmlDeviceGetPowerManagementLimitConstraints");
            code = _getLimit(device, &current);
        }

        if (code == NotSupported)
        {
            limit = default;
            return false;
        }

        Check(code, "nvmlDeviceGetPowerManagementLimit");
        limit = (min, max, current);
        return true;
    }

    /// <summary>The product name, e.g. <c>NVIDIA GeForce RTX 3060</c>.</summary>
    public string Name(IntPtr device)
    {
        const int length = 96;
        var buffer = stackalloc byte[length];
        Check(_getName(device, buffer, length), "nvmlDeviceGetName");
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? "NVIDIA GPU";
    }

    /// <summary>Returns the NVML code: <see cref="Success"/>, <see cref="NoPermission"/> (not elevated), ...</summary>
    public int SetLimitMilliwatts(IntPtr device, uint milliwatts) => _setLimit(device, milliwatts);

    private static void Check(int code, string function)
    {
        if (code != Success)
        {
            throw new InvalidOperationException($"{function} failed ({code})");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown();
        NativeLibrary.Free(_library);
    }
}
