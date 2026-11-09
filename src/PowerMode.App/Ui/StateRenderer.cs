using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerMode.Core;
using PowerMode.Core.Presentation;
using PowerMode.Core.Settings;

namespace PowerMode.App.Ui;

/// <summary>What <c>--render-states</c> wrote, and every fixture whose content does not fit the flyout's maximum height.</summary>
internal sealed record RenderReport(IReadOnlyList<string> Written, IReadOnlyList<string> Overflows);

/// <summary><c>--render-states</c>: renders the flyout fixtures and tray icons to PNG with fake data. Touches nothing.</summary>
internal static class StateRenderer
{
    private const double Scale = 2.0;

    /// <summary>Tray icon sizes at 100/125/150/200 % scaling, each shown 4× enlarged pixel by pixel on the taskbar colour.</summary>
    private static readonly int[] TraySizes = [16, 20, 24, 32];
    private const int TrayZoom = 4;
    private const int TrayGap = 8;
    private const double Dpi = 96;
    private const double FrameBorder = 2;

    public static RenderReport Run(App app, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        var overflows = new List<string>();
        foreach (var fixture in RenderFixtures.All())
        {
            var path = Path.Combine(directory, fixture.FileName);
            var (bitmap, height) = RenderFlyout(app, fixture.State, fixture.LightTheme, fixture.Settings, fixture.Feedback, fixture.CalibrationReading);
            IconRenderer.SavePng(bitmap, path);
            written.Add(string.Create(CultureInfo.InvariantCulture, $"{path} ({height:0} of {FlyoutWindow.MaxFlyoutHeight:0} px)"));
            if (height > FlyoutWindow.MaxFlyoutHeight)
            {
                overflows.Add(string.Create(CultureInfo.InvariantCulture, $"{fixture.FileName}: content is {height:0} px, more than {FlyoutWindow.MaxFlyoutHeight:0} px (it would scroll)"));
            }
        }

        // Tray icons on both taskbar themes, at every size the notification area uses.
        foreach (var light in new[] { false, true })
        {
            var suffix = light ? "-light" : string.Empty;
            foreach (var mode in ModeNames.All)
            {
                var path = Path.Combine(directory, $"tray-{ModeNames.Key(mode)}{suffix}.png");
                IconRenderer.SavePng(TrayStrip(mode, light, spinnerFrame: null), path);
                written.Add(path);
            }

            var applying = Path.Combine(directory, $"tray-station-applying{suffix}.png");
            IconRenderer.SavePng(TrayStrip(PowerModeKind.Station, light, spinnerFrame: 1), applying);
            written.Add(applying);
        }

        return new RenderReport(written, overflows);
    }

    /// <summary>One tray icon at each of <see cref="TraySizes"/>, side by side on the taskbar colour, enlarged without smoothing.</summary>
    private static RenderTargetBitmap TrayStrip(PowerModeKind mode, bool lightTaskbar, int? spinnerFrame)
    {
        var cell = TraySizes.Max() * TrayZoom;
        var width = TrayGap + TraySizes.Length * (cell + TrayGap);
        var height = cell + 2 * TrayGap;
        var background = new SolidColorBrush(lightTaskbar ? Palette.TaskbarLight : Palette.TaskbarDark);
        background.Freeze();
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var context = group.Open())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            for (var i = 0; i < TraySizes.Length; i++)
            {
                var size = TraySizes[i];
                var icon = IconRenderer.Render(mode, size, lightTaskbar, spinnerFrame);
                var x = TrayGap + i * (cell + TrayGap) + (cell - size * TrayZoom) / 2.0;
                var y = TrayGap + (cell - size * TrayZoom) / 2.0;
                context.DrawImage(icon, new Rect(x, y, size * TrayZoom, size * TrayZoom));
            }
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawDrawing(group);
        }

        var bitmap = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The framed flyout as the window shows it (the Apps settings page when <paramref name="settings"/> is set), and the height its content needs without the window's maximum.</summary>
    public static (BitmapSource Bitmap, double ContentHeight) RenderFlyout(App app, FlyoutState state, bool light, AppSettingsPage? settings = null, SettingsFeedback? feedback = null, string? calibrationReading = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
        Palette.Apply(app.Resources, light);
        var model = new FlyoutViewModel();
        model.ApplyTheme(light);
        model.Update(state);
        if (settings is not null)
        {
            model.Settings.Load(settings);
            model.Settings.Show(feedback ?? SettingsFeedback.None);
            model.IsSettingsOpen = true;
        }

        if (calibrationReading is not null)
        {
            model.Power.Reading = calibrationReading;
            model.Power.IsCalibrating = true;
        }

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

        // The frame's MaxHeight clamps what it reports, so measure an unclamped copy of the view for the guard.
        var probe = new FlyoutView { DataContext = model };
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        probe.Measure(unbounded);
        probe.Arrange(new Rect(probe.DesiredSize));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        probe.UpdateLayout();
        probe.Measure(unbounded);
        return (bitmap, probe.DesiredSize.Height + FrameBorder);
    }
}
