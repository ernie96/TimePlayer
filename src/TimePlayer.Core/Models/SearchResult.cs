using static TimePlayer.Core.Localization.Loc;

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

    /// <summary>"1.2 million views", for the list row.</summary>
    public string? ViewCountText => ViewCount is { } views ? Accessibility.AccessibleTextFormatter.FormatViewCount(views) : null;
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
                var count = PlaylistVideoCount is { } n ? ", " + Plural(n, "{0} video", "{0} videos") : string.Empty;
                var owner = string.IsNullOrWhiteSpace(ChannelTitle) ? string.Empty : ", " + T("by {0}", ChannelTitle);
                return T("Playlist: {0}", Title) + count + owner;
            }

            if (Kind == SearchResultKind.Channel)
            {
                var channelParts = new List<string> { T("Channel: {0}", Title) };
                if (!string.IsNullOrWhiteSpace(StatusPrefix))
                    channelParts.Add(StatusPrefix);
                return string.Join(". ", channelParts);
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(StatusPrefix))
                parts.Add(StatusPrefix);
            parts.Add(Title);
            parts.Add(T("by {0}", ChannelTitle));
            if (IsLive)
                parts.Add(T("Live now"));
            else if (IsShort)
                parts.Add(T("Short"));

            if (Duration.HasValue && Duration.Value > TimeSpan.Zero)
            {
                parts.Add(Accessibility.AccessibleTextFormatter.FormatTimeSpoken(Duration.Value));
            }

            if (ViewCount.HasValue)
            {
                parts.Add(Accessibility.AccessibleTextFormatter.FormatViewCount(ViewCount.Value));
            }

            if (PublishedAt.HasValue)
            {
                parts.Add(Accessibility.AccessibleTextFormatter.FormatAgeSpoken(PublishedAt.Value, DateTimeOffset.UtcNow));
            }
            else if (!string.IsNullOrWhiteSpace(UploadDateText))
            {
                parts.Add(Accessibility.AccessibleTextFormatter.TranslateAge(UploadDateText) ?? UploadDateText);
            }

            return string.Join(". ", parts);
        }
    }


    public override string ToString() => AccessibleSummary;
}

