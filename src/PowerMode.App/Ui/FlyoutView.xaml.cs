using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PowerMode.Core;

namespace PowerMode.App.Ui;

/// <summary>The flyout content (lever table or Apps settings page). Used by the live window and by <c>--render-states</c>.</summary>
internal sealed partial class FlyoutView : UserControl
{
    public FlyoutView() => InitializeComponent();

    public event EventHandler<PowerModeKind>? ModeRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler? ConfigRequested;

    public event EventHandler? PinRequested;

    public event EventHandler? CalibrateRequested;

    public event EventHandler? CalibrationSaveRequested;

    public event EventHandler? CalibrationCancelRequested;

    public event EventHandler? CalibrationResetRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? BackRequested;

    public event EventHandler? AppsTabRequested;

    public event EventHandler? LeversTabRequested;

    public event EventHandler? AddRequested;

    public event EventHandler? RestartElevatedRequested;

    public event EventHandler<bool>? KeepDockerChanged;

    public event EventHandler<bool>? StartWithWindowsChanged;

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModeItem item })
        {
            ModeRequested?.Invoke(this, item.Mode);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnConfig(object sender, RoutedEventArgs e) => ConfigRequested?.Invoke(this, EventArgs.Empty);

    private void OnPin(object sender, RoutedEventArgs e) => PinRequested?.Invoke(this, EventArgs.Empty);

    private void OnCalibrate(object sender, RoutedEventArgs e) => CalibrateRequested?.Invoke(this, EventArgs.Empty);

    private void OnCalibrationSave(object sender, RoutedEventArgs e) => CalibrationSaveRequested?.Invoke(this, EventArgs.Empty);

    private void OnCalibrationCancel(object sender, RoutedEventArgs e) => CalibrationCancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnCalibrationReset(object sender, RoutedEventArgs e) => CalibrationResetRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Enter saves the reading, Esc cancels it (and does not close the flyout).</summary>
    private void OnReadingKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CalibrationSaveRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CalibrationCancelRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>Put the caret into the reading box (the calibration form just opened).</summary>
    public void FocusReading()
    {
        ReadingBox.Focus();
        ReadingBox.SelectAll();
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnAppsTab(object sender, RoutedEventArgs e) => AppsTabRequested?.Invoke(this, EventArgs.Empty);

    private void OnLeversTab(object sender, RoutedEventArgs e) => LeversTabRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Enter commits a typed number (the GPU limit) like leaving the box does.</summary>
    private void OnNumberKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e) => AddRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e) => RestartElevatedRequested?.Invoke(this, EventArgs.Empty);

    private void OnKeepDocker(object sender, RoutedEventArgs e) =>
        KeepDockerChanged?.Invoke(this, ((CheckBox)sender).IsChecked == true);

    private void OnStartWithWindows(object sender, RoutedEventArgs e) =>
        StartWithWindowsChanged?.Invoke(this, ((CheckBox)sender).IsChecked == true);
}

/// <summary>null/empty → Collapsed (or false when the target is bool), anything else → Visible (or true).</summary>
internal sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && (value is not string text || text.Length > 0);
        return targetType == typeof(bool) ? present : present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An empty text → Visible (a placeholder over an empty text box), anything else → Collapsed.</summary>
internal sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
