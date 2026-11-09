using System.Collections.ObjectModel;
using System.Globalization;
using PowerMode.Core;
using PowerMode.Core.Model;
using PowerMode.Core.Settings;

namespace PowerMode.App.Ui;

/// <summary>A user's change on the Levers page; <see cref="Value"/> null = "Keep" / leave alone.</summary>
internal sealed record LeverChange(LeverSetting Setting, PowerModeKind Mode, int? Value, int? Previous);

/// <summary>One line of <c>This PC</c>.</summary>
internal sealed record FactLine(string Label, string Value);

/// <summary>A typed GPU limit that is not a number.</summary>
internal sealed record LeverInputError(string Message);

/// <summary>
/// One row of the Levers page. A choice box binds <see cref="Station"/>/<see cref="Reserve"/> (with
/// <see cref="KeepValue"/> standing in for null, which a choice box cannot select); the GPU row binds the texts.
/// </summary>
internal sealed class LeverRowViewModel : ObservableObject
{
    /// <summary>"Keep" in a choice box (a null value would select nothing).</summary>
    public const int KeepValue = int.MinValue;

    private readonly LeversViewModel _owner;
    private int? _station;
    private int? _reserve;
    private string? _live;

    public LeverRowViewModel(LeversViewModel owner, LeverSettingRow row)
    {
        _owner = owner;
        Setting = row.Setting;
        Title = row.Title;
        Note = row.Note;
        Enabled = row.Enabled;
        IsNumeric = row.IsNumeric;
        Options = [.. row.Options.Select(o => new ChoiceOption(o.Value ?? KeepValue, o.Label))];
        _station = row.Station;
        _reserve = row.Reserve;
    }

    public LeverSetting Setting { get; }

    public string Title { get; }

    public string? Note { get; }

    public bool Enabled { get; }

    public bool IsNumeric { get; }

    public bool IsChoice => !IsNumeric;

    public IReadOnlyList<ChoiceOption> Options { get; }

    /// <summary>Live value beside the title, e.g. the estimated CPU watts.</summary>
    public string? Live { get => _live; set => Set(ref _live, value); }

    public int Station { get => _station ?? KeepValue; set => Choose(PowerModeKind.Station, value == KeepValue ? null : value); }

    public int Reserve { get => _reserve ?? KeepValue; set => Choose(PowerModeKind.Reserve, value == KeepValue ? null : value); }

    public string StationText { get => Text(_station); set => Type(PowerModeKind.Station, value); }

    public string ReserveText { get => Text(_reserve); set => Type(PowerModeKind.Reserve, value); }

    /// <summary>Show a stored value (clamped, or put back after a failed save) without reporting it.</summary>
    public void Show(PowerModeKind mode, int? value)
    {
        if (mode == PowerModeKind.Station)
        {
            _station = value;
            Raise(nameof(Station));
            Raise(nameof(StationText));
        }
        else
        {
            _reserve = value;
            Raise(nameof(Reserve));
            Raise(nameof(ReserveText));
        }
    }

    private void Choose(PowerModeKind mode, int? value)
    {
        var previous = mode == PowerModeKind.Station ? _station : _reserve;
        if (previous != value)
        {
            Show(mode, value);
            _owner.Report(new LeverChange(Setting, mode, value, previous));
        }
    }

    private void Type(PowerModeKind mode, string text)
    {
        if (LeverSettings.TryParseWatts(text, out var watts))
        {
            Choose(mode, watts);
        }
        else
        {
            _owner.ReportError(new LeverInputError("Type whole watts, or leave it empty to keep the limit"));
            Show(mode, mode == PowerModeKind.Station ? _station : _reserve);
        }
    }

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>The Levers page: a row per setting for Station and Reserve, the CPU note and <c>This PC</c>.</summary>
internal sealed class LeversViewModel : ObservableObject
{
    private IReadOnlyList<FactLine> _facts = [];

    public ObservableCollection<LeverRowViewModel> Rows { get; } = [];

    /// <summary>The <c>This PC</c> section.</summary>
    public IReadOnlyList<FactLine> Facts { get => _facts; private set => Set(ref _facts, value); }

    public event EventHandler<LeverChange>? Changed;

    public event EventHandler<LeverInputError>? InputError;

    public void Load(IReadOnlyList<LeverSettingRow> rows, MachineInfo machine)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(machine);
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(new LeverRowViewModel(this, row));
        }

        Facts = [.. machine.Facts().Select(f => new FactLine(f.Label, f.Value))];
    }

    /// <summary>The estimated CPU watts beside the CPU row, so the effect of the % is visible.</summary>
    public void ShowCpuWatts(string? text)
    {
        foreach (var row in Rows.Where(r => r.Setting == LeverSetting.CpuMaxPercent))
        {
            row.Live = text;
        }
    }

    internal void Report(LeverChange change) => Changed?.Invoke(this, change);

    internal void ReportError(LeverInputError error) => InputError?.Invoke(this, error);
}
