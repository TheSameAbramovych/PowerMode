using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PowerMode.Core.Power;

namespace PowerMode.App.Ui;

/// <summary>
/// The hero figure: <c>≈ 142</c>. A new <see cref="Value"/> rolls over 200 ms from the one shown; the first value
/// (and a value after "unknown") appears at once. Null shows <c>≈ –</c>.
/// </summary>
internal sealed class AnimatedWatts : TextBlock
{
    private static readonly Duration RollDuration = new(TimeSpan.FromMilliseconds(200));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(AnimatedWatts), new PropertyMetadata(null, OnValueChanged));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(AnimatedWatts), new PropertyMetadata(0.0, OnShownChanged));

    /// <summary>The "≈" is drawn smaller and muted, so the figure reads first.</summary>
    private const double ApproxScale = 0.6;

    /// <summary>After the XAML attributes (the font size) are set: show "≈ –" until a value arrives.</summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (Value is null)
        {
            ShowText(PowerCards.Unknown);
        }
    }

    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = (AnimatedWatts)d;
        if (e.NewValue is not double target)
        {
            text.BeginAnimation(ShownProperty, null);
            text.ShowText(PowerCards.Unknown);
            return;
        }

        if (e.OldValue is null)
        {
            text.BeginAnimation(ShownProperty, null);
            text.SetValue(ShownProperty, target);
            text.Show(target);
            return;
        }

        text.BeginAnimation(ShownProperty, new DoubleAnimation(target, RollDuration) { EasingFunction = new QuadraticEase() });
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AnimatedWatts)d).Show((double)e.NewValue);

    private void Show(double watts) => ShowText(watts.ToString("0", CultureInfo.InvariantCulture));

    private void ShowText(string figure)
    {
        Inlines.Clear();
        var approx = new Run(PowerCards.Approx + " ") { FontSize = FontSize * ApproxScale, FontWeight = FontWeights.Normal };
        approx.SetResourceReference(TextElement.ForegroundProperty, Palette.Muted);
        Inlines.Add(approx);
        Inlines.Add(new Run(figure));
    }
}

