using System.Globalization;
using System.Text.RegularExpressions;

namespace PowerMode.Core.Parsing;

public sealed record PowerSchemeEntry(Guid Id, bool IsActive);

public sealed record SettingIndex(uint? Ac, uint? Dc);

/// <summary>
/// Parses powercfg output by GUIDs and hex values only. Scheme and setting names are localized
/// (Ukrainian on this PC), so they are never read.
/// </summary>
public static partial class PowercfgParser
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@":\s*0x(?<hex>[0-9a-fA-F]{1,8})\s*$")]
    private static partial Regex HexValuePattern();

    [GeneratedRegex(@"Current AC Power Setting Index:\s*0x(?<hex>[0-9a-fA-F]{1,8})", RegexOptions.IgnoreCase)]
    private static partial Regex AcPattern();

    [GeneratedRegex(@"Current DC Power Setting Index:\s*0x(?<hex>[0-9a-fA-F]{1,8})", RegexOptions.IgnoreCase)]
    private static partial Regex DcPattern();

    /// <summary><c>powercfg /getactivescheme</c> → the active scheme GUID.</summary>
    public static Guid? ParseActiveScheme(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = GuidPattern().Match(output);
        return match.Success ? Guid.Parse(match.Value) : null;
    }

    /// <summary><c>powercfg /list</c> → every scheme; the active one ends with <c>*</c>.</summary>
    public static IReadOnlyList<PowerSchemeEntry> ParseSchemeList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var schemes = new List<PowerSchemeEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            var match = GuidPattern().Match(line);
            if (match.Success)
            {
                schemes.Add(new PowerSchemeEntry(Guid.Parse(match.Value), line.EndsWith('*')));
            }
        }

        return schemes;
    }

    /// <summary>
    /// <c>powercfg /q scheme sub setting</c> → current AC/DC index. The labels are English on this PC; when a
    /// Windows build localizes them, the last two hex values (always AC then DC) are used.
    /// A hidden setting (e.g. PERFBOOSTMODE) prints no index lines: both values are null.
    /// </summary>
    public static SettingIndex ParseSettingIndex(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var ac = AcPattern().Match(output);
        var dc = DcPattern().Match(output);
        if (ac.Success || dc.Success)
        {
            return new SettingIndex(ac.Success ? Hex(ac) : null, dc.Success ? Hex(dc) : null);
        }

        var values = output.Split('\n')
            .Select(line => HexValuePattern().Match(line.TrimEnd('\r')))
            .Where(m => m.Success)
            .Select(Hex)
            .ToList();
        return values.Count >= 2 ? new SettingIndex(values[^2], values[^1]) : new SettingIndex(null, null);
    }

    private static uint Hex(Match match) => uint.Parse(match.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
