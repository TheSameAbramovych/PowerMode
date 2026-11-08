using PowerMode.Core.Infrastructure;
using PowerMode.Core.Parsing;

namespace PowerMode.Core.Tests;

public sealed class ParserTests
{
    // Captured on the owner's PC (Ukrainian scheme names) on 2026-11-09.
    private const string ActiveScheme = "Power Scheme GUID: 9897998c-92de-4669-853f-b7cd3ecb2790  (AMD Ryzen™ Balanced)\r\n";

    private const string SchemeList = """
        Existing Power Schemes (* Active)
        -----------------------------------
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Збалансований)
        Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Висока продуктивність)
        Power Scheme GUID: 9897998c-92de-4669-853f-b7cd3ecb2790  (AMD Ryzen™ Balanced) *
        Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Економія енергії)
        """;

    private const string RangeSetting = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Збалансований)
          GUID Alias: SCHEME_BALANCED
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Керування живленням процесора)
            GUID Alias: SUB_PROCESSOR
            Power Setting GUID: bc5038f7-23e0-4960-96da-33abaf5935ec  (Максимальна швидкодія процесора)
              GUID Alias: PROCTHROTTLEMAX
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0x00000064
              Possible Settings increment: 0x00000001
              Possible Settings units: %
            Current AC Power Setting Index: 0x00000050
            Current DC Power Setting Index: 0x00000064
        """;

    private const string HiddenSettingWithQh = """
            Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Режим підвищеної продуктивності процесора)
              GUID Alias: PERFBOOSTMODE
              Possible Setting Index: 000
              Possible Setting Friendly Name: Вимкнуто
              Possible Setting Index: 002
              Possible Setting Friendly Name: Агресивний
            Current AC Power Setting Index: 0x00000002
            Current DC Power Setting Index: 0x00000002
        """;

    private const string HiddenSettingWithQ = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Збалансований)
          GUID Alias: SCHEME_BALANCED
        """;

    private const string LocalizedLabels = """
              Мінімальне значення: 0x00000000
              Максимальне значення: 0x00000064
            Поточний індекс змінного струму: 0x0000012c
            Поточний індекс постійного струму: 0x000000b4
        """;

    [Fact]
    public void Active_scheme_is_read_by_guid()
    {
        Assert.Equal(Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"), PowercfgParser.ParseActiveScheme(ActiveScheme));
        Assert.Null(PowercfgParser.ParseActiveScheme("Access denied"));
    }

    [Fact]
    public void Scheme_list_finds_every_scheme_and_the_active_one()
    {
        var schemes = PowercfgParser.ParseSchemeList(SchemeList);

        Assert.Equal(4, schemes.Count);
        Assert.Equal(Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"), schemes.Single(s => s.IsActive).Id);
        Assert.Contains(schemes, s => s.Id == Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a") && !s.IsActive);
    }

    [Fact]
    public void Setting_index_lines_give_ac_and_dc()
    {
        Assert.Equal(new SettingIndex(0x50, 0x64), PowercfgParser.ParseSettingIndex(RangeSetting));
        Assert.Equal(new SettingIndex(2, 2), PowercfgParser.ParseSettingIndex(HiddenSettingWithQh));
    }

    [Fact]
    public void Hidden_setting_without_qh_has_no_values()
    {
        Assert.Equal(new SettingIndex(null, null), PowercfgParser.ParseSettingIndex(HiddenSettingWithQ));
    }

    [Fact]
    public void Localized_labels_fall_back_to_the_last_two_hex_values()
    {
        Assert.Equal(new SettingIndex(300, 180), PowercfgParser.ParseSettingIndex(LocalizedLabels));
    }

    [Fact]
    public void Docker_ps_names_are_one_per_line()
    {
        var names = DockerParser.ParseNames("p365-kafka\r\np365-zookeeper\n\n  qm-sonar  \np365-kafka\n");

        Assert.Equal(["p365-kafka", "p365-zookeeper", "qm-sonar"], names);
        Assert.Empty(DockerParser.ParseNames(string.Empty));
    }

    [Theory]
    [InlineData("{\n    \"SessionID\": \"cb4b397a\",\n    \"Status\": \"running\"\n}", DockerEngineStatus.Running)]
    [InlineData("{\"Status\":\"stopped\"}", DockerEngineStatus.Stopped)]
    [InlineData("{\"status\":\"paused\"}", DockerEngineStatus.Stopped)]
    [InlineData("{\"Status\":\"starting\"}", DockerEngineStatus.Starting)]
    [InlineData("{\"Other\":1}", DockerEngineStatus.Unknown)]
    [InlineData("{ broken", DockerEngineStatus.Unknown)]
    [InlineData("Name                Value\nStatus              running\nSessionID           cb4b", DockerEngineStatus.Running)]
    [InlineData("Status    stopped", DockerEngineStatus.Stopped)]
    [InlineData("error during connect", DockerEngineStatus.Unknown)]
    public void Docker_desktop_status_is_parsed(string output, DockerEngineStatus expected)
    {
        Assert.Equal(expected, DockerParser.ParseDesktopStatus(output));
    }

    [Fact]
    public void Process_results_describe_failures_in_one_line()
    {
        Assert.Equal("docker timed out", new ProcessResult(-1, string.Empty, string.Empty, true).Describe("docker"));
        Assert.Equal("docker: no such container", new ProcessResult(1, string.Empty, "no such container\r\nmore", false).Describe("docker"));
        Assert.Equal("powercfg: Invalid parameters", new ProcessResult(1, "Invalid parameters\n", string.Empty, false).Describe("powercfg"));
        Assert.Equal("powercfg exited with code 5", new ProcessResult(5, string.Empty, string.Empty, false).Describe("powercfg"));
        Assert.True(new ProcessResult(0, string.Empty, string.Empty, false).Succeeded);
        Assert.False(new ProcessResult(0, string.Empty, string.Empty, true).Succeeded);
    }
}
