using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.App.Ui;
using PowerMode.Core;
using PowerMode.Core.Cli;
using PowerMode.Core.Levers;
using PowerMode.Core.Logging;
using PowerMode.Core.Planning;
using PowerMode.Core.Power;
using PowerMode.Core.Presentation;
using PowerMode.Core.Switching;

namespace PowerMode.App;

/// <summary>The tray app: icon, flyout, hotkeys, forwarded commands, system events and the switch queue.</summary>
internal sealed class TrayController : IDisposable
{
    internal const string Category = "tray";
    /// <summary>The one live sampler: CPU, GPU and the power estimate every 2 s (DDC/CI and Docker only while the flyout is open).</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpinnerStep = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan DisplayDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StartWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PromotionRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(1);
    private static readonly LeverKind[] DisplayLevers = [LeverKind.DisplayMode, LeverKind.Brightness];

    private readonly App _app;
    private readonly AppServices _services;
    private readonly IReadOnlyList<string> _arguments;
    private readonly FlyoutViewModel _model = new();
    private readonly FlyoutWindow _flyout;
    private readonly TrayIcon _tray = new();
    private readonly HotkeyManager _hotkeys;
    private readonly DriftGuard _driftGuard = new(TimeProvider.System);
    private readonly DispatcherTimer _liveTimer;
    private readonly DispatcherTimer _spinnerTimer;
    private readonly DispatcherTimer _displayDebounce;
    private SingleInstance _instance;
    private readonly SwitchScheduler _queue;
    private readonly SettingsController _settings;
    private readonly LeversController _levers;
    private SwitchInProgress? _progress;
    private bool _readingLive;
    private bool _disposed;
    private bool _autostart;
    private int _spinnerFrame;
    private LiveValues _live = LiveValues.Empty;
    private readonly PowerTracker _power = new();

    public TrayController(App app, AppServices services, SingleInstance instance, IReadOnlyList<string> arguments)
    {
        _app = app;
        _services = services;
        _instance = instance;
        _arguments = arguments;
        _hotkeys = new HotkeyManager(services.Log);
        _queue = new SwitchScheduler(RunSwitchAsync, Shutdown, RestartElevated, services.Log);
        _settings = new SettingsController(services, _model, Refresh);
        _levers = new LeversController(services, _model, Refresh);
        var view = new FlyoutView { DataContext = _model };
        _flyout = new FlyoutWindow(view, () => _services.Config.Current.Flyout);
        _model.IsPinned = _flyout.IsPinned = services.Switcher.State.FlyoutPinned;
        _liveTimer = new DispatcherTimer(SampleInterval, DispatcherPriority.Background, async (_, _) => await RefreshLiveAsync(sample: true), _app.Dispatcher);
        _spinnerTimer = new DispatcherTimer(SpinnerStep, DispatcherPriority.Normal, (_, _) => AdvanceSpinner(), _app.Dispatcher) { IsEnabled = false };
        _displayDebounce = new DispatcherTimer(DisplayDebounce, DispatcherPriority.Normal, async (_, _) => await OnDisplaySettledAsync(), _app.Dispatcher) { IsEnabled = false };
    }

    /// <summary>
    /// Runs on the dispatcher once the message loop is up. The tray icon is shown first; nothing here waits for a
    /// tool, the pipe or a lever: autostart state, lost-state recovery and the re-apply run afterwards, off this thread.
    /// </summary>
    public void Start(bool openFlyout)
    {
        // If a step blocks this thread (the shell not answering the tray-icon call, say), the log names the step.
        var step = "theme";
        using (new Timer(_ => _services.Log.Write(Category, $"Start-up is still at step \"{step}\" after {StartWatchdog.TotalSeconds:0} s"), null, StartWatchdog, Timeout.InfiniteTimeSpan))
        {
            _app.DispatcherUnhandledException += OnUnhandledException;
            _app.SessionEnding += OnSessionEnding;
            System.Windows.Forms.Application.EnableVisualStyles();
            ApplyTheme();
            Wire();
            step = "tray icon";
            Refresh();
            _tray.Show();
            step = "pipe, hotkeys, config watcher";
            _instance.StartServer(_services.Log, OnForwarded);
            _hotkeys.Register(_services.Config.Current.Hotkeys);
            _services.Config.StartWatching();
            _liveTimer.Start();
        }

        _services.Log.Write(Category, $"Started ({(Elevation.IsElevated ? "elevated" : "not elevated")}), mode {ModeNames.Display(_services.Switcher.State.Mode)}");

        _ = RefreshAutostartAsync();
        _ = ReapplyOnStartAsync();
        _ = PromoteTrayIconAsync();

        if (openFlyout)
        {
            OpenFlyout();
        }
    }

