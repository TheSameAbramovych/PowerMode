using PowerMode.App.Levers;
using PowerMode.App.Native;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// Live values for the right column and the power estimate. CPU watts are not measurable without a kernel driver, so
/// CPU shows the effective clock (% Processor Performance × nominal MHz) and % Processor Utility from PDH.
/// </summary>
internal sealed class LiveProbe : IDisposable
{
    private const string PerformanceCounter = @"\Processor Information(_Total)\% Processor Performance";
    private const string UtilityCounter = @"\Processor Information(_Total)\% Processor Utility";
    private const string FrequencyCounter = @"\Processor Information(_Total)\Processor Frequency";
    private static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DdcTimeout = TimeSpan.FromSeconds(5);

    private readonly NvmlProvider _nvml;
    private readonly DockerLever _docker;
    private readonly Lock _gate = new();
    private IntPtr _query;
    private IntPtr _performance;
    private IntPtr _utility;
    private IntPtr _frequency;
    private DateTime _brightnessReadAt = DateTime.MinValue;
    private DateTime _dockerReadAt = DateTime.MinValue;

    /// <summary>DDC/CI brightness per display device from the last read; null before the first one.</summary>
    private Dictionary<string, int>? _ddc;
    private int? _panel;
    private (bool Running, int Containers)? _dockerLive;
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
    /// CPU, GPU, the display topology and refresh rates every call. Monitor brightness (DDC/CI, and WMI for a built-in
    /// panel) at most every 10 s while the flyout is open and every 60 s otherwise, only when <paramref name="allowDdc"/>
    /// (never during a switch or a re-apply: DDC reads would interleave with the brightness writes); cached per device
    /// in between. Docker at most every 10 s, only while the flyout is open.
    /// </summary>
    /// <param name="flyoutVisible">The flyout is open.</param>
    /// <param name="allowDdc">No switch or re-apply is running.</param>
    /// <param name="fallbackPercent">The brightness assumed until it has been read once (see <see cref="LiveMonitors.Fallback"/>).</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<LiveValues> ReadAsync(bool flyoutVisible, bool allowDdc, int? fallbackPercent, CancellationToken cancellationToken)
    {
        var (ghz, utility) = ReadCpu();
        var gpu = _nvml.PowerWatts();
        var now = DateTime.UtcNow;
        if (allowDdc && now - _brightnessReadAt > (flyoutVisible ? VisibleInterval : HiddenInterval))
        {
            _brightnessReadAt = now;
            await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
        }

        if (flyoutVisible && now - _dockerReadAt > VisibleInterval)
        {
            _dockerReadAt = now;
            _dockerLive = await _docker.ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        }

        List<DisplayReading> displays;
        try
        {
            displays = await NativeCall.RunAsync(ReadDisplays, DdcTimeout, "Displays", cancellationToken).ConfigureAwait(false);
        }
        catch (Core.Levers.LeverException)
        {
            displays = [];
        }

        var monitors = LiveMonitors.Merge(displays, _ddc, _panel, fallbackPercent);
        var primary = monitors.FirstOrDefault();
        return new LiveValues(
            CpuGhz: ghz,
            CpuUtilityPercent: utility,
            GpuWatts: gpu,
            BrightnessPercent: primary?.BrightnessPercent,
            RefreshHz: primary?.RefreshHz,
            DockerEngineRunning: _dockerLive?.Running,
            DockerContainersRunning: _dockerLive?.Containers,
            Monitors: monitors.Count > 1 ? monitors : null,
            BrightnessKnown: _ddc is not null);
    }

    /// <summary>Read the monitor brightness again on the next allowed call (after a switch or a re-apply).</summary>
    public void Invalidate() => _brightnessReadAt = DateTime.MinValue;

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

    /// <summary>DDC/CI brightness per display device (the first physical monitor of each), and the built-in panel's through WMI.</summary>
    private async Task ReadBrightnessAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (ddc, panel) = await NativeCall.RunAsync(ReadBrightness, DdcTimeout, "Monitors", cancellationToken).ConfigureAwait(false);
            _ddc = ddc;
            _panel = panel;
        }
        catch (Core.Levers.LeverException)
        {
            // Keep the last values; the next interval reads again.
        }
    }

    private static (Dictionary<string, int> Ddc, int? Panel) ReadBrightness()
    {
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

        return (brightness, PanelBrightness.Read());
    }

    /// <summary>Every connected monitor, primary first, with its display's refresh rate; no DDC/CI.</summary>
    private static List<DisplayReading> ReadDisplays()
    {
        var modes = DisplayModeLever.CurrentModes().ToDictionary(m => m.Device, m => m.RefreshHz, StringComparer.OrdinalIgnoreCase);
        return [.. Displays.All().Select(d => new DisplayReading(
            d.Name,
            d.Device,
            d.Internal,
            d.Device is { } device && modes.TryGetValue(device, out var hz) ? hz : null))];
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
