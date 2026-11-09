using PowerMode.Core.Config;
using PowerMode.Core.Power;
using PowerMode.Core.Presentation;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

public sealed class PowerEstimatorTests
{
    private static readonly PowerConfig Config = new();
    private static readonly PowerInputs Typical = new(20, 3.0, 21, 80, 200);

    [Fact]
    public void Defaults_match_this_pc()
    {
        Assert.Equal(15, PowerEstimator.CpuW(Config, 0, 3.2), 3);
        Assert.Equal(60, PowerEstimator.CpuW(Config, 100, 3.2), 3);
        Assert.Equal(85.3, PowerEstimator.CpuW(Config, 100, 4.0), 1);
        Assert.Equal(15, PowerEstimator.CpuW(Config, null, 3.2));
        Assert.Equal(15, PowerEstimator.CpuW(Config, 50, null));

        Assert.Equal(33, PowerEstimator.MonitorW(Config, 80, 60), 3);
        Assert.Equal(36, PowerEstimator.MonitorW(Config, 80, 200), 3);
        Assert.Equal(19.5, PowerEstimator.MonitorW(Config, 35, 60), 3);
        Assert.Equal(12, PowerEstimator.MonitorW(Config, 10, 60), 3);
        Assert.Equal(PowerEstimator.MonitorW(Config, 50, 60), PowerEstimator.MonitorW(Config, null, 60));
        Assert.Equal(PowerEstimator.MonitorW(Config, 120, 60), PowerEstimator.MonitorW(Config, 100, 120));
    }

    [Fact]
    public void Utility_is_capped_at_125_percent()
    {
        Assert.Equal(PowerEstimator.CpuW(Config, 125, 3.2), PowerEstimator.CpuW(Config, 400, 3.2));
        Assert.Equal(15, PowerEstimator.CpuW(Config, -5, 3.2));
    }

    [Fact]
    public void The_total_rises_with_every_input()
    {
        var baseline = PowerEstimator.Estimate(Config, Typical).TotalW;

        Assert.True(PowerEstimator.Estimate(Config, Typical with { CpuUtilityPercent = 60 }).TotalW > baseline);
        Assert.True(PowerEstimator.Estimate(Config, Typical with { CpuGhz = 3.8 }).TotalW > baseline);
        Assert.True(PowerEstimator.Estimate(Config, Typical with { GpuW = 90 }).TotalW > baseline);
        Assert.True(PowerEstimator.Estimate(Config, Typical with { BrightnessPercent = 100 }).TotalW > baseline);
        Assert.True(PowerEstimator.Estimate(Config, Typical with { RefreshHz = 60 }).TotalW < baseline);
        Assert.True(PowerEstimator.Estimate(new PowerConfig { OtherW = 60 }, Typical).TotalW > baseline);
        Assert.True(PowerEstimator.Estimate(new PowerConfig { PsuEfficiency = 0.8 }, Typical).TotalW > baseline);
    }

    [Fact]
    public void Other_is_what_is_left_of_the_total()
    {
        var calibration = new PowerCalibration(-30, DateTimeOffset.UnixEpoch);
        foreach (var estimate in new[] { PowerEstimator.Estimate(Config, Typical), PowerEstimator.Estimate(Config, Typical with { GpuW = null }, calibration) })
        {
            Assert.Equal(estimate.TotalW - estimate.CpuW - (estimate.GpuW ?? 0) - estimate.MonitorW, estimate.OtherW, 9);
        }

        var plain = PowerEstimator.Estimate(Config, Typical);
        Assert.Equal(21, plain.GpuW);
        Assert.Equal((plain.CpuW + 21 + 40) / 0.88 + plain.MonitorW, plain.TotalW, 9);
        Assert.Null(PowerEstimator.Estimate(Config, Typical with { GpuW = null }).GpuW);
        Assert.Equal(0, PowerEstimator.Estimate(Config, Typical with { GpuW = -3 }).GpuW);
        Assert.Equal(plain.MonitorW + (plain.CpuW + 21 + 40), PowerEstimator.Estimate(new PowerConfig { PsuEfficiency = 0 }, Typical).TotalW, 9);
    }

