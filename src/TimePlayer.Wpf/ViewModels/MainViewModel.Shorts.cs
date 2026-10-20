using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

public enum ChannelTab { Videos, Shorts, Live, Playlists }

/// <summary>
/// Channel tabs (Videos, Shorts, Live, Playlists), Shorts mode (Control+Shift+S: Up and Down between Shorts,
/// each repeating, the next loaded ahead) and downloading a whole list.
/// </summary>
public sealed partial class MainViewModel
{
    // ----- Channel tabs -----

    public IReadOnlyList<ChoiceOption<ChannelTab>> ChannelTabs { get; } = new[]
    {
        new ChoiceOption<ChannelTab>(ChannelTab.Videos, T("Videos")),
        new ChoiceOption<ChannelTab>(ChannelTab.Shorts, T("Shorts")),
        new ChoiceOption<ChannelTab>(ChannelTab.Live, T("Live")),
        new ChoiceOption<ChannelTab>(ChannelTab.Playlists, T("Playlists")),
    };

    [ObservableProperty] private ChannelTab _selectedChannelTab = ChannelTab.Videos;

    partial void OnSelectedChannelTabChanged(ChannelTab value)
    {
        if (CurrentSection == BrowseSection.Channel)
            _ = ShowSectionAsync(BrowseSection.Channel, moveFocus: false);
    }

    /// <summary>A channel's Shorts, Live or Playlists tab, read from YouTube's own tab list.</summary>
    private async Task ShowChannelTabAsync(string channelId, bool moveFocus, CancellationToken token)
    {
        var tabName = ChannelTabs.First(t => t.Value == SelectedChannelTab).Label;
        SectionTitle = T("{0}: {1}", _currentChannelName, tabName);
        SectionEmptyText = T("This channel has nothing in {0}.", tabName);
        if (_innerTubeDirect is null)
        {
            ShowItems(Array.Empty<SearchResult>(), moveFocus);
            return;
        }
        BeginLoading(T("Loading..."));
        var tabs = await _innerTubeDirect.GetChannelTabsAsync(channelId, token);
        token.ThrowIfCancellationRequested();
        // YouTube's names for them (the request asks for English).
        var youTubeName = SelectedChannelTab switch { ChannelTab.Shorts => "Shorts", ChannelTab.Live => "Live", _ => "Playlists" };
        if (!tabs.IsSuccess || !tabs.Value.TryGetValue(youTubeName, out var tabParams))
        {
            ShowItems(Array.Empty<SearchResult>(), moveFocus);
            return;
        }

        string? next = null;
        async Task<(IReadOnlyList<SearchResult> Items, bool HasMore)> Page(string? continuation, CancellationToken t)
        {
            var page = await _innerTubeDirect!.GetChannelTabPageAsync(channelId, tabParams, continuation, t);
            if (!page.IsSuccess)
                return (Array.Empty<SearchResult>(), false);
            next = page.Value.NextPageToken;
            // A channel's own tabs don't repeat its name on every item.
            // (On a channel's own tabs YouTube leaves the name out; the first detail is the views instead.)
            var items = page.Value.Items.Select(i => i.Kind == SearchResultKind.Playlist
                ? i with { ChannelTitle = string.IsNullOrWhiteSpace(i.ChannelTitle) ? _currentChannelName : i.ChannelTitle }
                : i with { ChannelTitle = _currentChannelName, ChannelId = channelId }).ToList();
            return (items, page.Value.HasNextPage);
        }

        var first = await Page(null, token);
        token.ThrowIfCancellationRequested();
        ShowItems(first.Items, moveFocus);
        if (first.HasMore)
            SetLoadMore(t => Page(next, t));
    }

    // ----- Shorts mode -----

    [ObservableProperty] private bool _isShortsMode;

    private readonly List<SearchResult> _shorts = new();
    private int _shortsIndex;
    private Func<CancellationToken, Task<(IReadOnlyList<SearchResult> Items, bool HasMore)>>? _shortsMore;
    // The page being loaded, so Down and the read-ahead share it instead of loading the same page twice.
    private Task<bool>? _shortsLoading;
    private bool _shortsHasMore;
    // Set while a Short is started from Shorts mode, so playing it doesn't end the mode.
    private bool _playingShort;

