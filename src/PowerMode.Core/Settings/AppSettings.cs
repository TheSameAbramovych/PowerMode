using System.Buffers;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Presentation;

namespace PowerMode.Core.Settings;

/// <summary>What a saving mode does with one app: nothing, efficiency mode (<c>efficiencyApps</c>), or close it (<c>closeApps</c>).</summary>
public enum AppChoice
{
    Keep,
    Efficiency,
    Close,
}

/// <summary>A process running in the user's session, as the App sees it.</summary>
/// <param name="Name">Process name without <c>.exe</c>.</param>
/// <param name="HasWindow">It owns a visible top-level window.</param>
/// <param name="IsSystem">Its image lives under the Windows folder (or it could not be read): never listed.</param>
public sealed record RunningApp(string Name, bool HasWindow, bool IsSystem);

/// <summary>One row of the Apps settings page.</summary>
public sealed record AppRow(string Name, AppChoice Station, AppChoice Reserve)
{
    public string DisplayName => FriendlyNames.Process(Name);

    public string FileName => Name + ".exe";

    public AppChoice For(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Station => Station,
        PowerModeKind.Reserve => Reserve,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

/// <summary>
/// The Apps settings page: app rows plus Wallpaper Engine (Keep / Pause) per saving mode; that row is shown only when
/// <see cref="WallpaperEngineInstalled"/>, like the lever itself.
/// </summary>
public sealed record AppSettingsPage(IReadOnlyList<AppRow> Rows, bool PauseWallpaperStation, bool PauseWallpaperReserve, bool WallpaperEngineInstalled = true)
{
    public bool PausesWallpaper(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Station => PauseWallpaperStation,
        PowerModeKind.Reserve => PauseWallpaperReserve,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

/// <summary>
/// The decision logic of the Apps settings page: which rows it shows, how a choice changes the config, and which
/// levers a change re-applies at once. The WPF page only binds to this.
/// </summary>
public static class AppSettings
{
    public static IReadOnlyList<PowerModeKind> SavingModes { get; } = [PowerModeKind.Station, PowerModeKind.Reserve];

    /// <summary>Common background apps, listed while they run even without a window.</summary>
    public static IReadOnlyList<string> Catalogue { get; } =
    [
        "steam", "discord", "spotify", "Telegram", "Slack", "Viber", "ms-teams", "ChatGPT", "OneDrive", "Postman Agent",
        "EPSDNMON", "EpicGamesLauncher", "Battle.net", "chrome", "msedge", "msedgewebview2", "PhoneExperienceHost",
        "CrossDeviceService", "Code", "idea64", "rider64", "webstorm64", "Zoom",
    ];

    /// <summary>Shell and system processes that own windows but are never an app to close (besides anything under the Windows folder).</summary>
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PowerMode", "explorer", "ApplicationFrameHost", "SystemSettings", "TextInputHost", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp", "Widgets", "WidgetService", "ShellHost",
        "dwm", "csrss", "winlogon", "sihost", "ctfmon", "conhost", "RuntimeBroker", "taskhostw", "fontdrvhost",
        "svchost", "System", "Idle", "Registry", "smss", "services", "lsass", "wininit", "dllhost", "audiodg",
        "SecurityHealthSystray", "Taskmgr", "UserOOBEBroker", "GameBar", "GameBarFTServer",
    };

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("\\/:*?\"<>|");

    /// <summary>
    /// Rows: every name in a close or efficiency list, plus running apps that have a window or are in the catalogue
    /// (never system processes), plus names typed into "Add process…" while the page is open. Rows with a non-Keep
    /// choice first, then alphabetical by the shown name.
    /// </summary>
    public static AppSettingsPage Build(AppConfig config, IEnumerable<RunningApp> running, IEnumerable<string>? added = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(running);
        var names = new List<string>();
        void Add(string name)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        foreach (var mode in SavingModes)
        {
            var m = config.Modes.For(mode)!;
            m.CloseApps.ForEach(Add);
            m.EfficiencyApps.ForEach(Add);
        }

        foreach (var app in running.Where(a => !a.IsSystem && !SystemProcesses.Contains(a.Name) && (a.HasWindow || IsCatalogued(a.Name))))
        {
            Add(app.Name);
        }

        foreach (var name in added ?? [])
        {
            Add(name);
        }

        var rows = names
            .Select(n => new AppRow(n, ChoiceOf(config.Modes.Station, n), ChoiceOf(config.Modes.Reserve, n)))
            .OrderBy(r => r.Station == AppChoice.Keep && r.Reserve == AppChoice.Keep)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AppSettingsPage(rows, config.Modes.Station.PauseWallpaperEngine, config.Modes.Reserve.PauseWallpaperEngine);
    }

    public static bool IsCatalogued(string name) => Catalogue.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static AppChoice ChoiceOf(ModeConfig mode, string name)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return Contains(mode.CloseApps, name) ? AppChoice.Close
            : Contains(mode.EfficiencyApps, name) ? AppChoice.Efficiency
            : AppChoice.Keep;
    }

    /// <summary>
    /// Put the app in exactly one list of the mode (or none for Keep). The spelling already in the config is kept, so
    /// moving "steam" never adds a second "Steam".
    /// </summary>
    public static void SetChoice(ModeConfig mode, string name, AppChoice choice)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var spelling = mode.CloseApps.Concat(mode.EfficiencyApps).FirstOrDefault(n => Same(n, name)) ?? name;
        mode.CloseApps.RemoveAll(n => Same(n, name));
        mode.EfficiencyApps.RemoveAll(n => Same(n, name));
        switch (choice)
        {
            case AppChoice.Close:
                mode.CloseApps.Add(spelling);
                break;
            case AppChoice.Efficiency:
                mode.EfficiencyApps.Add(spelling);
                break;
            case AppChoice.Keep:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, null);
        }
    }

