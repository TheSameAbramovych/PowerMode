using PowerMode.Core.Logging;

namespace PowerMode.Core.Switching;

/// <summary>
/// The tray's switch requests (flyout, hotkeys, tray menu, forwarded <c>--mode</c>, resume). One switch runs at a
/// time; while it runs, the latest request waits and runs next. Quit and "Restart as administrator" wait for the
/// running switch so state.json is consistent. Once a quit has started, every other request is ignored, so
/// "Switch to Grid and quit" always ends in Grid. Used from one (UI) thread.
/// </summary>
public sealed class SwitchScheduler
{
    private const string Category = "tray";

    private readonly Func<PowerModeKind, Task> _runSwitch;
    private readonly Action _shutdown;
    private readonly Action _restartElevated;
    private readonly ILog _log;
    private PowerModeKind? _pending;
    private bool _restartAfterSwitch;

    /// <param name="runSwitch">Runs one switch; must not throw.</param>
    /// <param name="shutdown">Ends the app; called once.</param>
    /// <param name="restartElevated">Starts the elevated copy (which may be cancelled at the UAC prompt).</param>
    /// <param name="log">The app log.</param>
    public SwitchScheduler(Func<PowerModeKind, Task> runSwitch, Action shutdown, Action restartElevated, ILog log)
    {
        _runSwitch = runSwitch;
        _shutdown = shutdown;
        _restartElevated = restartElevated;
        _log = log;
    }

    public bool IsSwitching { get; private set; }

    public bool IsQuitting { get; private set; }

    /// <summary>Apply a mode now, or after the running switch (the latest request wins). Ignored once quitting.</summary>
    public async Task RequestAsync(PowerModeKind mode)
    {
        if (IsQuitting)
        {
            _log.Write(Category, $"Ignoring {ModeNames.Display(mode)}: quitting");
            return;
        }

        if (IsSwitching)
        {
            _pending = mode;
            return;
        }

        await RunAsync(mode).ConfigureAwait(true);
    }

    /// <summary>Quit, optionally switching to Grid first. A running switch finishes first; then Grid (if asked) runs and the app ends.</summary>
    public async Task QuitAsync(bool toGrid)
    {
        if (IsQuitting)
        {
            return;
        }

        IsQuitting = true;
        if (IsSwitching)
        {
            // A queued request is dropped: quitting wins.
            _pending = toGrid ? PowerModeKind.Grid : null;
            return;
        }

        if (toGrid)
        {
            await RunAsync(PowerModeKind.Grid).ConfigureAwait(true);
            return;
        }

        _shutdown();
    }

    /// <summary>Restart as administrator now, or once the running switch (and anything queued) has finished.</summary>
    public void RestartElevated()
    {
        if (IsSwitching)
        {
            _restartAfterSwitch = true;
            return;
        }

        _restartElevated();
    }

    private async Task RunAsync(PowerModeKind mode)
    {
        IsSwitching = true;
        try
        {
            PowerModeKind? next = mode;
            while (next is { } current)
            {
                _pending = null;
                await _runSwitch(current).ConfigureAwait(true);
                next = _pending;
            }
        }
        finally
        {
            IsSwitching = false;
        }

        if (IsQuitting)
        {
            _shutdown();
            return;
        }

        if (_restartAfterSwitch)
        {
            _restartAfterSwitch = false;
            _restartElevated();
        }
    }
}
