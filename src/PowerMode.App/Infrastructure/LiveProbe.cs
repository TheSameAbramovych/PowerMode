using PowerMode.App.Levers;
using PowerMode.App.Native;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// Live values for the right column. CPU watts are not measurable without a kernel driver, so CPU shows the
/// effective clock (% Processor Performance × nominal MHz) and % Processor Utility from PDH.
/// </summary>
internal sealed class LiveProbe : IDisposable
{
    private const string PerformanceCounter = @"\Processor Information(_Total)\% Processor Performance";
    private const string UtilityCounter = @"\Processor Information(_Total)\% Processor Utility";
    private const string FrequencyCounter = @"\Processor Information(_Total)\Processor Frequency";
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DdcTimeout = TimeSpan.FromSeconds(5);

    private readonly NvmlProvider _nvml;
    private readonly DockerLever _docker;
    private readonly Lock _gate = new();
    private IntPtr _query;
    private IntPtr _performance;
    private IntPtr _utility;
    private IntPtr _frequency;
    private DateTime _slowReadAt = DateTime.MinValue;
    private LiveValues _slow = LiveValues.Empty;
    private bool _disposed;

    public LiveProbe(NvmlProvider nvml, DockerLever docker)
    {
        _nvml = nvml;
        _docker = docker;

        // Off the calling (UI) thread: PDH loads the performance-counter providers, which can stall. Reads wait on the lock.
        _ = Task.Run(OpenCounters);
    }

    private void OpenCounters()
    {
        lock (_gate)
        {
            if (!_disposed && Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) == Pdh.Success)
            {
                _ = Pdh.PdhAddEnglishCounter(_query, PerformanceCounter, IntPtr.Zero, out _performance);
                _ = Pdh.PdhAddEnglishCounter(_query, UtilityCounter, IntPtr.Zero, out _utility);
                _ = Pdh.PdhAddEnglishCounter(_query, FrequencyCounter, IntPtr.Zero, out _frequency);
                _ = Pdh.PdhCollectQueryData(_query);
            }
        }
    }

    /// <summary>
    /// CPU and GPU every call; monitor (DDC/CI) and Docker at most every 10 s and only when <paramref name="includeSlow"/>
    /// (never during a switch: DDC reads would interleave with the brightness writes).
    /// </summary>
    public async Task<LiveValues> ReadAsync(bool includeSlow, CancellationToken cancellationToken)
    {
        var (ghz, utility) = ReadCpu();
        var gpu = _nvml.PowerWatts();
        if (includeSlow && DateTime.UtcNow - _slowReadAt > SlowInterval)
        {
            _slowReadAt = DateTime.UtcNow;
            _slow = await ReadSlowAsync(cancellationToken).ConfigureAwait(false);
        }

        return _slow with { CpuGhz = ghz, CpuUtilityPercent = utility, GpuWatts = gpu };
    }

    /// <summary>Force the monitor/Docker values to be read again on the next call (after a switch).</summary>
    public void Invalidate() => _slowReadAt = DateTime.MinValue;

    public double? GpuWatts() => _nvml.PowerWatts();

    private (double? Ghz, double? Utility) ReadCpu()
    {
        lock (_gate)
        {
            if (_query == IntPtr.Zero || Pdh.PdhCollectQueryData(_query) != Pdh.Success)
            {
                return (null, null);
            }

            var performance = Read(_performance);
            var frequency = Read(_frequency);
            var ghz = performance is { } p && frequency is { } f ? p / 100.0 * f / 1000.0 : (double?)null;
            return (ghz, Read(_utility) is { } u ? Math.Min(u, 100) : null);
        }
    }

    private static double? Read(IntPtr counter) =>
        counter != IntPtr.Zero && Pdh.PdhGetFormattedCounterValue(counter, Pdh.FormatDouble | Pdh.FormatNoCap100, IntPtr.Zero, out var value) == Pdh.Success
            ? value.DoubleValue
            : null;

    private async Task<LiveValues> ReadSlowAsync(CancellationToken cancellationToken)
    {
        List<MonitorLive> monitors;
        try
        {
            monitors = await NativeCall.RunAsync(ReadMonitors, DdcTimeout, "Monitors", cancellationToken).ConfigureAwait(false);
        }
        catch (Core.Levers.LeverException)
        {
            monitors = [];
        }

        var docker = await _docker.ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        var primary = monitors.FirstOrDefault();
        return new LiveValues(
            BrightnessPercent: primary?.BrightnessPercent,
            RefreshHz: primary?.RefreshHz,
            DockerEngineRunning: docker?.Running,
            DockerContainersRunning: docker?.Containers,
            Monitors: monitors.Count > 1 ? monitors : null);
    }

    /// <summary>
    /// Every connected monitor, primary first: name, brightness (DDC/CI of the first physical monitor on its display,
    /// or the laptop panel through WMI), the display's refresh rate, and whether it is on.
    /// </summary>
    private static List<MonitorLive> ReadMonitors()
    {
        var modes = DisplayModeLever.CurrentModes().ToDictionary(m => m.Device, m => m.RefreshHz, StringComparer.OrdinalIgnoreCase);
        var brightness = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var ddc = DdcMonitors.Open())
        {
            foreach (var monitor in ddc.Monitors)
            {
                var device = monitor.Id[..monitor.Id.LastIndexOf('#')];
                if (!brightness.ContainsKey(device) && DdcMonitors.Brightness(monitor) is { } b)
                {
                    brightness[device] = DdcMonitors.ToPercent(b.Current, b.Min, b.Max);
                }
            }
        }

        var panel = PanelBrightness.Read();
        var monitors = new List<MonitorLive>();
        foreach (var display in Displays.All())
        {
            if (display.Device is not { } device)
            {
                monitors.Add(new MonitorLive(display.Name, null, null, Active: false));
                continue;
            }

            int? percent = brightness.TryGetValue(device, out var b) ? b : null;
            if (percent is null && panel is { } built)
            {
                // The panel has no DDC/CI; the first monitor without it is the panel.
                percent = built;
                panel = null;
            }

            monitors.Add(new MonitorLive(display.Name, percent, modes.TryGetValue(device, out var hz) ? hz : null));
        }

        return monitors;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_query != IntPtr.Zero)
            {
                _ = Pdh.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }
}
