using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Infrastructure;
using PowerMode.Core.Levers;
using PowerMode.Core.Logging;

namespace PowerMode.App.Levers;

/// <summary>The real levers of this PC, plus the shared NVML session and Docker access the live probe reuses.</summary>
internal sealed class LeverSet : IDisposable
{
    public LeverSet(Func<AppConfig> config, ILog log)
    {
        Runner = new ProcessRunner();
        var launcher = new UserLauncher(log);
        Nvml = new NvmlProvider();
        Docker = new DockerLever(Runner, launcher, config);
        Powercfg = new PowercfgLever(Runner);
        Levers =
        [
            Powercfg,
            new DisplayModeLever(),
            new DdcBrightnessLever(),
            new NvmlGpuLever(Nvml),
            new WallpaperEngineLever(Runner, config),
            Docker,
            new ProcessLever(launcher, config),
            new EcoQosLever(),
            new ServiceLever(),
        ];
    }

    public IProcessRunner Runner { get; }

    public NvmlProvider Nvml { get; }

    public DockerLever Docker { get; }

    public PowercfgLever Powercfg { get; }

    public IReadOnlyList<ILever> Levers { get; }

    public void Dispose() => Nvml.Dispose();
}