    private void Wire()
    {
        var view = _flyout.View;
        view.ModeRequested += (_, mode) => _ = SwitchAsync(mode);
        view.CloseRequested += (_, _) => _flyout.HideFlyout();
        view.PinRequested += async (_, _) => await TogglePinAsync();
        view.CalibrateRequested += (_, _) => StartCalibration();
        view.CalibrationSaveRequested += async (_, _) => await SaveCalibrationAsync();
        view.CalibrationCancelRequested += (_, _) => _model.Power.IsCalibrating = false;
        view.CalibrationResetRequested += async (_, _) => await SetCalibrationAsync(null);
        view.ConfigRequested += (_, _) => OpenConfig();
        view.SettingsRequested += async (_, _) => await _settings.OpenAsync();
        view.BackRequested += (_, _) => _settings.Close();
        view.AppsTabRequested += async (_, _) => await _settings.OpenAsync();
        view.LeversTabRequested += (_, _) => _levers.Open();
        view.AddRequested += async (_, _) => await _settings.AddAsync();
        view.RestartElevatedRequested += (_, _) => _queue.RestartElevated();
        view.KeepDockerChanged += async (_, keep) => await SetKeepDockerAsync(keep);
        view.StartWithWindowsChanged += async (_, on) => await SetAutostartAsync(on);

        // Keys 1/2/3 act like the segments, which are disabled while a switch runs.
        _flyout.ModeKey += (_, mode) =>
        {
            if (!_queue.IsSwitching)
            {
                _ = SwitchAsync(mode);
            }
        };
        _flyout.IsVisibleChanged += (_, _) => OnFlyoutVisibilityChanged();

        _tray.ModeRequested += (_, mode) => _ = SwitchAsync(mode);
        _tray.OpenRequested += (_, _) => OpenFlyout();
        _tray.AutostartToggled += async (_, on) => await SetAutostartAsync(on);
        _tray.ConfigRequested += (_, _) => OpenConfig();
        _tray.QuitRequested += async (_, toGrid) => await _queue.QuitAsync(toGrid);
        _tray.LeftClicked += (_, _) => ToggleFlyout();

        _hotkeys.Pressed += (_, mode) => _ = SwitchAsync(mode);
        _services.Switcher.Progress += (_, progress) => _app.Dispatcher.BeginInvoke(() => OnProgress(progress));
        _services.Config.Changed += (_, _) => _app.Dispatcher.BeginInvoke(OnConfigChanged);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Apply a mode. While a switch runs, the latest request waits and runs next (see <see cref="SwitchScheduler"/>).</summary>
    public async Task SwitchAsync(PowerModeKind mode)
    {
        await _queue.RequestAsync(mode);
        if (!_queue.IsSwitching && !_disposed)
        {
            await RefreshLiveAsync(sample: false);
        }
    }

    /// <summary>The card turns into "Station shows [ ] W": the user types what the power station's display shows.</summary>
    private void StartCalibration()
    {
        var power = _model.Power;
        power.Reading = string.Empty;
        power.ReadingError = null;
        power.IsCalibrating = true;
        _app.Dispatcher.BeginInvoke(_flyout.View.FocusReading, DispatcherPriority.Input);
    }

    /// <summary>Calibrate: the offset that makes the current estimate equal the reading.</summary>
    private async Task SaveCalibrationAsync()
    {
        var power = _model.Power;
        if (!PowerEstimator.TryParseReading(power.Reading, out var watts, out var error))
        {
            power.ReadingError = error;
            return;
        }

        if (_power.Estimate(_services.Config.Current.Power, calibration: null) is not { } estimate)
        {
            power.ReadingError = "No sample yet; try again in a moment";
            return;
        }

        var calibration = PowerEstimator.Calibrate(watts, estimate, DateTimeOffset.Now);
        _services.Log.Write(Category, string.Create(CultureInfo.InvariantCulture, $"Power calibrated: station {watts:0} W, estimate {estimate.TotalW:0} W, offset {calibration.OffsetW:0} W"));
        await SetCalibrationAsync(calibration);
    }

    private async Task SetCalibrationAsync(PowerCalibration? calibration)
    {
        _model.Power.IsCalibrating = false;
        _model.Power.ReadingError = null;
        await Task.Run(() => _services.Switcher.SetPowerCalibrationAsync(calibration));
        if (calibration is null)
        {
            _services.Log.Write(Category, "Power calibration reset");
        }

        Refresh();
    }

    /// <summary>Pinned: the flyout stays open when it loses focus. Shown at once, saved in state.json.</summary>
    private async Task TogglePinAsync()
    {
        var pinned = !_flyout.IsPinned;
        _flyout.IsPinned = pinned;
        _model.IsPinned = pinned;
        await Task.Run(() => _services.Switcher.SetFlyoutPinnedAsync(pinned));
    }

    /// <summary>
    /// Windows 11 puts a new tray icon into the overflow, where a running PowerMode cannot be seen. Promote our own
    /// icon onto the taskbar once per exe path (see <see cref="TrayPromotion"/>); Windows writes the entry shortly
    /// after the icon appears, so look again once after <see cref="PromotionRetry"/>.
    /// </summary>
    private async Task PromoteTrayIconAsync()
    {
        var exe = AppPaths.ExecutablePath;
        for (var attempt = 1; ; attempt++)
        {
            var entries = await Task.Run(NotifyIconSettings.Read);
            var decision = TrayPromotion.Decide(exe, _services.Switcher.State.TrayPromotedFor, entries, NotifyIconSettings.KnownFolder);
            switch (decision.Action)
            {
                case TrayPromotionAction.Skip:
                    return;
                case TrayPromotionAction.NotFound when attempt == 1:
                    await Task.Delay(PromotionRetry);
                    continue;
                case TrayPromotionAction.NotFound:
                    _services.Log.Write(Category, "Tray icon: no taskbar entry for this exe yet; trying again on the next start");
                    return;
                case TrayPromotionAction.Unsupported:
                    _services.Log.Write(Category, "Tray icon: this Windows has no per-icon settings; showed how to keep it visible");
                    _tray.Toast("PowerMode", TrayPromotion.Hint);
                    break;
                case TrayPromotionAction.AlreadyPromoted:
                    _services.Log.Write(Category, "Tray icon: already on the taskbar");
                    break;
                case TrayPromotionAction.Promote:
                    await Task.Run(() => NotifyIconSettings.Promote(decision.Keys));
                    _services.Log.Write(Category, $"Tray icon: promoted onto the taskbar (NotifyIconSettings {string.Join(", ", decision.Keys)})");

                    // The taskbar reads the setting when an icon is added: add ours again, once.
                    _tray.Readd();
                    break;
            }

            await Task.Run(() => _services.Switcher.SetTrayPromotedForAsync(exe));
            return;
        }
    }

    /// <summary>The power scheme survives a reboot, the GPU limit and closed apps do not: re-apply (after recovering a lost state).</summary>
    private async Task ReapplyOnStartAsync()
    {
        var machine = await Task.Run(_services.RefreshMachine);
        _services.Log.Write(Category, $"This PC: {string.Join("; ", machine.Facts().Select(f => $"{f.Label} {f.Value}"))}");
        await Task.Run(_services.TuneFirstRunAsync);
        await Task.Run(_services.RecoverLostModeAsync);
        Refresh();

        // startInGrid: every new start begins without limits; otherwise the saved mode is applied again.
        var state = _services.Switcher.State;
        switch (StartAndShutdown.Decide(state, _services.Config.Current.StartInGrid))
        {
            case StartAction.RestoreGrid:
                _services.Log.Write(Category, $"Start in Grid: {ModeNames.Display(state.Mode)} was saved; restoring Grid (startInGrid)");
                await SwitchAsync(PowerModeKind.Grid);
                break;
            case StartAction.Reapply:
                _services.Log.Write(Category, $"Re-applying {ModeNames.Display(state.Mode)} on start");
                await SwitchAsync(state.Mode);
                break;
            case StartAction.Nothing:
                break;
        }
    }

    /// <summary>
    /// Windows is shutting down or signing out: in a saving mode (and with <c>gridOnShutdown</c>) restore Grid's power
    /// scheme, displays, brightness and GPU limit first, within 5 s, so the next start has no limits. Windows shows
    /// the reason while it waits. The rest (Docker, apps, services) stays owed for the next start.
    /// </summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        var mode = _services.Switcher.State.Mode;
        if (!StartAndShutdown.RestoresAtShutdown(mode, _services.Config.Current.GridOnShutdown))
        {
            _services.Log.Write(Category, $"Session ending ({e.ReasonSessionEnding}) in {ModeNames.Display(mode)}; nothing to restore");
            return;
        }

        var window = new WindowInteropHelper(_flyout).EnsureHandle();
        var blocked = User32.ShutdownBlockReasonCreate(window, "PowerMode is restoring your power settings");
        try
        {
            _services.Log.Write(Category, $"Session ending ({e.ReasonSessionEnding}) in {ModeNames.Display(mode)}: restoring Grid");
            var restore = Task.Run(() => _services.Switcher.ShutdownToGridAsync(StartAndShutdown.ShutdownBudget));
            var done = restore.Wait(StartAndShutdown.ShutdownBudget + ShutdownGrace) && restore.Result;
            _services.Log.Write(Category, done ? "Shutdown: Grid restored" : "Shutdown: Grid not restored in time; the next start restores it");
        }
        finally
        {
            if (blocked)
            {
                User32.ShutdownBlockReasonDestroy(window);
            }
        }
    }

