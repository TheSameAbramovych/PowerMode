namespace PowerMode.Core.Presentation;

/// <summary>Readable names for the process and service names in the config; unknown names are shown as-is.</summary>
public static class FriendlyNames
{
    private static readonly Dictionary<string, string> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steam"] = "Steam",
        ["ms-teams"] = "Teams",
        ["msedgewebview2"] = "Edge WebView2",
        ["PhoneExperienceHost"] = "Phone Link",
        ["CrossDeviceService"] = "Cross Device",
    };

    private static readonly Dictionary<string, string> Services = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WSearch"] = "Search",
        ["DiagTrack"] = "Telemetry",
        ["Spooler"] = "Print Spooler",
    };

    public static string Process(string name) => Processes.GetValueOrDefault(name, name);

    public static string Service(string name) => Services.GetValueOrDefault(name, name);

    public static string ProcessList(IEnumerable<string> names) => string.Join(", ", names.Select(Process));

    public static string ServiceList(IEnumerable<string> names) => string.Join(", ", names.Select(Service));

    public static string Lever(Levers.LeverKind kind) => kind switch
    {
        Levers.LeverKind.PowerScheme => "Power scheme",
        Levers.LeverKind.DisplayMode => "Refresh rate",
        Levers.LeverKind.Brightness => "Brightness",
        Levers.LeverKind.Gpu => "GPU limit",
        Levers.LeverKind.WallpaperEngine => "Wallpaper Engine",
        Levers.LeverKind.Docker => "Docker",
        Levers.LeverKind.Apps => "Apps",
        Levers.LeverKind.Efficiency => "Efficiency mode",
        Levers.LeverKind.Services => "Services",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
