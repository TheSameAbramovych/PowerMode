using PowerMode.Core.Presentation;
using PowerMode.Core.Settings;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

public sealed class TrayPromotionTests
{
    private const string Exe = @"C:\Users\bohda\AppData\Local\Programs\PowerMode\PowerMode.exe";
    private static readonly Guid LocalAppData = Guid.Parse("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    private static string? Folders(Guid id) => id == LocalAppData ? @"C:\Users\bohda\AppData\Local" : null;

    [Fact]
    public void Our_hidden_entries_are_promoted_and_nobody_elses()
    {
        NotifyIconEntry[] entries =
        [
            new("1", @"C:\Program Files\Other\other.exe", 0),
            new("2", @"c:\users\BOHDA\appdata\local\programs\powermode\powermode.exe", 0),
            new("3", "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\\Programs\\PowerMode\\PowerMode.exe", null),
            new("4", "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\\Programs\\PowerMode\\PowerMode.exe", 1),
            new("5", null, 0),
        ];

        var decision = TrayPromotion.Decide(Exe, null, entries, Folders);

        Assert.Equal(TrayPromotionAction.Promote, decision.Action);
        Assert.Equal(["2", "3"], decision.Keys);
    }

    [Fact]
    public void Already_promoted_not_found_unsupported_and_done_once()
    {
        Assert.Equal(TrayPromotionAction.AlreadyPromoted, TrayPromotion.Decide(Exe, null, [new("2", Exe, 1)], Folders).Action);
        Assert.Equal(TrayPromotionAction.NotFound, TrayPromotion.Decide(Exe, null, [new("1", @"C:\x.exe", 0)], Folders).Action);
        Assert.Equal(TrayPromotionAction.NotFound, TrayPromotion.Decide(Exe, @"C:\Old\PowerMode.exe", [], Folders).Action);
        Assert.Equal(TrayPromotionAction.Unsupported, TrayPromotion.Decide(Exe, null, null, Folders).Action);

        // Done for this path: the user's later choice (hidden again) stands.
        Assert.Equal(TrayPromotionAction.Skip, TrayPromotion.Decide(Exe, Exe.ToUpperInvariant(), [new("2", Exe, 0)], Folders).Action);
        Assert.Empty(TrayPromotion.Decide(Exe, Exe, null, Folders).Keys);
    }

    [Theory]
    [InlineData(@"C:\a\b.exe", @"C:\a\b.exe")]
    [InlineData("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}\\Programs\\b.exe", @"C:\Users\bohda\AppData\Local\Programs\b.exe")]
    [InlineData("{00000000-0000-0000-0000-000000000001}\\b.exe", null)]
    [InlineData("{not-a-guid}\\b.exe", null)]
    [InlineData("{F1B32785", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Known_folder_prefixes_are_expanded(string? path, string? expanded)
    {
        Assert.Equal(expanded, TrayPromotion.Expand(path, Folders));
    }
}

public sealed class TooltipAndFeedbackTests
{
    [Fact]
    public void Tooltip_shows_mode_and_gpu_also_while_applying()
    {
        Assert.Equal("PowerMode · Station · GPU 9 W", FlyoutStateBuilder.Tooltip(PowerModeKind.Station, false, 9.4));
        Assert.Equal("PowerMode · Applying Reserve… · GPU 12 W", FlyoutStateBuilder.Tooltip(PowerModeKind.Reserve, true, 11.6));
        Assert.Equal("PowerMode · Grid", FlyoutStateBuilder.Tooltip(PowerModeKind.Grid, false, null));
    }

    [Fact]
    public void Saved_replaces_the_last_one_and_errors_follow_their_kind()
    {
        var saved = SettingsFeedback.SavedAt(new DateTimeOffset(2026, 11, 9, 10, 31, 59, TimeSpan.FromHours(2)));
        Assert.Equal(new SettingsFeedback("Saved 10:31", null), saved);

        var invalid = saved.Invalid("Type a process name");
        Assert.Equal(new SettingsFeedback("Saved 10:31", "Type a process name"), invalid);
        Assert.Equal(saved, invalid.Valid());

        Assert.Equal(new SettingsFeedback(null, "not saved"), SettingsFeedback.NotSaved("not saved"));
        Assert.Equal(new SettingsFeedback("Saved 10:32", null), SettingsFeedback.SavedAt(new DateTimeOffset(2026, 11, 9, 10, 32, 0, TimeSpan.Zero)));
        Assert.Equal(new SettingsFeedback(null, null), SettingsFeedback.None);
    }
}

public sealed class FlyoutStateTests
{
    [Fact]
    public void Pinned_is_the_default_and_survives_a_restart()
    {
        Assert.True(new PersistedState().FlyoutPinned);
        Assert.True(StateStore.Deserialize("""{ "mode": "grid" }""")!.FlyoutPinned);
        Assert.Null(StateStore.Deserialize("""{ "mode": "grid" }""")!.TrayPromotedFor);
    }

    [Fact]
    public async Task Pin_and_promotion_are_saved_at_once()
    {
        using var harness = new SwitcherHarness([]);

        await harness.Switcher.SetFlyoutPinnedAsync(false);
        await harness.Switcher.SetTrayPromotedForAsync(@"C:\Apps\PowerMode.exe");

        var saved = harness.Reload();
        Assert.False(saved.FlyoutPinned);
        Assert.Equal(@"C:\Apps\PowerMode.exe", saved.TrayPromotedFor);
        Assert.Contains("\"flyoutPinned\": false", File.ReadAllText(harness.Store.Path), StringComparison.Ordinal);
    }
}
