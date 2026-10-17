using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class SearchResultTests
{
    [Fact]
    public void AccessibleSummary_CombinesMetadataIntoNaturalSpeech()
    {
        var result = new SearchResult
        {
            Id = "dQw4w9WgXcQ",
            Title = "Never Gonna Give You Up",
            ChannelTitle = "Rick Astley",
            Duration = TimeSpan.FromSeconds(212), // 3m 32s
            ViewCount = 1_500_000_000,
            UploadDateText = "14 years ago"
        };

        var summary = result.AccessibleSummary;

        Assert.Contains("Never Gonna Give You Up", summary);
        Assert.Contains("by Rick Astley", summary);
        Assert.Contains("3 minutes, 32 seconds", summary);
        Assert.Contains("1.5 billion views", summary);
        Assert.Contains("14 years ago", summary);
    }

    [Fact]
    public void AccessibleSummary_HandlesMinimalMetadata()
    {
        var result = new SearchResult
        {
            Id = "test12345",
            Title = "Quick Demo",
            ChannelTitle = "Creator"
        };

        var summary = result.AccessibleSummary;

        Assert.Equal("Quick Demo. by Creator", summary);
    }

    [Fact]
    public void AccessibleSummary_FormatsThousandsAndMillionsCorrectly()
    {
        var resultMillions = new SearchResult
        {
            Id = "1",
            Title = "Song A",
            ChannelTitle = "Artist",
            ViewCount = 4_200_000
        };
        Assert.Contains("4.2 million views", resultMillions.AccessibleSummary);

        var resultThousands = new SearchResult
        {
            Id = "2",
            Title = "Song B",
            ChannelTitle = "Artist",
            ViewCount = 15_800
        };
        Assert.Contains("15.8 thousand views", resultThousands.AccessibleSummary);
    }
}
