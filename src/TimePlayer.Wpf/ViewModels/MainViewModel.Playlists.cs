using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

public enum SearchKind
{
    Videos,
    Playlists,
    Shorts
}

/// <summary>
/// Account-like features kept on this computer: For You recommendations, playlists (your own,
/// Liked videos and saved YouTube playlists), likes, Not interested, and Google Takeout import.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly IPlaylistService _playlists;
    private readonly IRecommendationService _recommendations;

    /// <summary>The playlist shown in <see cref="BrowseSection.Playlist"/>: a library id, or a YouTube id when not saved.</summary>
    private string? _openLibraryPlaylistId;
    private string? _openYouTubePlaylistId;
    private string _openPlaylistTitle = string.Empty;
    private string? _openPlaylistOwner;
    private string? _playlistPageToken;

    /// <summary>Back from channel and playlist pages returns to wherever they were opened from.</summary>
    private readonly Stack<NavigationEntry> _backStack = new();

    private sealed record NavigationEntry(
        BrowseSection Section, int SelectedIndex,
        string? ChannelId, string ChannelName,
        string? LibraryPlaylistId, string? YouTubePlaylistId, string PlaylistTitle);

    /// <summary>Set by the window: asks a yes/no question (title, message) before something can't be undone.</summary>
    public Func<string, string, bool>? Confirm { get; set; }

    /// <summary>Set by the window: asks for a name (title, prompt, initial text); null when cancelled.</summary>
    public Func<string, string, string, string?>? AskForText { get; set; }

    public IReadOnlyList<ChoiceOption<SearchKind>> SearchKinds { get; } =
        Enum.GetValues<SearchKind>().Select(k => new ChoiceOption<SearchKind>(k, T(k.ToString()))).ToList();

    [ObservableProperty]
    private SearchKind _selectedSearchKind = SearchKind.Videos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeButtonText))]
    private bool _isCurrentVideoLiked;

    [ObservableProperty]
    private bool _isImporting;

    public string LikeButtonText => IsCurrentVideoLiked ? T("Unlike") : T("Like");

    public bool IsForYouSection => CurrentSection == BrowseSection.ForYou;
    public bool IsPlaylistsSection => CurrentSection == BrowseSection.Playlists;
    public bool IsPlaylistView => CurrentSection == BrowseSection.Playlist;
    public bool IsSearchSection => CurrentSection == BrowseSection.Search;
    /// <summary>Search results, channel and playlist pages: Back (Alt+Left, Backspace, Escape) returns.</summary>
    public bool IsDetailView => CurrentSection is BrowseSection.Channel or BrowseSection.Playlist or BrowseSection.Search;
    public bool IsOpenPlaylistInLibrary => IsPlaylistView && _openLibraryPlaylistId is not null;
    public bool IsOpenPlaylistEditable => IsOpenPlaylistInLibrary && _openLibraryPlaylistId != LibraryService.LikedPlaylistId;
    public bool CanSaveOpenPlaylist => IsPlaylistView && _openLibraryPlaylistId is null && _openYouTubePlaylistId is not null;
    /// <summary>Play all and Shuffle: on a playlist page, or for the selected playlist in Playlists.</summary>
    public bool CanPlayAll => IsPlaylistView || IsPlaylistsSection;
    /// <summary>Rename and Delete: the open playlist, or the selected one in Playlists.</summary>
    public bool CanEditPlaylist => IsOpenPlaylistEditable || (IsPlaylistView && IsOpenPlaylistInLibrary) || IsPlaylistsSection;

    private void RaisePlaylistViewState()
    {
        OnPropertyChanged(nameof(IsOpenPlaylistInLibrary));
        OnPropertyChanged(nameof(IsOpenPlaylistEditable));
        OnPropertyChanged(nameof(CanSaveOpenPlaylist));
        OnPropertyChanged(nameof(CanPlayAll));
        OnPropertyChanged(nameof(CanEditPlaylist));
        OnPropertyChanged(nameof(CanRemoveFromSection));
    }

    partial void OnSelectedSearchKindChanged(SearchKind value)
    {
        _accessibility.Announce(value == SearchKind.Playlists ? T("Searching for playlists.") : T("Searching for videos."), AnnouncementPriority.Low);
        if (!string.IsNullOrWhiteSpace(SearchQuery) && CurrentSection == BrowseSection.Search && _lastSearchResults.Count > 0)
            _ = SearchAsync();
    }

    private void ConfigureShortsSearchPaging(string query, string? firstToken)
    {
        var token = firstToken;
        if (token is null)
        {
            SetLoadMore(null);
            return;
        }
        SetLoadMore(async ct =>
        {
            var page = await _innerTubeDirect!.SearchShortsAsync(query, token, ct);
            if (page.IsFailure)
                return (Array.Empty<SearchResult>(), false);
            token = page.Value.NextPageToken;
            return (page.Value.Items, token is not null);
        });
    }

    private void ConfigurePlaylistSearchPaging(string query, string? firstToken)
    {
        var token = firstToken;
        if (token is null)
        {
            SetLoadMore(null);
            return;
        }
        SetLoadMore(async ct =>
        {
            var page = await _playlists.SearchAsync(query, token, ct);
            if (page.IsFailure)
                return (Array.Empty<SearchResult>(), false);
            token = page.Value.NextPageToken;
            return (page.Value.Items, token is not null);
        });
    }

    // --- Navigation into and out of detail pages ---

    private void PushNavigation()
    {
        var index = SelectedSearchResult is null ? 0 : Math.Max(0, SearchResults.IndexOf(SelectedSearchResult));
        _backStack.Push(new NavigationEntry(CurrentSection, index, _currentChannelId, _currentChannelName,
            _openLibraryPlaylistId, _openYouTubePlaylistId, _openPlaylistTitle));
        // Deep wandering (channel → playlist → channel …) shouldn't grow forever.
        if (_backStack.Count > 20)
        {
            var kept = _backStack.Take(20).Reverse().ToList();
            _backStack.Clear();
            foreach (var entry in kept)
                _backStack.Push(entry);
        }
    }

    [RelayCommand]
    public async Task GoBackAsync()
    {
        if (!IsDetailView)
            return;
        if (_backStack.Count == 0)
        {
            await ShowSectionAsync(SelectedSectionTab?.Section ?? BrowseSection.Featured, moveFocus: true);
            return;
        }
        var entry = _backStack.Pop();
        _currentChannelId = entry.ChannelId;
        _currentChannelName = entry.ChannelName;
        _openLibraryPlaylistId = entry.LibraryPlaylistId;
        _openYouTubePlaylistId = entry.YouTubePlaylistId;
        _openPlaylistTitle = entry.PlaylistTitle;
        await ShowSectionAsync(entry.Section, moveFocus: false);
        if (entry.SelectedIndex < SearchResults.Count)
            SelectedSearchResult = SearchResults[entry.SelectedIndex];
        RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    // --- For You ---

    private async Task ShowForYouAsync(bool moveFocus, bool forceRefresh, CancellationToken token)
    {
        SectionTitle = T("For You");
        SectionEmptyText = T("No recommendations yet. Press F5 to try again.");
        var options = new RecommendationOptions(FeaturedRegion, HideShortsInFeed, forceRefresh);

        if (forceRefresh || !_recommendations.IsCacheFresh)
            BeginLoading(T("Finding videos for you..."));
        var result = await Task.Run(() => _recommendations.GetAsync(options, null, token), token);
        token.ThrowIfCancellationRequested();
        ShowItems(result.Videos, moveFocus,
            extraSummary: result.IsPersonal ? null : T("trending for now. Watch, like or subscribe and For You learns what you enjoy"));
    }

    [RelayCommand]
    public void NotInterested(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null || !item.IsVideo) return;
        _library.HideVideo(item.Id);
        _recommendations.Forget(v => v.Id == item.Id);
        _accessibility.Announce(T("Not interested. It won't be recommended again."), AnnouncementPriority.High);
        if (CurrentSection == BrowseSection.ForYou)
            RemoveItemKeepingPosition(item);
    }

    [RelayCommand]
    public void DontRecommendChannel(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null) return;
        var channelName = item.Kind == SearchResultKind.Channel ? item.Title : item.ChannelTitle;
        var channelId = (item.Kind == SearchResultKind.Channel ? item.Id : item.ChannelId) ?? channelName;
        if (string.IsNullOrWhiteSpace(channelName))
            return;
        _library.BlockChannel(channelId, channelName);
        _recommendations.Forget(v => v.ChannelId == channelId || string.Equals(v.ChannelTitle, channelName, StringComparison.OrdinalIgnoreCase));
        _accessibility.Announce(T("{0} won't be recommended again.", channelName), AnnouncementPriority.High);
        if (CurrentSection == BrowseSection.ForYou)
        {
            var index = SelectedSearchResult is null ? 0 : SearchResults.IndexOf(SelectedSearchResult);
            foreach (var row in SearchResults.Where(v => v.ChannelId == channelId || v.ChannelTitle == channelName).ToList())
                SearchResults.Remove(row);
            if (SearchResults.Count > 0)
            {
                SelectedSearchResult = SearchResults[Math.Clamp(index, 0, SearchResults.Count - 1)];
                RequestFocusResults?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // --- Likes ---

    [RelayCommand]
    public void ToggleLikeForItem(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item is null || !item.IsVideo) return;
        bool liked = _library.ToggleLike(item);
        SyncLikeToYouTube(item.Id, liked);
        _accessibility.Announce(liked ? T("Liked {0}.", item.Title) : T("Removed like from {0}.", item.Title), AnnouncementPriority.High);
        if (!liked && CurrentSection == BrowseSection.Playlist && _openLibraryPlaylistId == LibraryService.LikedPlaylistId)
            RemoveItemKeepingPosition(item);
        UpdateCurrentVideoLibraryState();
    }

    [RelayCommand]
    public void ToggleLikeCurrent()
    {
        var video = CurrentLibraryVideoForActions();
        if (video is null)
        {
            _accessibility.Announce(T("No video is loaded."), AnnouncementPriority.Medium);
            return;
        }
        bool liked = _library.ToggleLike(video);
        SyncLikeToYouTube(video.Id, liked);
        UpdateCurrentVideoLibraryState();
        _accessibility.Announce(liked ? T("Liked.") : T("Like removed."), AnnouncementPriority.High);
    }

    /// <summary>The playing video with the best details known (title and channel once loaded).</summary>
    private SearchResult? CurrentLibraryVideoForActions() =>
        CurrentVideo is { } details && _currentLibraryVideo is { } lib && details.Id.Value == lib.Id
            ? details.ToSearchResult()
            : _currentLibraryVideo;

    // --- Playlists section ---

    private void ShowPlaylists(bool moveFocus)
    {
        SectionTitle = T("Playlists");
        SectionEmptyText = T("No playlists yet.");
        var playlists = _library.Playlists.ToList();
        if (playlists.All(p => p.Kind != PlaylistKind.Liked))
            playlists.Insert(0, _library.GetPlaylist(LibraryService.LikedPlaylistId)!);
        ShowItems(playlists.Select(p => p.ToSearchResult()).ToList(), moveFocus);
    }

    /// <summary>The window shows the "Add to playlist" menu for this video (null: the playing video).</summary>
    public event EventHandler<SearchResult?>? RequestAddToPlaylistMenu;

    [RelayCommand]
    public void ShowAddToPlaylistMenu(SearchResult? item)
    {
        if (item is not null && !item.IsVideo)
            return;
        if (item is null && CurrentLibraryVideoForActions() is null)
        {
            _accessibility.Announce(T("No video is loaded."), AnnouncementPriority.Medium);
            return;
        }
        RequestAddToPlaylistMenu?.Invoke(this, item);
    }

    public IReadOnlyList<Playlist> PlaylistsForMenu() =>
        _library.Playlists.Where(p => p.Kind == PlaylistKind.Local).ToList();

    [RelayCommand]
    public void CreatePlaylist()
    {
        var name = AskForText?.Invoke(T("New playlist"), T("Playlist name:"), string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return;
        var playlist = _library.CreatePlaylist(name);
        _accessibility.Announce(T("Created playlist {0}.", playlist.Name), AnnouncementPriority.High);
        if (CurrentSection == BrowseSection.Playlists)
        {
            ShowPlaylists(moveFocus: false);
            SelectedSearchResult = SearchResults.FirstOrDefault(r => r.LibraryPlaylistId == playlist.Id);
            RequestFocusResults?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Adds a video to a playlist; a null <paramref name="playlistId"/> makes a new playlist first.</summary>
    public void AddToPlaylist(SearchResult? item, string? playlistId)
    {
        item ??= CurrentLibraryVideoForActions();
        if (item is null || !item.IsVideo)
            return;
        string name;
        if (playlistId is null)
        {
            var newName = AskForText?.Invoke(T("New playlist"), T("Playlist name:"), string.Empty);
            if (string.IsNullOrWhiteSpace(newName))
                return;
            var created = _library.CreatePlaylist(newName, new[] { item });
            _accessibility.Announce(T("Created {0} with {1}.", created.Name, item.Title), AnnouncementPriority.High);
            return;
        }
        name = _library.GetPlaylist(playlistId)?.Name ?? "playlist";
        bool added = _library.AddToPlaylist(playlistId, item);
        _accessibility.Announce(added ? T("Added to {0}.", name) : T("Already in {0}.", name), AnnouncementPriority.High);
    }

    [RelayCommand]
    public async Task RenameOpenPlaylistAsync()
    {
        var id = SelectedSearchResult?.LibraryPlaylistId is { } selectedId && CurrentSection == BrowseSection.Playlists
            ? selectedId
            : _openLibraryPlaylistId;
        if (id is null || id == LibraryService.LikedPlaylistId || _library.GetPlaylist(id) is not { } playlist)
        {
            _accessibility.Announce(T("Liked videos can't be renamed."), AnnouncementPriority.Medium);
            return;
        }
        var name = AskForText?.Invoke(T("Rename playlist"), T("New name:"), playlist.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == playlist.Name)
            return;
        _library.RenamePlaylist(id, name);
        _accessibility.Announce(T("Renamed to {0}.", name.Trim()), AnnouncementPriority.High);
        if (CurrentSection == BrowseSection.Playlist)
        {
            _openPlaylistTitle = name.Trim();
            SectionTitle = _openPlaylistTitle;
        }
        else
        {
            var index = SelectedSearchResult is null ? 0 : SearchResults.IndexOf(SelectedSearchResult);
            ShowPlaylists(moveFocus: false);
            if (index >= 0 && index < SearchResults.Count)
                SelectedSearchResult = SearchResults[index];
            RequestFocusResults?.Invoke(this, EventArgs.Empty);
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    public async Task DeleteOpenPlaylistAsync()
    {
        var id = CurrentSection == BrowseSection.Playlists ? SelectedSearchResult?.LibraryPlaylistId : _openLibraryPlaylistId;
        if (id is null || _library.GetPlaylist(id) is not { } playlist)
            return;
        if (playlist.Kind == PlaylistKind.Liked)
        {
            _accessibility.Announce(T("Liked videos can't be deleted. Remove videos with Delete instead."), AnnouncementPriority.Medium);
            return;
        }
        var what = playlist.Kind == PlaylistKind.YouTube
            ? T("Remove the saved YouTube playlist \"{0}\" from your library? It stays on YouTube.", playlist.Name)
            : T("Delete the playlist \"{0}\" with {1}? This cannot be undone.", playlist.Name, Plural(playlist.Videos.Count, "{0} video", "{0} videos"));
        if (Confirm?.Invoke(T("Delete playlist"), what) != true)
            return;
        _library.DeletePlaylist(id);
        _accessibility.Announce(T("Deleted {0}.", playlist.Name), AnnouncementPriority.High);
        if (CurrentSection == BrowseSection.Playlist)
            await GoBackAsync();
        else if (SelectedSearchResult is { } row)
            RemoveItemKeepingPosition(row);
    }

    // --- Playlist page ---

    /// <summary>Opens a playlist row: from the library, or a YouTube playlist from search or a link.</summary>
    public async Task OpenPlaylistAsync(SearchResult item)
    {
        if (item.LibraryPlaylistId is { } libraryId)
            await OpenPlaylistPageAsync(libraryId, null, item.Title);
        else
            await OpenPlaylistPageAsync(null, item.Id, item.Title);
    }

    public async Task OpenYouTubePlaylistAsync(string youTubePlaylistId)
    {
        await LeavePlayerViewAsync();
        var saved = _library.FindSavedYouTubePlaylist(youTubePlaylistId);
        await OpenPlaylistPageAsync(saved?.Id, youTubePlaylistId, saved?.Name ?? "YouTube playlist");
    }

    private async Task OpenPlaylistPageAsync(string? libraryId, string? youTubeId, string title)
    {
        if (!IsDetailView || CurrentSection != BrowseSection.Playlist ||
            _openLibraryPlaylistId != libraryId || _openYouTubePlaylistId != youTubeId)
        {
            PushNavigation();
        }
        _openLibraryPlaylistId = libraryId;
        _openYouTubePlaylistId = youTubeId ?? (libraryId is null ? null : _library.GetPlaylist(libraryId)?.YouTubeId);
        _openPlaylistTitle = title;
        _openPlaylistOwner = null;
        await ShowSectionAsync(BrowseSection.Playlist, moveFocus: true);
    }

    private async Task ShowPlaylistPageAsync(bool moveFocus, bool forceRefresh, CancellationToken token)
    {
        RaisePlaylistViewState();
        if (_openLibraryPlaylistId is { } libraryId)
        {
            var playlist = _library.GetPlaylist(libraryId);
            if (playlist is null)
            {
                SectionTitle = T("Playlist");
                SectionEmptyText = T("This playlist was deleted. Press Alt+Left to go back.");
                ShowItems(Array.Empty<SearchResult>(), moveFocus);
                return;
            }

            // A saved YouTube playlist refreshes from YouTube on F5.
            if (forceRefresh && playlist.Kind == PlaylistKind.YouTube && playlist.YouTubeId is { } ytId)
            {
                BeginLoading(T("Updating from YouTube..."));
                var fresh = await _playlists.GetAllAsync(ytId, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                if (fresh.IsSuccess)
                    playlist = _library.SaveYouTubePlaylist(ytId, fresh.Value.Title, fresh.Value.Owner, fresh.Value.Videos);
                else
                    _accessibility.Announce(T("Could not update from YouTube. {0}", fresh.Error?.Message), AnnouncementPriority.High);
            }

            _openPlaylistTitle = playlist.Name;
            SectionTitle = playlist.Name;
            SectionEmptyText = playlist.Kind switch
            {
                PlaylistKind.Liked => T("No liked videos yet. Press Control+K on any video, or in the player, to like it."),
                _ => T("This playlist is empty. Press Control+P on any video, or in the player, to add it here.")
            };
            ShowItems(playlist.Videos.Select(v => v.ToSearchResult()).ToList(), moveFocus);
            return;
        }

        if (_openYouTubePlaylistId is not { } playlistId)
            return;

        SectionTitle = _openPlaylistTitle;
        SectionEmptyText = T("This playlist could not be loaded. It may be private or deleted. Press F5 to try again, or Alt+Left to go back.");
        BeginLoading(T("Loading playlist..."));
        var page = await _playlists.GetPageAsync(playlistId, null, token);
        token.ThrowIfCancellationRequested();
        if (page.IsFailure)
        {
            ShowItems(Array.Empty<SearchResult>(), moveFocus: false);
            _accessibility.Announce(T("Could not load the playlist. {0}", page.Error?.Message), AnnouncementPriority.High);
            return;
        }

        if (!string.IsNullOrWhiteSpace(page.Value.Title))
            _openPlaylistTitle = page.Value.Title;
        _openPlaylistOwner = page.Value.Owner;
        SectionTitle = _openPlaylistTitle;
        ShowItems(page.Value.Videos, moveFocus, extraSummary: _openPlaylistOwner is { Length: > 0 } owner ? T("by {0}", owner) : null);
        _playlistPageToken = page.Value.NextPageToken;
        if (_playlistPageToken is not null)
        {
            SetLoadMore(async ct =>
            {
                var next = await _playlists.GetPageAsync(playlistId, _playlistPageToken, ct);
                if (next.IsFailure)
                    return (Array.Empty<SearchResult>(), false);
                _playlistPageToken = next.Value.NextPageToken;
                return (next.Value.Videos, _playlistPageToken is not null);
            });
        }
    }

    [RelayCommand]
    public async Task SaveOpenPlaylistAsync()
    {
        if (_openYouTubePlaylistId is not { } playlistId || _openLibraryPlaylistId is not null)
            return;
        _accessibility.Announce(T("Saving playlist..."), AnnouncementPriority.Low);
        var all = await _playlists.GetAllAsync(playlistId);
        if (all.IsFailure)
        {
            _accessibility.Announce(T("Could not save the playlist. {0}", all.Error?.Message), AnnouncementPriority.High);
            return;
        }
        var title = string.IsNullOrWhiteSpace(all.Value.Title) ? _openPlaylistTitle : all.Value.Title;
        var saved = _library.SaveYouTubePlaylist(playlistId, title, all.Value.Owner, all.Value.Videos);
        if (CurrentSection == BrowseSection.Playlist && _openYouTubePlaylistId == playlistId)
        {
            _openLibraryPlaylistId = saved.Id;
            RaisePlaylistViewState();
        }
        _accessibility.Announce(T("Saved {0} to Playlists, {1}. Press Control+5 to find it.", saved.Name, Plural(saved.Videos.Count, "{0} video", "{0} videos")), AnnouncementPriority.High);
    }

    /// <summary>Plays the open playlist (or the selected one in Playlists) from the top, or shuffled.</summary>
    [RelayCommand]
    public Task PlayAllAsync() => PlayPlaylistAsync(shuffle: false);

    [RelayCommand]
    public Task ShufflePlayAsync() => PlayPlaylistAsync(shuffle: true);

    private async Task PlayPlaylistAsync(bool shuffle)
    {
        List<SearchResult> videos;
        if (CurrentSection == BrowseSection.Playlists && SelectedSearchResult is { Kind: SearchResultKind.Playlist } row)
        {
            videos = await VideosOfPlaylistRowAsync(row);
        }
        else if (CurrentSection == BrowseSection.Playlist && _openLibraryPlaylistId is null && _openYouTubePlaylistId is { } ytId && CanLoadMore)
        {
            _accessibility.Announce(T("Loading the whole playlist..."), AnnouncementPriority.Medium);
            var all = await _playlists.GetAllAsync(ytId);
            videos = all.IsSuccess ? all.Value.Videos.ToList() : SearchResults.ToList();
        }
        else
        {
            videos = SearchResults.Where(r => r.IsVideo).ToList();
        }

        videos = videos.Where(v => v.IsVideo).ToList();
        if (videos.Count == 0)
        {
            _accessibility.Announce(T("Nothing to play."), AnnouncementPriority.Medium);
            return;
        }
        if (shuffle)
            videos = videos.OrderBy(_ => Random.Shared.Next()).ToList();

        _accessibility.Announce(shuffle ? T("Shuffling {0}.", Plural(videos.Count, "{0} video", "{0} videos")) : T("Playing {0}.", Plural(videos.Count, "{0} video", "{0} videos")), AnnouncementPriority.Medium);
        // One queue change for the whole list; adding them one at a time froze the window on long playlists.
        if (_playbackCoordinator is PlaybackCoordinator coord)
            await coord.ReplaceQueueAsync(videos.Select(v => v with { StatusPrefix = null }));
        else
            foreach (var video in videos)
                await _playbackCoordinator.EnqueueAsync(video with { StatusPrefix = null });
        await PlaySearchResultAsync(videos[0]);
    }

    private async Task<List<SearchResult>> VideosOfPlaylistRowAsync(SearchResult row)
    {
        if (row.LibraryPlaylistId is { } id)
            return _library.GetPlaylist(id)?.Videos.Select(v => v.ToSearchResult()).ToList() ?? new();
        _accessibility.Announce(T("Loading the whole playlist..."), AnnouncementPriority.Medium);
        var all = await _playlists.GetAllAsync(row.Id);
        if (all.IsFailure)
            _accessibility.Announce(T("Could not load the playlist. {0}", all.Error?.Message), AnnouncementPriority.High);
        return all.IsSuccess ? all.Value.Videos.ToList() : new();
    }

    /// <summary>Alt+Up / Alt+Down in your own playlists.</summary>
    public void MoveSelectedInPlaylist(int offset)
    {
        if (!IsOpenPlaylistInLibrary || SelectedSearchResult is not { IsVideo: true } item || _openLibraryPlaylistId is not { } id)
            return;
        var index = SearchResults.IndexOf(item);
        var target = _library.MoveInPlaylist(id, item.Id, offset);
        if (target < 0 || target == index)
        {
            _accessibility.Announce(offset < 0 ? T("Already first.") : T("Already last."), AnnouncementPriority.Low);
            return;
        }
        SearchResults.Move(index, target);
        SelectedSearchResult = SearchResults[target];
        RequestFocusResults?.Invoke(this, EventArgs.Empty);
        _accessibility.Announce(T("Moved to {0} of {1}.", target + 1, SearchResults.Count), AnnouncementPriority.Medium);
    }

    // --- Google Takeout ---

    /// <summary>Everything in a Takeout zip: subscriptions, playlists, likes, Watch later and history.</summary>
    public async Task ImportTakeoutAsync(string path)
    {
        if (IsImporting)
            return;
        IsImporting = true;
        try
        {
            TakeoutContents contents;
            await using (var stream = File.OpenRead(path))
                contents = await Task.Run(() => TakeoutImport.ParseZip(stream));

            if (contents.IsEmpty)
            {
                _accessibility.Announce(T("Nothing to import was found in this zip. In Google Takeout, choose YouTube and YouTube Music, with subscriptions, playlists and history."), AnnouncementPriority.High);
                return;
            }

            var report = new List<string>();
            if (contents.Subscriptions is { Channels.Count: > 0 } subs)
            {
                int added = _library.ImportSubscriptions(subs.Channels);
                report.Add(Plural(added, "{0} channel", "{0} channels"));
                if (added > 0)
                    _feed = null;
            }
            if (contents.History.Count > 0)
            {
                int added = _library.ImportHistory(contents.History);
                report.Add(Plural(added, "{0} watched video", "{0} watched videos"));
            }

            // Takeout lists only video ids; look up titles so the lists are readable.
            var known = _library.History.ToDictionary(h => h.VideoId, h => h.ToSearchResult());
            var ids = contents.Playlists.SelectMany(p => p.VideoIds)
                .Concat(contents.LikedVideoIds).Concat(contents.WatchLaterIds)
                .Distinct().Where(id => !known.ContainsKey(id)).ToList();
            if (ids.Count > 0)
            {
                _accessibility.Announce(T("Looking up {0}. This can take a minute.", Plural(ids.Count, "{0} video title", "{0} video titles")), AnnouncementPriority.Medium);
                var described = await _playlists.DescribeVideosAsync(ids);
                foreach (var video in described)
                    known[video.Id] = video;
            }
            SearchResult Video(string id) => known.TryGetValue(id, out var v) ? v : TakeoutImport.PlaceholderVideo(id);

            if (contents.LikedVideoIds.Count > 0)
            {
                int added = _library.ImportLikes(contents.LikedVideoIds.Select(Video));
                report.Add(Plural(added, "{0} liked video", "{0} liked videos"));
            }
            if (contents.WatchLaterIds.Count > 0)
            {
                int added = _library.ImportWatchLater(contents.WatchLaterIds.Select(Video));
                report.Add(Plural(added, "{0} Watch Later video", "{0} Watch Later videos"));
            }
            int playlistsAdded = 0;
            var existingNames = _library.Playlists.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var playlist in contents.Playlists)
            {
                if (!existingNames.Add(playlist.Name))
                    continue; // imported before
                _library.CreatePlaylist(playlist.Name, playlist.VideoIds.Select(Video));
                playlistsAdded++;
            }
            if (contents.Playlists.Count > 0)
                report.Add(Plural(playlistsAdded, "{0} playlist", "{0} playlists"));

            _accessibility.Announce(T("Imported from Google Takeout: {0}.", string.Join(", ", report)), AnnouncementPriority.High);
            if (CurrentSection is BrowseSection.Playlists or BrowseSection.Channels or BrowseSection.History)
                _ = ShowSectionAsync(CurrentSection, moveFocus: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _accessibility.Announce(T("Could not import: {0}", ex.Message), AnnouncementPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Takeout import failed.");
            _accessibility.Announce(T("Could not import: {0}", ex.Message), AnnouncementPriority.High);
        }
        finally
        {
            IsImporting = false;
        }
    }
}
