using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class ModelConversionTests
{
    [Fact]
    public void VideoDetails_ToSearchResult_ConvertsCorrectlyAndTruncatesDescription()
    {
        var longDescription = new string('x', 500);
        var details = new VideoDetails
        {
            Id = new YouTubeVideoId("dQw4w9WgXcQ"),
            Title = "Rick Astley - Never Gonna Give You Up",
            Channel = new ChannelInfo
            {
                Id = "UCuAXFkgsw1L7xaCfnd5JJOw",
                Name = "Rick Astley",
                SubscriberCount = 15_000_000,
                IsVerified = true
            },
            Duration = TimeSpan.FromSeconds(212),
            ViewCount = 1_500_000_000,
            Description = longDescription,
            ThumbnailUrl = "https://img.example.com/thumb.jpg"
        };

        var searchResult = details.ToSearchResult();

        Assert.Equal("dQw4w9WgXcQ", searchResult.Id);
        Assert.Equal("Rick Astley - Never Gonna Give You Up", searchResult.Title);
        Assert.Equal("Rick Astley", searchResult.ChannelTitle);
        Assert.Equal("UCuAXFkgsw1L7xaCfnd5JJOw", searchResult.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(212), searchResult.Duration);
        Assert.Equal(1_500_000_000, searchResult.ViewCount);
        Assert.Equal(200, searchResult.DescriptionSnippet?.Length);
    }

    [Fact]
    public void PlaybackQueueItem_FromSearchResult_InitializesQueueItem()
    {
        var result = new SearchResult
        {
            Id = "dQw4w9WgXcQ",
            Title = "Awesome Video",
            ChannelTitle = "Creator",
            Duration = TimeSpan.FromSeconds(185)
        };

        var queueItem = PlaybackQueueItem.FromSearchResult(result);

        Assert.NotEqual(Guid.Empty, queueItem.QueueItemId);
        Assert.Equal("dQw4w9WgXcQ", queueItem.VideoId.Value);
        Assert.Equal("Awesome Video", queueItem.Title);
        Assert.Equal("Creator", queueItem.ChannelTitle);
        Assert.False(queueItem.IsPlayed);
        Assert.Contains("Awesome Video, by Creator, duration 3 minutes 5 seconds", queueItem.AccessibleLabel);
    }

    [Fact]
    public void PlaybackQueueItem_AccessibleLabel_MarksPlayedStatus()
    {
        var item = new PlaybackQueueItem
        {
            VideoId = new YouTubeVideoId("dQw4w9WgXcQ"),
            Title = "Short Clip",
            ChannelTitle = "Artist",
            Duration = TimeSpan.FromSeconds(15),
            IsPlayed = true
        };

        Assert.Contains("(Played)", item.AccessibleLabel);
    }

    [Fact]
    public void ChannelInfo_AccessibleDescription_FormatsVerifiedAndSubscribers()
    {
        var channel = new ChannelInfo
        {
            Id = "chan123",
            Name = "Tech Explained",
            SubscriberCount = 2_400_000,
            IsVerified = true
        };

        var desc = channel.AccessibleDescription;

        Assert.Contains("Tech Explained", desc);
        Assert.Contains("2.4 million subscribers", desc);
        Assert.Contains("verified channel", desc);
    }

    [Fact]
    public void PagedResult_SinglePage_SetsCorrectBounds()
    {
        var items = new[] { "item1", "item2", "item3" };
        var paged = PagedResult<string>.SinglePage(items);

        Assert.Equal(3, paged.Items.Count);
        Assert.Equal(1, paged.PageIndex);
        Assert.False(paged.HasNextPage);
        Assert.Equal(3, paged.TotalEstimatedResults);
    }
}
