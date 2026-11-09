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
        _model.Settings.Message = null;
        await ReloadAsync();
        _model.IsSettingsOpen = true;
    }

    public void Close() => _model.IsSettingsOpen = false;

    /// <summary>Rebuild the rows (page opened, a process added, config.json changed on disk).</summary>
    public async Task ReloadAsync()
    {
        var running = await Task.Run(ProcessInfo.RunningApps);
        _model.Settings.Load(AppSettings.Build(_services.Config.Current, running, _added));
    }

    public async Task AddAsync()
    {
        var settings = _model.Settings;
        if (!AppSettings.TryParseProcessName(settings.AddText, out var name, out var error))
        {
            settings.Message = error;
            return;
        }

        settings.Message = null;
        settings.AddText = string.Empty;
        if (!_added.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _added.Add(name);
        }

        await ReloadAsync();
    }

    private async Task OnAppChangedAsync(AppChoiceChange change)
    {
        var saved = await Task.Run(() => _services.Config.UpdateModeApps(change.Mode, m => AppSettings.SetChoice(m, change.Name, change.To)));
        if (!saved)
        {
            _model.Settings.Rows.FirstOrDefault(r => r.Name == change.Name)?.Show(change.Mode, change.From);
            _model.Settings.Message = NotSaved;
            return;
        }

        _model.Settings.Message = null;
        _services.Log.Write(Category, $"{change.Name} in {ModeNames.Display(change.Mode)}: {change.From} -> {change.To}");
        await ApplyAsync(current => AppSettings.ImmediateLevers(current, change.Mode, change.From, change.To));
    }

    private async Task OnWallpaperChangedAsync(WallpaperChange change)
    {
        var saved = await Task.Run(() => _services.Config.UpdateModeApps(change.Mode, m => m.PauseWallpaperEngine = change.Pause));
        if (!saved)
        {
            _model.Settings.ShowWallpaper(change.Mode, !change.Pause);
            _model.Settings.Message = NotSaved;
            return;
        }

        _model.Settings.Message = null;
        await ApplyAsync(current => AppSettings.WallpaperLevers(current, change.Mode));
    }

    /// <summary>The lever table shows the new lists at once; the re-apply (if any) follows.</summary>
    private async Task ApplyAsync(Func<PowerModeKind, IReadOnlyList<Core.Levers.LeverKind>> levers)
    {
        _refresh();
        try
        {
            await Task.Run(() => _services.Switcher.ApplySettingChangeAsync(levers));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Log.Error(Category, "Applying the changed app setting failed", ex);
        }

        _services.Live.Invalidate();
        _refresh();
    }
}