    /// <summary>
    /// Control+Shift+S: the Shorts in the list on screen (a channel's Shorts tab, a Shorts search, What's New),
    /// from the selected one; otherwise Shorts from your channels, or Shorts like what you watch.
    /// </summary>
    [RelayCommand]
    public async Task StartShortsAsync()
    {
        _shorts.Clear();
        _shortsMore = null;
        _shortsLoading = null;
        _shortsHasMore = false;
        var onScreen = SearchResults.Where(r => r.IsShort).ToList();
        if (onScreen.Count > 0)
        {
            _shorts.AddRange(onScreen);
            _shortsIndex = SelectedSearchResult is { IsShort: true } selected ? Math.Max(0, onScreen.FindIndex(s => s.Id == selected.Id)) : 0;
            if (_loadMore is not null)
            {
                // Through the list itself, so it stays whole for when you go back to it.
                _shortsMore = async _ =>
                {
                    var before = SearchResults.Count;
                    await LoadMoreQuietlyAsync();
                    return (SearchResults.Skip(before).ToList(), _loadMore is not null);
                };
                _shortsHasMore = true;
            }
        }
        else if (_feed?.Videos.Where(v => v.IsShort).ToList() is { Count: > 0 } fromChannels)
        {
            _shorts.AddRange(fromChannels);
            _shortsIndex = 0;
        }
        else if (_innerTubeDirect is { } innerTube)
        {
            // Like what you watch: the channels you watched last, or what's popular.
            var channels = _library.History.Take(20).Select(h => h.ToSearchResult().ChannelTitle)
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Take(2).ToList();
            var query = channels.Count > 0 ? string.Join(" ", channels) : "trending";
            _accessibility.Announce(T("Finding Shorts..."), AnnouncementPriority.Medium);
            var found = await innerTube.SearchShortsAsync(query, null);
            if (found.IsSuccess)
            {
                _shorts.AddRange(found.Value.Items.Where(i => i.IsShort));
                string? token = found.Value.NextPageToken;
                _shortsHasMore = found.Value.HasNextPage;
                _shortsMore = async t =>
                {
                    var page = await innerTube.SearchShortsAsync(query, token, t);
                    if (!page.IsSuccess) return (Array.Empty<SearchResult>(), false);
                    token = page.Value.NextPageToken;
                    return (page.Value.Items.Where(i => i.IsShort).ToList(), page.Value.HasNextPage);
                };
            }
            _shortsIndex = 0;
        }

        if (_shorts.Count == 0)
        {
            _accessibility.Announce(T("No Shorts were found."), AnnouncementPriority.High);
            return;
        }

        IsShortsMode = true;
        await _playerEngine.SetLoopAsync(true);
        if (_playbackCoordinator is PlaybackCoordinator coordinator)
            await coordinator.ReplaceQueueAsync(_shorts.Select(s => s with { StatusPrefix = null }));
        _accessibility.Announce(T("Shorts. Up and Down move between them, and each one repeats."), AnnouncementPriority.Medium);
        await PlayShortAsync();
    }

    [RelayCommand]
    public async Task NextShortAsync()
    {
        if (!IsShortsMode)
            return;
        if (_shortsIndex + 1 >= _shorts.Count && !await LoadMoreShortsAsync())
        {
            _accessibility.Announce(T("That was the last Short."), AnnouncementPriority.Medium);
            return;
        }
        _shortsIndex++;
        await PlayShortAsync();
    }

    [RelayCommand]
    public async Task PreviousShortAsync()
    {
        if (!IsShortsMode)
            return;
        if (_shortsIndex == 0)
        {
            _accessibility.Announce(T("This is the first Short."), AnnouncementPriority.Medium);
            return;
        }
        _shortsIndex--;
        await PlayShortAsync();
    }

    private Task<bool> LoadMoreShortsAsync()
    {
        if (_shortsLoading is { IsCompleted: false } loading)
            return loading;
        return _shortsLoading = LoadMoreShortsCoreAsync();
    }

