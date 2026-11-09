using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerMode.App.Infrastructure;
using PowerMode.App.Ui;
using PowerMode.Core;
using PowerMode.Core.Cli;
using PowerMode.Core.Levers;
using PowerMode.Core.Planning;
using PowerMode.Core.Presentation;
using PowerMode.Core.Switching;

namespace PowerMode.App;

/// <summary>The tray app: icon, flyout, hotkeys, forwarded commands, system events and the switch queue.</summary>
internal sealed class TrayController : IDisposable
{
    private const string Category = "tray";
    private static readonly TimeSpan FlyoutRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TooltipRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SpinnerStep = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan DisplayDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);
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
    private SwitchInProgress? _progress;
    private PowerModeKind? _pending;
    private bool _switching;
    private bool _readingLive;
    private bool _disposed;
    private bool _quitAfterSwitch;
    private bool _autostart;
    private int _spinnerFrame;
    private LiveValues _live = LiveValues.Empty;

    public TrayController(App app, AppServices services, SingleInstance instance, IReadOnlyList<string> arguments)
    {
        _app = app;
        _services = services;
        _instance = instance;
        _arguments = arguments;
        _hotkeys = new HotkeyManager(services.Log);
        var view = new FlyoutView { DataContext = _model };
        _flyout = new FlyoutWindow(view, () => _services.Config.Current.Flyout);
        _liveTimer = new DispatcherTimer(TooltipRefresh, DispatcherPriority.Background, async (_, _) => await RefreshLiveAsync(), _app.Dispatcher);
        _spinnerTimer = new DispatcherTimer(SpinnerStep, DispatcherPriority.Normal, (_, _) => AdvanceSpinner(), _app.Dispatcher) { IsEnabled = false };
        _displayDebounce = new DispatcherTimer(DisplayDebounce, DispatcherPriority.Normal, async (_, _) => await OnDisplaySettledAsync(), _app.Dispatcher) { IsEnabled = false };
    }

    public void Start(bool openFlyout)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        ApplyTheme();
        Wire();
        _hotkeys.Register(_services.Config.Current.Hotkeys);
        _services.Config.StartWatching();
        _instance.StartServer(_services.Log, OnForwarded);
        _tray.Show();
        _ = RefreshAutostartAsync();
        Refresh();
        _liveTimer.Start();
        _services.Log.Write(Category, $"Started ({(Elevation.IsElevated ? "elevated" : "not elevated")}), mode {ModeNames.Display(_services.Switcher.State.Mode)}");

        _ = ReapplyOnStartAsync();

        if (openFlyout)
        {
            OpenFlyout();
        }
    }

    /// <summary>The power scheme survives a reboot, the GPU limit and closed apps do not: re-apply (after recovering a lost state).</summary>
    private async Task ReapplyOnStartAsync()
    {
        await _services.RecoverLostModeAsync();
        Refresh();
        if (_services.Switcher.NeedsReapplyOnStart)
        {
            await SwitchAsync(_services.Switcher.State.Mode);
        }
    }

    private void Wire()
    {
        var view = _flyout.View;
        view.ModeRequested += (_, mode) => _ = SwitchAsync(mode);
        view.CloseRequested += (_, _) => _flyout.HideFlyout();
        view.ConfigRequested += (_, _) => OpenConfig();
        view.RestartElevatedRequested += (_, _) => RestartElevated();
        view.KeepDockerChanged += async (_, keep) => await SetKeepDockerAsync(keep);
        view.StartWithWindowsChanged += async (_, on) => await SetAutostartAsync(on);
        _flyout.ModeKey += (_, mode) => _ = SwitchAsync(mode);
        _flyout.IsVisibleChanged += (_, _) => OnFlyoutVisibilityChanged();

        _tray.ModeRequested += (_, mode) => _ = SwitchAsync(mode);
        _tray.OpenRequested += (_, _) => OpenFlyout();
        _tray.AutostartToggled += async (_, on) => await SetAutostartAsync(on);
        _tray.ConfigRequested += (_, _) => OpenConfig();
        _tray.QuitRequested += async (_, toGrid) => await QuitAsync(toGrid);
        _tray.LeftClicked += (_, _) => ToggleFlyout();

        _hotkeys.Pressed += (_, mode) => _ = SwitchAsync(mode);
        _services.Switcher.Progress += (_, progress) => _app.Dispatcher.BeginInvoke(() => OnProgress(progress));
        _services.Config.Changed += (_, _) => _app.Dispatcher.BeginInvoke(OnConfigChanged);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Apply a mode. While a switch runs, the latest request waits and runs next.</summary>
    public async Task SwitchAsync(PowerModeKind mode)
    {
        if (_switching)
        {
            _pending = mode;
            return;
        }

        _switching = true;
        try
        {
            PowerModeKind? next = mode;
            while (next is { } current)
            {
                _pending = null;
                var outcome = await Task.Run(() => _services.Switcher.SwitchAsync(current));
                var target = TargetPlanner.Plan(current, _services.Config.Current, _services.Switcher.State.Snapshot, _services.Switcher.State.OptionsFor(current));
                var (title, text) = FlyoutStateBuilder.Toast(target, outcome.Results);
                _tray.Toast(title, text);
                next = _pending;
            }
        }
        finally
        {
            _switching = false;
            _services.Live.Invalidate();
            Refresh();
        }

        if (_quitAfterSwitch)
        {
            Shutdown();
            return;
        }

        await RefreshLiveAsync();
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
        var state = _services.BuildState(_live, _autostart, _progress);
        _model.Update(state);
        var mode = _progress?.Mode ?? _services.Switcher.State.Mode;
        var tooltip = _progress is not null
            ? $"PowerMode{FlyoutStateBuilder.Dot}Applying {ModeNames.Display(mode)}…"
            : $"PowerMode{FlyoutStateBuilder.Dot}{ModeNames.Display(mode)}"
              + (_live.GpuWatts is { } watts ? string.Create(CultureInfo.InvariantCulture, $"{FlyoutStateBuilder.Dot}GPU {watts:0} W") : string.Empty);
        _tray.Update(mode, _switching || _progress is not null, _progress is null ? null : _spinnerFrame, _autostart, tooltip);
    }

    private void AdvanceSpinner()
    {
        _spinnerFrame = (_spinnerFrame + 1) % IconRenderer.SpinnerFrames;
        Refresh();
    }

    private async Task RefreshLiveAsync()
    {
        if (_readingLive)
        {
            return;
        }

        _readingLive = true;
        try
        {
            _live = _flyout.IsVisible
                ? await Task.Run(() => _services.Live.ReadAsync(includeSlow: !_switching, CancellationToken.None))
                : _live with { GpuWatts = await Task.Run(_services.Live.GpuWatts) };
            Refresh();
        }
        finally
        {
            _readingLive = false;
        }
    }

    private void OnFlyoutVisibilityChanged()
    {
        _liveTimer.Interval = _flyout.IsVisible ? FlyoutRefresh : TooltipRefresh;
        if (_flyout.IsVisible)
        {
            _ = RefreshLiveAsync();
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
            if (options.Command == CliCommand.Mode && options.Mode is { } mode)
            {
                _ = SwitchAsync(mode);
            }
            else
            {
                OpenFlyout();
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
        _autostart = await _services.Autostart.IsInstalledAsync(CancellationToken.None);
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

    /// <summary>Quit, optionally switching to Grid first. A running switch is finished first so state.json stays consistent.</summary>
    private async Task QuitAsync(bool toGrid)
    {
        if (_switching)
        {
            _pending = toGrid ? PowerModeKind.Grid : null;
            _quitAfterSwitch = true;
            return;
        }

        if (toGrid)
        {
            await SwitchAsync(PowerModeKind.Grid);
        }

        Shutdown();
    }

    private void Shutdown()
    {
        Dispose();
        _app.Shutdown();
    }

    private void OnConfigChanged()
    {
        _hotkeys.Register(_services.Config.Current.Hotkeys);
        Refresh();
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

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _app.Dispatcher.BeginInvoke(() =>
        {
            _displayDebounce.Stop();
            _displayDebounce.Start();
        });

    /// <summary>Brightness/refresh drifted (another app, a reconnect): correct it, but never more than once a minute.</summary>
    private async Task OnDisplaySettledAsync()
    {
        _displayDebounce.Stop();
        if (_switching || _services.Switcher.State.Mode == PowerModeKind.Grid || !_services.Config.Current.ReapplyOnDisplayChange)
        {
            return;
        }

        if (await Task.Run(() => _services.Switcher.HasDriftedAsync(DisplayLevers)) && _driftGuard.TryEnter())
        {
            _services.Log.Write(Category, "Display settings drifted; re-applying refresh rate and brightness");
            await Task.Run(() => _services.Switcher.ReapplyAsync(DisplayLevers));
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
