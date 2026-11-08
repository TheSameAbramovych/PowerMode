namespace PowerMode.Core;

/// <summary>The three situations the PC can be in. Names are situations, not speeds.</summary>
public enum PowerModeKind
{
    Grid,
    Station,
    Reserve,
}

public static class ModeNames
{
    public const string GridKey = "grid";
    public const string StationKey = "station";
    public const string ReserveKey = "reserve";

    public static IReadOnlyList<PowerModeKind> All { get; } = [PowerModeKind.Grid, PowerModeKind.Station, PowerModeKind.Reserve];

    public static string Display(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Grid => "Grid",
        PowerModeKind.Station => "Station",
        PowerModeKind.Reserve => "Reserve",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string Key(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Grid => GridKey,
        PowerModeKind.Station => StationKey,
        PowerModeKind.Reserve => ReserveKey,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static bool TryParse(string? text, out PowerModeKind mode)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(text?.Trim(), Key(candidate), StringComparison.OrdinalIgnoreCase))
            {
                mode = candidate;
                return true;
            }
        }

        mode = PowerModeKind.Grid;
        return false;
    }
}