    private async Task<bool> LoadMoreShortsCoreAsync()
    {
        if (_shortsMore is null || !_shortsHasMore)
            return false;
        (IReadOnlyList<SearchResult> Items, bool HasMore) page;
        try
        {
            page = await _shortsMore(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading more Shorts failed.");
            return false;
        }
        _shortsHasMore = page.HasMore;
        var fresh = page.Items.Where(i => i.IsShort && _shorts.All(s => s.Id != i.Id)).ToList();
        _shorts.AddRange(fresh);
        foreach (var item in fresh)
            await _playbackCoordinator.EnqueueAsync(item with { StatusPrefix = null });
        return fresh.Count > 0;
    }

    private async Task PlayShortAsync()
    {
        var item = _shorts[_shortsIndex];
        var by = string.IsNullOrWhiteSpace(item.ChannelTitle) ? string.Empty : ", " + T("by {0}", item.ChannelTitle);
        _accessibility.Announce(T("Short {0} of {1}: {2}", _shortsIndex + 1, _shorts.Count, item.Title) + by, AnnouncementPriority.High);
        _playingShort = true;
        try
        {
            await PlayItemAsync(item, continueQueue: true);
        }
        finally
        {
            _playingShort = false;
        }
        // The next one loads while this plays, so Down starts it at once.
        if (_shortsIndex + 1 < _shorts.Count && _playbackCoordinator is PlaybackCoordinator coordinator)
            _ = coordinator.PrefetchAsync(_shorts[_shortsIndex + 1].Id);
        else if (_shortsHasMore)
            _ = LoadMoreShortsAsync();
    }

    /// <summary>Leaving the player or playing something else ends Shorts mode.</summary>
    private void EndShortsMode()
    {
        if (!IsShortsMode)
            return;
        IsShortsMode = false;
        _ = _playerEngine.SetLoopAsync(false);
    }

    // ----- Downloading a whole list -----

    [RelayCommand]
    public Task DownloadAllAudioAsync() => DownloadAllAsync(audioOnly: true);

    [RelayCommand]
    public Task DownloadAllVideoAsync() => DownloadAllAsync(audioOnly: false);

    /// <summary>
    /// Every video in the list on screen, one after another, into a folder named after it: a whole playlist
    /// (all of it, not just what is loaded), a channel tab, search results or any section.
    /// </summary>
    public async Task DownloadAllAsync(bool audioOnly)
    {
        List<SearchResult> videos;
        if (CurrentSection == BrowseSection.Playlist && _openLibraryPlaylistId is null && _openYouTubePlaylistId is { } ytId)
        {
            _accessibility.Announce(T("Loading the whole playlist..."), AnnouncementPriority.Medium);
            var all = await _playlists.GetAllAsync(ytId);
            videos = all.IsSuccess ? all.Value.Videos.ToList() : SearchResults.ToList();
        }
        else
        {
            // Everything the list can load, up to a sensible limit.
            while (_loadMore is not null && SearchResults.Count < 500 && await LoadMoreQuietlyAsync())
            {
            }
            videos = SearchResults.ToList();
        }
        videos = videos.Where(v => v.IsVideo && !v.IsLive).DistinctBy(v => v.Id).ToList();
        if (videos.Count == 0)
        {
            _accessibility.Announce(T("There are no videos to download here."), AnnouncementPriority.Medium);
            return;
        }

        var listName = CurrentSection switch
        {
            BrowseSection.Playlist => _openPlaylistTitle,
            BrowseSection.Channel => $"{_currentChannelName} - {ChannelTabs.First(t => t.Value == SelectedChannelTab).Label}",
            _ => SectionTitle
        };
        var folderName = DownloadService.SafeFolderName(string.IsNullOrWhiteSpace(listName) ? T("Downloads") : listName);
        var format = audioOnly ? (_downloads.CanConvert ? T("audio, MP3") : T("audio, M4A")) : T("video");
        if (Confirm is { } confirm && !confirm(T("Download all"),
                T("Download {0} as {1} into the folder {2}? They download one after another; Cancel downloads stops them.",
                    Plural(videos.Count, "{0} video", "{0} videos"), format, folderName)))
            return;

        _accessibility.Announce(T("Downloading {0} into {1}.", Plural(videos.Count, "{0} video", "{0} videos"), folderName), AnnouncementPriority.Medium);
        ActiveDownloads++;
        int done = 0, failed = 0;
        var token = _downloadCts.Token;
        try
        {
            for (var i = 0; i < videos.Count; i++)
            {
                if (token.IsCancellationRequested)
                    break;
                var result = await _downloads.DownloadIntoFolderAsync(videos[i].Id, audioOnly, folderName, i + 1, null, token);
                if (result.IsSuccess)
                    done++;
                else if (result.Error?.Code == "DOWNLOAD_CANCELED")
                    break;
                else
                    failed++;
                // A word now and then, not after every file.
                if (i + 1 == videos.Count / 2 && videos.Count >= 6)
                    _accessibility.Announce(T("Download halfway: {0}", folderName), AnnouncementPriority.Low);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downloading the list stopped.");
        }
        finally
        {
            ActiveDownloads--;
        }

        var summary = token.IsCancellationRequested
            ? T("Downloads canceled after {0}.", Plural(done, "{0} video", "{0} videos"))
            : failed == 0
                ? T("Downloaded {0} into {1}.", Plural(done, "{0} video", "{0} videos"), folderName)
                : T("Downloaded {0} into {1}; {2} could not be downloaded.", Plural(done, "{0} video", "{0} videos"), folderName, failed);
        _accessibility.Announce(summary, AnnouncementPriority.Medium);
    }

    /// <summary>Loads the next page without speaking; false when there is no more.</summary>
    private async Task<bool> LoadMoreQuietlyAsync()
    {
        // A page the list is already loading counts too.
        while (IsLoadingMore)
            await Task.Delay(100);
        var before = SearchResults.Count;
        if (_loadMore is null)
            return false;
        _speakNextLoadMore = false;
        await LoadMoreAsync();
        return SearchResults.Count > before;
    }
}
