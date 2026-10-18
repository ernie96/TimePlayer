using System.Text.Json;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public interface ILibraryService
{
    /// <summary>Folder holding the library JSON files.</summary>
    string DataDirectory { get; }

    IReadOnlyList<ChannelSubscription> Subscriptions { get; }
    IReadOnlyList<HistoryEntry> History { get; }
    IReadOnlyList<SavedVideo> WatchLater { get; }

    /// <summary>When the user last opened What's New; uploads after this are announced as new.</summary>
    DateTimeOffset FeedLastViewedUtc { get; }
    SubscriptionFeedResult? CachedFeed { get; }

    event EventHandler? Changed;

    bool IsSubscribed(string channelId);
    /// <returns>True when now subscribed.</returns>
    bool ToggleSubscription(string channelId, string channelName);
    void Unsubscribe(string channelId);
    /// <returns>Number of channels that were not already subscribed.</returns>
    int ImportSubscriptions(IEnumerable<ChannelSubscription> channels);

    HistoryEntry? GetHistory(string videoId);
    /// <summary>Records playback progress. Writes to disk at most every few seconds unless <paramref name="flush"/>.</summary>
    void RecordProgress(SearchResult video, TimeSpan position, TimeSpan duration, bool flush = false);
    void RemoveFromHistory(string videoId);
    void ClearHistory();
    /// <summary>Adds watch history from an export, keeping what's already here.</summary>
    /// <returns>Number of videos that were not already in history.</returns>
    int ImportHistory(IEnumerable<HistoryEntry> entries);

    bool IsInWatchLater(string videoId);
    /// <returns>True when now saved.</returns>
    bool ToggleWatchLater(SearchResult video);
    void RemoveFromWatchLater(string videoId);

    void SaveFeed(SubscriptionFeedResult feed);
    void MarkFeedViewed(DateTimeOffset whenUtc);

    /// <summary>All playlists; "Liked videos" first, then the user's in the order they were made.</summary>
    IReadOnlyList<Playlist> Playlists { get; }
    Playlist? GetPlaylist(string id);
    Playlist CreatePlaylist(string name, IEnumerable<SearchResult>? videos = null);
    void RenamePlaylist(string id, string name);
    void DeletePlaylist(string id);
    /// <returns>False when the video was already in the playlist.</returns>
    bool AddToPlaylist(string id, SearchResult video);
    void RemoveFromPlaylist(string id, string videoId);
    /// <summary>Moves a video up (negative) or down (positive). Returns its new index, or -1.</summary>
    int MoveInPlaylist(string id, string videoId, int offset);
    /// <summary>Saves (or refreshes) a YouTube playlist in the library.</summary>
    Playlist SaveYouTubePlaylist(string youTubeId, string title, string? owner, IEnumerable<SearchResult> videos);
    Playlist? FindSavedYouTubePlaylist(string youTubeId);

    bool IsLiked(string videoId);
    /// <returns>True when now liked.</returns>
    bool ToggleLike(SearchResult video);
    IReadOnlyList<SavedVideo> LikedVideos { get; }
    /// <returns>Number of videos that were not already there.</returns>
    int ImportLikes(IEnumerable<SearchResult> videos);
    /// <returns>Number of videos that were not already there.</returns>
    int ImportWatchLater(IEnumerable<SearchResult> videos);

    HiddenFromRecommendations Hidden { get; }
    void HideVideo(string videoId);
    void BlockChannel(string channelId, string channelName);
    void UnblockChannel(string channelId);
    bool IsHidden(SearchResult video);

    void Flush();
}