    /// <summary>One switch of the queue. Never throws: a failure is logged and the queue goes on.</summary>
    private async Task RunSwitchAsync(PowerModeKind mode)
    {
        // Leaving Grid: the last minute in Grid is the reference for "vs Grid" (saved under the switcher's gate).
        var (leavesGrid, reference) = _power.StartSwitch(_services.Switcher.State.Mode, mode, DateTimeOffset.Now);
        if (leavesGrid)
        {
            _ = Task.Run(() => _services.Switcher.SetGridReferenceAsync(reference));
        }

        // Show the switch at once: capturing the baseline (Docker, DDC/CI, powercfg) takes seconds before the switcher reports Started.
        _progress = new SwitchInProgress(mode, _services.Switcher.Levers.Select(l => l.Kind).ToHashSet());
        _spinnerTimer.Start();
        Refresh();
        try
        {
            var outcome = await Task.Run(() => _services.Switcher.SwitchAsync(mode));
            var target = TargetPlanner.Plan(mode, _services.Config.Current, _services.Switcher.State.Snapshot, _services.Switcher.State.OptionsFor(mode));
            var (title, text) = FlyoutStateBuilder.Toast(target, outcome.Results);
            _tray.Toast(title, text);
            if (_services.Switcher.OwesRetry(DisplayLevers))
            {
                // The monitor still did not answer: try once more when the display has settled.
                RestartDisplayCheck();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Log.Error(Category, $"Switch to {ModeNames.Display(mode)} failed", ex);
        }
        finally
        {
            _progress = null;
            _spinnerTimer.Stop();
            _services.Live.Invalidate();
            Refresh();
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // An async UI handler must not take the tray down and leave the PC reduced without it.
        _services.Log.Error(Category, "Unhandled exception on the UI thread", e.Exception);
        e.Handled = true;
    }

    private void OnProgress(SwitchProgress progress)
    {
        switch (progress.Phase)
        {
            case SwitchPhase.Started:
                _progress = new SwitchInProgress(progress.Mode, progress.Levers);
                _spinnerTimer.Start();
                break;
            case SwitchPhase.LeverFinished when _progress is not null && progress.Lever is { } lever && progress.Result is { } result:
                _progress.Running.Remove(lever);
                _progress.Finished[lever] = result;
                break;
            case SwitchPhase.Completed:
                _progress = null;
                _spinnerTimer.Stop();
                break;
        }

        Refresh();
    }

    private void Refresh()
    {
        var saved = _services.Switcher.State;
        var mode = _progress?.Mode ?? saved.Mode;
        var card = _power.Card(_services.Config.Current.Power, saved.PowerCalibration, mode, saved.GridReferenceW, saved.Since, DateTimeOffset.Now);
        var state = _services.BuildState(_live, _autostart, _progress, card);
        _model.Update(state);
        var tooltip = FlyoutStateBuilder.Tooltip(mode, _progress is not null, card.TotalW);
        if (_model.IsLeversOpen)
        {
            _levers.ShowCpuWatts(_power.Estimate(_services.Config.Current.Power, saved.PowerCalibration)?.CpuW);
        }

        _tray.Update(mode, _queue.IsSwitching || _progress is not null, _progress is null ? null : _spinnerFrame, _autostart, tooltip);
    }

    private void AdvanceSpinner()
    {
        _spinnerFrame = (_spinnerFrame + 1) % IconRenderer.SpinnerFrames;
        Refresh();
    }

    /// <summary>
    /// Read the live values (CPU and GPU always; monitor and Docker, at most every 10 s, only while the flyout is open
    /// and no switch runs) and, from the timer, add a power sample to the history.
    /// </summary>
    private async Task RefreshLiveAsync(bool sample)
    {
        if (_readingLive)
        {
            return;
        }

        _readingLive = true;
        try
        {
            var includeSlow = _flyout.IsVisible && !_queue.IsSwitching;
            _live = await Task.Run(() => _services.Live.ReadAsync(includeSlow, CancellationToken.None));
            if (sample)
            {
                var state = _services.Switcher.State;
                _power.Add(_live.ToPowerInputs(), DateTimeOffset.Now, _services.Config.Current.Power, state.PowerCalibration);
            }

            Refresh();
        }
        finally
        {
            _readingLive = false;
        }
    }

    private void OnFlyoutVisibilityChanged()
    {
        if (_flyout.IsVisible)
        {
            _ = RefreshLiveAsync(sample: false);
        }
        else
        {
            // The next open shows the lever table again, without a half-typed calibration.
            _model.Power.IsCalibrating = false;
            _settings.Close();
        }
    }

    private void ToggleFlyout()
    {
        if (_flyout.IsVisible)
        {
            _flyout.HideFlyout();
        }
        else if (DateTime.UtcNow - _flyout.HiddenAt > ReopenGuard)
        {
            OpenFlyout();
        }
    }

    private void OpenFlyout()
    {
        Refresh();
        _flyout.ShowFlyout();
    }

    private void OnForwarded(string message)
    {
        var options = CliOptions.FromPipeMessage(message);
        _app.Dispatcher.BeginInvoke(() =>
        {
            switch (options.Command)
            {
                case CliCommand.Mode when options.Mode is { } mode:
                    _ = SwitchAsync(mode);
                    break;
                case CliCommand.Quit:
                    // Like the tray menu's Quit (keep mode): a running switch finishes first.
                    _ = _queue.QuitAsync(toGrid: false);
                    break;
                default:
                    OpenFlyout();
                    break;
            }
        });
    }

    /// <summary>Saved at once; Docker is re-applied in Station, after a running switch if there is one (it planned with the old option).</summary>
    private async Task SetKeepDockerAsync(bool keep)
    {
        await Task.Run(() => _services.Switcher.SetKeepDockerAsync(PowerModeKind.Station, keep));
        Refresh();
    }

    private async Task SetAutostartAsync(bool on)
    {
        var flag = on ? CliOptions.InstallAutostartFlag : CliOptions.UninstallAutostartFlag;
        if (Elevation.IsElevated)
        {
            var error = on
                ? await _services.Autostart.InstallAsync(CancellationToken.None)
                : await _services.Autostart.UninstallAsync(CancellationToken.None);
            _services.Log.Write(Category, $"Autostart {flag}: {error ?? "ok"}");
        }
        else
        {
            await Task.Run(() => Elevation.StartElevated([flag], waitForExit: true));
        }

        await RefreshAutostartAsync();
    }

    private async Task RefreshAutostartAsync()
    {
        _autostart = await Task.Run(() => _services.Autostart.IsInstalledAsync(CancellationToken.None));
        Refresh();
    }

    private void OpenConfig()
    {
        _flyout.HideFlyout();
        try
        {
            using var editor = Process.Start(new ProcessStartInfo(_services.Config.Path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            using var notepad = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_services.Config.Path}\"") { UseShellExecute = true });
        }
    }

    private void RestartElevated()
    {
        _services.Log.Write(Category, "Restarting as administrator");
        _instance.Release();
        if (Elevation.StartElevated(_arguments, waitForExit: false) is not null)
        {
            Shutdown();
            return;
        }

        // UAC prompt cancelled: stay the single instance.
        _instance.Dispose();
        _instance = new SingleInstance();
        if (_instance.TryAcquire())
        {
            _instance.StartServer(_services.Log, OnForwarded);
        }
    }

    /// <summary>Called once by the queue; guarded anyway so a second path can never shut down twice.</summary>
    private void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        Dispose();
        _app.Shutdown();
    }

