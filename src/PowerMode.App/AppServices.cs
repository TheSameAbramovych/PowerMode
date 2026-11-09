using PowerMode.App.Infrastructure;
using PowerMode.App.Levers;
using PowerMode.Core;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Logging;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.Power;
using PowerMode.Core.Presentation;
using PowerMode.Core.State;
using PowerMode.Core.Switching;

namespace PowerMode.App;

/// <summary>A switch being applied: which levers started, which still run, and the results so far.</summary>
internal sealed class SwitchInProgress(PowerModeKind mode, IReadOnlySet<LeverKind> started)
{
    public PowerModeKind Mode { get; } = mode;

    public IReadOnlySet<LeverKind> Started { get; } = started;

    public HashSet<LeverKind> Running { get; } = [.. started];

    public Dictionary<LeverKind, LeverResult> Finished { get; } = [];
}

/// <summary>Composition root shared by the tray app and the CLI commands.</summary>
internal sealed class AppServices : IDisposable
{
    private AppServices()
    {
        Log = LogSinks.Create();
        Log.Prune();
        Config = new ConfigStore(AppPaths.ConfigFile, Log);
        Config.Load();
        LeverSet = new LeverSet(() => Config.Current, Log);
        Switcher = new ModeSwitcher(LeverSet.Levers, new StateStore(AppPaths.StateFile, Log), () => Config.Current, () => Elevation.IsElevated, TimeProvider.System, Log);
        Live = new LiveProbe(LeverSet.Nvml, LeverSet.Docker);
        Autostart = new AutostartTask(LeverSet.Runner);
    }

    public FileLog Log { get; }

    public ConfigStore Config { get; }

    public LeverSet LeverSet { get; }

    public ModeSwitcher Switcher { get; }

    public LiveProbe Live { get; }

    public AutostartTask Autostart { get; }

    public static AppServices Create() => new();

    /// <summary>What this PC has; <see cref="MachineInfo.Assumed"/> until <see cref="RefreshMachine"/> has run.</summary>
    public MachineInfo Machine { get; private set; } = MachineInfo.Assumed;

    /// <summary>Detect CPU, GPU, monitors, Docker and Wallpaper Engine (registry, NVML, CCD: run it off the UI thread).</summary>
    public MachineInfo RefreshMachine() => Machine = MachineProbe.Read(Config.Current, LeverSet);

    /// <summary>
    /// The first start on this PC (config.json was just created): Grid's fallback scheme becomes the scheme active now
    /// (unless it is one of PowerMode's), and the CPU part of the estimate is tuned to this CPU.
    /// </summary>
    public async Task TuneFirstRunAsync()
    {
        if (!Config.CreatedOnLoad)
        {
            return;
        }

        Guid? active = null;
        try
        {
            active = await LeverSet.Powercfg.ActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (LeverException ex)
        {
            Log.Error("config", "Cannot read the active power scheme for the fallback scheme", ex);
        }

        var machine = Machine;
        var fallback = active is { } scheme && !PowerSchemes.IsOwn(scheme) ? scheme : (Guid?)null;
        Config.Update(
            config =>
            {
                config.FallbackScheme = fallback ?? config.FallbackScheme;
                CpuProfiles.Tune(config.Power, machine.CpuName, machine.CpuMaxMhz);
            },
            $"first start on this PC: fallback scheme {fallback?.ToString() ?? "unchanged"}, CPU estimate for {machine.CpuName} ({CpuProfiles.GuessTdpW(machine.CpuName)} W)");
    }

    /// <summary>Before applying anything: if the state was lost while a PowerMode scheme is active, take that mode back.</summary>
    public async Task RecoverLostModeAsync()
    {
        try
        {
            var active = await LeverSet.Powercfg.ActiveSchemeAsync(CancellationToken.None).ConfigureAwait(false);
            await Switcher.RecoverLostModeAsync(active).ConfigureAwait(false);
        }
        catch (LeverException ex)
        {
            Log.Error("state", "Cannot read the active power scheme to check for a lost state", ex);
        }
    }

    /// <summary>The flyout/status state for the current mode (or the one being applied), results and live values.</summary>
    public FlyoutState BuildState(LiveValues live, bool startWithWindows, SwitchInProgress? progress = null, PowerCard? power = null)
    {
        var state = Switcher.State;
        var mode = progress?.Mode ?? state.Mode;
        var target = TargetPlanner.Plan(mode, Config.Current, state.Snapshot, state.OptionsFor(mode));
        var results = progress is null
            ? state.LastResults
            : state.LastResults.Where(r => !progress.Started.Contains(r.Key)).Concat(progress.Finished).ToDictionary(r => r.Key, r => r.Value);
        return FlyoutStateBuilder.Build(new FlyoutInput(
            target,
            results,
            progress?.Running ?? new HashSet<LeverKind>(),
            live,
            state.Since?.ToLocalTime(),
            DateTimeOffset.Now,
            Elevation.IsElevated,
            state.OptionsFor(PowerModeKind.Station).KeepDocker,
            startWithWindows,
            Config.Current.Hotkeys,
            power,
            Machine));
    }

    /// <summary>The power card from one live reading (<c>--status</c>: no history, no "vs Grid").</summary>
    public PowerCard PowerCardOf(LiveValues live)
    {
        var calibration = Switcher.State.PowerCalibration;
        var estimate = PowerEstimator.Estimate(Config.Current.Power, live.ToPowerInputs(), calibration);
        return PowerCards.Build(estimate, calibration, null, [], null, DateTimeOffset.Now);
    }

    public void Dispose()
    {
        Live.Dispose();
        Switcher.Dispose();
        LeverSet.Dispose();
        Config.Dispose();
    }
}
