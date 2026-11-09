namespace PowerMode.Core.Presentation;

/// <summary>
/// One notification-area icon as Windows 11 (22H2+) records it under
/// <c>HKCU\Control Panel\NotifyIconSettings\&lt;Key&gt;</c>.
/// </summary>
/// <param name="Key">The subkey name.</param>
/// <param name="ExecutablePath">The icon's exe; may start with a known-folder GUID, e.g. <c>{F1B32785-…}\Programs\…</c>.</param>
/// <param name="IsPromoted">1 = shown on the taskbar, 0 = in the overflow, null = not set yet.</param>
public sealed record NotifyIconEntry(string Key, string? ExecutablePath, int? IsPromoted);

public enum TrayPromotionAction
{
    /// <summary>Already done for this exe path once: leave the user's choice alone.</summary>
    Skip,

    /// <summary>This Windows keeps no per-icon settings: tell the user once to drag the icon onto the taskbar.</summary>
    Unsupported,

    /// <summary>Our entry does not exist yet (Windows writes it shortly after the icon appears): look again later.</summary>
    NotFound,

    /// <summary>Our icon is already on the taskbar: just remember that.</summary>
    AlreadyPromoted,

    /// <summary>Set <c>IsPromoted = 1</c> on <see cref="TrayPromotionDecision.Keys"/>.</summary>
    Promote,
}

public sealed record TrayPromotionDecision(TrayPromotionAction Action, IReadOnlyList<string> Keys)
{
    public static TrayPromotionDecision Of(TrayPromotionAction action) => new(action, []);
}

/// <summary>
/// Windows 11 puts a new tray icon into the overflow, so a running PowerMode is invisible. On the first start of an
/// exe path its own icon entries are promoted onto the taskbar, once; after that the user decides.
/// </summary>
public static class TrayPromotion
{
    public const string Hint = "Drag the PowerMode icon onto the taskbar to keep it visible.";

    /// <param name="exePath">This process's exe.</param>
    /// <param name="promotedFor">The exe path already handled (from state.json), if any.</param>
    /// <param name="entries">The icon entries; null when the NotifyIconSettings key does not exist.</param>
    /// <param name="knownFolder">The path of a known folder by its GUID, or null when unknown.</param>
    public static TrayPromotionDecision Decide(string exePath, string? promotedFor, IReadOnlyList<NotifyIconEntry>? entries, Func<Guid, string?> knownFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        ArgumentNullException.ThrowIfNull(knownFolder);
        if (promotedFor is not null && SamePath(promotedFor, exePath))
        {
            return TrayPromotionDecision.Of(TrayPromotionAction.Skip);
        }

        if (entries is null)
        {
            return TrayPromotionDecision.Of(TrayPromotionAction.Unsupported);
        }

        var ours = entries.Where(e => Expand(e.ExecutablePath, knownFolder) is { } path && SamePath(path, exePath)).ToList();
        if (ours.Count == 0)
        {
            return TrayPromotionDecision.Of(TrayPromotionAction.NotFound);
        }

        var hidden = ours.Where(e => e.IsPromoted != 1).Select(e => e.Key).ToList();
        return hidden.Count == 0
            ? TrayPromotionDecision.Of(TrayPromotionAction.AlreadyPromoted)
            : new TrayPromotionDecision(TrayPromotionAction.Promote, hidden);
    }

    /// <summary>Replace a leading known-folder GUID (<c>{GUID}\rest</c>) with that folder's path; null when it cannot be resolved.</summary>
    public static string? Expand(string? path, Func<Guid, string?> knownFolder)
    {
        ArgumentNullException.ThrowIfNull(knownFolder);
        if (string.IsNullOrEmpty(path) || !path.StartsWith('{'))
        {
            return path;
        }

        var end = path.IndexOf('}', StringComparison.Ordinal);
        if (end < 0 || !Guid.TryParse(path.AsSpan(0, end + 1), out var id) || knownFolder(id) is not { } folder)
        {
            return null;
        }

        return folder.TrimEnd('\\') + path[(end + 1)..];
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('/', '\\').Trim();
}
