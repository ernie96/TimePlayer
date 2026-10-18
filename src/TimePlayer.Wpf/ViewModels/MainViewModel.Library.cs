using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

namespace TimePlayer.Wpf.ViewModels;

public enum BrowseSection
{
    Search,
    Featured,
    WhatsNew,
    Channels,
    History,
    WatchLater,
    Queue,
    /// <summary>Uploads of one channel, opened from any list; Back returns to the previous section.</summary>
    Channel,
    /// <summary>Recommendations from history, likes and subscriptions.</summary>
    ForYou,
    /// <summary>Liked videos, your playlists and saved YouTube playlists.</summary>
    Playlists,
    /// <summary>The videos of one playlist, opened from any list; Back returns.</summary>
    Playlist
}

public sealed record SectionTab(BrowseSection Section, string Header, string Shortcut)
{
    public string AccessibleName => $"{Header}, {Shortcut}";
    public override string ToString() => Header;
}

/// <summary>
/// Library and discovery: Featured, What's New (subscription feed), Channels, History,
/// Watch Later, channel pages, resume playback and the library actions on list items.
/// All sections share the results list so every key and action works the same everywhere.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan FeaturedCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FeedAutoRefreshAge = TimeSpan.FromMinutes(30);

    private readonly ILibraryService _library;
    private readonly IDiscoveryService _discovery;

    private List<SearchResult> _lastSearchResults = new();
    private readonly Dictionary<FeaturedCategory, (DateTimeOffset Fetched, IReadOnlyList<SearchResult> Items, string? NextPage)> _featuredCache = new();
    private CancellationTokenSource? _sectionCts;
    private bool _syncingTab;
    private SubscriptionFeedResult? _feed;
    private Task? _feedRefreshTask;

    /// <summary>The video currently in the player, as a library item (for history, watch later, subscribe).</summary>
    private SearchResult? _currentLibraryVideo;
    private string? _resumeHandledForId;

    public ObservableCollection<SectionTab> SectionTabs { get; } = new()
    {
        new(BrowseSection.Search, "Search", "Control+1"),
        new(BrowseSection.Featured, "Featured", "Control+2"),
        new(BrowseSection.WhatsNew, "What's New", "Control+3"),
        new(BrowseSection.Channels, "Channels", "Control+4"),
        new(BrowseSection.History, "History", "Control+5"),
        new(BrowseSection.WatchLater, "Watch Later", "Control+6"),
        new(BrowseSection.Queue, "Queue", "Control+7"),
        new(BrowseSection.ForYou, "For You", "Control+8"),
        new(BrowseSection.Playlists, "Playlists", "Control+9"),
    };

    public IReadOnlyList<FeaturedCategory> FeaturedCategories { get; } = Enum.GetValues<FeaturedCategory>();

    [ObservableProperty]
    private SectionTab? _selectedSectionTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFeaturedSection), nameof(IsChannelsSection), nameof(IsHistorySection), nameof(IsChannelView),
        nameof(IsForYouSection), nameof(IsPlaylistsSection), nameof(IsPlaylistView), nameof(IsDetailView), nameof(IsSearchSection))]
    private BrowseSection _currentSection = BrowseSection.Search;

    [ObservableProperty]
    private string _sectionTitle = "Search results";

    [ObservableProperty]
    private string _sectionEmptyText = "Type keywords or paste a YouTube video link above and press Enter to search.\nOr press Control+2 for featured videos.";

    [ObservableProperty]
    private bool _isSectionLoading;

    [ObservableProperty]
    private FeaturedCategory _selectedFeaturedCategory = FeaturedCategory.Trending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubscribeButtonText))]
    private bool _isCurrentChannelSubscribed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchLaterButtonText))]
    private bool _isCurrentVideoInWatchLater;

    // Library preferences (Settings dialog)
    [ObservableProperty] private bool _resumePlayback = true;
    [ObservableProperty] private bool _recordHistory = true;
    [ObservableProperty] private bool _hideShortsInFeed;
    [ObservableProperty] private bool _checkFeedOnStartup = true;
    [ObservableProperty] private string _featuredRegion = "US";

    public bool IsFeaturedSection => CurrentSection == BrowseSection.Featured;
    public bool IsChannelsSection => CurrentSection == BrowseSection.Channels;
    public bool IsHistorySection => CurrentSection == BrowseSection.History;
    public bool IsChannelView => CurrentSection == BrowseSection.Channel;

    public string SubscribeButtonText => IsCurrentChannelSubscribed ? "Unsubscribe" : "Subscribe";
    public string WatchLaterButtonText => IsCurrentVideoInWatchLater ? "Remove from Watch Later" : "Watch Later";

    private void InitializeLibraryPreferences()
    {
        var lib = _configService.CurrentConfig.Library;
        _resumePlayback = lib.ResumePlayback;
        _recordHistory = lib.RecordHistory;
        _hideShortsInFeed = lib.HideShortsInFeed;
        _checkFeedOnStartup = lib.CheckFeedOnStartup;
        _featuredRegion = lib.FeaturedRegion;
        _selectedSectionTab = SectionTabs[0];
        _feed = _library.CachedFeed;
    }

    partial void OnSelectedSectionTabChanged(SectionTab? value)
    {
        // Arrowing through the tabs shows each section but leaves focus on the tabs.
        if (value is not null && !_syncingTab && value.Section != CurrentSection)
            _ = ShowSectionAsync(value.Section, moveFocus: false);
    }

    partial void OnSelectedFeaturedCategoryChanged(FeaturedCategory value)
    {
        if (CurrentSection == BrowseSection.Featured)
            _ = ShowSectionAsync(BrowseSection.Featured, moveFocus: false);
    }

    partial void OnHideShortsInFeedChanged(bool value)
    {
        if (CurrentSection == BrowseSection.WhatsNew)
            _ = ShowSectionAsync(BrowseSection.WhatsNew, moveFocus: false);
    }

    // --- Section navigation ---

    [RelayCommand]
    public Task ShowSectionByNumberAsync(int number) =>
        number >= 1 && number <= SectionTabs.Count
            ? ShowSectionAsync(SectionTabs[number - 1].Section, moveFocus: true)
            : Task.CompletedTask;

    [RelayCommand]
    public Task RefreshSectionAsync() => ShowSectionAsync(CurrentSection, moveFocus: false, forceRefresh: true);

    public async Task ShowSectionAsync(BrowseSection section, bool moveFocus, bool forceRefresh = false)
    {
        _sectionCts?.Cancel();
        _sectionCts?.Dispose();
        _sectionCts = new CancellationTokenSource();
        var token = _sectionCts.Token;

        CurrentSection = section;
        SyncSelectedTab(section);
        SetLoadMore(null);

        try
        {
            switch (section)
            {
                case BrowseSection.Search:
                    SectionTitle = "Search results";
                    SectionEmptyText = "Type keywords or paste a YouTube video link above and press Enter to search.\nOr press Control+2 for featured videos.";
                    ShowItems(_lastSearchResults, moveFocus);
                    if (_lastSearchResults.Count > 0 && _lastSearchResults[0].Kind != SearchResultKind.Playlist)
                        ConfigureSearchPaging();
                    break;

                case BrowseSection.Featured:
                    await ShowFeaturedAsync(moveFocus, forceRefresh, token);
                    break;

                case BrowseSection.WhatsNew:
                    await ShowWhatsNewAsync(moveFocus, forceRefresh, token);
                    break;

                case BrowseSection.Channels:
                    var subs = _library.Subscriptions;
                    SectionTitle = "Channels";
                    SectionEmptyText = "You are not subscribed to any channels yet.\nPress Control+S on a video to subscribe to its channel, or choose \"How to get my YouTube subscriptions\" above to bring them from YouTube. Import subscriptions also reads NewPipe, LibreTube and FreeTube files.";
                    ShowItems(subs.Select(s => s.ToSearchResult()).ToList(), moveFocus);
                    break;

                case BrowseSection.History:
                    SectionTitle = "History";
                    SectionEmptyText = RecordHistory
                        ? "Nothing watched yet. Videos you play appear here, newest first, and continue where you left off."
                        : "Watch history is turned off in Settings (F2).";
                    var now = DateTimeOffset.UtcNow;
                    ShowItems(_library.History
                        .Select(h => h.ToSearchResult() with { UploadDateText = $"watched {AccessibleTextFormatter.FormatAgeSpoken(h.LastWatchedUtc, now)}" })
                        .ToList(), moveFocus);
                    break;

                case BrowseSection.WatchLater:
                    SectionTitle = "Watch Later";
                    SectionEmptyText = "Watch Later is empty. Press Control+L on any video, or in the player, to save it here.";
                    ShowItems(_library.WatchLater.Select(v => v.ToSearchResult()).ToList(), moveFocus);
                    break;

                case BrowseSection.Channel:
                    await ShowChannelUploadsAsync(moveFocus, token);
                    break;

                case BrowseSection.ForYou:
                    await ShowForYouAsync(moveFocus, forceRefresh, token);
                    break;

                case BrowseSection.Playlists:
                    ShowPlaylists(moveFocus);
                    break;

                case BrowseSection.Playlist:
                    await ShowPlaylistPageAsync(moveFocus, forceRefresh, token);
                    break;

                case BrowseSection.Queue:
                    SectionTitle = "Queue";
                    SectionEmptyText = "The queue is empty. Press Control+Q on any video to add it; queued videos play one after another.";
                    SearchResults.Clear();
                    IsSectionLoading = false;
                    _accessibility.Announce(PlaybackQueue.Count == 0
                        ? SectionEmptyText
                        : $"{PlaybackQueue.Count} video{(PlaybackQueue.Count == 1 ? "" : "s")}", AnnouncementPriority.Medium);
                    if (moveFocus && PlaybackQueue.Count > 0)
                        RequestFocusResults?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer section request replaced this one.
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsSectionLoading = false;
        }
    }

    private void SyncSelectedTab(BrowseSection section)
    {
        if (section is BrowseSection.Channel or BrowseSection.Playlist)
            return; // keep the tab the page was opened from
        _syncingTab = true;
        SelectedSectionTab = SectionTabs.First(t => t.Section == section);
        _syncingTab = false;
    }

    private async Task ShowFeaturedAsync(bool moveFocus, bool forceRefresh, CancellationToken token)
    {
        var category = SelectedFeaturedCategory;
        SectionTitle = category switch
        {
            FeaturedCategory.Trending => "Featured",
            FeaturedCategory.Music => "Featured: top music videos",
            FeaturedCategory.Gaming => "Featured: gaming this week",
            FeaturedCategory.Movies => "Featured: movie trailers this week",
            FeaturedCategory.Live => "Featured: live now",
            _ => $"Featured: {category}"
        };
        SectionEmptyText = "Featured videos could not be loaded. Press F5 to try again.";

        if (!forceRefresh && _featuredCache.TryGetValue(category, out var cached) && DateTimeOffset.UtcNow - cached.Fetched < FeaturedCacheLifetime)
        {
            ShowItems(cached.Items, moveFocus);
            ConfigureFeaturedPaging(category, cached.NextPage);
            return;
        }

        BeginLoading("Loading...");
        var region = FeaturedRegion;
        var result = await _discovery.GetFeaturedAsync(category, region, null, token);
        token.ThrowIfCancellationRequested();
        if (result.IsSuccess)
        {
            _featuredCache[category] = (DateTimeOffset.UtcNow, result.Value.Items, result.Value.NextPageToken);
            ShowItems(result.Value.Items, moveFocus);
            ConfigureFeaturedPaging(category, result.Value.NextPageToken);
        }
        else
        {
            ShowItems(Array.Empty<SearchResult>(), moveFocus: false);
            _accessibility.Announce($"Featured videos are unavailable right now. {result.Error?.Message} Press F5 to retry.", AnnouncementPriority.High);
        }
    }

    /// <summary>Music, Gaming and Movies keep loading as you reach the end of the list.</summary>
    private void ConfigureFeaturedPaging(FeaturedCategory category, string? firstToken)
    {
        var next = firstToken;
        if (next is null)
        {
            SetLoadMore(null);
            return;
        }
        var region = FeaturedRegion;
        SetLoadMore(async token =>
        {
            var page = await _discovery.GetFeaturedAsync(category, region, next, token);
            if (page.IsFailure)
                return (Array.Empty<SearchResult>(), false);
            next = page.Value.NextPageToken;
            return (page.Value.Items, next is not null);
        });
    }

    private async Task ShowWhatsNewAsync(bool moveFocus, bool forceRefresh, CancellationToken token)
    {
        SectionTitle = "What's New";
        var subs = _library.Subscriptions;
        if (subs.Count == 0)
        {
            SectionEmptyText = "You have no subscriptions yet.\nPress Control+S on any video to subscribe to its channel, or press Control+4 and import your subscriptions.";
            ShowItems(Array.Empty<SearchResult>(), moveFocus);
            return;
        }
        SectionEmptyText = "No recent uploads from your channels. Press F5 to refresh.";

        // Whatever was last seen counts as old; capture it before this visit moves it forward.
        var newSince = _library.FeedLastViewedUtc;

        bool stale = _feed is null || DateTimeOffset.UtcNow - _feed.FetchedUtc > FeedAutoRefreshAge;
        if (_feed is not null && !forceRefresh)
            ShowFeedItems(newSince, moveFocus && !stale);

        if (forceRefresh || stale)
        {
            BeginLoading($"Checking {subs.Count} channels for new videos...");
            await RefreshFeedAsync(token);
            token.ThrowIfCancellationRequested();
            ShowFeedItems(newSince, moveFocus);
            if (_feed is { FailedChannels.Count: > 0 } f)
            {
                _accessibility.Announce($"{f.FailedChannels.Count} channel{(f.FailedChannels.Count == 1 ? "" : "s")} could not be checked.", AnnouncementPriority.Low);
            }
        }

        _library.MarkFeedViewed(DateTimeOffset.UtcNow);
    }

    private void ShowFeedItems(DateTimeOffset newSince, bool moveFocus)
    {
        var videos = FeedVideos();
        int newCount = newSince == default ? 0 : videos.Count(v => v.PublishedAt > newSince);
        ShowItems(videos, moveFocus, extraSummary: newCount > 0 ? $"{newCount} new" : null, newSince: newSince);
    }

    private List<SearchResult> FeedVideos() =>
        (_feed?.Videos ?? Array.Empty<SearchResult>())
            .Where(v => !(HideShortsInFeed && v.IsShort))
            .ToList();

    private Task RefreshFeedAsync(CancellationToken token)
    {
        // Share one refresh between the startup check and an impatient F5.
        if (_feedRefreshTask is { IsCompleted: false })
            return _feedRefreshTask.WaitAsync(token);

        _feedRefreshTask = Task.Run(async () =>
        {
            var feed = await _discovery.GetSubscriptionFeedAsync(_library.Subscriptions);
            _feed = feed;
            _library.SaveFeed(feed);
        });
        return _feedRefreshTask.WaitAsync(token);
    }

    /// <summary>Background check at startup; announces new uploads without moving focus.</summary>
    public async Task CheckFeedInBackgroundAsync()
    {
        if (!CheckFeedOnStartup || _library.Subscriptions.Count == 0)
            return;
        try
        {
            var lastViewed = _library.FeedLastViewedUtc;
            await RefreshFeedAsync(CancellationToken.None);
            if (lastViewed == default || CurrentSection == BrowseSection.WhatsNew)
                return;
            int newCount = FeedVideos().Count(v => v.PublishedAt > lastViewed);
            if (newCount > 0)
            {
                _accessibility.Announce(
                    $"{newCount} new video{(newCount == 1 ? "" : "s")} from your channels. Press Control+3 for What's New.",
                    AnnouncementPriority.Low);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background subscription feed check failed.");
        }
    }

    private async Task ShowChannelUploadsAsync(bool moveFocus, CancellationToken token)
    {
        var channelId = _currentChannelId;
        if (channelId is null)
            return;

        SectionTitle = $"{_currentChannelName}: latest uploads";
        SectionEmptyText = "This channel's uploads could not be loaded. Press F5 to try again, or Alt+Left to go back.";
        BeginLoading("Loading...");

        var result = await _discovery.GetChannelUploadsAsync(channelId, token);
        token.ThrowIfCancellationRequested();
        if (result.IsSuccess)
        {
            _currentChannelName = result.Value.ChannelName;
            SectionTitle = $"{_currentChannelName}: latest uploads";
            ShowItems(result.Value.Videos, moveFocus, extraSummary: _library.IsSubscribed(channelId) ? "subscribed" : null);
            ConfigureChannelPaging(channelId);
        }
        else
        {
            ShowItems(Array.Empty<SearchResult>(), moveFocus: false);
            _accessibility.Announce($"Could not load the channel. {result.Error?.Message}", AnnouncementPriority.High);
        }
    }

    private string? _currentChannelId;
    private string _currentChannelName = string.Empty;

    // --- Paging: more results load as the user nears the end of the list ---

    private Func<CancellationToken, Task<(IReadOnlyList<SearchResult> Items, bool HasMore)>>? _loadMore;
    private bool _speakNextLoadMore;
    private string? _channelPageToken;

    [ObservableProperty] private bool _isLoadingMore;

    public bool CanLoadMore => _loadMore is not null;

    private void SetLoadMore(Func<CancellationToken, Task<(IReadOnlyList<SearchResult> Items, bool HasMore)>>? loader)
    {
        _loadMore = loader;
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(SectionCountText));

        // A short first page leaves the selection already near the end, so no arrow press would ever
        // ask for more; fill the list up front instead.
        if (loader is not null && SearchResults.Count < 10)
            _ = LoadMoreAsync();
    }

    /// <summary>After a search: more pages come from the backend that answered it.</summary>
    private void ConfigureSearchPaging()
    {
        if (_playbackCoordinator is PlaybackCoordinator coord && coord.CanLoadMoreSearchResults)
        {
            SetLoadMore(async token =>
            {
                var page = await coord.SearchMoreAsync(token);
                return page.IsSuccess
                    ? (page.Value.Items, coord.CanLoadMoreSearchResults)
                    : (Array.Empty<SearchResult>(), false);
            });
        }
        else
        {
            SetLoadMore(null);
        }
    }

    /// <summary>Channel pages start with the RSS feed's 15 newest; older uploads page in from Invidious.</summary>
    private void ConfigureChannelPaging(string channelId)
    {
        _channelPageToken = null;
        SetLoadMore(async token =>
        {
            var page = await _discovery.GetChannelUploadsPageAsync(channelId, _channelPageToken, token);
            if (!page.IsSuccess)
                return (Array.Empty<SearchResult>(), false);
            _channelPageToken = page.Value.NextPageToken;
            return (page.Value.Items, page.Value.HasNextPage);
        });
    }

    partial void OnSelectedSearchResultChanged(SearchResult? value)
    {
        if (value is null || _loadMore is null) return;
        var index = SearchResults.IndexOf(value);
        if (index < 0 || index < SearchResults.Count - 5) return;
        if (index == SearchResults.Count - 1)
            _speakNextLoadMore = true;
        _ = LoadMoreAsync();
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (_loadMore is not { } loader || IsLoadingMore)
            return;

        var section = CurrentSection;
        var token = _sectionCts?.Token ?? CancellationToken.None;
        IsLoadingMore = true;
        try
        {
            // Pages that only repeat what is listed are skipped; try a few before giving up.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var (items, hasMore) = await loader(token);
                if (token.IsCancellationRequested || section != CurrentSection || !ReferenceEquals(loader, _loadMore))
                    return;

                var existing = SearchResults.Select(r => r.Id).ToHashSet();
                var fresh = items.Where(i => existing.Add(i.Id)).ToList();
                foreach (var item in fresh)
                    SearchResults.Add(Decorate(item));
                if (section == BrowseSection.Search)
                    _lastSearchResults.AddRange(fresh);

                if (!hasMore)
                    SetLoadMore(null);

                if (fresh.Count > 0 || !hasMore)
                {
                    // Speak only if the user is sitting on the last item waiting for this.
                    if (_speakNextLoadMore)
                        _accessibility.Announce(fresh.Count > 0 ? $"{fresh.Count} more." : "End of list.", AnnouncementPriority.Low);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading more results failed.");
        }
        finally
        {
            _speakNextLoadMore = false;
            IsLoadingMore = false;
        }
    }

    private void BeginLoading(string message)
    {
        IsSectionLoading = true;
        SearchResults.Clear();
        _accessibility.Announce(message, AnnouncementPriority.Low);
    }

    private void ShowItems(IReadOnlyList<SearchResult> items, bool moveFocus, string? extraSummary = null, DateTimeOffset? newSince = null)
    {
        SearchResults.Clear();
        foreach (var item in items)
            SearchResults.Add(Decorate(item, newSince));
        SelectedSearchResult = SearchResults.FirstOrDefault();
        IsSectionLoading = false;

        // The tab or list name already says which section this is; only add what is new to the user.
        var count = SearchResults.Count;
        string spoken;
        if (count == 0)
        {
            spoken = SectionEmptyText.Replace('\n', ' ');
        }
        else
        {
            var noun = $"{count} {SectionNoun(count)}";
            spoken = string.Join(", ", new[] { noun, extraSummary }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        _accessibility.Announce(spoken, AnnouncementPriority.Medium);

        if (moveFocus && count > 0)
            RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    private string SectionNoun(int count)
    {
        var noun = CurrentSection switch
        {
            BrowseSection.Channels => "channel",
            BrowseSection.Playlists => "playlist",
            BrowseSection.Search when SearchResults.FirstOrDefault()?.Kind == SearchResultKind.Playlist => "playlist",
            _ => "video"
        };
        return count == 1 ? noun : noun + "s";
    }

    /// <summary>Adds the spoken library status: New, Watched, Resume at ...</summary>
    private SearchResult Decorate(SearchResult item, DateTimeOffset? newSince = null)
    {
        if (item.Kind != SearchResultKind.Video)
            return item with { StatusPrefix = null };

        string? status = null;
        if (newSince is { } since && since != default && item.PublishedAt > since)
        {
            status = "New";
        }
        else if (RecordHistory && _library.GetHistory(item.Id) is { } watched)
        {
            status = watched.ResumePosition is { } resume
                ? $"Resume at {AccessibleTextFormatter.FormatTimeSpoken(resume)}"
                : "Watched";
        }
        return item with { StatusPrefix = status };
    }

    // --- Item actions ---

    [RelayCommand]
    public async Task OpenChannelAsync(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        var channelId = item?.Kind == SearchResultKind.Channel ? item.Id : item?.ChannelId;
        if (IsPlayerDialogOpen && !string.IsNullOrWhiteSpace(channelId))
            await ClosePlayerDialogAsync();
        if (item is null || string.IsNullOrWhiteSpace(channelId))
        {
            _accessibility.Announce("The channel for this video is not known yet. Play the video, then press Control+G in the player.", AnnouncementPriority.Medium);
            return;
        }

        if (CurrentSection != BrowseSection.Channel || _currentChannelId != channelId)
            PushNavigation();
        _currentChannelId = channelId;
        _currentChannelName = item.Kind == SearchResultKind.Channel ? item.Title : item.ChannelTitle;
        await ShowSectionAsync(BrowseSection.Channel, moveFocus: true);
    }

    [RelayCommand]
    public void ToggleSubscriptionForItem(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null) return;
        var channelId = item.Kind == SearchResultKind.Channel ? item.Id : item.ChannelId;
        var channelName = item.Kind == SearchResultKind.Channel ? item.Title : item.ChannelTitle;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            _accessibility.Announce("The channel for this video is not known yet. Play the video, then press Control+S in the player.", AnnouncementPriority.Medium);
            return;
        }

        bool subscribed = _library.ToggleSubscription(channelId, channelName);
        _accessibility.Announce(subscribed ? $"Subscribed to {channelName}." : $"Unsubscribed from {channelName}.", AnnouncementPriority.High);

        // In Channels, keep the row so Control+S again undoes the change.
        if (CurrentSection == BrowseSection.Channels)
            ReplaceItem(item, item with { StatusPrefix = subscribed ? null : "Unsubscribed" });
        UpdateCurrentVideoLibraryState();
    }

    [RelayCommand]
    public void ToggleWatchLaterForItem(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null || item.Kind != SearchResultKind.Video) return;

        bool saved = _library.ToggleWatchLater(item);
        _accessibility.Announce(saved ? $"Saved to Watch Later: {item.Title}." : $"Removed from Watch Later: {item.Title}.", AnnouncementPriority.High);
        if (!saved && CurrentSection == BrowseSection.WatchLater)
            RemoveItemKeepingPosition(item);
        UpdateCurrentVideoLibraryState();
    }

    [RelayCommand]
    public void RemoveSelectedItem(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null) return;

        switch (CurrentSection)
        {
            case BrowseSection.History:
                _library.RemoveFromHistory(item.Id);
                _accessibility.Announce($"Removed from history: {item.Title}.", AnnouncementPriority.High);
                break;
            case BrowseSection.WatchLater:
                _library.RemoveFromWatchLater(item.Id);
                _accessibility.Announce($"Removed from Watch Later: {item.Title}.", AnnouncementPriority.High);
                break;
            case BrowseSection.Channels:
                _library.Unsubscribe(item.Id);
                _accessibility.Announce($"Unsubscribed from {item.Title}.", AnnouncementPriority.High);
                break;
            case BrowseSection.ForYou:
                NotInterested(item);
                return;
            case BrowseSection.Playlists:
                _ = DeleteOpenPlaylistAsync();
                return;
            case BrowseSection.Playlist when _openLibraryPlaylistId is { } playlistId:
                if (playlistId == LibraryService.LikedPlaylistId)
                    _library.ToggleLike(item);
                else
                    _library.RemoveFromPlaylist(playlistId, item.Id);
                _accessibility.Announce($"Removed {item.Title}.", AnnouncementPriority.High);
                break;
            default:
                _accessibility.Announce("Delete removes items from History, Watch Later, Channels, For You and your playlists.", AnnouncementPriority.Medium);
                return;
        }
        RemoveItemKeepingPosition(item);
    }

    [RelayCommand]
    public void ClearHistory()
    {
        _library.ClearHistory();
        _accessibility.Announce("Watch history cleared.", AnnouncementPriority.High);
        if (CurrentSection == BrowseSection.History)
            _ = ShowSectionAsync(BrowseSection.History, moveFocus: false);
    }

    /// <summary>After watching, "Resume at" / "Watched" on the visible rows is out of date.</summary>
    private void RefreshLibraryStatusOfVisibleItems()
    {
        if (CurrentSection == BrowseSection.History)
        {
            _ = ShowSectionAsync(BrowseSection.History, moveFocus: false);
            return;
        }
        var selectedIndex = SelectedSearchResult is null ? -1 : SearchResults.IndexOf(SelectedSearchResult);
        for (int i = 0; i < SearchResults.Count; i++)
        {
            var item = SearchResults[i];
            if (item.Kind != SearchResultKind.Video || item.StatusPrefix == "New")
                continue;
            var updated = Decorate(item);
            if (updated.StatusPrefix != item.StatusPrefix)
                SearchResults[i] = updated;
        }
        if (selectedIndex >= 0 && selectedIndex < SearchResults.Count)
            SelectedSearchResult = SearchResults[selectedIndex];
    }

    private void ReplaceItem(SearchResult oldItem, SearchResult newItem)
    {
        var index = SearchResults.IndexOf(oldItem);
        if (index < 0) return;
        SearchResults[index] = newItem;
        SelectedSearchResult = newItem;
        RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveItemKeepingPosition(SearchResult item)
    {
        var index = SearchResults.IndexOf(item);
        if (index < 0) return;
        SearchResults.RemoveAt(index);
        if (SearchResults.Count == 0)
        {
            _accessibility.Announce($"{SectionTitle} is now empty.", AnnouncementPriority.Low);
            return;
        }
        SelectedSearchResult = SearchResults[Math.Min(index, SearchResults.Count - 1)];
        RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    // --- Import / export ---

    public void ImportSubscriptionsFromFile(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // A Takeout zip holds playlists, likes and history too; bring them all.
                _ = ImportTakeoutAsync(path);
                return;
            }
            var parsed = SubscriptionFormats.Parse(File.ReadAllText(path));
            int added = _library.ImportSubscriptions(parsed.Channels);
            int already = parsed.Channels.Count - added;
            var message = $"Imported {added} channel{(added == 1 ? "" : "s")} from {parsed.FormatName}.";
            if (already > 0) message += $" {already} already subscribed.";
            if (parsed.SkippedEntries > 0) message += $" {parsed.SkippedEntries} entries were not YouTube channels and were skipped.";
            if (added > 0) message += " Press Control+3 to see their latest videos.";
            _accessibility.Announce(message, AnnouncementPriority.High);
            if (added > 0)
                _feed = null; // new channels: next visit to What's New refreshes
            if (CurrentSection == BrowseSection.Channels)
                _ = ShowSectionAsync(BrowseSection.Channels, moveFocus: false);
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException or UnauthorizedAccessException or InvalidDataException)
        {
            _accessibility.Announce($"Could not import subscriptions: {ex.Message}", AnnouncementPriority.High);
        }
    }

    public void ExportSubscriptionsToFile(string path)
    {
        try
        {
            var subs = _library.Subscriptions;
            var content = Path.GetExtension(path).Equals(".opml", StringComparison.OrdinalIgnoreCase)
                ? SubscriptionFormats.ToOpml(subs)
                : SubscriptionFormats.ToNewPipeJson(subs);
            File.WriteAllText(path, content);
            _accessibility.Announce($"Exported {subs.Count} channels to {Path.GetFileName(path)}.", AnnouncementPriority.High);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _accessibility.Announce($"Could not export subscriptions: {ex.Message}", AnnouncementPriority.High);
        }
    }

    // --- Player: library actions and spoken info ---

    private string? CurrentVideoId => _currentLibraryVideo?.Id;

    private void SetCurrentLibraryVideo(SearchResult? video)
    {
        // Leaving a video (auto-advance, Up next, queue): save where it was left before switching.
        if (_currentLibraryVideo is { } previous && previous.Id != video?.Id)
            RecordCurrentProgress(CurrentTime, flush: true);

        _currentLibraryVideo = video is null ? null : video with { StatusPrefix = null };
        UpdateCurrentVideoLibraryState();
        ResetWatchPage(_currentLibraryVideo?.Id);
    }

    private void UpdateCurrentVideoLibraryState()
    {
        var channelId = CurrentVideo?.Channel.Id ?? _currentLibraryVideo?.ChannelId;
        IsCurrentChannelSubscribed = !string.IsNullOrWhiteSpace(channelId) && _library.IsSubscribed(channelId);
        IsCurrentVideoInWatchLater = CurrentVideoId is { } id && _library.IsInWatchLater(id);
        IsCurrentVideoLiked = CurrentVideoId is { } likedId && _library.IsLiked(likedId);
    }

    [RelayCommand]
    public void ToggleSubscribeCurrentChannel()
    {
        var channelId = CurrentVideo?.Channel.Id ?? _currentLibraryVideo?.ChannelId;
        var channelName = CurrentVideo?.Channel.Name ?? _currentLibraryVideo?.ChannelTitle ?? CurrentChannelTitle;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            _accessibility.Announce("The channel is not known yet; try again once the video is playing.", AnnouncementPriority.Medium);
            return;
        }
        bool subscribed = _library.ToggleSubscription(channelId, channelName);
        UpdateCurrentVideoLibraryState();
        _accessibility.Announce(subscribed ? $"Subscribed to {channelName}." : $"Unsubscribed from {channelName}.", AnnouncementPriority.High);
    }

    [RelayCommand]
    public void ToggleWatchLaterCurrent()
    {
        if (_currentLibraryVideo is null)
        {
            _accessibility.Announce("No video is loaded.", AnnouncementPriority.Medium);
            return;
        }
        bool saved = _library.ToggleWatchLater(_currentLibraryVideo);
        UpdateCurrentVideoLibraryState();
        _accessibility.Announce(saved ? "Saved to Watch Later." : "Removed from Watch Later.", AnnouncementPriority.High);
    }

    [RelayCommand]
    public async Task OpenCurrentChannelAsync()
    {
        var video = CurrentVideo?.ToSearchResult() ?? _currentLibraryVideo;
        if (video is null || string.IsNullOrWhiteSpace(video.ChannelId))
        {
            _accessibility.Announce("The channel is not known yet; try again once the video is playing.", AnnouncementPriority.Medium);
            return;
        }
        await ClosePlayerDialogAsync();
        await OpenChannelAsync(video);
    }

    [RelayCommand]
    public void AnnounceTime()
    {
        var dur = Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);
        if (dur <= TimeSpan.Zero)
        {
            _accessibility.Announce($"{AccessibleTextFormatter.FormatTimeSpoken(CurrentTime)} elapsed.", AnnouncementPriority.High);
            return;
        }
        var remaining = dur - CurrentTime;
        int percent = (int)Math.Round(CurrentTime / dur * 100);
        _accessibility.Announce(
            $"{AccessibleTextFormatter.FormatTimeSpoken(CurrentTime)} of {AccessibleTextFormatter.FormatTimeSpoken(dur)}. " +
            $"{AccessibleTextFormatter.FormatTimeSpoken(remaining)} remaining. {percent} percent.",
            AnnouncementPriority.High);
    }

    [RelayCommand]
    public void AnnounceVideoInfo()
    {
        var parts = new List<string> { CurrentVideoTitle };
        if (!string.IsNullOrWhiteSpace(CurrentChannelTitle))
            parts.Add($"by {CurrentChannelTitle}{(IsCurrentChannelSubscribed ? ", subscribed" : "")}");
        if (CurrentVideo?.UploadDate is { } uploaded)
            parts.Add($"uploaded {AccessibleTextFormatter.FormatAgeSpoken(uploaded, DateTimeOffset.UtcNow)}");
        if (CurrentVideo?.ViewCount is { } views)
            parts.Add($"{views:N0} views");
        if (IsCurrentVideoLiked)
            parts.Add("liked");
        if (IsCurrentVideoInWatchLater)
            parts.Add("in Watch Later");
        _accessibility.Announce(string.Join(". ", parts) + ".", AnnouncementPriority.High);
    }

    /// <summary>Digit keys 0-9 jump to 0%-90%, as on YouTube.</summary>
    public async Task SeekToPercentAsync(int tenths)
    {
        var dur = Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);
        if (!IsEngineInitialized || dur <= TimeSpan.Zero) return;
        var target = dur * (Math.Clamp(tenths, 0, 9) / 10.0);
        CurrentTime = target;
        UpdateBrailleDisplayLine();
        await _playerEngine.SeekAsync(target);
        _accessibility.Announce($"{tenths * 10} percent, {AccessibleTextFormatter.FormatTimeSpoken(target)}.", AnnouncementPriority.Medium);
    }

    [RelayCommand]
    public async Task RestartFromBeginningAsync()
    {
        if (!IsEngineInitialized) return;
        CurrentTime = TimeSpan.Zero;
        UpdateBrailleDisplayLine();
        await _playerEngine.SeekAsync(TimeSpan.Zero);
        _accessibility.Announce("From the beginning.", AnnouncementPriority.Medium);
    }

    // --- History & resume hooks (called from the engine event handlers) ---

    private void OnLibraryPlaybackStateChanged(PlaybackState state)
    {
        if (state == PlaybackState.Playing && CurrentVideoId is { } id && id != _resumeHandledForId)
        {
            _resumeHandledForId = id;
            if (ResumePlayback && RecordHistory && _library.GetHistory(id)?.ResumePosition is { } resume)
            {
                _ = ResumeAtAsync(resume);
            }
        }

        if (state == PlaybackState.Ended)
            RecordCurrentProgress(Duration, flush: true);
    }

    private async Task ResumeAtAsync(TimeSpan position)
    {
        try
        {
            CurrentTime = position;
            UpdateBrailleDisplayLine();
            await _playerEngine.SeekAsync(position);
            _accessibility.Announce(
                $"Resumed at {AccessibleTextFormatter.FormatTimeSpoken(position)}. Home starts over.",
                AnnouncementPriority.Medium);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resume playback position.");
        }
    }

    private void OnLibraryPositionChanged(TimeSpan position)
    {
        if (PlaybackState == PlaybackState.Playing && position > TimeSpan.FromSeconds(5))
            RecordCurrentProgress(position, flush: false);
    }

    private void RecordCurrentProgress(TimeSpan position, bool flush)
    {
        if (!RecordHistory || _currentLibraryVideo is null || _resumeHandledForId != _currentLibraryVideo.Id)
            return; // don't record before the resume decision, or the old resume point is overwritten
        var video = CurrentVideo is { } details && details.Id.Value == _currentLibraryVideo.Id
            ? details.ToSearchResult()
            : _currentLibraryVideo;
        _library.RecordProgress(video, position, Duration, flush);
    }

    private async Task PersistLibraryPreferencesAsync()
    {
        var current = _configService.CurrentConfig;
        var region = string.IsNullOrWhiteSpace(FeaturedRegion) ? "US" : FeaturedRegion.Trim().ToUpperInvariant();
        if (region != current.Library.FeaturedRegion)
            _featuredCache.Clear();
        await _configService.UpdateConfigAsync(current with
        {
            Library = current.Library with
            {
                ResumePlayback = ResumePlayback,
                RecordHistory = RecordHistory,
                HideShortsInFeed = HideShortsInFeed,
                CheckFeedOnStartup = CheckFeedOnStartup,
                FeaturedRegion = region
            }
        });
    }
}
