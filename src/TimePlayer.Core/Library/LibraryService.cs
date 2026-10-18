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

    bool IsInWatchLater(string videoId);
    /// <returns>True when now saved.</returns>
    bool ToggleWatchLater(SearchResult video);
    void RemoveFromWatchLater(string videoId);

    void SaveFeed(SubscriptionFeedResult feed);
    void MarkFeedViewed(DateTimeOffset whenUtc);

    void Flush();
}

/// <summary>
/// Local, account-free library stored as JSON files, NewPipe style:
/// subscriptions.json (NewPipe export format, so it can be copied straight into NewPipe),
/// history.json, watch_later.json and feed.json.
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
    }

    public static string DefaultDataDirectory() => Configuration.AppPaths.LibraryFolder;

    public string DataDirectory { get; }

    private string SubscriptionsFile => Path.Combine(DataDirectory, "subscriptions.json");
    private string HistoryFile => Path.Combine(DataDirectory, "history.json");
    private string WatchLaterFile => Path.Combine(DataDirectory, "watch_later.json");
    private string FeedFileName => Path.Combine(DataDirectory, "feed.json");

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
