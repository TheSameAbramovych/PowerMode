using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PowerMode.Core;
using PowerMode.Core.Levers;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Ui;

internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class ModeItem : ObservableObject
{
    /// <summary>Selected segment fill: the mode colour at 18 % opacity.</summary>
    private const byte FillAlpha = 0x2E;

    private bool _isSelected;
    private bool _isEnabled = true;
    private Brush _brush = Brushes.Transparent;
    private Brush _fill = Brushes.Transparent;

    public ModeItem(PowerModeKind mode) => Mode = mode;

    public PowerModeKind Mode { get; }

    public string Label => ModeNames.Display(Mode);

    public string Glyph => Glyphs.ForMode(Mode);

    public string Shortcut => $"Key {ModeNames.All.ToList().IndexOf(Mode) + 1}";

    public Brush Brush { get => _brush; private set => Set(ref _brush, value); }

    public Brush Fill { get => _fill; private set => Set(ref _fill, value); }

    public void ApplyTheme(bool light)
    {
        var color = Palette.ModeColor(Mode, light);
        Brush = Frozen(new SolidColorBrush(color));
        Fill = Frozen(new SolidColorBrush(Color.FromArgb(FillAlpha, color.R, color.G, color.B)));
    }

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}

internal sealed class RowViewModel : ObservableObject
{
    private IReadOnlyList<string> _lines = [];
    private string? _live;
    private RowGlyph _glyph;
    private string? _failureReason;
    private bool _showKeepDocker;
    private bool _keepDocker;

    public RowViewModel(LeverRow row) => Title = LeverRows.Title(row);

    public string Title { get; }

    public IReadOnlyList<string> Lines
    {
        get => _lines;
        set
        {
            if (!_lines.SequenceEqual(value))
            {
                Set(ref _lines, value);
            }
        }
    }

    public string? Live { get => _live; set => Set(ref _live, value); }

    public RowGlyph Glyph { get => _glyph; set => Set(ref _glyph, value); }

    public string? FailureReason { get => _failureReason; set => Set(ref _failureReason, value); }

    public bool ShowKeepDocker { get => _showKeepDocker; set => Set(ref _showKeepDocker, value); }

    public bool KeepDocker { get => _keepDocker; set => Set(ref _keepDocker, value); }

    public void Update(RowState state)
    {
        Lines = state.Lines;
        Live = state.Live;
        Glyph = state.Glyph;
        FailureReason = state.FailureReason;
        ShowKeepDocker = state.ShowKeepDocker;
        KeepDocker = state.KeepDocker;
    }
}

/// <summary>Mutable mirror of <see cref="FlyoutState"/> that updates in place, so animations and tooltips survive refreshes.</summary>
internal sealed class FlyoutViewModel : ObservableObject
{
    private string _statusLine = string.Empty;
    private bool _statusIsWarning;
    private string? _banner;
    private string _footer = string.Empty;
    private bool _startWithWindows;
    private bool _isSettingsOpen;
    private bool _isPinned = true;

    public FlyoutViewModel()
    {
        Modes = [.. ModeNames.All.Select(m => new ModeItem(m))];
        Rows = [.. LeverRows.Ordered.Select(r => new RowViewModel(r))];
    }

    public IReadOnlyList<ModeItem> Modes { get; }

    public ObservableCollection<RowViewModel> Rows { get; }

    public string StatusLine { get => _statusLine; set => Set(ref _statusLine, value); }

    public bool StatusIsWarning { get => _statusIsWarning; set => Set(ref _statusIsWarning, value); }

    public string? Banner { get => _banner; set => Set(ref _banner, value); }

    public string Footer { get => _footer; set => Set(ref _footer, value); }

    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    public SettingsViewModel Settings { get; } = new();

    public PowerViewModel Power { get; } = new();

    /// <summary>The Apps settings page replaces the lever table.</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (Set(ref _isSettingsOpen, value))
            {
                Raise(nameof(IsMainOpen));
            }
        }
    }

    public bool IsMainOpen => !_isSettingsOpen;

    /// <summary>The flyout stays open when it loses focus.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (Set(ref _isPinned, value))
            {
                Raise(nameof(PinGlyph));
                Raise(nameof(PinToolTip));
            }
        }
    }

    public string PinGlyph => _isPinned ? Glyphs.PinnedFill : Glyphs.Pin;

    /// <summary>What a click does.</summary>
    public string PinToolTip => _isPinned ? "Hide when clicking elsewhere" : "Keep open";

    public void ApplyTheme(bool light)
    {
        foreach (var mode in Modes)
        {
            mode.ApplyTheme(light);
        }
    }

    public void Update(FlyoutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var mode in Modes)
        {
            mode.IsSelected = mode.Mode == state.Mode;
            mode.IsEnabled = !state.IsApplying;
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Update(state.Rows[i]);
        }

        StatusLine = state.StatusLine;
        StatusIsWarning = state.StatusTone == StatusTone.Warning;
        Banner = state.Banner;
        Footer = state.Footer;
        StartWithWindows = state.StartWithWindows;
        if (state.Power is { } power)
        {
            Power.Update(power);
        }
    }
}

/// <summary>Segoe Fluent Icons code points used by the flyout and the tray icon.</summary>
internal static class Glyphs
{
    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";
    public const string Plug = "";
    public const string Battery = "";
    public const string LowBattery = "";
    public const string Check = "";
    public const string Sync = "";
    public const string Warning = "";
    public const string Settings = "";
    public const string Close = "";
    public const string Shield = "";

    public const string Back = "";
    public const string Pin = "";
    public const string PinnedFill = "";

    public static string ForMode(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Grid => Plug,
        PowerModeKind.Station => Battery,
        PowerModeKind.Reserve => LowBattery,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
