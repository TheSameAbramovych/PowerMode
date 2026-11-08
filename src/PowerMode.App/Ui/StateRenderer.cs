using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerMode.Core;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Ui;

/// <summary><c>--render-states</c>: renders the flyout fixtures and tray icons to PNG with fake data. Touches nothing.</summary>
internal static class StateRenderer
{
    private const double Scale = 2.0;
    private const int TrayPngSize = 32;
    private const double Dpi = 96;

    public static IReadOnlyList<string> Run(App app, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        foreach (var fixture in RenderFixtures.All())
        {
            var path = Path.Combine(directory, fixture.FileName);
            IconRenderer.SavePng(RenderFlyout(app, fixture.State, fixture.LightTheme), path);
            written.Add(path);
        }

        var lightTaskbar = Palette.TaskbarUsesLightTheme();
        foreach (var mode in ModeNames.All)
        {
            var path = Path.Combine(directory, $"tray-{ModeNames.Key(mode)}.png");
            IconRenderer.SavePng(IconRenderer.Render(mode, TrayPngSize, lightTaskbar, spinnerFrame: null), path);
            written.Add(path);
        }

        var applying = Path.Combine(directory, "tray-station-applying.png");
        IconRenderer.SavePng(IconRenderer.Render(PowerModeKind.Station, TrayPngSize, lightTaskbar, spinnerFrame: 1), applying);
        written.Add(applying);
        return written;
    }

    public static BitmapSource RenderFlyout(App app, FlyoutState state, bool light)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
        Palette.Apply(app.Resources, light);
        var model = new FlyoutViewModel();
        model.ApplyTheme(light);
        model.Update(state);

        // The live window gets its rounded corners and 1 px border from DWM; draw the same frame here.
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            MaxHeight = FlyoutWindow.MaxFlyoutHeight,
            Child = new FlyoutView { DataContext = model },
        };
        frame.SetResourceReference(Border.BackgroundProperty, Palette.Background);
        frame.SetResourceReference(Border.BorderBrushProperty, Palette.Border);
        var host = new Border { Padding = new Thickness(8), Background = Brushes.Transparent, Child = frame };

        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var width = (int)Math.Ceiling(host.ActualWidth * Scale);
        var height = (int)Math.Ceiling(host.ActualHeight * Scale);
        var bitmap = new RenderTargetBitmap(width, height, Dpi * Scale, Dpi * Scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return bitmap;
    }
}
