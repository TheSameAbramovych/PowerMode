using PowerMode.Core.Switching;

namespace PowerMode.Core.Tests;

public sealed class SwitchSchedulerTests
{
    /// <summary>A switch that blocks until released, recording what ran, shutdowns and restarts.</summary>
    private sealed class Harness
    {
        private TaskCompletionSource _gate = new();

        public Harness() => Scheduler = new SwitchScheduler(RunAsync, () => Shutdowns++, () => Restarts++, Log);

        public SwitchScheduler Scheduler { get; }

        public ListLog Log { get; } = new();

        public List<PowerModeKind> Ran { get; } = [];

        public int Shutdowns { get; private set; }

        public int Restarts { get; private set; }

        public bool Block { get; set; }

        public void Release()
        {
            var gate = _gate;
            _gate = new TaskCompletionSource();
            gate.SetResult();
        }

        private async Task RunAsync(PowerModeKind mode)
        {
            Ran.Add(mode);
            if (Block)
            {
                await _gate.Task;
            }
        }
    }

    [Fact]
    public async Task Requests_while_switching_wait_and_the_latest_wins()
    {
        var h = new Harness { Block = true };
        var first = h.Scheduler.RequestAsync(PowerModeKind.Station);
        Assert.True(h.Scheduler.IsSwitching);

        await h.Scheduler.RequestAsync(PowerModeKind.Reserve);
        await h.Scheduler.RequestAsync(PowerModeKind.Grid);
        h.Block = false;
        h.Release();
        await first;

        Assert.Equal([PowerModeKind.Station, PowerModeKind.Grid], h.Ran);
        Assert.False(h.Scheduler.IsSwitching);
        Assert.Equal(0, h.Shutdowns);
    }

    [Fact]
    public async Task Switch_to_grid_and_quit_during_a_switch_ends_in_grid_even_when_more_requests_come()
    {
        var h = new Harness { Block = true };
        var first = h.Scheduler.RequestAsync(PowerModeKind.Station);
        await h.Scheduler.RequestAsync(PowerModeKind.Reserve);

        await h.Scheduler.QuitAsync(toGrid: true);
        await h.Scheduler.RequestAsync(PowerModeKind.Reserve);
        await h.Scheduler.QuitAsync(toGrid: false);
        h.Block = false;
        h.Release();
        await first;

        Assert.Equal([PowerModeKind.Station, PowerModeKind.Grid], h.Ran);
        Assert.Equal(1, h.Shutdowns);
        Assert.Contains(h.Log.Lines, l => l.Contains("Ignoring Reserve: quitting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Quit_keeping_the_mode_during_a_switch_drops_the_queued_request()
    {
        var h = new Harness { Block = true };
        var first = h.Scheduler.RequestAsync(PowerModeKind.Station);
        await h.Scheduler.RequestAsync(PowerModeKind.Reserve);

        await h.Scheduler.QuitAsync(toGrid: false);
        h.Release();
        await first;

        Assert.Equal([PowerModeKind.Station], h.Ran);
        Assert.Equal(1, h.Shutdowns);
    }

    [Fact]
    public async Task Quit_when_idle_switches_to_grid_first_or_ends_at_once()
    {
        var toGrid = new Harness();
        await toGrid.Scheduler.QuitAsync(toGrid: true);
        Assert.Equal([PowerModeKind.Grid], toGrid.Ran);
        Assert.Equal(1, toGrid.Shutdowns);

        var keep = new Harness();
        await keep.Scheduler.QuitAsync(toGrid: false);
        await keep.Scheduler.RequestAsync(PowerModeKind.Station);
        Assert.Empty(keep.Ran);
        Assert.Equal(1, keep.Shutdowns);
        Assert.True(keep.Scheduler.IsQuitting);
    }

    [Fact]
    public async Task Restart_as_administrator_waits_for_the_running_switch()
    {
        var h = new Harness { Block = true };
        var first = h.Scheduler.RequestAsync(PowerModeKind.Station);

        h.Scheduler.RestartElevated();
        Assert.Equal(0, h.Restarts);
        h.Block = false;
        h.Release();
        await first;
        Assert.Equal(1, h.Restarts);

        h.Scheduler.RestartElevated();
        Assert.Equal(2, h.Restarts);
        await h.Scheduler.RequestAsync(PowerModeKind.Grid);
        Assert.Equal(2, h.Restarts);
    }
}
