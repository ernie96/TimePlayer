namespace TimePlayer.Core.Models;

/// <summary>
/// Detailed metadata for a YouTube video.
/// </summary>
public record VideoMetadata
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ChannelTitle { get; init; }
    public string? ChannelId { get; init; }
    public string? Description { get; init; }
    public TimeSpan Duration { get; init; }
    public long? ViewCount { get; init; }
    public DateTimeOffset? UploadDate { get; init; }
    public string? ThumbnailUrl { get; init; }
    public IReadOnlyList<StreamInfo> Streams { get; init; } = Array.Empty<StreamInfo>();
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = Array.Empty<SubtitleTrack>();

    /// <summary>
    /// Gets a screen reader friendly status summary for the currently loaded video.
    /// </summary>
    public string AccessiblePlaybackHeader =>
        $"{Title}, by {ChannelTitle}, duration {(int)Duration.TotalMinutes} minutes {Duration.Seconds} seconds.";
}
