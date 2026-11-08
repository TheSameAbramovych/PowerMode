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

    public static BitmapSource Render(PowerModeKind mode, int size, bool lightTaskbar, int? spinnerFrame)
    {
        var color = mode switch
        {
            PowerModeKind.Station => Palette.TrayStation,
            PowerModeKind.Reserve => Palette.TrayReserve,
            _ => lightTaskbar ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White,
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();

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
            if (spinnerFrame is { } frame)
            {
                var radius = Math.Max(1.5, size * DotRadiusRatio);
                var angle = 2 * Math.PI * (frame % SpinnerFrames) / SpinnerFrames;
                var orbit = size * 0.18;
                var centre = new Point(size - radius - orbit * 0.5 + Math.Cos(angle) * orbit * 0.5, size - radius - orbit * 0.5 + Math.Sin(angle) * orbit * 0.5);
                var outline = new Pen(lightTaskbar ? Brushes.White : Brushes.Black, Math.Max(1, size / 32.0));
                outline.Freeze();
                var accent = new SolidColorBrush(lightTaskbar ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x60, 0xCD, 0xFF));
                accent.Freeze();
                context.DrawEllipse(accent, outline, centre, radius, radius);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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
