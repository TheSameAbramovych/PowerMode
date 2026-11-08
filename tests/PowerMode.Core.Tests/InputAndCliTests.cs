using PowerMode.Core.Cli;
using PowerMode.Core.Input;

namespace PowerMode.Core.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+1", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31u)]
    [InlineData("ctrl + alt + 3", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x33u)]
    [InlineData("Control+Shift+P", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50u)]
    [InlineData("Win+F12", HotkeyModifiers.Windows, 0x7Bu)]
    [InlineData("Windows+Alt+F1", HotkeyModifiers.Windows | HotkeyModifiers.Alt, 0x70u)]
    [InlineData("Alt+Num7", HotkeyModifiers.Alt, 0x67u)]
    [InlineData("Shift+a", HotkeyModifiers.Shift, 0x41u)]
    public void Valid_hotkeys_parse(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Ctrl+1")]
    [InlineData("Hyper+1")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+NumX")]
    public void Invalid_hotkeys_are_rejected(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }
}

public sealed class CliOptionsTests
{
    [Fact]
    public void No_arguments_start_the_tray()
    {
        var options = CliOptions.Parse([]);

        Assert.Equal(CliCommand.Tray, options.Command);
        Assert.True(options.IsForwardable);
        Assert.Equal(CliOptions.OpenFlag, options.ToPipeMessage());
    }

    [Theory]
    [InlineData("grid", PowerModeKind.Grid)]
    [InlineData("Station", PowerModeKind.Station)]
    [InlineData("RESERVE", PowerModeKind.Reserve)]
    public void Mode_flag_parses_each_mode(string value, PowerModeKind mode)
    {
        var options = CliOptions.Parse(["--mode", value]);

        Assert.Equal(CliCommand.Mode, options.Command);
        Assert.Equal(mode, options.Mode);
        Assert.Equal($"--mode {ModeNames.Key(mode)}", options.ToPipeMessage());
    }

    [Fact]
    public void Dry_run_needs_a_mode()
    {
        Assert.Equal(CliCommand.DryRun, CliOptions.Parse(["--dry-run", "--mode", "station"]).Command);
        Assert.Equal(CliCommand.DryRun, CliOptions.Parse(["--mode", "reserve", "--dry-run"]).Command);
        Assert.False(CliOptions.Parse(["--dry-run"]).IsValid);
    }

    [Theory]
    [InlineData("--status", CliCommand.Status)]
    [InlineData("--open", CliCommand.Open)]
    [InlineData("--install-autostart", CliCommand.InstallAutostart)]
    [InlineData("--uninstall-autostart", CliCommand.UninstallAutostart)]
    [InlineData("--help", CliCommand.Help)]
    [InlineData("/?", CliCommand.Help)]
    [InlineData("-h", CliCommand.Help)]
    public void Single_flags(string flag, CliCommand command)
    {
        var options = CliOptions.Parse([flag]);

        Assert.Equal(command, options.Command);
        Assert.True(options.IsValid);
    }

    [Fact]
    public void Render_states_takes_a_directory()
    {
        var options = CliOptions.Parse(["--render-states", @"C:\review"]);

        Assert.Equal(CliCommand.RenderStates, options.Command);
        Assert.Equal(@"C:\review", options.Directory);
        Assert.False(options.IsForwardable);
        Assert.False(CliOptions.Parse(["--render-states"]).IsValid);
        Assert.False(CliOptions.Parse(["--render-states", "--status"]).IsValid);
    }

    [Theory]
    [InlineData("--mode")]
    [InlineData("--mode", "turbo")]
    [InlineData("--frobnicate")]
    public void Bad_arguments_are_errors(params string[] args)
    {
        var options = CliOptions.Parse(args);

        Assert.False(options.IsValid);
        Assert.Equal(CliCommand.Help, options.Command);
    }

    [Fact]
    public void Pipe_messages_round_trip_and_fall_back_to_open()
    {
        Assert.Equal(PowerModeKind.Reserve, CliOptions.FromPipeMessage("--mode reserve").Mode);
        Assert.Equal(CliCommand.Open, CliOptions.FromPipeMessage("--open").Command);
        Assert.Equal(CliCommand.Open, CliOptions.FromPipeMessage("--status").Command);
        Assert.Equal(CliCommand.Open, CliOptions.FromPipeMessage("garbage").Command);
        Assert.Contains("--render-states", CliOptions.Usage, StringComparison.Ordinal);
    }
}
