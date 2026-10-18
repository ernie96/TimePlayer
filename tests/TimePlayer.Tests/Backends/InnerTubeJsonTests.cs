using System.Text.Json;
using TimePlayer.Core.Backends.InnerTube;
using Xunit;

namespace TimePlayer.Tests.Backends;

/// <summary>Fixtures follow the shapes of real 2026 InnerTube responses, trimmed to what the parsers read.</summary>
public class InnerTubeJsonTests
{
    private const string WatchNext = """
    {"contents":{"twoColumnWatchNextResults":{
      "results":{"results":{"contents":[
        {"itemSectionRenderer":{"sectionIdentifier":"comment-item-section","contents":[
          {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"COMMENTS_TOKEN"}}}}]}}]}},
      "secondaryResults":{"secondaryResults":{"results":[
        {"lockupViewModel":{"contentId":"cK5gbgiv23k","contentType":"LOCKUP_CONTENT_TYPE_VIDEO",
          "contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[{"thumbnailBadgeViewModel":{"text":"22:32"}}]}}]}},
          "metadata":{"lockupMetadataViewModel":{"title":{"content":"A related video"},
            "image":{"decoratedAvatarViewModel":{"rendererContext":{"commandContext":{"onTap":{"innertubeCommand":{"browseEndpoint":{"browseId":"UCVTNyohzRvBI0kVGIcQDJHQ"}}}}}}},
            "metadata":{"contentMetadataViewModel":{"metadataRows":[
              {"metadataParts":[{"text":{"content":"DerekFDB"}}]},
              {"metadataParts":[{"text":{"content":"4M views"}},{"text":{"content":"2w ago"}}]}]}}}}}},
        {"lockupViewModel":{"contentId":"RDdQw4w9WgXcQ","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST",
          "metadata":{"lockupMetadataViewModel":{"title":{"content":"A mix"}}}}},
        {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"MORE_RELATED"}}}}]}}}},
     "engagementPanels":[{"structuredDescriptionContentRenderer":{"items":[{"expandableVideoDescriptionBodyRenderer":{"attributedDescription":{"content":"The description."}}}]}}]}
    """;

    private const string Comments = """
    {"onResponseReceivedEndpoints":[
      {"reloadContinuationItemsCommand":{"continuationItems":[{"commentsHeaderRenderer":{}}]}},
      {"reloadContinuationItemsCommand":{"continuationItems":[
        {"commentThreadRenderer":{
          "replies":{"commentRepliesRenderer":{"contents":[{"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"REPLIES_1"}}}}]}},
          "commentViewModel":{"commentViewModel":{"commentKey":"KEY1","commentId":"Ugx1"}}}},
        {"commentThreadRenderer":{"commentViewModel":{"commentViewModel":{"commentKey":"KEY2","commentId":"Ugx2"}}}},
        {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"NEXT_PAGE"}}}}]}}],
     "frameworkUpdates":{"entityBatchUpdate":{"mutations":[
       {"entityKey":"KEY1","payload":{"commentEntityPayload":{"key":"KEY1",
         "properties":{"commentId":"Ugx1","content":{"content":"First comment"},"publishedTime":"6 years ago (edited)","replyLevel":0},
         "author":{"displayName":"@first","channelId":"UCKrzgYonJnMd0Dk7zQXjYXw","isCreator":false},
         "toolbar":{"likeCountA11y":"104K likes","replyCount":"616"}}}},
       {"entityKey":"KEY2","payload":{"commentEntityPayload":{"key":"KEY2",
         "properties":{"commentId":"Ugx2","content":{"content":"From the channel"},"publishedTime":"2d ago","replyLevel":0},
         "author":{"displayName":"@creator","isCreator":true},
         "toolbar":{"likeCountA11y":"0 likes","replyCount":""}}}}]}}}
    """;

    [Fact]
    public void ParseWatchNext_ReadsVideoLockupsCommentsTokenAndDescription()
    {
        using var doc = JsonDocument.Parse(WatchNext);
        var info = InnerTubeJson.ParseWatchNext(doc.RootElement);

        var related = Assert.Single(info.Related); // the mix is skipped
        Assert.Equal("cK5gbgiv23k", related.Id);
        Assert.Equal("DerekFDB", related.ChannelTitle);
        Assert.Equal("UCVTNyohzRvBI0kVGIcQDJHQ", related.ChannelId);
        Assert.Equal(new TimeSpan(0, 22, 32), related.Duration);
        Assert.Equal(4_000_000, related.ViewCount);
        Assert.Equal("2 weeks ago", related.UploadDateText);
        Assert.Equal("COMMENTS_TOKEN", info.CommentsToken);
        Assert.Equal("The description.", info.Description);
    }

    [Fact]
    public void ParseComments_JoinsThreadsWithEntitiesInOrder()
    {
        using var doc = JsonDocument.Parse(Comments);
        var page = InnerTubeJson.ParseComments(doc.RootElement, isReplyPage: false);

        Assert.Equal("NEXT_PAGE", page.NextPageToken);
        Assert.Equal(2, page.Comments.Count);

        var first = page.Comments[0];
        Assert.Equal("First comment", first.Text);
        Assert.Equal("104 thousand likes", first.LikesText);
        Assert.Equal(616, first.ReplyCount);
        Assert.Equal("REPLIES_1", first.RepliesToken);
        Assert.Equal("@first: First comment (6 years ago (edited), 104 thousand likes, 616 replies)", first.AccessibleSummary);

        var second = page.Comments[1];
        Assert.True(second.IsCreator);
        Assert.Null(second.LikesText);
        Assert.False(second.HasReplies);
        Assert.Equal("@creator, channel owner: From the channel (2 days ago)", second.AccessibleSummary);
    }

    [Theory]
    [InlineData("1.2M views", 1_200_000L)]
    [InlineData("889,722 views", 889_722L)]
    [InlineData("500K", 500_000L)]
    [InlineData("No views", null)]
    public void ParseCount_HandlesAbbreviations(string text, long? expected) =>
        Assert.Equal(expected, InnerTubeJson.ParseCount(text));

    [Theory]
    [InlineData("1y ago", "1 year ago")]
    [InlineData("3mo ago", "3 months ago")]
    [InlineData("5 days ago", "5 days ago")]
    public void ExpandAge_SpellsOutUnits(string text, string expected) =>
        Assert.Equal(expected, InnerTubeJson.ExpandAge(text));
}
