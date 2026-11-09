using System.Buffers.Binary;
using PowerMode.Core.Cli;
using PowerMode.Core.Presentation;

namespace PowerMode.Core.Tests;

public sealed class IcoWriterTests
{
    [Fact]
    public void Writes_a_directory_entry_per_image_and_the_data_after_it()
    {
        byte[] small = [1, 2, 3];
        byte[] large = [9, 8, 7, 6, 5];

        var ico = IcoWriter.Write([new IcoImage(16, small), new IcoImage(256, large)]);

        Assert.Equal(6 + 2 * 16 + 8, ico.Length);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));

        // 16 px entry
        Assert.Equal(16, ico[6]);
        Assert.Equal(16, ico[7]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(6 + 4)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(6 + 6)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 8)));
        Assert.Equal(38, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 12)));

        // 256 px is written as 0
        Assert.Equal(0, ico[22]);
        Assert.Equal(0, ico[23]);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 8)));
        Assert.Equal(41, BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(22 + 12)));
        Assert.Equal(small, ico[38..41]);
        Assert.Equal(large, ico[41..]);
    }

    [Fact]
    public void Rejects_no_images_and_bad_sizes()
    {
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([new IcoImage(512, [1])]));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([new IcoImage(0, [1])]));
    }

    [Fact]
    public void Make_icon_takes_a_file()
    {
        var options = CliOptions.Parse(["--make-icon", @"src\PowerMode.App\Assets\PowerMode.ico"]);

        Assert.Equal(CliCommand.MakeIcon, options.Command);
        Assert.Equal(@"src\PowerMode.App\Assets\PowerMode.ico", options.IconFile);
        Assert.False(options.IsForwardable);
        Assert.False(CliOptions.Parse(["--make-icon"]).IsValid);
        Assert.False(CliOptions.Parse(["--make-icon", "--status"]).IsValid);
        Assert.Contains("--make-icon", CliOptions.Usage, StringComparison.Ordinal);
    }
}

public sealed class QuitCommandTests
{
    [Fact]
    public void Quit_is_forwarded_to_the_running_instance()
    {
        var options = CliOptions.Parse(["--quit"]);

        Assert.Equal(CliCommand.Quit, options.Command);
        Assert.True(options.IsForwardable);
        Assert.Equal(CliOptions.QuitFlag, options.ToPipeMessage());
        Assert.Equal("Sent to the running PowerMode: --quit", options.ForwardedText);
        Assert.Equal(CliCommand.Quit, CliOptions.FromPipeMessage("--quit").Command);
        Assert.Contains("--quit", CliOptions.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_wins_over_quit_and_help_wins_over_everything()
    {
        Assert.Equal(CliCommand.Mode, CliOptions.Parse(["--quit", "--mode", "grid"]).Command);
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--quit", "--help"]).Command);
    }
}

public sealed class ConsoleTextTests
{
    [Fact]
    public void Report_symbols_become_ascii_look_alikes()
    {
        Assert.Equal("PowerMode - Grid - since 09:09", Cli.ConsoleText.ToAscii("PowerMode · Grid · since 09:09"));
        Assert.Equal("Total ~ 127 W - v 61 W vs Grid - ^ 3 W ... 5-6", Cli.ConsoleText.ToAscii("Total ≈ 127 W · ▼ 61 W vs Grid · ▲ 3 W … 5–6"));
        Assert.Equal("a-b", Cli.ConsoleText.ToAscii("a—b"));
        Assert.Equal("Привіт", Cli.ConsoleText.ToAscii("Привіт"));
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(65001u, false)]
    [InlineData(866u, true)]
    [InlineData(437u, true)]
    public void Only_a_non_utf8_console_needs_ascii(uint codePage, bool ascii) =>
        Assert.Equal(ascii, Cli.ConsoleText.NeedsAscii(codePage));

    [Fact]
    public void The_status_and_dry_run_reports_become_pure_ascii()
    {
        var config = Config.AppConfig.CreateDefault();
        var fixture = Presentation.RenderFixtures.All(config).Single(f => f.FileName == Presentation.RenderFixtures.ReserveFailed);

        var text = Cli.ConsoleText.ToAscii(Presentation.TextReports.Status(fixture.State));

        Assert.All(text, c => Assert.True(c < 128, $"'{c}' (U+{(int)c:X4}) is not ASCII"));
    }
}

public sealed class VersionAndSchemeTests
{
    [Fact]
    public void Version_flag_prints_the_version()
    {
        Assert.Equal(CliCommand.Version, CliOptions.Parse(["--version"]).Command);
        Assert.False(CliOptions.Parse(["--version"]).IsForwardable);
        Assert.Contains("--version", CliOptions.Usage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Power Scheme GUID: 9897998c-92de-4669-853f-b7cd3ecb2790  (AMD Ryzen Balanced)", "AMD Ryzen Balanced")]
    [InlineData("GUID схеми живлення: 7d0a5e01-0000-4000-8000-00504d535441  (PowerMode Station)", "PowerMode Station")]
    [InlineData("Power Scheme GUID: 9897998c-92de-4669-853f-b7cd3ecb2790", null)]
    public void The_active_scheme_name_is_read_after_its_guid(string output, string? name)
    {
        var scheme = Parsing.PowercfgParser.ParseActiveSchemeWithName(output);

        Assert.NotNull(scheme);
        Assert.Equal(name, scheme.Value.Name);
        Assert.Null(Parsing.PowercfgParser.ParseActiveSchemeWithName("no scheme"));
    }

    [Fact]
    public void Status_prints_the_active_scheme_under_the_cpu_row()
    {
        var config = Config.AppConfig.CreateDefault();
        var state = FlyoutStateBuilder.Build(new FlyoutInput(
            Planning.TargetPlanner.Plan(PowerModeKind.Grid, config, null),
            new Dictionary<Levers.LeverKind, Levers.LeverResult>(),
            new HashSet<Levers.LeverKind>(),
            LiveValues.Empty,
            null,
            DateTimeOffset.UnixEpoch,
            true,
            false,
            false,
            config.Hotkeys));

        var text = TextReports.Status(state, (Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"), "AMD Ryzen Balanced"));

        Assert.Contains("Active scheme: AMD Ryzen Balanced", text, StringComparison.Ordinal);
        Assert.Contains("9897998c-92de-4669-853f-b7cd3ecb2790", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Active scheme", TextReports.Status(state), StringComparison.Ordinal);
    }
}
