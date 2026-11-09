using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Levers;

/// <summary>One NVML session for the lever and the live readings; opened on first use.</summary>
internal sealed class NvmlProvider : IDisposable
{
    private readonly Lazy<(Nvml? Nvml, string? Error)> _session = new(() => (Nvml.TryOpen(out var error), error), LazyThreadSafetyMode.ExecutionAndPublication);

    public Nvml? Session => _session.Value.Nvml;

    public string Error => _session.Value.Error ?? string.Empty;

    /// <summary>The first NVIDIA GPU: name and the limit range it accepts; null without NVML, without a controllable limit, or on a read error.</summary>
    public GpuInfo? Info()
    {
        if (Session is not { } nvml || nvml.DeviceCount() == 0)
        {
            return null;
        }

        try
        {
            var device = nvml.Device(0);
            return nvml.TryPowerLimit(device, out var limit) ? new GpuInfo(nvml.Name(device), (int)(limit.Min / 1000), (int)(limit.Max / 1000)) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Total board power of all NVIDIA GPUs in watts, or null when unavailable.</summary>
    public double? PowerWatts()
    {
        if (Session is not { } nvml)
        {
            return null;
        }

        try
        {
            var total = 0u;
            for (var i = 0; i < nvml.DeviceCount(); i++)
            {
                total += nvml.PowerUsageMilliwatts(nvml.Device(i));
            }

            return total / 1000.0;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value.Nvml?.Dispose();
        }
    }
}

/// <summary>GPU board power limit through NVML. Setting it needs administrator; the target is clamped to the card's range.</summary>
internal sealed class NvmlGpuLever : ILever
{
    private const string What = "NVML";
    private const uint MilliwattsPerWatt = 1000;
    private const string NotSupported = "GPU power limit not supported";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly NvmlProvider _nvml;

    public NvmlGpuLever(NvmlProvider nvml) => _nvml = nvml;

    public LeverKind Kind => LeverKind.Gpu;

    /// <summary>
    /// The first GPU's limit. No NVML, no NVIDIA GPU, or a GPU without a controllable limit is a legitimately empty
    /// baseline (Grid then leaves the GPU alone); other read errors throw, so the part is recorded as missing.
    /// </summary>
    public async Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Gpu = await NativeCall.RunAsync(Capture, Timeout, What, cancellationToken).ConfigureAwait(false);
    }

    private GpuSnapshot? Capture()
    {
        if (_nvml.Session is not { } nvml || nvml.DeviceCount() == 0)
        {
            return null;
        }

        var device = nvml.Device(0);
        return nvml.TryPowerLimit(device, out var limit) ? new GpuSnapshot(limit.Current, nvml.DefaultLimitMilliwatts(device)) : null;
    }

    public async Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.Gpu is not { } target)
        {
            return LeverPlan.None;
        }

        return await NativeCall.RunAsync(() => Plan(target), Timeout, What, cancellationToken).ConfigureAwait(false);
    }

    private LeverPlan Plan(GpuTarget target)
    {
        if (_nvml.Session is not { } nvml || nvml.DeviceCount() == 0)
        {
            return LeverPlan.Skip(MachineInfo.NoNvidiaGpu);
        }

        var actions = new List<LeverAction>();
        var controllable = 0;
        for (var i = 0; i < nvml.DeviceCount(); i++)
        {
            if (!nvml.TryPowerLimit(nvml.Device(i), out var limit))
            {
                continue;
            }

            controllable++;
            var (min, max, current) = limit;
            var wanted = target.RestoreMilliwatts ?? (uint)target.LimitWatts!.Value * MilliwattsPerWatt;
            wanted = Math.Clamp(wanted, min, max);
            if (current != wanted)
            {
                var index = i;
                actions.Add(new LeverAction(
                    $"GPU {index}: power limit {Watts(current)} -> {Watts(wanted)} (range {Watts(min)}-{Watts(max)})",
                    ct => NativeCall.RunAsync(() => Set(nvml, index, wanted), Timeout, What, ct),
                    RequiresElevation: true));
            }
        }

        return controllable == 0 ? LeverPlan.Skip(NotSupported) : LeverPlan.Of(actions);
    }

    private static bool Set(Nvml nvml, int index, uint milliwatts)
    {
        var code = nvml.SetLimitMilliwatts(nvml.Device(index), milliwatts);
        return code switch
        {
            Nvml.Success => true,
            Nvml.NoPermission => throw new LeverException(LeverResult.NeedsAdministrator, needsElevation: true),
            _ => throw new LeverException($"nvmlDeviceSetPowerManagementLimit failed ({code})"),
        };
    }

    private static string Watts(uint milliwatts) => $"{milliwatts / MilliwattsPerWatt} W";
}
