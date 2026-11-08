using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PowerMode.App.Ui;

/// <summary>A word of a fact line, or the " · " between two facts.</summary>
internal sealed record FactToken(string Text, bool IsSeparator);

/// <summary>
/// Splits a fact line ("Stopped · was 6 containers", "Closed: Steam, Postman Agent") into tokens: list items
/// keep their comma, facts are divided by separator tokens. Names are never split.
/// </summary>
internal sealed class FactTokensConverter : IValueConverter
{
    private const string FactSeparator = " · ";
    private const string ItemSeparator = ", ";
    private const string Dot = "·";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Split(value as string ?? string.Empty);

    public static IReadOnlyList<FactToken> Split(string line)
    {
        var tokens = new List<FactToken>();
        var facts = line.Split(FactSeparator);
        for (var f = 0; f < facts.Length; f++)
        {
            if (f > 0)
            {
                tokens.Add(new FactToken(Dot, IsSeparator: true));
            }

            var items = facts[f].Split(ItemSeparator);
            for (var i = 0; i < items.Length; i++)
            {
                tokens.Add(new FactToken(i < items.Length - 1 ? items[i] + "," : items[i], IsSeparator: false));
            }
        }

        return tokens;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Wraps fact tokens like a WrapPanel, but a separator never ends or starts a line: when the fact after it does
/// not fit, the separator is dropped and the fact starts the next line.
/// </summary>
internal sealed class FactPanel : Panel
{
    private const double Gap = 4;
    private static readonly Rect Hidden = new(-10000, -10000, 0, 0);

    public FactPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        var layout = Layout(availableSize.Width);
        return new Size(double.IsInfinity(availableSize.Width) ? layout.Width : Math.Min(layout.Width, availableSize.Width), layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = Layout(finalSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(layout.Places[i] ?? Hidden);
        }

        return finalSize;
    }

    private (double Width, double Height, Rect?[] Places) Layout(double width)
    {
        var places = new Rect?[InternalChildren.Count];
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        double widest = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var size = child.DesiredSize;
            if (IsSeparator(child))
            {
                var next = i + 1 < InternalChildren.Count ? InternalChildren[i + 1].DesiredSize.Width : 0;
                if (x == 0 || x + Gap + size.Width + Gap + next > width)
                {
                    // Drop the separator; the next fact starts a new line.
                    y += lineHeight;
                    x = 0;
                    lineHeight = 0;
                    continue;
                }
            }
            else if (x > 0 && x + Gap + size.Width > width)
            {
                y += lineHeight;
                x = 0;
                lineHeight = 0;
            }

            var left = x == 0 ? 0 : x + Gap;
            places[i] = new Rect(left, y, Math.Min(size.Width, width), size.Height);
            x = left + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
            widest = Math.Max(widest, x);
        }

        return (widest, y + lineHeight, places);
    }

    private static bool IsSeparator(UIElement child) => child is FrameworkElement { DataContext: FactToken { IsSeparator: true } };
}
