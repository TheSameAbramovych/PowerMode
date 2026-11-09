using System.Globalization;
using System.Text;
using PowerMode.Core.Levers;
using PowerMode.Core.Switching;

namespace PowerMode.Core.Presentation;

/// <summary>Plain-text output of <c>--status</c> and <c>--dry-run</c>.</summary>
public static class TextReports
{
    private const int NameWidth = 12;
    private const int SetWidth = 44;
    private const int LiveWidth = 17;
    private const string Bullet = "  - ";

    /// <param name="state">The flyout state.</param>
    /// <param name="activeScheme">The active power scheme (GUID and its name), printed under the CPU row; null when unknown.</param>
    public static string Status(FlyoutState state, (Guid Id, string? Name)? activeScheme = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var text = new StringBuilder();
        text.AppendLine("PowerMode" + FlyoutStateBuilder.Dot + state.StatusLine);
        if (state.Power is { } power)
        {
            text.AppendLine(power.SummaryLine);
            text.AppendLine(power.PartsLine);
        }

        if (state.Banner is { } banner)
        {
            text.AppendLine(banner);
        }

        text.AppendLine();
        text.AppendLine(Line("Lever", "Set", "Live", "Status"));
        text.AppendLine(Line(new string('-', NameWidth - 1), new string('-', SetWidth - 1), new string('-', LiveWidth - 1), "------"));
        foreach (var row in state.Rows)
        {
            IReadOnlyList<string> rowLines = row.Row == Levers.LeverRow.Cpu && activeScheme is { } scheme
                ? [.. row.Lines, $"Active scheme: {scheme.Name ?? "(no name)"} {scheme.Id}"]
                : row.Lines;
            var lines = rowLines.SelectMany(l => Wrap(l, SetWidth - 1)).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var first = i == 0;
                text.AppendLine(Line(first ? row.Title : string.Empty, lines[i], first ? row.Live ?? string.Empty : string.Empty, first ? GlyphText(row) : string.Empty));
            }

            if (row.ShowKeepDocker)
            {
                text.AppendLine(Line(string.Empty, $"Keep running: {(row.KeepDocker ? "on" : "off")}", string.Empty, string.Empty));
            }
        }

        text.AppendLine();
        text.AppendLine(state.Footer);
        return text.ToString();
    }

    public static string DryRun(DryRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Dry run: {ModeNames.Display(report.From)} -> {ModeNames.Display(report.To)}. Nothing is changed.");
        if (report.CapturesSnapshot)
        {
            text.AppendLine("A baseline snapshot would be captured first (Grid restores it later).");
        }

        text.AppendLine();
        foreach (var lever in report.Levers)
        {
            var name = FriendlyNames.Lever(lever.Kind);
            if (lever.Plan.Unavailable is { } reason)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{name}: would fail - {reason}");
            }
            else if (lever.Plan.NotOwned)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{name}: {(lever.Plan.SkipReason is { } skip ? $"skipped ({skip})" : "not used in this mode")}");
            }
            else if (lever.Plan.Actions.Count == 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{name}: already set");
            }
            else
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{name}:");
                foreach (var action in lever.Plan.Actions)
                {
                    var blocked = action.RequiresElevation && !lever.IsElevated ? $"  [{LeverResult.NeedsAdministrator.ToLowerInvariant()}: would fail]" : string.Empty;
                    text.AppendLine(Bullet + action.Description + blocked);
                }
            }
        }

        return text.ToString();
    }

    private static string GlyphText(RowState row) => row.Glyph switch
    {
        RowGlyph.Ok => "ok",
        RowGlyph.Applying => "applying",
        RowGlyph.Failed => "FAILED: " + row.FailureReason,
        _ => "-",
    };

    private static string Line(string name, string set, string live, string status) =>
        name.PadRight(NameWidth) + Fit(set, SetWidth) + live.PadRight(LiveWidth) + status;

    /// <summary>Pad to the column; a single word longer than the column keeps going instead of being cut.</summary>
    private static string Fit(string value, int width) => value.Length < width ? value.PadRight(width) : value + " ";

    /// <summary>Word-wrap at spaces so nothing is truncated.</summary>
    internal static IEnumerable<string> Wrap(string value, int width)
    {
        var line = new StringBuilder();
        foreach (var word in value.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        yield return line.ToString();
    }
}
