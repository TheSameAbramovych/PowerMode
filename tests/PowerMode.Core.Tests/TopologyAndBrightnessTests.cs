using PowerMode.Core.Config;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.Presentation;

namespace PowerMode.Core.Tests;

public sealed class PrimaryOnlyTests
{
    [Fact]
    public void Keeps_the_primary_paths_even_when_the_primary_is_second()
    {
        PathSource[] paths = [new(@"\\.\DISPLAY2", false), new(@"\\.\DISPLAY1", true)];

        Assert.Equal([1], TopologyRules.PrimaryPaths(paths, @"\\.\display1"));
    }

    [Fact]
    public void A_cloned_primary_keeps_its_clones()
    {
        PathSource[] paths = [new(@"\\.\DISPLAY1", true), new(@"\\.\DISPLAY2", false), new(@"\\.\DISPLAY1", true)];

        Assert.Equal([0, 2], TopologyRules.PrimaryPaths(paths, @"\\.\DISPLAY1"));
    }

    [Fact]
    public void Without_a_name_match_the_source_at_the_origin_is_the_primary_otherwise_nothing()
    {
        PathSource[] paths = [new(null, false), new(@"\\.\DISPLAY3", true)];

        Assert.Equal([1], TopologyRules.PrimaryPaths(paths, @"\\.\DISPLAY9"));
        Assert.Equal([1], TopologyRules.PrimaryPaths(paths, null));
        Assert.Empty(TopologyRules.PrimaryPaths([new(@"\\.\DISPLAY2", false)], @"\\.\DISPLAY1"));
    }

    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(11u, true)]
    [InlineData(13u, true)]
    [InlineData(5u, false)]
    [InlineData(10u, false)]
    [InlineData(6u, false)]
    public void Built_in_panels_are_internal_or_embedded_outputs(uint technology, bool isInternal) =>
        Assert.Equal(isInternal, TopologyRules.IsInternalOutput(technology));
}

public sealed class TopologyRemapTests
{
    private const long OldA = 54904;
    private const long OldB = 61000;
    private const long NewA = 71234;
    private const long NewB = 72000;

    private static (byte[] Paths, byte[] Modes) Layout()
    {
        var paths = new byte[2 * TopologyRules.PathSize];
        var modes = new byte[1 * TopologyRules.ModeSize];
        BitConverter.GetBytes(OldA).CopyTo(paths, 0);
        BitConverter.GetBytes(OldA).CopyTo(paths, 20);
        BitConverter.GetBytes(OldB).CopyTo(paths, TopologyRules.PathSize);
        BitConverter.GetBytes(OldB).CopyTo(paths, TopologyRules.PathSize + 20);
        BitConverter.GetBytes(OldB).CopyTo(modes, 8);
        return (paths, modes);
    }

    [Fact]
    public void Saved_luids_move_to_the_current_boot_by_device_path()
    {
        var (paths, modes) = Layout();
        Assert.Equal([OldA, OldB], TopologyRules.Adapters(paths, 2, modes, 1));
        var map = TopologyRules.AdapterMap(
            [new DisplayAdapter(OldA, @"\\?\PCI#A"), new DisplayAdapter(OldB, @"\\?\PCI#B")],
            new Dictionary<string, long> { [@"\\?\PCI#A"] = NewA, [@"\\?\PCI#B"] = NewB })!;

        Assert.True(TopologyRules.RemapAdapters(paths, 2, modes, 1, map));

        Assert.Equal([NewA, NewB], TopologyRules.Adapters(paths, 2, modes, 1));
        Assert.Equal(NewA, BitConverter.ToInt64(paths, 20));
        Assert.Equal(NewB, BitConverter.ToInt64(modes, 8));
    }

    [Fact]
    public void A_missing_adapter_leaves_the_layout_unchanged()
    {
        Assert.Null(TopologyRules.AdapterMap([new DisplayAdapter(OldA, @"\\?\PCI#A")], new Dictionary<string, long>()));

        var (paths, modes) = Layout();
        Assert.False(TopologyRules.RemapAdapters(paths, 2, modes, 1, new Dictionary<long, long> { [OldA] = NewA }));
        Assert.Equal([OldA, OldB], TopologyRules.Adapters(paths, 2, modes, 1));
    }

    [Fact]
    public void An_older_topology_without_adapters_still_reads()
    {
        var topology = new DisplayTopology(2, "AAAA");

        Assert.Null(topology.Adapters);
    }
}

public sealed class BrightnessBaselineTests
{
    private const string A = @"\\.\DISPLAY1#0";
    private const string B = @"\\.\DISPLAY2#0";

