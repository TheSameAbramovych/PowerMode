using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PowerMode.Core;

namespace PowerMode.App.Ui;

/// <summary>The flyout content. Used by the live window and by <c>--render-states</c>.</summary>
internal sealed partial class FlyoutView : UserControl
{
    public FlyoutView() => InitializeComponent();

    public event EventHandler<PowerModeKind>? ModeRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler? ConfigRequested;

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