    /// <summary>
    /// The levers to re-apply at once after an app's choice for <paramref name="changed"/> went from
    /// <paramref name="from"/> to <paramref name="to"/>. Nothing unless that mode is the current one (other modes use
    /// the new lists on their next apply).
    /// <list type="bullet">
    /// <item>to Keep: Apps relaunches it if PowerMode closed it; Efficiency clears EcoQoS if PowerMode set it.</item>
    /// <item>to Close: Apps closes it now and records it, so Grid relaunches it; Efficiency drops a previous EcoQoS.</item>
    /// <item>to Efficiency: EcoQoS on it now; an app PowerMode closed stays closed (Apps is not re-applied) until the next apply.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<LeverKind> ImmediateLevers(PowerModeKind current, PowerModeKind changed, AppChoice from, AppChoice to)
    {
        if (current != changed || current == PowerModeKind.Grid || from == to)
        {
            return [];
        }

        return to == AppChoice.Efficiency ? [LeverKind.Efficiency] : [LeverKind.Apps, LeverKind.Efficiency];
    }

    /// <summary>Wallpaper Engine Keep/Pause for the current saving mode takes effect at once.</summary>
    public static IReadOnlyList<LeverKind> WallpaperLevers(PowerModeKind current, PowerModeKind changed) =>
        current == changed && current != PowerModeKind.Grid ? [LeverKind.WallpaperEngine] : [];

    /// <summary>A process name typed into "Add process…": trimmed, without <c>.exe</c>, no path, not empty.</summary>
    public static bool TryParseProcessName(string? text, out string name, out string? error)
    {
        name = (text ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4].TrimEnd();
        }

        error = name.Length == 0 ? "Type a process name"
            : name.IndexOfAny(InvalidNameChars) >= 0 ? "A process name, not a path"
            : null;
        return error is null;
    }

    private static bool Contains(IEnumerable<string> names, string name) => names.Any(n => Same(n, name));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The status line of the Apps settings page: <see cref="Saved"/> (muted, next to the title) after every saved
/// change, <see cref="Error"/> (red, under the add field) when a change was not saved or a typed name is invalid.
/// </summary>
public sealed record SettingsFeedback(string? Saved, string? Error)
{
    public static SettingsFeedback None { get; } = new(null, null);

    /// <summary>A change was saved: "Saved 10:31" (local time) replaces any earlier one, and any error goes.</summary>
    public static SettingsFeedback SavedAt(DateTimeOffset localTime) =>
        new($"Saved {localTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}", null);

    /// <summary>A change was not saved: no "Saved" for it, only the error.</summary>
    public static SettingsFeedback NotSaved(string error) => new(null, error);

    /// <summary>A typed name is invalid: the earlier "Saved" still holds.</summary>
    public SettingsFeedback Invalid(string error) => this with { Error = error };

    /// <summary>The input is fine again.</summary>
    public SettingsFeedback Valid() => this with { Error = null };

    /// <summary>
    /// The saved change was applied at once but a lever failed: the reasons, for the warning line; null when nothing
    /// was applied or everything held.
    /// </summary>
    public static string? NotAppliedMessage(Switching.SwitchOutcome? outcome)
    {
        if (outcome is null || outcome.FailedCount == 0)
        {
            return null;
        }

        return NotAppliedMessage(string.Join("; ", outcome.Results.Values.Where(r => r.IsFailure).Select(r => r.Detail).Distinct(StringComparer.Ordinal)));
    }

    /// <summary>The saved change could not be applied at once, for <paramref name="reason"/>.</summary>
    public static string NotAppliedMessage(string reason) => $"Saved, but not applied now: {reason}. The main page shows the lever.";
}
