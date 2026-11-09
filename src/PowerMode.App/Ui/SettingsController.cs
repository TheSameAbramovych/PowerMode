using PowerMode.App.Infrastructure;
using PowerMode.Core;
using PowerMode.Core.Logging;
using PowerMode.Core.Settings;

namespace PowerMode.App.Ui;

/// <summary>
/// The Apps settings page of the live flyout: opens it with the current rows, saves every change to config.json at
/// once and re-applies what the change affects in the current mode (see <see cref="AppSettings.ImmediateLevers"/>).
/// Used from the UI thread; the file and the levers run on the pool.
/// </summary>
internal sealed class SettingsController
{
    private const string Category = "settings";
    private const string NotSaved = "Not saved: config.json is not valid right now. Fix it with Open config.json.";

    private readonly AppServices _services;
    private readonly FlyoutViewModel _model;
    private readonly Action _refresh;

    /// <summary>Names typed into "Add process…" since the page opened; they stay listed while it is open.</summary>
    private readonly List<string> _added = [];

    private SettingsFeedback _feedback = SettingsFeedback.None;

    /// <summary>Counts applied changes, so the apply of an older one never overwrites a newer one's feedback.</summary>
    private int _changes;

    /// <summary>The config the rows were last built from; the Apps tab rebuilds them only when the file changed since.</summary>
    private Core.Config.AppConfig? _builtFrom;

    public SettingsController(AppServices services, FlyoutViewModel model, Action refresh)
    {
        _services = services;
        _model = model;
        _refresh = refresh;
        model.Settings.AppChanged += async (_, change) => await OnAppChangedAsync(change);
        model.Settings.WallpaperChanged += async (_, change) => await OnWallpaperChangedAsync(change);
    }

    public async Task OpenAsync()
    {
        _added.Clear();
        _model.Settings.AddText = string.Empty;
        Show(SettingsFeedback.None);
        await ReloadAsync();
        _model.Page = FlyoutPage.Apps;
    }

    /// <summary>
    /// The Apps tab: back to the page as it was (Saved, added names and the row order stay); the rows are rebuilt only
    /// when config.json changed meanwhile.
    /// </summary>
    public async Task ShowTabAsync()
    {
        if (_model.Page == FlyoutPage.Apps)
        {
            return;
        }

        if (!ReferenceEquals(_builtFrom, _services.Config.Current))
        {
            await ReloadAsync();
        }

        _model.Page = FlyoutPage.Apps;
    }

    /// <summary>Back to the lever table, which shows the lists as saved.</summary>
    public void Close()
    {
        _model.Page = FlyoutPage.Main;
        _refresh();
    }

    /// <summary>Rebuild the rows (page opened, a process added, config.json changed on disk).</summary>
    public async Task ReloadAsync()
    {
        var config = _services.Config.Current;
        var running = await Task.Run(ProcessInfo.RunningApps);
        _builtFrom = config;
        _model.Settings.Load(AppSettings.Build(config, running, _added) with { WallpaperEngineInstalled = _services.Machine.WallpaperEngineInstalled });
    }

    public async Task AddAsync()
    {
        var settings = _model.Settings;
        if (!AppSettings.TryParseProcessName(settings.AddText, out var name, out var error))
        {
            Show(_feedback.Invalid(error!));
            return;
        }

        Show(_feedback.Valid());
        settings.AddText = string.Empty;
        if (!_added.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _added.Add(name);
        }

        await ReloadAsync();
    }

    private async Task OnAppChangedAsync(AppChoiceChange change)
    {
        var saved = await Task.Run(() => _services.Config.UpdateMode(change.Mode, m => AppSettings.SetChoice(m, change.Name, change.To), $"{change.Name} -> {change.To}"));
        if (!saved)
        {
            _model.Settings.Rows.FirstOrDefault(r => r.Name == change.Name)?.Show(change.Mode, change.From);
            Show(SettingsFeedback.NotSaved(NotSaved));
            return;
        }

        Show(SettingsFeedback.SavedAt(DateTimeOffset.Now));
        _services.Log.Write(Category, $"{change.Name} in {ModeNames.Display(change.Mode)}: {change.From} -> {change.To}");
        await ApplyAsync(current => AppSettings.ImmediateLevers(current, change.Mode, change.From, change.To));
    }

    private async Task OnWallpaperChangedAsync(WallpaperChange change)
    {
        var saved = await Task.Run(() => _services.Config.UpdateMode(change.Mode, m => m.PauseWallpaperEngine = change.Pause, $"Wallpaper Engine {(change.Pause ? "pause" : "keep")}"));
        if (!saved)
        {
            _model.Settings.ShowWallpaper(change.Mode, !change.Pause);
            Show(SettingsFeedback.NotSaved(NotSaved));
            return;
        }

        Show(SettingsFeedback.SavedAt(DateTimeOffset.Now));
        await ApplyAsync(current => AppSettings.WallpaperLevers(current, change.Mode));
    }

    private void Show(SettingsFeedback feedback)
    {
        _feedback = feedback;
        _model.Settings.Show(feedback);
    }

    /// <summary>
    /// The lever table shows the new lists at once; the re-apply (if any) follows, and a lever that failed in it is
    /// named in the warning line beside "Saved".
    /// </summary>
    private async Task ApplyAsync(Func<PowerModeKind, IReadOnlyList<Core.Levers.LeverKind>> levers)
    {
        var number = ++_changes;
        _refresh();
        string? failure;
        try
        {
            var outcome = await Task.Run(() => _services.Switcher.ApplySettingChangeAsync(levers));
            failure = SettingsFeedback.NotAppliedMessage(outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Log.Error(Category, "Applying the changed app setting failed", ex);
            failure = SettingsFeedback.NotAppliedMessage(ex.Message);
        }

        if (failure is { } message && number == _changes)
        {
            Show(_feedback.Invalid(message));
        }

        _services.Live.Invalidate();
        _refresh();
    }
}
