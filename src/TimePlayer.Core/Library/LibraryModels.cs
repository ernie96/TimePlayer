using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public sealed record ChannelSubscription(string ChannelId, string Name)
{
    public string Url => $"https://www.youtube.com/channel/{ChannelId}";

    public SearchResult ToSearchResult() => new()
    {
        Kind = SearchResultKind.Channel,
        Id = ChannelId,
        Title = Name,
        ChannelTitle = Name,
        ChannelId = ChannelId
    };
}

/// <summary>A watched (or partly watched) video, used for history and resume.</summary>
public sealed record HistoryEntry
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public string ChannelTitle { get; init; } = string.Empty;
    public string? ChannelId { get; init; }
    public double DurationSeconds { get; init; }
    public double PositionSeconds { get; init; }
    public DateTimeOffset LastWatchedUtc { get; init; }
    public int WatchCount { get; init; } = 1;

    /// <summary>Treat the last 5% (or last 20 seconds) as finished, so credits don't leave a stale resume point.</summary>
    public bool IsFinished =>
        DurationSeconds > 0 &&
        (PositionSeconds >= DurationSeconds * 0.95 || DurationSeconds - PositionSeconds <= 20);

    /// <summary>Position to resume from, or null when there is nothing worth resuming.</summary>
    public TimeSpan? ResumePosition =>
        !IsFinished && PositionSeconds >= 15 ? TimeSpan.FromSeconds(PositionSeconds) : null;

    public SearchResult ToSearchResult() => new()
    {
        Id = VideoId,
        Title = Title,
        ChannelTitle = ChannelTitle,
        ChannelId = ChannelId,
        Duration = DurationSeconds > 0 ? TimeSpan.FromSeconds(DurationSeconds) : null,
        PublishedAt = null,
        UploadDateText = null
    };
}

/// <summary>A video saved to Watch Later.</summary>
public sealed record SavedVideo
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public string ChannelTitle { get; init; } = string.Empty;
    public string? ChannelId { get; init; }
    public double? DurationSeconds { get; init; }
    public DateTimeOffset AddedUtc { get; init; }

    public static SavedVideo From(SearchResult r, DateTimeOffset now) => new()
    {
        VideoId = r.Id,
        Title = r.Title,
        ChannelTitle = r.ChannelTitle,
        ChannelId = r.ChannelId,
        DurationSeconds = r.Duration?.TotalSeconds,
        AddedUtc = now
    };

    public SearchResult ToSearchResult() => new()
    {
        Id = VideoId,
        Title = Title,
        ChannelTitle = ChannelTitle,
        ChannelId = ChannelId,
        Duration = DurationSeconds is > 0 ? TimeSpan.FromSeconds(DurationSeconds.Value) : null
    };
}

/// <summary>Latest uploads of one channel, from its public RSS feed.</summary>
public sealed record ChannelUploads(string ChannelId, string ChannelName, IReadOnlyList<SearchResult> Videos);

public sealed record SubscriptionFeedResult(
    IReadOnlyList<SearchResult> Videos,
    IReadOnlyList<ChannelSubscription> FailedChannels,
    DateTimeOffset FetchedUtc);

public enum FeaturedCategory
{
    /// <summary>A mix of what YouTube's Music, Sports, News and Gaming pages feature now.</summary>
    Trending = 0,
    /// <summary>YouTube Charts: the region's Top 100 music videos.</summary>
    Music = 1,
    /// <summary>Gaming videos from this week.</summary>
    Gaming = 2,
    /// <summary>The most watched movie trailers this week.</summary>
    Movies = 3,
    News = 4,
    Sports = 5,
    Live = 6
}

public enum PlaylistKind
{
    /// <summary>Made in TimePlayer (or imported from YouTube Takeout).</summary>
    Local,
    /// <summary>The "Liked videos" playlist, filled by Like.</summary>
    Liked,
    /// <summary>A YouTube playlist saved to the library; its videos are refreshed from YouTube.</summary>
    YouTube
}

public sealed record Playlist
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public PlaylistKind Kind { get; init; } = PlaylistKind.Local;
    /// <summary>For saved YouTube playlists: the playlist id after "list=".</summary>
    public string? YouTubeId { get; init; }
    public string? Owner { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public List<SavedVideo> Videos { get; init; } = new();

    public SearchResult ToSearchResult() => new()
    {
        Kind = SearchResultKind.Playlist,
        Id = YouTubeId ?? Id,
        LibraryPlaylistId = Id,
        Title = Name,
        ChannelTitle = Kind == PlaylistKind.YouTube ? Owner ?? string.Empty : string.Empty,
        PlaylistVideoCount = Videos.Count
    };
}

/// <summary>Things the user asked not to be recommended.</summary>
public sealed record HiddenFromRecommendations
{
    public List<string> VideoIds { get; init; } = new();
    public List<ChannelSubscription> Channels { get; init; } = new();
}

/// <summary>A YouTube playlist's title and one page of its videos.</summary>
public sealed record YouTubePlaylistPage(string PlaylistId, string Title, string? Owner, IReadOnlyList<SearchResult> Videos, string? NextPageToken);
