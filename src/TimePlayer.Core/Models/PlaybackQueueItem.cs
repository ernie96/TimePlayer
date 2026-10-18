namespace TimePlayer.Core.Models;

/// <summary>
/// An item in the user's playback queue.
/// </summary>
public record PlaybackQueueItem
{
    public Guid QueueItemId { get; init; } = Guid.NewGuid();
    public required YouTubeVideoId VideoId { get; init; }
    public required string Title { get; init; }
    public required string ChannelTitle { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsPlayed { get; init; }

    /// <summary>
    /// Gets screen reader friendly label for list item navigation.
    /// </summary>
    public string AccessibleLabel
    {
        get
        {
            var playedStatus = IsPlayed ? " (Played)" : "";
            var durationStr = Duration > TimeSpan.Zero
                ? $", duration {(int)Duration.TotalMinutes} minutes {Duration.Seconds} seconds"
                : "";
            return $"{Title}, by {ChannelTitle}{durationStr}{playedStatus}";
        }
    }

    public static PlaybackQueueItem FromSearchResult(SearchResult result) => new()
    {
        VideoId = new YouTubeVideoId(result.Id),
        Title = result.Title,
        ChannelTitle = result.ChannelTitle,
        Duration = result.Duration ?? TimeSpan.Zero,
        ThumbnailUrl = result.ThumbnailUrl
    };

    public static PlaybackQueueItem FromVideoDetails(VideoDetails details) => new()
    {
        VideoId = details.Id,
        Title = details.Title,
        ChannelTitle = details.Channel.Name,
        Duration = details.Duration,
        ThumbnailUrl = details.ThumbnailUrl
    };

    public override string ToString() => AccessibleLabel;
}
