using PowerMode.Core.Levers;
using PowerMode.Core.State;

namespace PowerMode.Core.Switching;

public enum StartAction
{
    /// <summary>Grid with nothing owed: leave everything as it is.</summary>
    Nothing,

    /// <summary>Apply the saved mode again (the GPU limit and closed apps do not survive a reboot), or finish an owed Grid restore.</summary>
    Reapply,

    /// <summary><c>startInGrid</c>: a saved Station or Reserve is not applied again; Grid restores the baseline.</summary>
    RestoreGrid,
}

/// <summary>What happens at start and at Windows shutdown, apart from the Windows calls.</summary>
public static class StartAndShutdown
{
    /// <summary>
    /// The levers a shutdown restores: the ones that would otherwise greet the next start with limits. Docker, app
    /// relaunch, services, efficiency mode and Wallpaper Engine are pointless at shutdown and stay owed.
    /// </summary>
    public static IReadOnlyList<LeverKind> ShutdownLevers { get; } =
        [LeverKind.PowerScheme, LeverKind.DisplayMode, LeverKind.Brightness, LeverKind.Gpu, LeverKind.SecondaryDisplays];

    /// <summary>The whole shutdown restore must finish within this (Windows waits about as long before it complains).</summary>
    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(5);

    public static StartAction Decide(PersistedState state, bool startInGrid)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Mode == PowerModeKind.Grid)
        {
            return state.Snapshot is null ? StartAction.Nothing : StartAction.Reapply;
        }

        return startInGrid ? StartAction.RestoreGrid : StartAction.Reapply;
    }

    /// <summary>Restore Grid at shutdown only from a saving mode, and only when the config asks for it.</summary>
    public static bool RestoresAtShutdown(PowerModeKind mode, bool gridOnShutdown) => gridOnShutdown && mode != PowerModeKind.Grid;
}
