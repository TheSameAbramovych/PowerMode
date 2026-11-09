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

/// <summary>What <c>--render-states</c> wrote, and every lever-table fixture whose content does not fit the flyout's maximum height (settings pages scroll instead).</summary>
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
            var (bitmap, height) = RenderFlyout(app, fixture);
            IconRenderer.SavePng(bitmap, path);
            var limit = fixture.MaxHeight ?? FlyoutWindow.MaxFlyoutHeight;
            var scrolls = height > limit;
            written.Add(string.Create(CultureInfo.InvariantCulture, $"{path} ({height:0} of {limit:0} px{(scrolls ? ", scrolls" : string.Empty)})"));

            // A settings page scrolls under its header and tabs; the lever table should fit this PC's work area.
            if (scrolls && fixture.Settings is null && fixture.Levers is null)
            {
                overflows.Add(string.Create(CultureInfo.InvariantCulture, $"{fixture.FileName}: content is {height:0} px, more than {limit:0} px (the lever table would scroll)"));
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

        var overview = Path.Combine(directory, "tray-icons.png");
        IconRenderer.SavePng(TrayOverview(), overview);
        written.Add(overview);

        return new RenderReport(written, overflows);
    }

    /// <summary>One tray icon at each of <see cref="TraySizes"/>, side by side on the taskbar colour, enlarged without smoothing.</summary>
    /// <summary>The three modes at 32 px, twice enlarged, on the dark taskbar (top) and the light one (bottom): the README picture.</summary>
    private static RenderTargetBitmap TrayOverview()
    {
        const int icon = 32;
        const int zoom = 2;
        var cell = icon * zoom + 2 * TrayGap;
        var modes = ModeNames.All;
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var context = group.Open())
        {
            foreach (var (light, row) in new[] { (false, 0), (true, 1) })
            {
                var background = new SolidColorBrush(light ? Palette.TaskbarLight : Palette.TaskbarDark);
                background.Freeze();
                context.DrawRectangle(background, null, new Rect(0, row * cell, modes.Count * cell, cell));
                for (var i = 0; i < modes.Count; i++)
                {
                    context.DrawImage(IconRenderer.Render(modes[i], icon, light, spinnerFrame: null), new Rect(i * cell + TrayGap, row * cell + TrayGap, icon * zoom, icon * zoom));
                }
            }
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawDrawing(group);
        }

        var bitmap = new RenderTargetBitmap(modes.Count * cell, 2 * cell, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

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

    /// <summary>The framed flyout as the window shows the fixture (lever table, Apps or Levers page, calibration form), and the height its content needs without the window's maximum.</summary>
    public static (BitmapSource Bitmap, double ContentHeight) RenderFlyout(App app, RenderFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(fixture);
        var (state, light, settings, calibrationReading) = (fixture.State, fixture.LightTheme, fixture.Settings, fixture.CalibrationReading);
        app.ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
        Palette.Apply(app.Resources, light);
        var model = new FlyoutViewModel();
        model.ApplyTheme(light);
        model.Update(state);
        if (settings is not null)
        {
            model.Settings.Load(settings);
            model.Page = FlyoutPage.Apps;
        }

        if (fixture is { Levers: { } levers, Machine: { } machine })
        {
            model.Levers.Load(levers, machine);
            model.Levers.ShowCpuWatts("≈ 18 W now");
            model.Page = FlyoutPage.Levers;
        }

        model.Settings.Show(fixture.Feedback ?? SettingsFeedback.None);

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
            MaxHeight = fixture.MaxHeight ?? FlyoutWindow.MaxFlyoutHeight,
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
