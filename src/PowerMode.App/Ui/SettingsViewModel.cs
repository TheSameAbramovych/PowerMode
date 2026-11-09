using System.Collections.ObjectModel;
using PowerMode.Core;
using PowerMode.Core.Settings;

namespace PowerMode.App.Ui;

/// <summary>One entry of a mode cell's choice list.</summary>
internal sealed record ChoiceOption(object Value, string Label);

/// <summary>A user's change of one mode cell on the Apps settings page.</summary>
internal sealed record AppChoiceChange(string Name, PowerModeKind Mode, AppChoice From, AppChoice To);

/// <summary>A user's change of Wallpaper Engine Keep/Pause for one mode.</summary>
internal sealed record WallpaperChange(PowerModeKind Mode, bool Pause);

internal sealed class AppRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private AppChoice _station;
    private AppChoice _reserve;

    public AppRowViewModel(SettingsViewModel owner, AppRow row)
    {
        _owner = owner;
        Name = row.Name;
        DisplayName = row.DisplayName;
        FileName = row.FileName;
        _station = row.Station;
        _reserve = row.Reserve;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string FileName { get; }

    public AppChoice Station { get => _station; set => Choose(PowerModeKind.Station, ref _station, value); }

    public AppChoice Reserve { get => _reserve; set => Choose(PowerModeKind.Reserve, ref _reserve, value); }

    /// <summary>Show a value without reporting it as the user's change (a failed save is undone this way).</summary>
    public void Show(PowerModeKind mode, AppChoice choice)
    {
        if (mode == PowerModeKind.Station)
        {
            Set(ref _station, choice, nameof(Station));
        }
        else
        {
            Set(ref _reserve, choice, nameof(Reserve));
        }
    }

    private void Choose(PowerModeKind mode, ref AppChoice field, AppChoice value)
    {
        var from = field;
        if (Set(ref field, value, mode == PowerModeKind.Station ? nameof(Station) : nameof(Reserve)))
        {
            _owner.Report(new AppChoiceChange(Name, mode, from, value));
        }
    }
}

/// <summary>
/// The Apps settings page. Rows are built by <see cref="AppSettings.Build"/> when the page opens and keep their order
/// while it is open (a changed row does not jump); changes are reported, the controller saves and applies them.
/// </summary>
internal sealed class SettingsViewModel : ObservableObject
{
    private bool _pauseStation;
    private bool _pauseReserve;
    private string _addText = string.Empty;
    private string? _message;

    public static IReadOnlyList<ChoiceOption> AppChoices { get; } =
    [
        new(AppChoice.Keep, "Keep"),
        new(AppChoice.Efficiency, "Efficiency"),
        new(AppChoice.Close, "Close"),
    ];

    public static IReadOnlyList<ChoiceOption> WallpaperChoices { get; } = [new(false, "Keep"), new(true, "Pause")];

    public ObservableCollection<AppRowViewModel> Rows { get; } = [];

    public event EventHandler<AppChoiceChange>? AppChanged;

    public event EventHandler<WallpaperChange>? WallpaperChanged;

    public bool PauseWallpaperStation
    {
        get => _pauseStation;
        set
        {
            if (Set(ref _pauseStation, value))
            {
                WallpaperChanged?.Invoke(this, new WallpaperChange(PowerModeKind.Station, value));
            }
        }
    }

    public bool PauseWallpaperReserve
    {
        get => _pauseReserve;
        set
        {
            if (Set(ref _pauseReserve, value))
            {
                WallpaperChanged?.Invoke(this, new WallpaperChange(PowerModeKind.Reserve, value));
            }
        }
    }

    public string AddText { get => _addText; set => Set(ref _addText, value); }

    /// <summary>A validation or save problem shown under the add field; null when there is none.</summary>
    public string? Message { get => _message; set => Set(ref _message, value); }

    /// <summary>Replace the page content (page opened, process added, config.json changed on disk). Reports nothing.</summary>
    public void Load(AppSettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Rows.Clear();
        foreach (var row in page.Rows)
        {
            Rows.Add(new AppRowViewModel(this, row));
        }

        Set(ref _pauseStation, page.PauseWallpaperStation, nameof(PauseWallpaperStation));
        Set(ref _pauseReserve, page.PauseWallpaperReserve, nameof(PauseWallpaperReserve));
    }

    /// <summary>Show a Wallpaper Engine value without reporting it (a failed save is undone this way).</summary>
    public void ShowWallpaper(PowerModeKind mode, bool pause)
    {
        if (mode == PowerModeKind.Station)
        {
            Set(ref _pauseStation, pause, nameof(PauseWallpaperStation));
        }
        else
        {
            Set(ref _pauseReserve, pause, nameof(PauseWallpaperReserve));
        }
    }

    internal void Report(AppChoiceChange change) => AppChanged?.Invoke(this, change);
}
