using PowerMode.App.Levers;
using PowerMode.App.Native;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// Live values for the right column. CPU watts are not measurable without a kernel driver, so CPU shows the
/// effective clock (% Processor Performance × nominal MHz) and % Processor Utility from PDH.
/// </summary>
internal sealed class LiveProbe : ILiveProbe, IDisposable
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

    public LiveProbe(NvmlProvider nvml, DockerLever docker)
    {
        _nvml = nvml;
        _docker = docker;
        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) == Pdh.Success)
        {
            _ = Pdh.PdhAddEnglishCounter(_query, PerformanceCounter, IntPtr.Zero, out _performance);
            _ = Pdh.PdhAddEnglishCounter(_query, UtilityCounter, IntPtr.Zero, out _utility);
            _ = Pdh.PdhAddEnglishCounter(_query, FrequencyCounter, IntPtr.Zero, out _frequency);
            _ = Pdh.PdhCollectQueryData(_query);
        }
    }

    public async Task<LiveValues> ReadAsync(CancellationToken cancellationToken)
    {
        var (ghz, utility) = ReadCpu();
        var gpu = _nvml.PowerWatts();
        if (DateTime.UtcNow - _slowReadAt > SlowInterval)
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
        int? brightness = null;
        try
        {
            brightness = await NativeCall.RunAsync(ReadBrightnessPercent, DdcTimeout, "DDC/CI", cancellationToken).ConfigureAwait(false);
        }
        catch (Core.Levers.LeverException)
        {
            brightness = null;
        }

        var refresh = DisplayModeLever.CurrentModes().FirstOrDefault()?.RefreshHz;
        var docker = await _docker.ReadLiveAsync(cancellationToken).ConfigureAwait(false);
        return new LiveValues(BrightnessPercent: brightness, RefreshHz: refresh, DockerEngineRunning: docker?.Running, DockerContainersRunning: docker?.Containers);
    }

    private static int? ReadBrightnessPercent()
    {
        using var monitors = DdcMonitors.Open();
        foreach (var monitor in monitors.Monitors)
        {
            if (DdcMonitors.Brightness(monitor) is { } b)
            {
                return DdcMonitors.ToPercent(b.Current, b.Min, b.Max);
            }
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero)
            {
                _ = Pdh.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }
}
