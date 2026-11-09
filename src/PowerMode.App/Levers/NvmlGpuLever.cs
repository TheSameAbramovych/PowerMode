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

    /// <summary>The first NVIDIA GPU: name and the limit range it accepts; null without NVML or on a read error.</summary>
    public GpuInfo? Info()
    {
        if (Session is not { } nvml || nvml.DeviceCount() == 0)
        {
            return null;
        }

        try
        {
            var device = nvml.Device(0);
            var (min, max) = nvml.Constraints(device);
            return new GpuInfo(nvml.Name(device), (int)(min / 1000), (int)(max / 1000));
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly NvmlProvider _nvml;

    public NvmlGpuLever(NvmlProvider nvml) => _nvml = nvml;

    public LeverKind Kind => LeverKind.Gpu;

    public async Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Gpu = await NativeCall.RunAsync(
            () => _nvml.Session is { } nvml && nvml.DeviceCount() > 0
                ? new GpuSnapshot(nvml.LimitMilliwatts(nvml.Device(0)), nvml.DefaultLimitMilliwatts(nvml.Device(0)))
                : null,
            Timeout,
            What,
            cancellationToken).ConfigureAwait(false);
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
        if (_nvml.Session is not { } nvml)
        {
            return LeverPlan.Skip(MachineInfo.NoNvidiaGpu);
        }

        var actions = new List<LeverAction>();
        for (var i = 0; i < nvml.DeviceCount(); i++)
        {
            var device = nvml.Device(i);
            var (min, max) = nvml.Constraints(device);
            var wanted = target.RestoreMilliwatts ?? (uint)target.LimitWatts!.Value * MilliwattsPerWatt;
            wanted = Math.Clamp(wanted, min, max);
            var current = nvml.LimitMilliwatts(device);
            if (current != wanted)
            {
                var index = i;
                actions.Add(new LeverAction(
                    $"GPU {index}: power limit {Watts(current)} -> {Watts(wanted)} (range {Watts(min)}-{Watts(max)})",
                    ct => NativeCall.RunAsync(() => Set(nvml, index, wanted), Timeout, What, ct),
                    RequiresElevation: true));
            }
        }

        return LeverPlan.Of(actions);
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
