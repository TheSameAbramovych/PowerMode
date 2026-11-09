using PowerMode.Core.Power;

namespace PowerMode.App.Ui;

/// <summary>The power card of the flyout: a mirror of <see cref="PowerCard"/> plus the inline calibration form.</summary>
internal sealed class PowerViewModel : ObservableObject
{
    private double? _totalW;
    private string? _deltaText;
    private bool _deltaIsSaving;
    private string _calibrationNote = "Estimate";
    private string _calibrationAction = "Calibrate";
    private bool _isCalibrated;
    private IReadOnlyList<PowerPart> _parts = [];
    private IReadOnlyList<PowerPoint> _history = [];
    private DateTimeOffset? _switchMarker;
    private string? _minLabel;
    private string? _maxLabel;
    private bool _isCalibrating;
    private string _reading = string.Empty;
    private string? _readingError;

    public double? TotalW { get => _totalW; private set => Set(ref _totalW, value); }

    public string? DeltaText { get => _deltaText; private set => Set(ref _deltaText, value); }

    public bool DeltaIsSaving { get => _deltaIsSaving; private set => Set(ref _deltaIsSaving, value); }

    public string CalibrationNote { get => _calibrationNote; private set => Set(ref _calibrationNote, value); }

    public string CalibrationAction { get => _calibrationAction; private set => Set(ref _calibrationAction, value); }

    public bool IsCalibrated { get => _isCalibrated; private set => Set(ref _isCalibrated, value); }

    /// <summary>CPU, GPU, Monitor, Other (bar and legend).</summary>
    public IReadOnlyList<PowerPart> Parts { get => _parts; private set => Set(ref _parts, value); }

    public IReadOnlyList<PowerPoint> History { get => _history; private set => Set(ref _history, value); }

    public DateTimeOffset? SwitchMarker { get => _switchMarker; private set => Set(ref _switchMarker, value); }

    public string? MinLabel { get => _minLabel; private set => Set(ref _minLabel, value); }

    public string? MaxLabel { get => _maxLabel; private set => Set(ref _maxLabel, value); }

    /// <summary>The right side of the card shows "Station shows [ ] W" instead of the sparkline.</summary>
    public bool IsCalibrating
    {
        get => _isCalibrating;
        set
        {
            if (Set(ref _isCalibrating, value))
            {
                Raise(nameof(IsNotCalibrating));
            }
        }
    }

    public bool IsNotCalibrating => !_isCalibrating;

    /// <summary>What the user typed as the station's reading.</summary>
    public string Reading { get => _reading; set => Set(ref _reading, value); }

    public string? ReadingError { get => _readingError; set => Set(ref _readingError, value); }

    public void Update(PowerCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        TotalW = card.TotalW is { } total ? Math.Round(total) : null;
        DeltaText = card.DeltaText;
        DeltaIsSaving = card.DeltaIsSaving;
        CalibrationNote = card.CalibrationNote;
        CalibrationAction = card.CalibrationAction;
        IsCalibrated = card.IsCalibrated;
        if (!card.Parts.Select(p => p.Text).SequenceEqual(Parts.Select(p => p.Text)))
        {
            Parts = card.Parts;
        }

        if (!ReferenceEquals(card.History, History))
        {
            History = card.History;
        }

        SwitchMarker = card.SwitchMarker;
        MinLabel = card.MinLabel;
        MaxLabel = card.MaxLabel;
    }
}