    [Fact]
    public void Calibration_makes_the_estimate_equal_the_reading_within_the_clamp()
    {
        var at = new DateTimeOffset(2026, 11, 9, 10, 31, 0, TimeSpan.FromHours(2));
        var uncalibrated = PowerEstimator.Estimate(Config, Typical);

        var calibration = PowerEstimator.Calibrate(186, uncalibrated, at);
        Assert.Equal(186, PowerEstimator.Estimate(Config, Typical, calibration).TotalW, 9);
        Assert.Equal(at, calibration.CalibratedAt);

        Assert.Equal(PowerEstimator.MaxOffsetW, PowerEstimator.Calibrate(1900, uncalibrated, at).OffsetW);
        Assert.Equal(PowerEstimator.MinOffsetW, PowerEstimator.Calibrate(20, uncalibrated, at).OffsetW);

        var state = new PersistedState { PowerCalibration = calibration, GridReferenceW = 203.5 };
        var copy = StateStore.Deserialize(StateStore.Serialize(state))!;
        Assert.Equal(calibration, copy.PowerCalibration);
        Assert.Equal(203.5, copy.GridReferenceW);
        Assert.Null(new PersistedState().PowerCalibration);
    }

    [Theory]
    [InlineData("186", 186.0)]
    [InlineData(" 142 W ", 142.0)]
    [InlineData("99,5", 99.5)]
    [InlineData("20", 20.0)]
    [InlineData("2000w", 2000.0)]
    public void Station_readings_are_parsed(string text, double watts)
    {
        Assert.True(PowerEstimator.TryParseReading(text, out var parsed, out var error));
        Assert.Equal(watts, parsed);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, "Type the watts the station shows")]
    [InlineData("", "Type the watts the station shows")]
    [InlineData("abc", "Type the watts the station shows")]
    [InlineData("19", "Between 20 and 2000 W")]
    [InlineData("2001", "Between 20 and 2000 W")]
    public void Bad_readings_are_rejected(string? text, string message)
    {
        Assert.False(PowerEstimator.TryParseReading(text, out _, out var error));
        Assert.Equal(message, error);
    }

    [Fact]
    public void Power_coefficients_are_validated()
    {
        var config = AppConfig.CreateDefault();
        Assert.Empty(ConfigLoader.Validate(config));

        config.Power.PsuEfficiency = 0.2;
        config.Power.CpuBaseGhz = double.NaN;
        var errors = ConfigLoader.Validate(config);
        Assert.Contains(errors, e => e.StartsWith("power.psuEfficiency", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("power.cpuBaseGhz", StringComparison.Ordinal));

        var parsed = ConfigLoader.Parse("""{ "power": { "otherW": 55 } }""");
        Assert.True(parsed.IsValid);
        Assert.Equal(55, parsed.Config!.Power.OtherW);
        Assert.Equal(0.88, parsed.Config.Power.PsuEfficiency);
    }
}

