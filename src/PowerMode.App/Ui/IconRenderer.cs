using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerMode.Core;

namespace PowerMode.App.Ui;

/// <summary>
/// Draws the tray icon in code from the Segoe Fluent Icons glyph geometry, scaled to fill the exact pixel size
/// (crisp at any DPI). While applying, a small dot orbits the bottom-right corner.
/// </summary>
internal static class IconRenderer
{
    public const int SpinnerFrames = 8;
    private const double GlyphEm = 100;
    private const double DotRadiusRatio = 0.17;
    private const double InkInset = 0.04;

    /// <summary>
    /// The tray icon. Every mode has a colour, so a running PowerMode always looks "on": Grid the plug in blue,
    /// Station the amber battery, Reserve the red low battery; the two saving modes also carry a small filled dot
    /// (replaced by the orbiting dot while a switch runs).
    /// </summary>
    public static BitmapSource Render(PowerModeKind mode, int size, bool lightTaskbar, int? spinnerFrame)
    {
        var color = mode switch
        {
            PowerModeKind.Station => Palette.TrayStation,
            PowerModeKind.Reserve => Palette.TrayReserve,
            _ => lightTaskbar ? Palette.TrayGridOnLight : Palette.TrayGridOnDark,
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var outline = new Pen(new SolidColorBrush(lightTaskbar ? Palette.TaskbarLight : Palette.TaskbarDark), Math.Max(1, size / 16.0));
        outline.Freeze();

        var geometry = GlyphGeometry(Glyphs.ForMode(mode));
        var bounds = geometry.Bounds;
        var inset = size * InkInset;
        var scale = Math.Min((size - 2 * inset) / bounds.Width, (size - 2 * inset) / bounds.Height);
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2));
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(size / 2.0, size / 2.0));
        geometry.Transform = transform;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(brush, null, geometry);
            var radius = Math.Max(1.5, size * DotRadiusRatio);
            if (spinnerFrame is { } frame)
            {
                var angle = 2 * Math.PI * (frame % SpinnerFrames) / SpinnerFrames;
                var orbit = size * 0.18;
                var centre = new Point(size - radius - orbit * 0.5 + Math.Cos(angle) * orbit * 0.5, size - radius - orbit * 0.5 + Math.Sin(angle) * orbit * 0.5);
                var accent = new SolidColorBrush(lightTaskbar ? Palette.TrayGridOnLight : Palette.TrayGridOnDark);
                accent.Freeze();
                context.DrawEllipse(accent, outline, centre, radius, radius);
            }
            else if (mode != PowerModeKind.Grid)
            {
                // "Active" dot in the mode colour, cut out of the glyph by a ring in the taskbar colour.
                context.DrawEllipse(brush, outline, new Point(size - radius - outline.Thickness / 2, size - radius - outline.Thickness / 2), radius, radius);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Sizes of the application icon (Explorer, taskbar, Alt+Tab, desktop shortcut at every DPI).</summary>
    public static IReadOnlyList<int> AppIconSizes { get; } = [16, 20, 24, 32, 48, 64, 128, 256];

    private const double TileCornerRatio = 0.22;
    private const double TileGlyphRatio = 0.64;
    private static readonly Color TileTop = Color.FromRgb(0x2E, 0x2E, 0x2E);
    private static readonly Color TileBottom = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly Color TileEdge = Color.FromRgb(0x4A, 0x4A, 0x4A);
    private static readonly Color AppAccent = Color.FromRgb(0x60, 0xCD, 0xFF);

    /// <summary>
    /// The application icon: the Grid plug (the tray icon's glyph) in the accent colour on a rounded dark tile with a
    /// 1 px edge, so it reads on light and dark desktops.
    /// </summary>
    public static BitmapSource RenderAppIcon(int size)
    {
        var tile = new LinearGradientBrush(TileTop, TileBottom, 90);
        tile.Freeze();
        var edge = new Pen(new SolidColorBrush(TileEdge), Math.Max(1, size / 64.0));
        edge.Freeze();
        var accent = new SolidColorBrush(AppAccent);
        accent.Freeze();

        var geometry = GlyphGeometry(Glyphs.Plug);
        var bounds = geometry.Bounds;
        var scale = size * TileGlyphRatio / Math.Max(bounds.Width, bounds.Height);
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2));
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(size / 2.0, size / 2.0));
        geometry.Transform = transform;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var half = edge.Thickness / 2;
            var radius = size * TileCornerRatio;
            context.DrawRoundedRectangle(tile, edge, new Rect(half, half, size - edge.Thickness, size - edge.Thickness), radius, radius);
            context.DrawGeometry(accent, null, geometry);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary><c>--make-icon</c>: write the application icon with every size in <see cref="AppIconSizes"/>.</summary>
    public static void SaveAppIcon(string path)
    {
        var images = AppIconSizes.Select(size => new Core.Presentation.IcoImage(size, EncodePng(RenderAppIcon(size)))).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Core.Presentation.IcoWriter.Write(images));
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A GDI+ icon for NotifyIcon. The caller owns <paramref name="handle"/> and must DestroyIcon it.</summary>
    public static System.Drawing.Icon ToIcon(BitmapSource source, out IntPtr handle)
    {
        var straight = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = straight.PixelWidth;
        var height = straight.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(pixels, row * stride, data.Scan0 + row * data.Stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        handle = bitmap.GetHicon();
        return System.Drawing.Icon.FromHandle(handle);
    }

    public static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Geometry GlyphGeometry(string glyph)
    {
        var typeface = new Typeface(new FontFamily(Glyphs.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, GlyphEm, Brushes.Black, 1.0);
        return text.BuildGeometry(new Point(0, 0)).Clone();
    }
}
