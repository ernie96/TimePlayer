namespace TimePlayer.Core.Models;

/// <summary>
/// Full video details, channel information, streams, and captions.
/// </summary>
public record VideoDetails
{
    public required YouTubeVideoId Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required ChannelInfo Channel { get; init; }
    public TimeSpan Duration { get; init; }
    public long? ViewCount { get; init; }
    public long? LikeCount { get; init; }
    public DateTimeOffset? UploadDate { get; init; }
    public string? ThumbnailUrl { get; init; }
    public IReadOnlyList<StreamCandidate> Streams { get; init; } = Array.Empty<StreamCandidate>();
    public IReadOnlyList<CaptionTrack> Captions { get; init; } = Array.Empty<CaptionTrack>();
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public bool IsLive { get; init; }

    /// <summary>
    /// Converts full video details to a SearchResult item suitable for queue or list displays.
    /// </summary>
    public SearchResult ToSearchResult() => new()
    {
        Id = Id.Value,
        Title = Title,
        ChannelTitle = Channel.Name,
        ChannelId = Channel.Id,
        Duration = Duration,
        ViewCount = ViewCount,
        ThumbnailUrl = ThumbnailUrl,
        DescriptionSnippet = Description != null && Description.Length > 200 ? Description[..200] : Description
    };

}
