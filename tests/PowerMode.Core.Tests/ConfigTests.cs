using PowerMode.Core.Config;
using PowerMode.Core.Model;

namespace PowerMode.Core.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal("Ctrl+Alt+1", config.Hotkeys.Grid);
        Assert.Equal("Ctrl+Alt+2", config.Hotkeys.Station);
        Assert.Equal("Ctrl+Alt+3", config.Hotkeys.Reserve);
        Assert.Equal(FlyoutAnchor.BottomRight, config.Flyout.Anchor);
        Assert.Equal(12, config.Flyout.Margin);
        Assert.Equal(35, config.Modes.Station.BrightnessPercent);
        Assert.Equal(10, config.Modes.Reserve.BrightnessPercent);
        Assert.Equal(90, config.Modes.Station.GpuLimitWatts);
        Assert.Equal(60, config.Modes.Reserve.GpuLimitWatts);
        Assert.Equal(PowerSchemes.Station, config.Modes.Station.Scheme.Id);
        Assert.Equal(PowerSchemes.Reserve, config.Modes.Reserve.Scheme.Id);
        Assert.Equal("-silent", config.RelaunchArgumentsFor("STEAM"));
        Assert.Equal("/background", config.RelaunchArgumentsFor("onedrive"));
        Assert.Null(config.RelaunchArgumentsFor("Viber"));
        Assert.Equal(120, config.Docker.EngineWaitSeconds);
        Assert.Empty(ConfigLoader.Validate(config));
        Assert.Null(config.Modes.For(PowerModeKind.Grid));
    }

    [Fact]
    public void Unions_cover_every_list_once()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(7, config.AllCloseApps().Count);
        Assert.Equal(7, config.AllServices().Count);
        Assert.Equal(5, config.AllEfficiencyApps().Count);
    }

    [Fact]
    public void Defaults_round_trip_through_json()
    {
        var json = ConfigLoader.Serialize(AppConfig.CreateDefault());

        var parsed = ConfigLoader.Parse(json);

        Assert.True(parsed.IsValid);
        Assert.Equal(json, ConfigLoader.Serialize(parsed.Config!));
        Assert.Contains("\"brightnessPercent\": 35", json, StringComparison.Ordinal);
        Assert.Contains("\"anchor\": \"bottomRight\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_config_is_merged_onto_the_defaults()
    {
        const string json = """
            {
              // comments are fine
              "modes": { "station": { "brightnessPercent": 50, "scheme": { "maxProcessorPercent": 70 } } },
              "Hotkeys": { "reserve": "Win+Shift+F9" },
              "relaunchArguments": { "Viber": "/minimized" },
            }
            """;

        var result = ConfigLoader.Parse(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var config = result.Config!;
        Assert.Equal(50, config.Modes.Station.BrightnessPercent);
        Assert.Equal(70, config.Modes.Station.Scheme.MaxProcessorPercent);
        Assert.Equal(5, config.Modes.Station.Scheme.MinProcessorPercent);
        Assert.Equal(PowerSchemes.Station, config.Modes.Station.Scheme.Id);
        Assert.Equal(60, config.Modes.Station.RefreshRateHz);
        Assert.Equal(10, config.Modes.Reserve.BrightnessPercent);
        Assert.Equal("Win+Shift+F9", config.Hotkeys.Reserve);
        Assert.Equal("Ctrl+Alt+1", config.Hotkeys.Grid);
        Assert.Equal("/minimized", config.RelaunchArgumentsFor("viber"));
        Assert.Equal("-silent", config.RelaunchArgumentsFor("steam"));
    }

    [Fact]
    public void Lists_replace_and_null_means_leave_alone()
    {
        var result = ConfigLoader.Parse("""{ "modes": { "reserve": { "closeApps": ["Discord"], "gpuLimitWatts": null } } }""");

        Assert.True(result.IsValid);
        Assert.Equal(["Discord"], result.Config!.Modes.Reserve.CloseApps);
        Assert.Null(result.Config.Modes.Reserve.GpuLimitWatts);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "modes": { "station": { "brightnessPercent": 150 } } }""")]
    [InlineData("""{ "modes": { "station": { "brightnessPercent": "bright" } } }""")]
    [InlineData("""{ "modes": { "station": { "scheme": { "minProcessorPercent": 90 } } } }""")]
    [InlineData("""{ "modes": { "reserve": { "scheme": { "id": "7d0a5e01-0000-4000-8000-00504d535441" } } } }""")]
    [InlineData("""{ "modes": { "station": { "scheme": { "sourceGuid": "00000000-0000-0000-0000-000000000000" } } } }""")]
    [InlineData("""{ "hotkeys": { "grid": "Banana+1" } }""")]
    [InlineData("""{ "docker": { "engineWaitSeconds": 0 } }""")]
    [InlineData("null")]
    public void Invalid_config_is_rejected_with_a_reason(string json)
    {
        var result = ConfigLoader.Parse(json);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Empty_hotkey_disables_it_without_making_the_config_invalid()
    {
        Assert.True(ConfigLoader.Parse("""{ "hotkeys": { "grid": "" } }""").IsValid);
    }

    [Fact]
    public void Store_creates_the_file_with_defaults_on_first_run()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var store = new ConfigStore(Path.Combine(dir.Path, "sub", "config.json"), log);

        Assert.True(store.Load());

        Assert.True(File.Exists(store.Path));
        Assert.Equal(ConfigLoader.Serialize(AppConfig.CreateDefault()), File.ReadAllText(store.Path));
        Assert.Contains(log.Lines, l => l.Contains("with defaults", StringComparison.Ordinal));
    }

    [Fact]
    public void Store_keeps_the_last_good_config_when_the_file_turns_invalid()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "modes": { "station": { "brightnessPercent": 42 } } }""");
        using var store = new ConfigStore(path, log);
        Assert.True(store.Load());
        Assert.Equal(42, store.Current.Modes.Station.BrightnessPercent);

        File.WriteAllText(path, "{ broken");
        Assert.False(store.Load());

        Assert.Equal(42, store.Current.Modes.Station.BrightnessPercent);
        Assert.Contains(log.Lines, l => l.Contains("keeping last good", StringComparison.Ordinal));
    }

    [Fact]
    public void Store_reports_unreadable_files()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var path = dir.File("config.json");
        File.WriteAllText(path, "{}");
        using var store = new ConfigStore(path, log);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(store.Load());
        }

        Assert.Contains(log.Lines, l => l.Contains("Cannot read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Store_reloads_when_the_file_changes()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        using var store = new ConfigStore(path, new ListLog());
        store.Load();
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();
        store.StartWatching();

        await File.WriteAllTextAsync(path, """{ "modes": { "reserve": { "brightnessPercent": 5 } } }""");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, store.Current.Modes.Reserve.BrightnessPercent);
    }

    [Fact]
    public void Hotkey_config_maps_modes()
    {
        var hotkeys = new HotkeyConfig();

        Assert.Equal("Ctrl+Alt+2", hotkeys.For(PowerModeKind.Station));
        Assert.Throws<ArgumentOutOfRangeException>(() => hotkeys.For((PowerModeKind)42));
    }
}
