using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimePlayer.Core.Backends.Invidious;

/// <summary>
/// Internal DTOs for Invidious API responses according to official documentation.
/// Kept strictly inside the Invidious backend namespace.
/// </summary>
internal sealed class InvidiousSearchItem
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("authorUrl")]
    public string? AuthorUrl { get; set; }

    [JsonPropertyName("authorVerified")]
    public bool? AuthorVerified { get; set; }

    [JsonPropertyName("videoThumbnails")]
    public List<InvidiousThumbnail>? VideoThumbnails { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("viewCount")]
    public long? ViewCount { get; set; }

    [JsonPropertyName("publishedText")]
    public string? PublishedText { get; set; }

    [JsonPropertyName("lengthSeconds")]
    public int? LengthSeconds { get; set; }

    [JsonPropertyName("liveNow")]
    public bool? LiveNow { get; set; }
}

internal sealed class InvidiousThumbnail
{
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}

internal sealed class InvidiousVideoResponse
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("publishedText")]
    public string? PublishedText { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("authorUrl")]
    public string? AuthorUrl { get; set; }

    [JsonPropertyName("authorVerified")]
    public bool? AuthorVerified { get; set; }

    [JsonPropertyName("authorThumbnails")]
    public List<InvidiousThumbnail>? AuthorThumbnails { get; set; }

    [JsonPropertyName("lengthSeconds")]
    public int? LengthSeconds { get; set; }

    [JsonPropertyName("viewCount")]
    public long? ViewCount { get; set; }

    [JsonPropertyName("likeCount")]
    public long? LikeCount { get; set; }

    [JsonPropertyName("liveNow")]
    public bool? LiveNow { get; set; }

    [JsonPropertyName("hlsUrl")]
    public string? HlsUrl { get; set; }

    [JsonPropertyName("dashUrl")]
    public string? DashUrl { get; set; }

    [JsonPropertyName("videoThumbnails")]
    public List<InvidiousThumbnail>? VideoThumbnails { get; set; }

    [JsonPropertyName("formatStreams")]
    public List<InvidiousFormatStream>? FormatStreams { get; set; }

    [JsonPropertyName("adaptiveFormats")]
    public List<InvidiousAdaptiveFormat>? AdaptiveFormats { get; set; }

    [JsonPropertyName("captions")]
    public List<InvidiousCaption>? Captions { get; set; }
}

internal sealed class InvidiousFormatStream
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("itag")]
    public string? Itag { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("qualityLabel")]
    public string? QualityLabel { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("container")]
    public string? Container { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("bitrate")]
    public JsonElement? Bitrate { get; set; }
}

internal sealed class InvidiousAdaptiveFormat
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("itag")]
    public string? Itag { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("bitrate")]
    public JsonElement? Bitrate { get; set; }

    [JsonPropertyName("container")]
    public string? Container { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("qualityLabel")]
    public string? QualityLabel { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonPropertyName("audioSampleRate")]
    public string? AudioSampleRate { get; set; }

    [JsonPropertyName("audioChannels")]
    public int? AudioChannels { get; set; }
}

internal sealed class InvidiousCaption
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class InvidiousRecommendedResponse
{
    [JsonPropertyName("recommendedVideos")]
    public List<InvidiousRecommendedVideo>? RecommendedVideos { get; set; }
}

internal sealed class InvidiousRecommendedVideo
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("lengthSeconds")]
    public int? LengthSeconds { get; set; }

    [JsonPropertyName("viewCountText")]
    public string? ViewCountText { get; set; }

    [JsonPropertyName("publishedText")]
    public string? PublishedText { get; set; }
}

internal sealed class InvidiousCommentsResponse
{
    [JsonPropertyName("comments")]
    public List<InvidiousComment>? Comments { get; set; }

    [JsonPropertyName("continuation")]
    public string? Continuation { get; set; }
}

internal sealed class InvidiousComment
{
    [JsonPropertyName("commentId")]
    public string? CommentId { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("publishedText")]
    public string? PublishedText { get; set; }

    [JsonPropertyName("likeCount")]
    public long? LikeCount { get; set; }

    [JsonPropertyName("isPinned")]
    public bool? IsPinned { get; set; }

    [JsonPropertyName("authorIsChannelOwner")]
    public bool? AuthorIsChannelOwner { get; set; }

    [JsonPropertyName("replies")]
    public InvidiousCommentReplies? Replies { get; set; }
}

internal sealed class InvidiousCommentReplies
{
    [JsonPropertyName("replyCount")]
    public int? ReplyCount { get; set; }

    [JsonPropertyName("continuation")]
    public string? Continuation { get; set; }
}

internal sealed class InvidiousChannelVideosResponse
{
    [JsonPropertyName("videos")]
    public List<InvidiousSearchItem>? Videos { get; set; }

    [JsonPropertyName("continuation")]
    public string? Continuation { get; set; }
}

internal sealed class InvidiousPlaylistResponse
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("videoCount")]
    public int? VideoCount { get; set; }

    [JsonPropertyName("videos")]
    public List<InvidiousSearchItem>? Videos { get; set; }
}
