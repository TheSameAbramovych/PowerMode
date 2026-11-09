namespace PowerMode.Core.Levers;

/// <summary>One lever implementation each. Several levers can feed one UI row (see <see cref="LeverRows"/>).</summary>
public enum LeverKind
{
    PowerScheme,
    DisplayMode,
    Brightness,
    Gpu,
    WallpaperEngine,
    Docker,
    Apps,
    Efficiency,
    Services,
    SecondaryDisplays,
}

/// <summary>The rows of the flyout lever table, in their fixed order.</summary>
public enum LeverRow
{
    Cpu,
    Gpu,
    Monitor,
    Display,
    Docker,
    Apps,
    Efficiency,
    Services,
}

public static class LeverRows
{
    public static IReadOnlyList<LeverRow> Ordered { get; } =
        [LeverRow.Cpu, LeverRow.Gpu, LeverRow.Monitor, LeverRow.Display, LeverRow.Docker, LeverRow.Apps, LeverRow.Efficiency, LeverRow.Services];

    public static string Title(LeverRow row) => row switch
    {
        LeverRow.Cpu => "CPU",
        LeverRow.Gpu => "GPU",
        LeverRow.Monitor => "Monitor",
        LeverRow.Display => "Display",
        LeverRow.Docker => "Docker",
        LeverRow.Apps => "Apps",
        LeverRow.Efficiency => "Efficiency",
        LeverRow.Services => "Services",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    /// <summary>Which levers decide the status glyph of a row.</summary>
    public static IReadOnlyList<LeverKind> LeversOf(LeverRow row) => row switch
    {
        LeverRow.Cpu => [LeverKind.PowerScheme],
        LeverRow.Gpu => [LeverKind.Gpu],
        LeverRow.Monitor => [LeverKind.Brightness, LeverKind.DisplayMode, LeverKind.SecondaryDisplays],
        LeverRow.Display => [LeverKind.PowerScheme],
        LeverRow.Docker => [LeverKind.Docker],
        LeverRow.Apps => [LeverKind.Apps, LeverKind.WallpaperEngine],
        LeverRow.Efficiency => [LeverKind.Efficiency],
        LeverRow.Services => [LeverKind.Services],
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    /// <summary>Short noun used in the elevation banner ("GPU limit and services need ...").</summary>
    public static string ElevationNoun(LeverKind kind) => kind switch
    {
        LeverKind.Gpu => "GPU limit",
        LeverKind.Services => "services",
        LeverKind.PowerScheme => "power scheme",
        LeverKind.DisplayMode => "refresh rate",
        LeverKind.Brightness => "brightness",
        LeverKind.WallpaperEngine => "Wallpaper Engine",
        LeverKind.Docker => "Docker",
        LeverKind.Apps => "apps",
        LeverKind.Efficiency => "efficiency mode",
        LeverKind.SecondaryDisplays => "secondary displays",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
