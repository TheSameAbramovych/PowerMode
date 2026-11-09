using System.Text;

namespace PowerMode.Core.Cli;

/// <summary>
/// Report text for a console that cannot show Unicode: Windows PowerShell 5.1 decodes a redirected native command with
/// the OEM code page (866, 437, …), which has no "≈", "▼" or "…". The reports' symbols become ASCII look-alikes;
/// anything else outside ASCII is left for the encoder to replace.
/// </summary>
public static class ConsoleText
{
    /// <summary>The report text with its symbols mapped to ASCII: · → -, ≈ → ~, … → ..., ▼ → v, ▲ → ^, – and — → -.</summary>
    public static string ToAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(Map(c));
        }

        return builder.ToString();
    }

    /// <summary>One character mapped as <see cref="ToAscii"/> does.</summary>
    public static string Map(char c) => c switch
    {
        '·' or '–' or '—' => "-",
        '≈' => "~",
        '…' => "...",
        '▼' => "v",
        '▲' => "^",
        _ => c.ToString(),
    };

    /// <summary>The console code page needs the ASCII mapping: anything but UTF-8 (65001) or unknown (0).</summary>
    public static bool NeedsAscii(uint codePage) => codePage is not (0 or 65001);
}
