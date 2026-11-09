using PowerMode.Core.Model;
using PowerMode.Core.Power;

namespace PowerMode.Core.Config;

public sealed class HotkeyConfig
{
    public string Grid { get; set; } = "Ctrl+Alt+1";
    public string Station { get; set; } = "Ctrl+Alt+2";
    public string Reserve { get; set; } = "Ctrl+Alt+3";

    public string For(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Grid => Grid,
        PowerModeKind.Station => Station,
        PowerModeKind.Reserve => Reserve,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

public enum FlyoutAnchor
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

public sealed class FlyoutConfig
{
    public FlyoutAnchor Anchor { get; set; } = FlyoutAnchor.BottomRight;
    public int Margin { get; set; } = 12;
}

/// <summary>Values written into one of the two PowerMode power schemes on every apply.</summary>
public sealed class SchemeConfig
{
    public Guid Id { get; set; }
    public Guid SourceGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Boost { get; set; }
    public int MaxProcessorPercent { get; set; } = 100;
    public int MinProcessorPercent { get; set; } = 5;
    public int DisplayOffSeconds { get; set; }
    public int SleepSeconds { get; set; }
    public int HibernateSeconds { get; set; }
}

/// <summary>Lever values of a saving mode. <c>null</c> numbers mean "leave this lever alone".</summary>
public sealed class ModeConfig
{
    public SchemeConfig Scheme { get; set; } = new();
    public int? BrightnessPercent { get; set; }
    public int? RefreshRateHz { get; set; }
    public int? GpuLimitWatts { get; set; }
    public bool PauseWallpaperEngine { get; set; }
    public bool StopDocker { get; set; }

    /// <summary>Turn every display but the primary off (display topology "primary only"); Grid turns them back on.</summary>
    public bool SecondaryDisplaysOff { get; set; }
    public List<string> CloseApps { get; set; } = [];
    public List<string> EfficiencyApps { get; set; } = [];
    public List<string> StopServices { get; set; } = [];
}

public sealed class ModesConfig
{
    public ModeConfig Station { get; set; } = new();
    public ModeConfig Reserve { get; set; } = new();

    /// <summary>The saving config of a mode; Grid has none (it restores the snapshot).</summary>
    public ModeConfig? For(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Station => Station,
        PowerModeKind.Reserve => Reserve,
        _ => null,
    };
}

public sealed class DockerConfig
{
    /// <summary>Containers Grid never starts again, even if they ran in the snapshot.</summary>
    public List<string> ExcludedContainers { get; set; } = [];

    /// <summary>How long Grid waits for the engine after <c>docker desktop start</c>.</summary>
    public int EngineWaitSeconds { get; set; } = 120;
}

public sealed class WallpaperEngineConfig
{
    public string Path { get; set; } = @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe";
}

public sealed class AppConfig
{
    public HotkeyConfig Hotkeys { get; set; } = new();
    public FlyoutConfig Flyout { get; set; } = new();
    public ModesConfig Modes { get; set; } = new();

    /// <summary>Process name → arguments used when Grid relaunches it (instead of the captured ones).</summary>
    public Dictionary<string, string> RelaunchArguments { get; set; } = [];

    public DockerConfig Docker { get; set; } = new();
    public WallpaperEngineConfig WallpaperEngine { get; set; } = new();

    /// <summary>Coefficients of the total-power estimate shown in the flyout and the tooltip.</summary>
    public PowerConfig Power { get; set; } = new();

    /// <summary>Re-apply brightness/refresh when Windows changes display settings (at most once a minute).</summary>
    public bool ReapplyOnDisplayChange { get; set; } = true;

    /// <summary>When Windows shuts down or signs out in Station or Reserve, restore Grid first (scheme, displays, GPU limit), so the next start has no limits.</summary>
    public bool GridOnShutdown { get; set; } = true;

    /// <summary>A start that finds Station or Reserve saved restores Grid instead of applying that mode again.</summary>
    public bool StartInGrid { get; set; } = true;

    /// <summary>
    /// The scheme Grid activates when it does not know which one was active before (the read failed, or the state was
    /// lost while a PowerMode scheme was active). Must not be one of the two PowerMode schemes.
    /// </summary>
    public Guid FallbackScheme { get; set; } = PowerSchemes.Balanced;

    /// <summary>Union of every close list (what the snapshot records).</summary>
    public IReadOnlyList<string> AllCloseApps() => Union(Modes.Station.CloseApps, Modes.Reserve.CloseApps);

    /// <summary>Union of every service list (what the snapshot records).</summary>
    public IReadOnlyList<string> AllServices() => Union(Modes.Station.StopServices, Modes.Reserve.StopServices);

    /// <summary>Union of every efficiency list.</summary>
    public IReadOnlyList<string> AllEfficiencyApps() => Union(Modes.Station.EfficiencyApps, Modes.Reserve.EfficiencyApps);

    public string? RelaunchArgumentsFor(string processName) =>
        RelaunchArguments.FirstOrDefault(pair => string.Equals(pair.Key, processName, StringComparison.OrdinalIgnoreCase)).Value;

    private static string[] Union(IEnumerable<string> first, IEnumerable<string> second) =>
        [.. first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The defaults of SPEC section 3, tuned to the owner's PC.</summary>
    public static AppConfig CreateDefault()
    {
        string[] stationClose = ["steam", "EPSDNMON"];
        return new AppConfig
        {
            Modes = new ModesConfig
            {
                Station = new ModeConfig
                {
                    Scheme = new SchemeConfig
                    {
                        Id = PowerSchemes.Station,
                        SourceGuid = PowerSchemes.Balanced,
                        Name = PowerSchemes.StationName,
                        Boost = false,
                        MaxProcessorPercent = 80,
                        MinProcessorPercent = 5,
                        DisplayOffSeconds = 300,
                        SleepSeconds = 0,
                        HibernateSeconds = 1800,
                    },
                    BrightnessPercent = 35,
                    RefreshRateHz = 60,
                    GpuLimitWatts = 90,
                    PauseWallpaperEngine = true,
                    StopDocker = true,
                    CloseApps = [.. stationClose],
                    EfficiencyApps = [],
                    StopServices = ["WSearch"],
                },
                Reserve = new ModeConfig
                {
                    Scheme = new SchemeConfig
                    {
                        Id = PowerSchemes.Reserve,
                        SourceGuid = PowerSchemes.PowerSaver,
                        Name = PowerSchemes.ReserveName,
                        Boost = false,
                        MaxProcessorPercent = 50,
                        MinProcessorPercent = 5,
                        DisplayOffSeconds = 120,
                        SleepSeconds = 0,
                        HibernateSeconds = 600,
                    },
                    BrightnessPercent = 10,
                    RefreshRateHz = 60,
                    GpuLimitWatts = 60,
                    PauseWallpaperEngine = true,
                    StopDocker = true,
                    CloseApps = [.. stationClose, "ChatGPT", "OneDrive", "Viber", "ms-teams", "Postman Agent"],
                    EfficiencyApps = ["Slack", "Telegram", "msedgewebview2", "PhoneExperienceHost", "CrossDeviceService"],
                    StopServices = ["WSearch", "SysMain", "DiagTrack", "Spooler", "MongoDB", "MySQL80", "postgresql-x64-18"],
                    SecondaryDisplaysOff = true,
                },
            },
            RelaunchArguments = new Dictionary<string, string>
            {
                ["steam"] = "-silent",
                ["OneDrive"] = "/background",
            },
        };
    }
}
