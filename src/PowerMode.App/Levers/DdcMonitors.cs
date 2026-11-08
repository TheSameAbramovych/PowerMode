using PowerMode.App.Native;

namespace PowerMode.App.Levers;

/// <summary>One DDC/CI physical monitor; id = display device + index (e.g. <c>\\.\DISPLAY1#0</c>).</summary>
internal sealed record DdcMonitor(string Id, string Description, IntPtr Handle);

/// <summary>Physical monitors of every display, opened for DDC/CI. Dispose to release the handles.</summary>
internal sealed class DdcMonitors : IDisposable
{
    private readonly List<PhysicalMonitor[]> _arrays = [];

    private DdcMonitors()
    {
    }

    public List<DdcMonitor> Monitors { get; } = [];

    public static DdcMonitors Open()
    {
        var result = new DdcMonitors();
        var handles = new List<IntPtr>();
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return true;
        }, IntPtr.Zero);

        foreach (var handle in handles)
        {
            var info = MonitorInfoEx.Create();
            if (!User32.GetMonitorInfo(handle, ref info)
                || !Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count)
                || count == 0)
            {
                continue;
            }

            var physical = new PhysicalMonitor[count];
            if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(handle, count, physical))
            {
                continue;
            }

            result._arrays.Add(physical);
            for (var i = 0; i < physical.Length; i++)
            {
                result.Monitors.Add(new DdcMonitor($"{info.DeviceName}#{i}", physical[i].Description, physical[i].Handle));
            }
        }

        return result;
    }

    public static (uint Min, uint Current, uint Max)? Brightness(DdcMonitor monitor) =>
        Dxva2.GetMonitorBrightness(monitor.Handle, out var min, out var current, out var max) ? (min, current, max) : null;

    public static (uint Min, uint Current, uint Max)? Contrast(DdcMonitor monitor) =>
        Dxva2.GetMonitorContrast(monitor.Handle, out var min, out var current, out var max) ? (min, current, max) : null;

    /// <summary>Map 0..100 % onto the monitor's own brightness range.</summary>
    public static uint FromPercent(int percent, uint min, uint max) =>
        min + (uint)Math.Round((max - min) * Math.Clamp(percent, 0, 100) / 100.0);

    public static int ToPercent(uint value, uint min, uint max) =>
        max <= min ? (int)value : (int)Math.Round((value - min) * 100.0 / (max - min));

    public void Dispose()
    {
        foreach (var array in _arrays)
        {
            Dxva2.DestroyPhysicalMonitors((uint)array.Length, array);
        }

        _arrays.Clear();
    }
}
