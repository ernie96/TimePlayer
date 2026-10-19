using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Video and Music are two modes of one window (Control+M). Music has its own sections and keeps
/// playing while you browse: the player view only shows when asked (Alt+P), and a Now playing bar
/// and keys (Control+Space, Control+Shift+N and P, media keys) control playback from anywhere.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly SectionTab[] VideoTabs =
    {
        new(BrowseSection.Featured, T("Featured"), T("Control+1")),
        new(BrowseSection.ForYou, T("For You"), T("Control+2")),
        new(BrowseSection.WhatsNew, T("What's New"), T("Control+3")),
        new(BrowseSection.Channels, T("Channels"), T("Control+4")),
        new(BrowseSection.Playlists, T("Playlists"), T("Control+5")),
        new(BrowseSection.WatchLater, T("Watch Later"), T("Control+6")),
        new(BrowseSection.History, T("History"), T("Control+7")),
        new(BrowseSection.Queue, T("Queue"), T("Control+8")),
    };

    private static readonly SectionTab[] MusicTabs =
    {
        new(BrowseSection.MusicCharts, T("Charts"), T("Control+1")),
        new(BrowseSection.MusicRecent, T("Recently played"), T("Control+2")),
        new(BrowseSection.Playlists, T("Playlists"), T("Control+3")),
        new(BrowseSection.Queue, T("Queue"), T("Control+4")),
    };

    /// <summary>
    /// Whether the player fills the window. In Video mode it shows whenever something plays; in
    /// Music mode only when opened (Alt+P), so the lists stay in front while music plays.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibraryVisible), nameof(IsNowPlayingBarVisible), nameof(WindowTitle))]
    private bool _isPlayerViewVisible;

    public bool IsLibraryVisible => !IsPlayerViewVisible;
    public bool IsNowPlayingBarVisible => IsPlayerDialogOpen && !IsPlayerViewVisible;
    public string ModeName => MusicMode ? T("Music") : T("Video");
    public string NowPlayingText =>
        string.IsNullOrWhiteSpace(CurrentChannelTitle) ? T("Now playing: {0}", CurrentVideoTitle) : T("Now playing: {0}, by {1}", CurrentVideoTitle, CurrentChannelTitle);

    partial void OnIsPlayerDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNowPlayingBarVisible));
        OnPropertyChanged(nameof(WindowTitle));
        if (!value)
            IsPlayerViewVisible = false;
    }

    partial void OnCurrentChannelTitleChanged(string value) => OnPropertyChanged(nameof(NowPlayingText));

    /// <summary>Starts a playback session; the player view opens only in Video mode.</summary>
    private void OpenPlayerSession()
    {
        IsPlayerDialogOpen = true;
        if (!MusicMode)
            IsPlayerViewVisible = true;
    }

    private void FocusPlayerIfShown()
    {
        if (IsPlayerViewVisible)
            RequestFocusPlayer?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Leaving the player for a list: music keeps playing, a video stops.</summary>
    public async Task LeavePlayerViewAsync()
    {
        if (!IsPlayerViewVisible)
            return;
        if (MusicMode)
        {
            IsPlayerViewVisible = false;
            RefreshLibraryStatusOfVisibleItems();
            RequestFocusResults?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            await ClosePlayerDialogAsync();
        }
    }

    /// <summary>Alt+P in Music mode: the player, with Up next and the queue.</summary>
    [RelayCommand]
    public void ShowPlayerView()
    {
        if (!IsPlayerDialogOpen)
        {
            _accessibility.Announce(T("Nothing is playing."), AnnouncementPriority.Medium);
            return;
        }
        IsPlayerViewVisible = true;
        RequestFocusPlayer?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the music and closes the session (the Now playing bar's Stop).</summary>
    [RelayCommand]
    public async Task StopPlaybackSessionAsync()
    {
        if (!IsPlayerDialogOpen)
            return;
        await ClosePlayerDialogAsync();
        _accessibility.Announce(T("Stopped."), AnnouncementPriority.Medium);
    }

    /// <summary>Switches the sections, and the player, to the mode just chosen.</summary>
    private void ApplyAppMode(bool announce)
    {
        if (_playbackCoordinator is TimePlayer.Core.Playback.PlaybackCoordinator captionSource)
            captionSource.LookUpMissingCaptions = !MusicMode || SpeakCaptions;
        var tabs = MusicMode ? MusicTabs : VideoTabs;
        _syncingTab = true;
        SectionTabs.Clear();
        foreach (var tab in tabs)
            SectionTabs.Add(tab);
        SelectedSectionTab = SectionTabs[0];
        _syncingTab = false;
        _backStack.Clear();
        OnPropertyChanged(nameof(ModeName));
        OnPropertyChanged(nameof(WindowTitle));

        // Something playing carries over: a song becomes a video in the video player, a video
        // keeps playing as music behind the lists.
        if (IsPlayerDialogOpen)
            IsPlayerViewVisible = !MusicMode;

        if (!announce)
            return;
        if (!IsPlayerViewVisible)
            _ = ShowSectionAsync(SectionTabs[0].Section, moveFocus: true);
        else
            RequestFocusPlayer?.Invoke(this, EventArgs.Empty);
    }

    // --- Music sections ---

    private async Task ShowMusicChartsAsync(bool moveFocus, bool forceRefresh, CancellationToken token)
    {
        SectionTitle = T("Charts: top music videos");
        SectionEmptyText = T("The charts could not be loaded. Press F5 to try again.");
        const FeaturedCategory category = FeaturedCategory.Music;
        if (!forceRefresh && _featuredCache.TryGetValue(category, out var cached) && DateTimeOffset.UtcNow - cached.Fetched < FeaturedCacheLifetime)
        {
            ShowItems(cached.Items, moveFocus);
            ConfigureFeaturedPaging(category, cached.NextPage);
            return;
        }
        BeginLoading(T("Loading..."));
        var result = await _discovery.GetFeaturedAsync(category, FeaturedRegion, null, token);
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
            _accessibility.Announce(T("The charts are unavailable right now. {0} Press F5 to retry.", result.Error?.Message), AnnouncementPriority.High);
        }
    }

    private void ShowMusicRecent(bool moveFocus)
    {
        SectionTitle = T("Recently played");
        SectionEmptyText = RecordHistory
            ? T("Nothing played in Music yet. Songs you play here appear in this list, newest first.")
            : T("Watch history is turned off in Settings (F2).");
        var now = DateTimeOffset.UtcNow;
        ShowItems(_library.History
            .Where(h => h.IsMusic)
            .Select(h => h.ToSearchResult() with { UploadDateText = T("played {0}", AccessibleTextFormatter.FormatAgeSpoken(h.LastWatchedUtc, now)) })
            .ToList(), moveFocus);
    }
}
