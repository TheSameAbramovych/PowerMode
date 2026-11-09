using System.Text.Json;
using PowerMode.Core.Model;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

public sealed class RelaunchTests
{
    private const string ChatGptPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1103.4239.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
    private const string ChatGptAumid = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private const string PostmanRoot = @"C:\Users\bohda\AppData\Local\Postman-Agent";

    [Fact]
    public void A_packaged_app_starts_through_its_aumid_without_its_versioned_path()
    {
        var launch = Relaunch.For(new CapturedProcess("ChatGPT", ChatGptPath, "--x", ChatGptAumid), string.Empty);

        Assert.True(launch.IsPackaged);
        Assert.Equal(@"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App", launch.File);
        Assert.Equal(string.Empty, launch.Arguments);
        Assert.Null(launch.WorkingDirectory);
        Assert.False(launch.ArgumentsDropped);
    }

    [Fact]
    public void Arguments_for_a_packaged_app_are_reported_as_dropped()
    {
        var launch = Relaunch.For(new CapturedProcess("ms-teams", "t.exe", string.Empty, "MSTeams_8wekyb3d8bbwe!MSTeams"), "msteams:system-initiated");

        Assert.True(launch.ArgumentsDropped);
        Assert.Equal(string.Empty, launch.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_unpackaged_app_starts_by_path_in_its_folder(string? aumid)
    {
        var launch = Relaunch.For(new CapturedProcess("steam", @"C:\Program Files (x86)\Steam\steam.exe", string.Empty, aumid), "-silent");

        Assert.False(launch.IsPackaged);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", launch.File);
        Assert.Equal("-silent", launch.Arguments);
        Assert.Equal(@"C:\Program Files (x86)\Steam", launch.WorkingDirectory);
        Assert.False(launch.ArgumentsDropped);
    }

    [Fact]
    public void Captured_processes_round_trip_with_and_without_an_aumid()
    {
        CapturedProcess[] processes = [new("ChatGPT", ChatGptPath, string.Empty, ChatGptAumid), new("steam", "s.exe", "-silent")];

        var json = JsonSerializer.Serialize(processes, StateJson.Options);
        var copy = JsonSerializer.Deserialize<CapturedProcess[]>(json, StateJson.Options)!;

        Assert.Equal(processes, copy);
        Assert.Null(JsonSerializer.Deserialize<CapturedProcess>("""{"name":"a","path":"a.exe","arguments":""}""", StateJson.Options)!.AppUserModelId);
    }

    [Fact]
    public void A_squirrel_app_is_recorded_by_its_stable_stub()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $@"{PostmanRoot}\Postman Agent.exe", $@"{PostmanRoot}\Update.exe" };

        Assert.Equal($@"{PostmanRoot}\Postman Agent.exe", Relaunch.StablePath($@"{PostmanRoot}\app-0.5.2\Postman Agent.exe", files.Contains));
        Assert.Equal($@"{PostmanRoot}\Postman Agent.exe", Relaunch.StablePath($@"{PostmanRoot}\APP-1\Postman Agent.exe", files.Contains));
    }

    [Theory]
    [InlineData(@"C:\Users\bohda\AppData\Local\Postman-Agent\app-0.5.2\Postman Agent.exe", false, true)]
    [InlineData(@"C:\Users\bohda\AppData\Local\Postman-Agent\app-0.5.2\Postman Agent.exe", true, false)]
    [InlineData(@"C:\Users\bohda\AppData\Local\Postman-Agent\bin\Postman Agent.exe", true, true)]
    [InlineData(@"C:\Users\bohda\AppData\Local\Postman-Agent\app-beta\Postman Agent.exe", true, true)]
    [InlineData(@"C:\x.exe", true, true)]
    [InlineData(@"C:\", true, true)]
    [InlineData(@"x.exe", true, true)]
    public void Anything_but_a_full_squirrel_layout_keeps_its_path(string path, bool stubExists, bool updaterExists)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stubExists)
        {
            files.Add($@"{PostmanRoot}\Postman Agent.exe");
            files.Add(@"C:\x.exe");
        }

        if (updaterExists)
        {
            files.Add($@"{PostmanRoot}\Update.exe");
            files.Add(@"C:\Update.exe");
        }

        Assert.Equal(path, Relaunch.StablePath(path, files.Contains));
    }
}