/// <summary>
/// The last 10 minutes as a line: 2 px line, 12 % fill under it, a 6 px dot on the newest point, a 1 px tick at
/// the last mode switch. No axes. Hovering shows the time and watts of the nearest point.
/// </summary>
internal sealed class Sparkline : FrameworkElement
{
    private const double LineThickness = 2;
    private const double DotRadius = 3;
    private const byte FillAlpha = 0x1F;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<PowerPoint>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SwitchAtProperty = DependencyProperty.Register(
        nameof(SwitchAt), typeof(DateTimeOffset?), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(SolidColorBrush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly TextBlock _tip = new();

    public Sparkline()
    {
        ToolTip = new ToolTip { Content = _tip };
        ToolTipService.SetInitialShowDelay(this, 0);
    }

    public IReadOnlyList<PowerPoint>? Points
    {
        get => (IReadOnlyList<PowerPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public DateTimeOffset? SwitchAt
    {
        get => (DateTimeOffset?)GetValue(SwitchAtProperty);
        set => SetValue(SwitchAtProperty, value);
    }

    public SolidColorBrush LineBrush
    {
        get => (SolidColorBrush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush TickBrush
    {
        get => (Brush)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        // Transparent background: the whole area takes the hover, not only the line.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Points is not { Count: > 0 } points || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var map = Mapping(points);
        if (SwitchAt is { } switched)
        {
            var x = Math.Round(map.X(switched)) + 0.5;
            if (x >= 0 && x <= ActualWidth)
            {
                drawingContext.DrawLine(new Pen(TickBrush, 1), new Point(x, 0), new Point(x, ActualHeight));
            }
        }

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var fillContext = fill.Open())
        {
            var first = new Point(map.X(points[0].At), map.Y(points[0].TotalW));
            lineContext.BeginFigure(first, false, false);
            fillContext.BeginFigure(new Point(first.X, ActualHeight), true, true);
            fillContext.LineTo(first, false, false);
            foreach (var point in points.Skip(1))
            {
                var p = new Point(map.X(point.At), map.Y(point.TotalW));
                lineContext.LineTo(p, true, true);
                fillContext.LineTo(p, false, false);
            }

            fillContext.LineTo(new Point(map.X(points[^1].At), ActualHeight), false, false);
        }

        var color = LineBrush.Color;
        drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb(FillAlpha, color.R, color.G, color.B)), null, fill);
        drawingContext.DrawGeometry(null, new Pen(LineBrush, LineThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        var last = points[^1];
        drawingContext.DrawEllipse(LineBrush, null, new Point(map.X(last.At), map.Y(last.TotalW)), DotRadius, DotRadius);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (Points is not { Count: > 0 } points)
        {
            _tip.Text = "No samples yet";
            return;
        }

        var map = Mapping(points);
        var x = e.GetPosition(this).X;
        var nearest = points.MinBy(p => Math.Abs(map.X(p.At) - x))!;
        _tip.Text = string.Create(CultureInfo.InvariantCulture, $"{nearest.At.ToLocalTime():HH:mm:ss} · {PowerCards.Approx} {nearest.TotalW:0} W");
    }

    /// <summary>Time over the last 10 minutes left to right; watts over <see cref="PowerHistory.PlotRange"/>, the range the min/max labels name.</summary>
    private (Func<DateTimeOffset, double> X, Func<double, double> Y) Mapping(IReadOnlyList<PowerPoint> points)
    {
        var end = points[^1].At;
        var start = end - PowerHistory.Window;
        var (min, max) = PowerHistory.PlotRange(points);

        var inset = DotRadius;
        var width = ActualWidth - inset;
        var height = ActualHeight - 2 * inset;
        return (
            at => (at - start).TotalSeconds / PowerHistory.Window.TotalSeconds * width,
            watts => inset + (max - watts) / (max - min) * height);
    }
}

/// <summary>
/// The breakdown bar: CPU, GPU, Monitor, Other side by side, widths proportional to their watts, 2 px gaps
/// (the card shows through), fully rounded ends.
/// </summary>
internal sealed class SegmentBar : FrameworkElement
{
    private const double Gap = 2;

    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(IReadOnlyList<PowerPart>), typeof(SegmentBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CpuBrushProperty = BrushProperty(nameof(CpuBrush));
    public static readonly DependencyProperty GpuBrushProperty = BrushProperty(nameof(GpuBrush));
    public static readonly DependencyProperty MonitorBrushProperty = BrushProperty(nameof(MonitorBrush));
    public static readonly DependencyProperty OtherBrushProperty = BrushProperty(nameof(OtherBrush));

    public IReadOnlyList<PowerPart>? Parts
    {
        get => (IReadOnlyList<PowerPart>?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    public Brush CpuBrush { get => (Brush)GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }

    public Brush GpuBrush { get => (Brush)GetValue(GpuBrushProperty); set => SetValue(GpuBrushProperty, value); }

    public Brush MonitorBrush { get => (Brush)GetValue(MonitorBrushProperty); set => SetValue(MonitorBrushProperty, value); }

    public Brush OtherBrush { get => (Brush)GetValue(OtherBrushProperty); set => SetValue(OtherBrushProperty, value); }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (Parts is not { Count: > 0 } parts || ActualWidth <= 0)
        {
            return;
        }

        Brush[] brushes = [CpuBrush, GpuBrush, MonitorBrush, OtherBrush];
        var shown = parts.Select((p, i) => (Watts: p.BarWatts, Brush: brushes[Math.Min(i, brushes.Length - 1)])).Where(p => p.Watts > 0).ToList();
        var total = shown.Sum(p => p.Watts);
        if (total <= 0)
        {
            return;
        }

        var height = ActualHeight;
        var space = ActualWidth - Gap * (shown.Count - 1);
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize), height / 2, height / 2));
        var x = 0.0;
        foreach (var (watts, brush) in shown)
        {
            var width = space * watts / total;
            drawingContext.DrawRectangle(brush, null, new Rect(x, 0, width, height));
            x += width + Gap;
        }

        drawingContext.Pop();
    }
}