    private void OnConfigChanged()
    {
        _hotkeys.Register(_services.Config.Current.Hotkeys);
        Refresh();
        if (_model.IsAppsOpen)
        {
            // Edited in the file while the page is open: show the file's lists.
            _ = _settings.ReloadAsync();
        }
        else if (_model.IsLeversOpen)
        {
            _levers.Reload();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _app.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(ResumeDelay);
            if (!_services.Switcher.NeedsReapplyOnStart)
            {
                // Grid with nothing left to restore: re-applying would only fight the owner's own changes.
                return;
            }

            _services.Log.Write(Category, "Resumed; re-applying");
            await SwitchAsync(_services.Switcher.State.Mode);
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _app.Dispatcher.BeginInvoke(RestartDisplayCheck);

    private void RestartDisplayCheck()
    {
        _displayDebounce.Stop();
        _displayDebounce.Start();
    }

    /// <summary>
    /// Brightness/refresh drifted (another app, a reconnect) or a switch left them failed (the monitor did not answer
    /// while it re-synced; also an unfinished Grid restore): correct it, but never more than once a minute.
    /// </summary>
    private async Task OnDisplaySettledAsync()
    {
        _displayDebounce.Stop();
        if (_queue.IsSwitching)
        {
            // The switch itself changed the display mode: check once it has finished.
            RestartDisplayCheck();
            return;
        }

        if (!_services.Switcher.WantsDisplayCheck(DisplayLevers, _services.Config.Current.ReapplyOnDisplayChange))
        {
            return;
        }

        if (await Task.Run(() => _services.Switcher.HasDriftedAsync(DisplayLevers)) && _driftGuard.TryEnter())
        {
            _services.Log.Write(Category, "Display levers drifted or failed; re-applying refresh rate and brightness");
            await Task.Run(() => _services.Switcher.ReapplyAsync(DisplayLevers));
            _services.Live.Invalidate();
            Refresh();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _app.Dispatcher.BeginInvoke(() =>
            {
                ApplyTheme();
                Refresh();
            });
        }
    }

    private void ApplyTheme()
    {
        var light = Palette.AppsUseLightTheme();
        Palette.Apply(_app.Resources, light);
        _model.ApplyTheme(light);
        _flyout.ApplyFrame();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _liveTimer.Stop();
        _spinnerTimer.Stop();
        _displayDebounce.Stop();
        _hotkeys.Dispose();
        _tray.Dispose();
        _instance.Dispose();
        _flyout.Close();
    }
}
