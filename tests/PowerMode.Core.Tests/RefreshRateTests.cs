using PowerMode.Core.Model;

namespace PowerMode.Core.Tests;

public sealed class RefreshRateTests
{
    [Theory]
    [InlineData(59, 60, true)]
    [InlineData(60, 60, true)]
    [InlineData(61, 60, true)]
    [InlineData(58, 60, false)]
    [InlineData(199, 200, true)]
    [InlineData(144, 60, false)]
    public void Rates_within_a_hertz_are_the_same(int a, int b, bool same)
    {
        Assert.Equal(same, RefreshRates.Same(a, b));
        Assert.Equal(same, RefreshRates.Same(b, a));
    }

    [Fact]
    public void Pick_prefers_the_exact_rate_then_the_closest_within_a_hertz()
    {
        Assert.Equal(60, RefreshRates.Pick([59, 60, 144], 60));
        Assert.Equal(59, RefreshRates.Pick([59, 144, 200], 60));
        Assert.Equal(61, RefreshRates.Pick([59, 61], 60));
        Assert.Null(RefreshRates.Pick([144, 200], 60));
        Assert.Null(RefreshRates.Pick([], 60));
    }

    [Fact]
    public void Display_shows_the_requested_rate_when_the_measurement_is_within_a_hertz()
    {
        Assert.Equal(60, RefreshRates.Display(59, 60));
        Assert.Equal(58, RefreshRates.Display(58, 60));
        Assert.Equal(59, RefreshRates.Display(59, null));
    }

    [Fact]
    public void Display_modes_match_within_a_hertz_on_the_same_device_and_resolution()
    {
        var set = new DisplayModeInfo(@"\\.\DISPLAY1", 2560, 1440, 60, 32);

        Assert.True(set.Matches(set with { RefreshHz = 59 }));
        Assert.True(set.Matches(set with { Device = @"\\.\display1" }));
        Assert.False(set.Matches(set with { RefreshHz = 200 }));
        Assert.False(set.Matches(set with { Width = 1920 }));
        Assert.False(set.Matches(set with { Height = 1080 }));
        Assert.False(set.Matches(set with { BitsPerPixel = 16 }));
        Assert.False(set.Matches(set with { Device = @"\\.\DISPLAY2" }));
    }
}
