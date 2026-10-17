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

    /// <summary>
    /// Gets natural speech summary for screen reader announcements when loading video details.
    /// </summary>
    public string AccessiblePlaybackHeader
    {
        get
        {
            var parts = new List<string> { Title, $"by {Channel.Name}" };

            if (IsLive)
            {
                parts.Add("Live stream");
            }
            else if (Duration > TimeSpan.Zero)
            {
                var d = Duration;
                if (d.TotalHours >= 1)
                {
                    parts.Add($"{(int)d.TotalHours} hour{( (int)d.TotalHours == 1 ? "" : "s" )}, {d.Minutes} minute{( d.Minutes == 1 ? "" : "s" )}, {d.Seconds} second{( d.Seconds == 1 ? "" : "s" )}");
                }
                else
                {
                    parts.Add($"{d.Minutes} minute{( d.Minutes == 1 ? "" : "s" )}, {d.Seconds} second{( d.Seconds == 1 ? "" : "s" )}");
                }
            }

            if (ViewCount.HasValue)
            {
                parts.Add(FormatCount(ViewCount.Value, "views"));
            }

            return string.Join(". ", parts);
        }
    }

    private static string FormatCount(long count, string unit)
    {
        return count switch
        {
            >= 1_000_000_000 => $"{count / 1_000_000_000.0:0.#} billion {unit}",
            >= 1_000_000 => $"{count / 1_000_000.0:0.#} million {unit}",
            >= 1_000 => $"{count / 1_000.0:0.#} thousand {unit}",
            1 => $"1 {unit.TrimEnd('s')}",
            _ => $"{count} {unit}"
        };
    }
}
