namespace TimePlayer.Core.Models;

public enum SearchResultKind
{
    Video = 0,
    /// <summary>A subscribed channel; <see cref="SearchResult.Id"/> holds the channel id.</summary>
    Channel = 1,
    /// <summary>A playlist (local or YouTube); <see cref="SearchResult.Id"/> holds its library or YouTube id.</summary>
    Playlist = 2
}

/// <summary>
/// Represents a single YouTube search result or queue item.
/// </summary>
public record SearchResult
{
    public SearchResultKind Kind { get; init; } = SearchResultKind.Video;
    public DateTimeOffset? PublishedAt { get; init; }
    public bool IsShort { get; init; }
    public bool IsLive { get; init; }

    /// <summary>
    /// Library status spoken first, e.g. "New", "Watched" or "Resume at 3 minutes 12 seconds".
    /// </summary>
    public string? StatusPrefix { get; init; }

    public bool IsVideo => Kind == SearchResultKind.Video;

    /// <summary>Number of videos, for playlist rows.</summary>
    public int? PlaylistVideoCount { get; init; }

    /// <summary>Set on playlist rows that are in the user's library (Playlists section); null for YouTube search results.</summary>
    public string? LibraryPlaylistId { get; init; }

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string ChannelTitle { get; init; }
    public string? ChannelId { get; init; }
    public TimeSpan? Duration { get; init; }
    public long? ViewCount { get; init; }
    public string? UploadDateText { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? DescriptionSnippet { get; init; }

    /// <summary>
    /// Gets a fully composed, natural speech summary designed specifically for screen readers.
    /// Follows accessibility guidelines: Title, Channel, Duration in words, and Views.
    /// </summary>
    public string AccessibleSummary
    {
        get
        {
            if (Kind == SearchResultKind.Playlist)
            {
                var count = PlaylistVideoCount is { } n ? (n == 1 ? ", 1 video" : $", {n} videos") : string.Empty;
                var owner = string.IsNullOrWhiteSpace(ChannelTitle) ? string.Empty : $", by {ChannelTitle}";
                return $"Playlist: {Title}{count}{owner}";
            }

            if (Kind == SearchResultKind.Channel)
            {
                var channelParts = new List<string> { $"Channel: {Title}" };
                if (!string.IsNullOrWhiteSpace(StatusPrefix))
                    channelParts.Add(StatusPrefix);
                return string.Join(". ", channelParts);
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(StatusPrefix))
                parts.Add(StatusPrefix);
            parts.Add(Title);
            parts.Add($"by {ChannelTitle}");
            if (IsLive)
                parts.Add("Live now");
            else if (IsShort)
                parts.Add("Short");

            if (Duration.HasValue && Duration.Value > TimeSpan.Zero)
            {
                parts.Add(Accessibility.AccessibleTextFormatter.FormatTimeSpoken(Duration.Value));
            }

            if (ViewCount.HasValue)
            {
                parts.Add(FormatViewCount(ViewCount.Value));
            }

            if (PublishedAt.HasValue)
            {
                parts.Add(Accessibility.AccessibleTextFormatter.FormatAgeSpoken(PublishedAt.Value, DateTimeOffset.UtcNow));
            }
            else if (!string.IsNullOrWhiteSpace(UploadDateText))
            {
                parts.Add(UploadDateText);
            }

            return string.Join(". ", parts);
        }
    }

    private static string FormatViewCount(long count)
    {
        return count switch
        {
            >= 1_000_000_000 => $"{count / 1_000_000_000.0:0.#} billion views",
            >= 1_000_000 => $"{count / 1_000_000.0:0.#} million views",
            >= 1_000 => $"{count / 1_000.0:0.#} thousand views",
            1 => "1 view",
            _ => $"{count} views"
        };
    }

    public override string ToString() => AccessibleSummary;
}