    [Fact]
    public void Grid_fails_while_a_saved_monitor_that_is_connected_stays_silent()
    {
        var grid = new MonitorTarget(null, [new MonitorLevel(A, 80), new MonitorLevel(B, 70)]);

        Assert.Equal([B], DisplayDecisions.SilentMonitors(grid, [A, B], [A]));
        Assert.Empty(DisplayDecisions.SilentMonitors(grid, [A, B], [A, B]));
    }

    [Fact]
    public void An_unplugged_saved_monitor_or_a_silent_one_outside_the_baseline_is_ignored()
    {
        var grid = new MonitorTarget(null, [new MonitorLevel(A, 80), new MonitorLevel(B, 70)]);
        Assert.Empty(DisplayDecisions.SilentMonitors(grid, [A], [A]));

        var station = new MonitorTarget(35, [], [A]);
        Assert.Empty(DisplayDecisions.SilentMonitors(station, [A, B], [A]));
        Assert.Equal([A], DisplayDecisions.SilentMonitors(station, [A, B], [B]));
        Assert.Empty(DisplayDecisions.SilentMonitors(new MonitorTarget(35, []), [A, B], []));
    }

    [Fact]
    public void A_saving_mode_dims_only_monitors_grid_can_put_back()
    {
        var station = new MonitorTarget(35, [], [A]);

        Assert.True(DisplayDecisions.Dims(station, A));
        Assert.False(DisplayDecisions.Dims(station, B));
        Assert.True(DisplayDecisions.Dims(new MonitorTarget(35, []), B));
    }

    [Fact]
    public void The_planner_hands_the_baseline_ids_to_a_saving_mode()
    {
        var config = AppConfig.CreateDefault();
        var snapshot = new Snapshot { Monitors = [new MonitorLevel(A, 80)] };

        var target = TargetPlanner.Plan(PowerModeKind.Station, config, snapshot).Monitor!;

        Assert.Equal([A], target.Baseline);
        Assert.Null(TargetPlanner.Plan(PowerModeKind.Station, config, null).Monitor!.Baseline);
    }

    [Fact]
    public void A_second_read_adds_monitors_that_answered_only_then_and_keeps_the_first_values()
    {
        var merged = DisplayDecisions.MergeReads([new MonitorLevel(A, 80)], [new MonitorLevel(A, 79), new MonitorLevel(B, 70, 50)]);

        Assert.Equal([new MonitorLevel(A, 80), new MonitorLevel(B, 70, 50)], merged);
    }
}

public sealed class LiveMonitorsTests
{
    private static readonly DisplayReading External = new("M27Q2", @"\\.\DISPLAY1", false, 60);
    private static readonly DisplayReading Panel = new("Built-in", @"\\.\DISPLAY2", true, 120);
    private static readonly DisplayReading Off = new("T24i-30", null, false, null);

    [Fact]
    public void Before_the_first_read_the_fallback_is_assumed_for_every_display_that_is_on()
    {
        var monitors = LiveMonitors.Merge([External, Panel, Off], null, null, 35);

        Assert.Equal(
            [new MonitorLive("M27Q2", 35, 60), new MonitorLive("Built-in", 35, 120), new MonitorLive("T24i-30", null, null, Active: false)],
            monitors);
    }

    [Fact]
    public void The_panel_value_goes_to_the_built_in_display_only()
    {
        var ddc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var monitors = LiveMonitors.Merge([External, Panel], ddc, 70, 35);

        Assert.Equal([null, 70], monitors.Select(m => m.BrightnessPercent));
        Assert.Equal([(int?)null], LiveMonitors.Merge([External], ddc, 70, 35).Select(m => m.BrightnessPercent));
    }

    [Fact]
    public void Ddc_values_follow_the_device_and_a_display_that_turned_on_keeps_null()
    {
        var ddc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [@"\\.\display1"] = 80, [@"\\.\DISPLAY2"] = 40 };
        var turnedOn = new DisplayReading("T24i-30", @"\\.\DISPLAY3", false, 75);

        var monitors = LiveMonitors.Merge([turnedOn, External, Panel], ddc, 70, 35);

        Assert.Equal([new MonitorLive("T24i-30", null, 75), new MonitorLive("M27Q2", 80, 60), new MonitorLive("Built-in", 40, 120)], monitors);
    }

    [Fact]
    public void The_fallback_is_the_mode_brightness_and_unknown_in_grid()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(config.Modes.Station.BrightnessPercent, LiveMonitors.Fallback(config, PowerModeKind.Station));
        Assert.Null(LiveMonitors.Fallback(config, PowerModeKind.Grid));
    }
}