/// <summary>
/// Local, account-free library stored as JSON files, NewPipe style:
/// subscriptions.json (NewPipe export format, so it can be copied straight into NewPipe),
/// history.json, watch_later.json, feed.json, playlists.json and hidden.json.
/// Lives next to the executable for portable installs (when settings.json is there),
/// otherwise in %LOCALAPPDATA%\TimePlayer\library.
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private const int MaxHistoryEntries = 1000;
    private static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private List<ChannelSubscription> _subscriptions;
    private List<HistoryEntry> _history;
    private List<SavedVideo> _watchLater;
    private FeedFile _feed;
    private List<Playlist> _playlists;
    private HiddenFromRecommendations _hidden;
    private DateTimeOffset _lastHistorySave = DateTimeOffset.MinValue;
    private bool _historyDirty;

    public LibraryService() : this(DefaultDataDirectory(), () => DateTimeOffset.UtcNow)
    {
    }

    public LibraryService(string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        DataDirectory = dataDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _subscriptions = LoadSubscriptions();
        _history = Load<List<HistoryEntry>>(HistoryFile) ?? new();
        _watchLater = Load<List<SavedVideo>>(WatchLaterFile) ?? new();
        _feed = Load<FeedFile>(FeedFileName) ?? new();
        _playlists = Load<List<Playlist>>(PlaylistsFile) ?? new();
        _hidden = Load<HiddenFromRecommendations>(HiddenFile) ?? new();
    }

    public static string DefaultDataDirectory() => Configuration.AppPaths.LibraryFolder;

    public string DataDirectory { get; }

    private string SubscriptionsFile => Path.Combine(DataDirectory, "subscriptions.json");
    private string HistoryFile => Path.Combine(DataDirectory, "history.json");
    private string WatchLaterFile => Path.Combine(DataDirectory, "watch_later.json");
    private string FeedFileName => Path.Combine(DataDirectory, "feed.json");
    private string PlaylistsFile => Path.Combine(DataDirectory, "playlists.json");
    private string HiddenFile => Path.Combine(DataDirectory, "hidden.json");

    public const string LikedPlaylistId = "liked";

    public event EventHandler? Changed;

    public IReadOnlyList<ChannelSubscription> Subscriptions { get { lock (_gate) return _subscriptions.ToList(); } }
    public IReadOnlyList<HistoryEntry> History { get { lock (_gate) return _history.ToList(); } }
    public IReadOnlyList<SavedVideo> WatchLater { get { lock (_gate) return _watchLater.ToList(); } }
    public DateTimeOffset FeedLastViewedUtc { get { lock (_gate) return _feed.LastViewedUtc; } }

    public SubscriptionFeedResult? CachedFeed
    {
        get
        {
            lock (_gate)
            {
                return _feed.FetchedUtc == default
                    ? null
                    : new SubscriptionFeedResult(_feed.Videos.ToList(), Array.Empty<ChannelSubscription>(), _feed.FetchedUtc);
            }
        }
    }

    // --- Subscriptions ---

    public bool IsSubscribed(string channelId)
    {
        lock (_gate) return _subscriptions.Any(s => s.ChannelId == channelId);
    }

    public bool ToggleSubscription(string channelId, string channelName)
    {
        bool subscribed;
        lock (_gate)
        {
            var existing = _subscriptions.FindIndex(s => s.ChannelId == channelId);
            if (existing >= 0)
            {
                _subscriptions.RemoveAt(existing);
                subscribed = false;
            }
            else
            {
                _subscriptions.Add(new ChannelSubscription(channelId, channelName));
                SortSubscriptions();
                subscribed = true;
            }
            SaveSubscriptions();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return subscribed;
    }

    public void Unsubscribe(string channelId)
    {
        lock (_gate)
        {
            if (_subscriptions.RemoveAll(s => s.ChannelId == channelId) == 0)
                return;
            SaveSubscriptions();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int ImportSubscriptions(IEnumerable<ChannelSubscription> channels)
    {
        int added = 0;
        lock (_gate)
        {
            foreach (var channel in channels)
            {
                if (_subscriptions.Any(s => s.ChannelId == channel.ChannelId))
                    continue;
                _subscriptions.Add(channel);
                added++;
            }
            if (added > 0)
            {
                SortSubscriptions();
                SaveSubscriptions();
            }
        }
        if (added > 0)
            Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private void SortSubscriptions() =>
        _subscriptions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

    private List<ChannelSubscription> LoadSubscriptions()
    {
        try
        {
            if (File.Exists(SubscriptionsFile))
                return SubscriptionFormats.Parse(File.ReadAllText(SubscriptionsFile)).Channels.ToList();
        }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException or UnauthorizedAccessException)
        {
            KeepCorruptCopy(SubscriptionsFile);
        }
        return new();
    }

    private void SaveSubscriptions() => WriteAtomic(SubscriptionsFile, SubscriptionFormats.ToNewPipeJson(_subscriptions));

    // --- History ---

    public HistoryEntry? GetHistory(string videoId)
    {
        lock (_gate) return _history.FirstOrDefault(h => h.VideoId == videoId);
    }

    public void RecordProgress(SearchResult video, TimeSpan position, TimeSpan duration, bool flush = false)
    {
        if (string.IsNullOrWhiteSpace(video.Id) || video.Kind != SearchResultKind.Video)
            return;

        bool listChanged;
        lock (_gate)
        {
            var now = _clock();
            var index = _history.FindIndex(h => h.VideoId == video.Id);
            var previous = index >= 0 ? _history[index] : null;
            // A new viewing session starts when the video was last touched more than an hour ago.
            bool newSession = previous is null || now - previous.LastWatchedUtc > TimeSpan.FromHours(1);
            var entry = new HistoryEntry
            {
                VideoId = video.Id,
                Title = string.IsNullOrWhiteSpace(video.Title) ? previous?.Title ?? video.Id : video.Title,
                ChannelTitle = string.IsNullOrWhiteSpace(video.ChannelTitle) ? previous?.ChannelTitle ?? string.Empty : video.ChannelTitle,
                ChannelId = video.ChannelId ?? previous?.ChannelId,
                DurationSeconds = duration > TimeSpan.Zero ? duration.TotalSeconds : previous?.DurationSeconds ?? 0,
                PositionSeconds = Math.Max(0, position.TotalSeconds),
                LastWatchedUtc = now,
                WatchCount = (previous?.WatchCount ?? 0) + (newSession ? 1 : 0)
            };

            listChanged = index != 0;
            if (index >= 0)
                _history.RemoveAt(index);
            _history.Insert(0, entry);
            if (_history.Count > MaxHistoryEntries)
                _history.RemoveRange(MaxHistoryEntries, _history.Count - MaxHistoryEntries);

            _historyDirty = true;
            if (flush || listChanged || now - _lastHistorySave >= ProgressSaveInterval)
                SaveHistory();
        }
        if (listChanged)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFromHistory(string videoId)
    {
        lock (_gate)
        {
            if (_history.RemoveAll(h => h.VideoId == videoId) == 0)
                return;
            SaveHistory();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            SaveHistory();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int ImportHistory(IEnumerable<HistoryEntry> entries)
    {
        int added = 0;
        lock (_gate)
        {
            var known = new HashSet<string>(_history.Select(h => h.VideoId));
            foreach (var entry in entries)
            {
                if (known.Add(entry.VideoId))
                {
                    _history.Add(entry);
                    added++;
                }
            }
            if (added == 0)
                return 0;
            _history.Sort((a, b) => b.LastWatchedUtc.CompareTo(a.LastWatchedUtc));
            if (_history.Count > MaxHistoryEntries)
                _history.RemoveRange(MaxHistoryEntries, _history.Count - MaxHistoryEntries);
            SaveHistory();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private void SaveHistory()
    {
        WriteAtomic(HistoryFile, JsonSerializer.Serialize(_history, JsonOptions));
        _lastHistorySave = _clock();
        _historyDirty = false;
    }

    // --- Watch later ---

    public bool IsInWatchLater(string videoId)
    {
        lock (_gate) return _watchLater.Any(v => v.VideoId == videoId);
    }

    public bool ToggleWatchLater(SearchResult video)
    {
        bool saved;
        lock (_gate)
        {
            if (_watchLater.RemoveAll(v => v.VideoId == video.Id) > 0)
            {
                saved = false;
            }
            else
            {
                _watchLater.Add(SavedVideo.From(video, _clock()));
                saved = true;
            }
            WriteAtomic(WatchLaterFile, JsonSerializer.Serialize(_watchLater, JsonOptions));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public void RemoveFromWatchLater(string videoId)
    {
        lock (_gate)
        {
            if (_watchLater.RemoveAll(v => v.VideoId == videoId) == 0)
                return;
            WriteAtomic(WatchLaterFile, JsonSerializer.Serialize(_watchLater, JsonOptions));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // --- Feed cache ---

    public void SaveFeed(SubscriptionFeedResult feed)
    {
        lock (_gate)
        {
            _feed = _feed with { FetchedUtc = feed.FetchedUtc, Videos = feed.Videos.ToList() };
            WriteAtomic(FeedFileName, JsonSerializer.Serialize(_feed, JsonOptions));
        }
    }

    public void MarkFeedViewed(DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            _feed = _feed with { LastViewedUtc = whenUtc };
            WriteAtomic(FeedFileName, JsonSerializer.Serialize(_feed, JsonOptions));
        }
    }

    // --- Playlists ---

    public IReadOnlyList<Playlist> Playlists
    {
        get
        {
            lock (_gate)
            {
                var liked = _playlists.Where(p => p.Kind == PlaylistKind.Liked);
                return liked.Concat(_playlists.Where(p => p.Kind != PlaylistKind.Liked)).Select(Copy).ToList();
            }
        }
    }

    public Playlist? GetPlaylist(string id)
    {
        lock (_gate)
        {
            var p = _playlists.FirstOrDefault(p => p.Id == id);
            if (p is null && id == LikedPlaylistId)
                return NewLiked();
            return p is null ? null : Copy(p);
        }
    }

    public Playlist CreatePlaylist(string name, IEnumerable<SearchResult>? videos = null)
    {
        Playlist created;
        lock (_gate)
        {
            var now = _clock();
            created = new Playlist
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = CleanName(name),
                CreatedUtc = now,
                UpdatedUtc = now,
                Videos = Distinct(videos ?? Array.Empty<SearchResult>(), now)
            };
            _playlists.Add(created);
            SavePlaylists();
            created = Copy(created);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public void RenamePlaylist(string id, string name) =>
        UpdatePlaylist(id, p => p.Kind == PlaylistKind.Liked ? null : p with { Name = CleanName(name) });

    public void DeletePlaylist(string id)
    {
        lock (_gate)
        {
            if (_playlists.RemoveAll(p => p.Id == id) == 0)
                return;
            SavePlaylists();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool AddToPlaylist(string id, SearchResult video)
    {
        if (!video.IsVideo || string.IsNullOrWhiteSpace(video.Id))
            return false;
        bool added = false;
        UpdatePlaylist(id, p =>
        {
            if (p.Videos.Any(v => v.VideoId == video.Id))
                return null;
            added = true;
            var videos = p.Videos.ToList();
            videos.Add(SavedVideo.From(video, _clock()));
            return p with { Videos = videos };
        });
        return added;
    }

    public void RemoveFromPlaylist(string id, string videoId) =>
        UpdatePlaylist(id, p =>
        {
            var videos = p.Videos.Where(v => v.VideoId != videoId).ToList();
            return videos.Count == p.Videos.Count ? null : p with { Videos = videos };
        });

    public int MoveInPlaylist(string id, string videoId, int offset)
    {
        int newIndex = -1;
        UpdatePlaylist(id, p =>
        {
            var videos = p.Videos.ToList();
            var index = videos.FindIndex(v => v.VideoId == videoId);
            if (index < 0)
                return null;
            var target = Math.Clamp(index + offset, 0, videos.Count - 1);
            newIndex = target;
            if (target == index)
                return null;
            var item = videos[index];
            videos.RemoveAt(index);
            videos.Insert(target, item);
            return p with { Videos = videos };
        });
        return newIndex;
    }

    public Playlist SaveYouTubePlaylist(string youTubeId, string title, string? owner, IEnumerable<SearchResult> videos)
    {
        Playlist saved;
        lock (_gate)
        {
            var now = _clock();
            var index = _playlists.FindIndex(p => p.Kind == PlaylistKind.YouTube && p.YouTubeId == youTubeId);
            var list = Distinct(videos, now);
            if (index >= 0)
            {
                // Keep the user's chosen name if they renamed it.
                saved = _playlists[index] with { Owner = owner ?? _playlists[index].Owner, UpdatedUtc = now, Videos = list };
                _playlists[index] = saved;
            }
            else
            {
                saved = new Playlist
                {
                    Id = "yt-" + youTubeId,
                    Name = CleanName(title),
                    Kind = PlaylistKind.YouTube,
                    YouTubeId = youTubeId,
                    Owner = owner,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Videos = list
                };
                _playlists.Add(saved);
            }
            SavePlaylists();
            saved = Copy(saved);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public Playlist? FindSavedYouTubePlaylist(string youTubeId)
    {
        lock (_gate)
        {
            var p = _playlists.FirstOrDefault(p => p.Kind == PlaylistKind.YouTube && p.YouTubeId == youTubeId);
            return p is null ? null : Copy(p);
        }
    }

    // --- Likes ---

    public bool IsLiked(string videoId)
    {
        lock (_gate) return LikedOrNull()?.Videos.Any(v => v.VideoId == videoId) == true;
    }

    public IReadOnlyList<SavedVideo> LikedVideos
    {
        get { lock (_gate) return LikedOrNull()?.Videos.ToList() ?? new List<SavedVideo>(); }
    }

    public bool ToggleLike(SearchResult video)
    {
        bool liked;
        lock (_gate)
        {
            var playlist = EnsureLiked();
            var videos = playlist.Videos.ToList();
            if (videos.RemoveAll(v => v.VideoId == video.Id) > 0)
            {
                liked = false;
            }
            else
            {
                // Newest likes first, like YouTube.
                videos.Insert(0, SavedVideo.From(video, _clock()));
                liked = true;
            }
            ReplacePlaylist(playlist with { Videos = videos, UpdatedUtc = _clock() });
            SavePlaylists();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return liked;
    }

    public int ImportLikes(IEnumerable<SearchResult> videos)
    {
        int added = 0;
        lock (_gate)
        {
            var playlist = EnsureLiked();
            var list = playlist.Videos.ToList();
            var now = _clock();
            foreach (var v in videos)
            {
                if (!v.IsVideo || string.IsNullOrWhiteSpace(v.Id) || list.Any(x => x.VideoId == v.Id))
                    continue;
                list.Add(SavedVideo.From(v, now));
                added++;
            }
            if (added > 0)
            {
                ReplacePlaylist(playlist with { Videos = list, UpdatedUtc = now });
                SavePlaylists();
            }
        }
        if (added > 0)
            Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public int ImportWatchLater(IEnumerable<SearchResult> videos)
    {
        int added = 0;
        lock (_gate)
        {
            var now = _clock();
            foreach (var v in videos)
            {
                if (!v.IsVideo || string.IsNullOrWhiteSpace(v.Id) || _watchLater.Any(x => x.VideoId == v.Id))
                    continue;
                _watchLater.Add(SavedVideo.From(v, now));
                added++;
            }
            if (added > 0)
                WriteAtomic(WatchLaterFile, JsonSerializer.Serialize(_watchLater, JsonOptions));
        }
        if (added > 0)
            Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    private Playlist? LikedOrNull() => _playlists.FirstOrDefault(p => p.Kind == PlaylistKind.Liked);

    private Playlist EnsureLiked()
    {
        var liked = LikedOrNull();
        if (liked is null)
        {
            liked = NewLiked();
            _playlists.Insert(0, liked);
        }
        return liked;
    }

    private Playlist NewLiked() => new()
    {
        Id = LikedPlaylistId,
        Name = "Liked videos",
        Kind = PlaylistKind.Liked,
        CreatedUtc = _clock(),
        UpdatedUtc = _clock()
    };

    private void UpdatePlaylist(string id, Func<Playlist, Playlist?> change)
    {
        lock (_gate)
        {
            var current = _playlists.FirstOrDefault(p => p.Id == id);
            if (current is null && id == LikedPlaylistId)
                current = EnsureLiked();
            if (current is null)
                return;
            var updated = change(current);
            if (updated is null)
                return;
            ReplacePlaylist(updated with { UpdatedUtc = _clock() });
            SavePlaylists();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReplacePlaylist(Playlist playlist)
    {
        var index = _playlists.FindIndex(p => p.Id == playlist.Id);
        if (index >= 0)
            _playlists[index] = playlist;
        else
            _playlists.Add(playlist);
    }

    private static Playlist Copy(Playlist p) => p with { Videos = p.Videos.ToList() };

    private static List<SavedVideo> Distinct(IEnumerable<SearchResult> videos, DateTimeOffset now) =>
        videos.Where(v => v.IsVideo && !string.IsNullOrWhiteSpace(v.Id))
            .DistinctBy(v => v.Id)
            .Select(v => SavedVideo.From(v, now))
            .ToList();

    private static string CleanName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Untitled playlist" : name.Trim();

    private void SavePlaylists() => WriteAtomic(PlaylistsFile, JsonSerializer.Serialize(_playlists, JsonOptions));

    // --- Not interested ---

    public HiddenFromRecommendations Hidden
    {
        get { lock (_gate) return new HiddenFromRecommendations { VideoIds = _hidden.VideoIds.ToList(), Channels = _hidden.Channels.ToList() }; }
    }

    public void HideVideo(string videoId)
    {
        lock (_gate)
        {
            if (_hidden.VideoIds.Contains(videoId))
                return;
            _hidden.VideoIds.Add(videoId);
            // Keep the file small; old entries rarely come back into recommendations anyway.
            if (_hidden.VideoIds.Count > 2000)
                _hidden.VideoIds.RemoveRange(0, _hidden.VideoIds.Count - 2000);
            SaveHidden();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void BlockChannel(string channelId, string channelName)
    {
        lock (_gate)
        {
            if (_hidden.Channels.Any(c => c.ChannelId == channelId))
                return;
            _hidden.Channels.Add(new ChannelSubscription(channelId, channelName));
            SaveHidden();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UnblockChannel(string channelId)
    {
        lock (_gate)
        {
            if (_hidden.Channels.RemoveAll(c => c.ChannelId == channelId) == 0)
                return;
            SaveHidden();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsHidden(SearchResult video)
    {
        lock (_gate)
        {
            return _hidden.VideoIds.Contains(video.Id) ||
                   (video.ChannelId is { } c && _hidden.Channels.Any(b => b.ChannelId == c)) ||
                   (!string.IsNullOrWhiteSpace(video.ChannelTitle) &&
                    _hidden.Channels.Any(b => string.Equals(b.Name, video.ChannelTitle, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void SaveHidden() => WriteAtomic(HiddenFile, JsonSerializer.Serialize(_hidden, JsonOptions));

    public void Flush()
    {
        lock (_gate)
        {
            if (_historyDirty)
                SaveHistory();
        }
    }

    // --- Storage helpers ---

    private T? Load<T>(string path) where T : class
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            KeepCorruptCopy(path);
        }
        return null;
    }

    /// <summary>Never silently overwrite a file we could not read; the user may want to recover it.</summary>
    private static void KeepCorruptCopy(string path)
    {
        try { File.Copy(path, path + ".unreadable", overwrite: true); } catch { }
    }

    private void WriteAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Library data is best-effort; in-memory state stays correct and the next change retries.
        }
    }

    private sealed record FeedFile
    {
        public DateTimeOffset LastViewedUtc { get; init; }
        public DateTimeOffset FetchedUtc { get; init; }
        public List<SearchResult> Videos { get; init; } = new();
    }
}
