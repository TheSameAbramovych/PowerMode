using System.Globalization;
using PowerMode.Core;
using PowerMode.Core.Logging;
using PowerMode.Core.Settings;

namespace PowerMode.App.Ui;

/// <summary>
/// The Levers page of the live flyout: rows from the config and this PC, every change saved to config.json at once
/// (clamped, with "Saved 10:31") and applied at once when it is for the current mode (see
/// <see cref="LeverSettings.ImmediateLevers"/>). Used from the UI thread; the file and the levers run on the pool.
/// </summary>
internal sealed class LeversController
{
    private const string Category = "settings";
    private const string NotSaved = "Not saved: config.json is not valid right now. Fix it with Open config.json.";

    private readonly AppServices _services;
    private readonly FlyoutViewModel _model;
    private readonly Action _refresh;

    public LeversController(AppServices services, FlyoutViewModel model, Action refresh)
    {
        _services = services;
        _model = model;
        _refresh = refresh;
        model.Levers.Changed += async (_, change) => await OnChangedAsync(change);
        model.Levers.InputError += (_, error) => _model.Settings.Show(new SettingsFeedback(_model.Settings.Saved, error.Message));
    }

    public void Open()
    {
        Reload();
        _model.Page = FlyoutPage.Levers;
    }

    /// <summary>Rebuild the rows (page opened, config.json changed on disk).</summary>
    public void Reload() => _model.Levers.Load(LeverSettings.Build(_services.Config.Current, _services.Machine), _services.Machine);

    private async Task OnChangedAsync(LeverChange change)
    {
        var row = _model.Levers.Rows.First(r => r.Setting == change.Setting);
        int? stored = null;
        var what = string.Create(CultureInfo.InvariantCulture, $"{row.Title} {change.Value?.ToString(CultureInfo.InvariantCulture) ?? "keep"}");
        var gpu = _services.Machine.Gpu;
        var saved = await Task.Run(() => _services.Config.UpdateMode(change.Mode, m => stored = LeverSettings.Apply(m, change.Setting, change.Value, gpu), what));
        if (!saved)
        {
            row.Show(change.Mode, change.Previous);
            _model.Settings.Show(SettingsFeedback.NotSaved(NotSaved));
            return;
        }

        // The stored value may be clamped (a GPU limit outside the card's range, a CPU % between steps).
        row.Show(change.Mode, stored);
        _model.Settings.Show(SettingsFeedback.SavedAt(DateTimeOffset.Now));
        _refresh();
        try
        {
            await Task.Run(() => _services.Switcher.ApplySettingChangeAsync(current => LeverSettings.ImmediateLevers(current, change.Mode, change.Setting)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Log.Error(Category, $"Applying the changed {row.Title} failed", ex);
        }

        _services.Live.Invalidate();
        _refresh();
    }

    /// <summary>The estimated CPU watts beside the CPU row.</summary>
    public void ShowCpuWatts(double? cpuW) =>
        _model.Levers.ShowCpuWatts(cpuW is { } watts ? string.Create(CultureInfo.InvariantCulture, $"≈ {watts:0} W now") : null);
}
