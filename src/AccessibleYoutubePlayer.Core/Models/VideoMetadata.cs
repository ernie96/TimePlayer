namespace AccessibleYoutubePlayer.Core.Models;

/// <summary>
/// Represents full metadata for a video result.
/// Designed specifically to provide rich, accessible summaries to screen readers.
/// </summary>
public record VideoMetadata
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Author { get; init; } = string.Empty;
    public string ChannelId { get; init; } = string.Empty;
    public string ChannelUrl { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; } = TimeSpan.Zero;
    public long ViewCount { get; init; }
    public string PublishedText { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ThumbnailUrl { get; init; } = string.Empty;
    public bool IsLive { get; init; }

    /// <summary>
    /// Human-readable duration formatted as "H hours M minutes S seconds" or "M minutes S seconds".
    /// </summary>
    public string FormattedDuration
    {
        get
        {
            if (IsLive) return "Live stream";
            if (Duration <= TimeSpan.Zero) return "Unknown duration";
            if (Duration.TotalHours >= 1)
            {
                return $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}";
            }
            return $"{Duration.Minutes}:{Duration.Seconds:D2}";
        }
    }

    /// <summary>
    /// Accessible summary string carefully crafted for screen readers to convey
    /// all key information in a natural, unambiguous sentence.
    /// </summary>
    public string AccessibleSummary
    {
        get
        {
            var authorPart = string.IsNullOrWhiteSpace(Author) ? string.Empty : $", by {Author}";
            var durationPart = IsLive ? ", Live stream" : $", duration {FormattedDuration}";
            var viewsPart = ViewCount > 0 ? $", {ViewCount:N0} views" : string.Empty;
            var pubPart = string.IsNullOrWhiteSpace(PublishedText) ? string.Empty : $", uploaded {PublishedText}";

            return $"{Title}{authorPart}{durationPart}{viewsPart}{pubPart}";
        }
    }
}