public sealed class PowerHistoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 14, 0, 0, TimeSpan.FromHours(2));

    private static PowerHistory Filled(int seconds, Func<int, double> watts, int step = 2)
    {
        var history = new PowerHistory();
        for (var s = 0; s <= seconds; s += step)
        {
            history.Add(T0.AddSeconds(s), watts(s));
        }

        return history;
    }

    [Fact]
    public void Keeps_ten_minutes_at_most_300_points()
    {
        var history = Filled(1200, s => s);

        Assert.Equal(PowerHistory.Capacity, history.Points.Count);
        Assert.Equal(T0.AddSeconds(1200), history.Points[^1].At);
        Assert.True(history.Points[^1].At - history.Points[0].At <= PowerHistory.Window);

        var sparse = Filled(1200, s => s, step: 30);
        Assert.True(sparse.Points[^1].At - sparse.Points[0].At <= PowerHistory.Window);
    }

    [Fact]
    public void Mean_covers_the_window_and_respects_a_floor()
    {
        var history = Filled(120, s => s < 60 ? 200 : 140);

        Assert.Equal(140, history.Mean(T0.AddSeconds(120), TimeSpan.FromSeconds(58))!.Value, 9);
        Assert.Equal(200, history.Mean(T0.AddSeconds(58), TimeSpan.FromSeconds(60))!.Value, 9);
        Assert.Equal(140, history.Mean(T0.AddSeconds(120), TimeSpan.FromMinutes(10), notBefore: T0.AddSeconds(60))!.Value, 9);
        Assert.Null(new PowerHistory().Mean(T0, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Delta_vs_grid_needs_a_saving_mode_a_reference_and_30_seconds_after_the_switch()
    {
        var switched = T0.AddSeconds(60);
        var history = Filled(120, s => s < 60 ? 200 : 140);
        history.MarkSwitch(switched);

        Assert.Equal(-60, history.DeltaVsGrid(PowerModeKind.Station, 200, switched, T0.AddSeconds(120))!.Value, 9);
        Assert.Null(history.DeltaVsGrid(PowerModeKind.Grid, 200, switched, T0.AddSeconds(120)));
        Assert.Null(history.DeltaVsGrid(PowerModeKind.Station, null, switched, T0.AddSeconds(120)));
        Assert.Null(history.DeltaVsGrid(PowerModeKind.Station, 200, null, T0.AddSeconds(120)));

        // 28 s of samples after the switch: too early.
        Assert.Null(history.DeltaVsGrid(PowerModeKind.Reserve, 200, switched, T0.AddSeconds(88)));
        Assert.NotNull(history.DeltaVsGrid(PowerModeKind.Reserve, 200, switched, T0.AddSeconds(90)));

        // No samples after the switch at all.
        Assert.Null(history.DeltaVsGrid(PowerModeKind.Reserve, 200, T0.AddSeconds(500), T0.AddSeconds(500)));
        Assert.Equal(switched, history.SwitchedAt);
    }
}

public sealed class PowerCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 5, 0, TimeSpan.FromHours(2));
    private static readonly PowerEstimate Estimate = new(24.4, 21, 36, 60.6, 142);

    [Fact]
    public void Shows_the_estimate_parts_and_saving()
    {
        PowerPoint[] history = [new(Now.AddMinutes(-9), 203), new(Now.AddMinutes(-4), 150), new(Now, 142)];

        var card = PowerCards.Build(Estimate, null, -61.2, history, Now.AddMinutes(-4), Now);

        Assert.Equal("≈ 142", card.TotalText);
        Assert.Equal("▼ 61 W vs Grid", card.DeltaText);
        Assert.True(card.DeltaIsSaving);
        Assert.Equal(("Estimate", "Calibrate", false), (card.CalibrationNote, card.CalibrationAction, card.IsCalibrated));
        Assert.Equal(["CPU ≈ 24 W", "GPU 21 W", "Monitor ≈ 36 W", "Other ≈ 61 W"], card.Parts.Select(p => p.Text));
        Assert.Equal(Now.AddMinutes(-4), card.SwitchMarker);
        Assert.Equal(("142", "203"), (card.MinLabel, card.MaxLabel));
        Assert.Equal("Total ≈ 142 W · ▼ 61 W vs Grid · Estimate", card.SummaryLine);
        Assert.Equal("CPU ≈ 24 W · GPU 21 W · Monitor ≈ 36 W · Other ≈ 61 W", card.PartsLine);
    }

    [Fact]
    public void Higher_than_grid_unknown_and_calibrated_cases()
    {
        var calibrated = new PowerCalibration(12, new DateTimeOffset(2026, 11, 9, 10, 31, 0, TimeSpan.FromHours(2)));
        var higher = PowerCards.Build(Estimate, calibrated, 12.4, [], Now.AddHours(-1), Now);
        Assert.Equal("▲ 12 W vs Grid", higher.DeltaText);
        Assert.False(higher.DeltaIsSaving);
        Assert.Equal(("Calibrated 10:31", "Recalibrate", true), (higher.CalibrationNote, higher.CalibrationAction, higher.IsCalibrated));
        Assert.Null(higher.SwitchMarker);
        Assert.Null(higher.MinLabel);

        var same = PowerCards.Build(Estimate, null, -0.3, [], null, Now);
        Assert.Equal("Same as Grid", same.DeltaText);
        Assert.False(same.DeltaIsSaving);

        var empty = PowerCards.Build(null, null, null, [], null, Now);
        Assert.Equal("≈ –", empty.TotalText);
        Assert.Null(empty.DeltaText);
        Assert.Equal(["CPU –", "GPU –", "Monitor –", "Other –"], empty.Parts.Select(p => p.Text));
        Assert.All(empty.Parts, p => Assert.Equal(0, p.BarWatts));

        Assert.Equal(0, new PowerPart("Other", -12, false).BarWatts);
        Assert.Equal("8 Nov 22:10", PowerCards.When(new DateTimeOffset(2026, 11, 8, 22, 10, 0, TimeSpan.FromHours(2)), Now));
    }

    [Fact]
    public void The_status_report_prints_total_and_parts()
    {
        var config = AppConfig.CreateDefault();
        var card = PowerCards.Build(Estimate, null, null, [], null, Now);
        var state = FlyoutStateBuilder.Build(new FlyoutInput(
            Planning.TargetPlanner.Plan(PowerModeKind.Grid, config, null),
            new Dictionary<Levers.LeverKind, Levers.LeverResult>(),
            new HashSet<Levers.LeverKind>(),
            LiveValues.Empty,
            null,
            Now,
            true,
            false,
            false,
            config.Hotkeys,
            card));

        var text = TextReports.Status(state);

        Assert.Contains("Total ≈ 142 W · Estimate", text, StringComparison.Ordinal);
        Assert.Contains("CPU ≈ 24 W · GPU 21 W · Monitor ≈ 36 W · Other ≈ 61 W", text, StringComparison.Ordinal);
    }
}
