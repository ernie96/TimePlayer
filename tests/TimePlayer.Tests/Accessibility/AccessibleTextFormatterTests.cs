using TimePlayer.Core.Accessibility;
using Xunit;

namespace TimePlayer.Tests.Accessibility;

public class AccessibleTextFormatterTests
{
    [Fact]
    public void FormatTimeSpoken_SecondsOnly()
    {
        var result = AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(45));
        Assert.Equal("45 seconds", result);
    }

    [Fact]
    public void FormatTimeSpoken_OneSecond()
    {
        var result = AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(1));
        Assert.Equal("1 second", result);
    }

    [Fact]
    public void FormatTimeSpoken_MinutesAndSeconds()
    {
        var result = AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(192)); // 3 mins 12 secs
        Assert.Equal("3 minutes, 12 seconds", result);
    }

    [Fact]
    public void FormatTimeSpoken_HoursMinutesSeconds()
    {
        var result = AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(3725)); // 1 hour 2 mins 5 secs
        Assert.Equal("1 hour, 2 minutes, 5 seconds", result);
    }

    [Fact]
    public void FormatVolumeSpoken_ClampsCorrectly()
    {
        Assert.Equal("Volume 80 percent", AccessibleTextFormatter.FormatVolumeSpoken(80.2));
        Assert.Equal("Volume 100 percent", AccessibleTextFormatter.FormatVolumeSpoken(120));
        Assert.Equal("Volume 0 percent", AccessibleTextFormatter.FormatVolumeSpoken(-10));
    }

    [Fact]
    public void FormatSpeedSpoken_FormatsMultiplier()
    {
        Assert.Equal("Playback speed 1.25x", AccessibleTextFormatter.FormatSpeedSpoken(1.25));
        Assert.Equal("Playback speed 1x", AccessibleTextFormatter.FormatSpeedSpoken(1.0));
    }

    [Fact]
    public void FormatSearchResultsSpoken_ZeroResults()
    {
        var speech = AccessibleTextFormatter.FormatSearchResultsSpoken(0, "lofi");
        Assert.Equal("No results for lofi.", speech);
    }

    [Fact]
    public void FormatSearchResultsSpoken_MultipleResults()
    {
        var speech = AccessibleTextFormatter.FormatSearchResultsSpoken(10, "lofi");
        Assert.Equal("10 results.", speech);
    }

    [Theory]
    [InlineData(240, "4 minutes")]
    [InlineData(3605, "1 hour, 5 seconds")]
    [InlineData(0, "0 seconds")]
    public void FormatTimeSpoken_LeavesOutZeroParts(int seconds, string expected)
    {
        Assert.Equal(expected, AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(seconds)));
    }
}
