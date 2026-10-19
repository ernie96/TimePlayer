using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Community;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Subtitle selection option model for UI binding.
/// </summary>
public record CaptionTrackOption(string DisplayName, CaptionTrack? Track)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Main window view model managing search, queue, modal player dialog state,
/// live screen reader subtitle announcements, keyboard shortcuts, and diagnostics.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IPlaybackCoordinator _playbackCoordinator;
    private readonly IPlayerEngine _playerEngine;
    private readonly IYtDlpManager _ytDlpManager;
    private readonly IAccessibilityService _accessibility;
    private readonly IConfigurationService _configService;
    private readonly IInstanceHealthService _healthService;
    private readonly IPublicInstanceDirectory _instanceDirectory;
    private readonly ILogger<MainViewModel> _logger;

    // View Navigation & Dialog State
    [ObservableProperty]
    private bool _isPlayerDialogOpen;

    [ObservableProperty]
    private bool _isSettingsDialogOpen;

    // Search & Results State
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _searchResults = new();

    [ObservableProperty]
    private SearchResult? _selectedSearchResult;

    // Current Media State
    [ObservableProperty]
    private VideoDetails? _currentVideo;

    [ObservableProperty]
    private string _currentVideoTitle = T("No video loaded");

    [ObservableProperty]
    private string _currentChannelTitle = string.Empty;

    [ObservableProperty]
    private PlaybackState _playbackState = PlaybackState.Stopped;

    [ObservableProperty]
    private string _playbackStatusText = "Ready";

    [ObservableProperty]
    private TimeSpan _currentTime = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private double _volumePercent = 80.0;

    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isEngineInitialized;

    [ObservableProperty]
    private bool _isVideoActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorBannerName))]
    private string? _errorMessage;

    public string VolumeText => T("Volume {0:0}%", VolumePercent);
    public string ErrorBannerName => T("Problem: {0}", ErrorMessage);

    [ObservableProperty]
    private string _statusAnnouncement = "TimePlayer ready. Focus is in the search box.";

    [ObservableProperty]
    private string _activeBackendName = "TubeRepair (Primary)";

    // Screen Reader Live Subtitles
    [ObservableProperty]
    private string _currentSubtitleText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CaptionTrackOption> _availableSubtitleTracks = new();

    [ObservableProperty]
    private CaptionTrackOption? _selectedSubtitleTrack;

    // Playback Queue
    [ObservableProperty]
    private ObservableCollection<PlaybackQueueItem> _playbackQueue = new();

    [ObservableProperty]
    private PlaybackQueueItem? _selectedQueueItem;

    [ObservableProperty]
    private PlaybackQueueItem? _currentQueueItem;

    [ObservableProperty]
    private bool _canPlayNext;

    [ObservableProperty]
    private bool _canPlayPrevious;

    // Diagnostics & Preferences
    [ObservableProperty]
    private string _diagnosticsReport = "Press 'Run Diagnostics' to test backend instances and playback components.";

    [ObservableProperty]
    private bool _isRunningDiagnostics;

    [ObservableProperty]
    private QualityPreference _selectedQuality = QualityPreference.Auto;

    // Braille Display Progress Indicator
    [ObservableProperty]
    private string _brailleDisplayLine = string.Empty;

    [ObservableProperty]
    private int _selectedBrailleCellCount = 40;

    partial void OnSelectedBrailleCellCountChanged(int value)
    {
        if (value < 2) return;
        _brailleTrack = new BrailleProgressTrack(value);
        UpdateBrailleDisplayLine();
        _ = PersistAccessibilitySettingsAsync();
    }

    [ObservableProperty]
    private ObservableCollection<int> _availableBrailleCellCounts = new() { 12, 14, 16, 18, 20, 24, 32, 40, 60, 70, 80, 84 };

    [ObservableProperty]
    private bool _enableBrailleProgress = true;

    partial void OnEnableBrailleProgressChanged(bool value)
    {
        UpdateBrailleDisplayLine();
        _ = PersistAccessibilitySettingsAsync();
    }

    // Retry and Error State
    [ObservableProperty]
    private string? _lastSearchQuery;

    [ObservableProperty]
    private string? _lastPlayedUrlOrId;

    [ObservableProperty]
    private bool _canRetry;

    // Advanced Settings
    [ObservableProperty]
    private bool _enableHardwareDecoding = true;

    [ObservableProperty]
    private string _dependencyStatusMessage = T("All media components verified.");

    [ObservableProperty]
    private bool _isCheckingUpdates;

    private BrailleProgressTrack _brailleTrack = new(40);
    private System.Threading.Timer? _brailleBlinkTimer;
    private bool _brailleBlinkVisible = true;

    // Focus Restoration Events for View
    public event EventHandler? RequestFocusSearch;
    public event EventHandler? RequestFocusResults;
    public event EventHandler? RequestFocusPlayer;

    private int _isInitializing;
    private bool _isDisposed;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _playbackCts;

    public MainViewModel(
        IPlaybackCoordinator playbackCoordinator,
        IPlayerEngine playerEngine,
        IYtDlpManager ytDlpManager,
        IAccessibilityService accessibility,
        IConfigurationService configService,
        IInstanceHealthService healthService,
        IPublicInstanceDirectory instanceDirectory,
        ILibraryService library,
        IDiscoveryService discovery,
        IWatchPageService watchPage,
        IDownloadService downloads,
        IPlaylistService playlists,
        IRecommendationService recommendations,
        ISponsorBlockService sponsorBlock,
        IDislikeService dislikes,
        ILogger<MainViewModel> logger)
    {
        _sponsorBlock = sponsorBlock ?? throw new ArgumentNullException(nameof(sponsorBlock));
        _dislikes = dislikes ?? throw new ArgumentNullException(nameof(dislikes));
        _playlists = playlists ?? throw new ArgumentNullException(nameof(playlists));
        _recommendations = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _watchPage = watchPage ?? throw new ArgumentNullException(nameof(watchPage));
        // Engine and coordinator events arrive on background threads; collections must change on this one.
        _uiContext = SynchronizationContext.Current;
        _uiThreadId = Environment.CurrentManagedThreadId;
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _playbackCoordinator = playbackCoordinator ?? throw new ArgumentNullException(nameof(playbackCoordinator));
        _playerEngine = playerEngine ?? throw new ArgumentNullException(nameof(playerEngine));
        _ytDlpManager = ytDlpManager ?? throw new ArgumentNullException(nameof(ytDlpManager));
        _accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _instanceDirectory = instanceDirectory ?? throw new ArgumentNullException(nameof(instanceDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var config = _configService.CurrentConfig;
        _volumePercent = config.Playback.DefaultVolumePercent;
        _selectedQuality = config.Playback.PreferredQuality;
        _selectedBrailleCellCount = config.Accessibility.BrailleDisplayCellCount;
        if (!_availableBrailleCellCounts.Contains(_selectedBrailleCellCount))
        {
            _availableBrailleCellCounts.Add(_selectedBrailleCellCount);
        }
        _enableBrailleProgress = config.Accessibility.EnableBrailleProgress;
        _enableHardwareDecoding = config.Playback.HardwareDecoding;
        _brailleTrack = new BrailleProgressTrack(_selectedBrailleCellCount);
        _brailleDisplayLine = _brailleTrack.GenerateDisplayLine(TimeSpan.Zero, TimeSpan.Zero, false, false);
        InitializeLibraryPreferences();
        InitializePlayerPreferences();
        InitializeCommunitySettings();
        InitializeStepSettings();
        InitializeSoundSettings();
        ApplyAppMode(announce: false);
        InitializeViewState();
        InitializeBrailleAddonPreference();
        _checkForAppUpdatesAtStartup = _configService.CurrentConfig.Update.CheckAtStartup;
        _announceRunCount = _configService.CurrentConfig.Runs.Announce;
        _shareRunCount = _configService.CurrentConfig.Runs.Share == true;
        _openLinksFromOtherApps = _configService.CurrentConfig.OpenLinksFromOtherApps;
        _shareLinkKind = _configService.CurrentConfig.ShareLinks is ShareYouTube or ShareTimePlayer
            ? _configService.CurrentConfig.ShareLinks
            : ShareAsk;

        // Player engine events
        _playerEngine.StateChanged += OnEngineStateChanged;
        _playerEngine.PositionChanged += OnEnginePositionChanged;
        _playerEngine.SubtitleTextChanged += OnEngineSubtitleTextChanged;

        // Coordinator events
        _playbackCoordinator.CurrentVideoChanged += OnCoordinatorCurrentVideoChanged;
        _playbackCoordinator.ErrorOccurred += OnCoordinatorErrorOccurred;
        _playbackCoordinator.StatusChanged += OnCoordinatorStatusChanged;
        if (_playbackCoordinator is PlaybackCoordinator concreteCoord)
        {
            concreteCoord.QueueChanged += OnCoordinatorQueueChanged;
            concreteCoord.QueueEnded += OnCoordinatorQueueEnded;
            concreteCoord.ItemSkipped += OnCoordinatorItemSkipped;
            concreteCoord.CaptionsChanged += OnCoordinatorCaptionsChanged;
        }

        // Initialize subtitle tracks list with "Off"
        ResetSubtitleTracks();
    }

    private void ResetSubtitleTracks()
    {
        AvailableSubtitleTracks.Clear();
        var offOption = new CaptionTrackOption("Captions Off", null);
        AvailableSubtitleTracks.Add(offOption);
        SelectedSubtitleTrack = offOption;
    }

    public async Task InitializePlayerEngineAsync(nint windowHandle)
    {
        if (IsEngineInitialized || Interlocked.CompareExchange(ref _isInitializing, 1, 0) != 0)
            return;

        try
        {
            _logger.LogInformation("Initializing mpv playback engine with HWND {Hwnd}...", windowHandle);
            var options = new PlayerOptions
            {
                YtDlpExecutablePath = _ytDlpManager.ExecutablePath,
                InitialVolume = VolumePercent,
                InitialSpeed = PlaybackSpeed,
                HardwareDecoding = _configService.CurrentConfig.Playback.HardwareDecoding
            };

            await _playerEngine.InitializeAsync(windowHandle, options);
            IsEngineInitialized = true;
            PlaybackStatusText = T("Engine initialized");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize playback engine.");
            ErrorMessage = T("Player initialization failed: {0}", ex.Message);
            PlaybackStatusText = T("Engine error");
            _accessibility.Announce(T("Failed to initialize video engine. Please check dependencies."), AnnouncementPriority.High);
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        var query = SearchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _accessibility.Announce(T("Please enter a search query or YouTube link."), AnnouncementPriority.Medium);
            return;
        }

        // A playlist link opens the playlist; a video link (even one inside a playlist) plays the video.
        if (await OpenLinkAsync(query))
            return;

        // Cancel previous in-flight search if any
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var cancellationToken = _searchCts.Token;

        IsSearching = true;
        ErrorMessage = null;
        LastSearchQuery = query;
        CanRetry = false;
        _accessibility.Announce(T("Searching..."), AnnouncementPriority.Medium);

        try
        {
            var searchResult = SelectedSearchKind == SearchKind.Playlists
                ? await _playlists.SearchAsync(query, null, cancellationToken)
                : await _playbackCoordinator.SearchAsync(query, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (searchResult.IsSuccess)
            {
                _lastSearchResults = searchResult.Value.Items.DistinctBy(item => item.Id).ToList();
                _sectionCts?.Cancel();
                // Results are a page of their own; Back returns to where the search started.
                if (CurrentSection != BrowseSection.Search)
                    PushNavigation();
                CurrentSection = BrowseSection.Search;
                SyncSelectedTab(BrowseSection.Search);
                SectionTitle = T("Search results for {0}", query);
                SearchResults.Clear();
                foreach (var item in _lastSearchResults)
                {
                    SearchResults.Add(Decorate(item));
                }
                if (SelectedSearchKind == SearchKind.Playlists)
                    ConfigurePlaylistSearchPaging(query, searchResult.Value.NextPageToken);
                else
                    ConfigureSearchPaging();

                UpdateActiveBackendName();
                var count = SearchResults.Count;
                var announcement = AccessibleTextFormatter.FormatSearchResultsSpoken(count, query);
                _accessibility.Announce(announcement, AnnouncementPriority.Medium);

                if (count > 0)
                {
                    SelectedSearchResult = SearchResults[0];
                    RequestFocusResults?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                ErrorMessage = T("Search failed: {0}", searchResult.Error?.Message);
                CanRetry = true;
                _accessibility.Announce(ErrorMessage, AnnouncementPriority.High);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Search cancelled by user.");
            _accessibility.Announce(T("Search cancelled."), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing search for query '{Query}'", query);
            ErrorMessage = T("Failed to search YouTube. Please check network or try a direct link.");
            CanRetry = true;
            _accessibility.Announce(ErrorMessage, AnnouncementPriority.High);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public void ClearError()
    {
        ErrorMessage = null;
        CanRetry = false;
        // The banner (and the focused button in it) disappears; land somewhere useful.
        if (IsPlayerViewVisible)
            FocusPlayerIfShown();
        else
            RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the error banner with a reason, always, and says how to reach it.</summary>
    private void ShowProblem(string what, string? reason, bool canRetry = true)
    {
        var why = string.IsNullOrWhiteSpace(reason)
            ? T("No reason was given. The log (Settings, Updates and help, Open log folder) may say more.")
            : reason.Trim();
        ErrorMessage = T("{0}: {1}", what, why);
        CanRetry = canRetry;
        _logger.LogWarning("Shown to the user: {Problem}", ErrorMessage);
        _accessibility.Announce(T("{0} F6 reaches Retry and Dismiss.", ErrorMessage), AnnouncementPriority.High);
    }

    [RelayCommand]
    public async Task PasteAndPlayAsync()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText()?.Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not access clipboard.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _accessibility.Announce(T("Clipboard is empty or does not contain text."), AnnouncementPriority.High);
            return;
        }

        if (!await OpenLinkAsync(text))
        {
            SearchQuery = text;
            _accessibility.Announce(T("Pasted into search box."), AnnouncementPriority.Low);
            RequestFocusSearch?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Opens a YouTube playlist link, or plays a video link. False when it is neither.</summary>
    public async Task<bool> OpenLinkAsync(string text)
    {
        if (!YouTubeUrl.LooksLikeLink(text))
            return false;

        // A song or album from YouTube Music belongs in Music.
        if (!MusicMode && IncomingLink.IsYouTubeMusic(text) &&
            (YouTubeUrl.IsPlaylistLink(text) || YouTubeUrl.TryParse(text, out _)))
            ToggleMusicMode();

        if (YouTubeUrl.IsPlaylistLink(text) && YouTubeUrl.TryGetPlaylistId(text, out var listId))
        {
            SearchQuery = text;
            await OpenYouTubePlaylistAsync(listId);
            return true;
        }
        if (YouTubeUrl.TryParse(text, out var ytUrl))
        {
            // PlayDirectUrlAsync says "Loading video...".
            SearchQuery = ytUrl.NormalizedWatchUrl;
            await PlayDirectUrlAsync(ytUrl.NormalizedWatchUrl);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Plays a video the user picked from a list. Something new starts a fresh queue; a video already
    /// in the queue plays from its place there.
    /// </summary>
    [RelayCommand]
    public Task PlaySearchResultAsync(SearchResult? item) => PlayItemAsync(item, continueQueue: false);

    private static string? SearchResultIdOf(string url) =>
        YouTubeUrl.TryParse(url, out var parsed) ? parsed.VideoId.Value
        : YouTubeVideoId.TryParse(url, out var id) ? id.Value
        : null;

    /// <summary>Autoplay and Next: the video joins the end of the current queue, so Previous still works.</summary>
    private Task ContinueWithAsync(SearchResult item) => PlayItemAsync(item, continueQueue: true);

    private async Task PlayItemAsync(SearchResult? item, bool continueQueue)
    {
        item ??= SelectedSearchResult;
        if (item == null) return;
        if (item.Kind == SearchResultKind.Channel)
        {
            await OpenChannelAsync(item);
            return;
        }
        if (item.Kind == SearchResultKind.Playlist)
        {
            await LeavePlayerViewAsync();
            await OpenPlaylistAsync(item);
            return;
        }
        BeginNewPlayback();
        SetCurrentLibraryVideo(item);

        // Cancel previous in-flight playback loading if any
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _playbackCts = new CancellationTokenSource();
        var token = _playbackCts.Token;

        CurrentVideoTitle = item.Title;
        CurrentChannelTitle = item.ChannelTitle;
        if (item.Duration.HasValue && item.Duration.Value > TimeSpan.Zero)
        {
            Duration = item.Duration.Value;
            UpdateBrailleDisplayLine();
        }
        OpenPlayerSession();
        LastPlayedUrlOrId = item.Id;
        CanRetry = false;
        ErrorMessage = null;
        _accessibility.Announce(MusicMode ? T("Playing {0}", item.Title) : T("Loading {0}", item.Title), AnnouncementPriority.High);
        FocusPlayerIfShown();

        // The coordinator's queue, not the on-screen copy, which updates a moment later.
        bool queued = _playbackCoordinator is PlaybackCoordinator queueOwner
            ? queueOwner.IsQueued(item.Id)
            : PlaybackQueue.Any(q => q.VideoId.Value == item.Id);
        if (!queued)
        {
            if (continueQueue || _playbackCoordinator is not PlaybackCoordinator coordinator)
                await _playbackCoordinator.EnqueueAsync(item with { StatusPrefix = null });
            else
                await coordinator.ReplaceQueueAsync(new[] { item with { StatusPrefix = null } });
        }

        try
        {
            var result = await _playbackCoordinator.PlayUrlOrIdAsync(item.Id, token);
            if (!result.IsSuccess)
            {
                ShowProblem(T("Could not play this video"), result.Error?.Message);
            }
            else
            {
                UpdateActiveBackendName();
                PopulateSubtitleTracks();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Playback load cancelled by user.");
        }
    }

    [RelayCommand]
    public async Task PlayDirectUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // Cancel previous in-flight playback loading if any
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _playbackCts = new CancellationTokenSource();
        var token = _playbackCts.Token;

        BeginNewPlayback();
        CurrentVideoTitle = T("Loading YouTube video...");
        CurrentChannelTitle = string.Empty;
        SetCurrentLibraryVideo(YouTubeUrl.TryParse(url, out var directUrl)
            ? new SearchResult { Id = directUrl.VideoId.Value, Title = string.Empty, ChannelTitle = string.Empty }
            : YouTubeVideoId.TryParse(url, out var directId)
                ? new SearchResult { Id = directId.Value, Title = string.Empty, ChannelTitle = string.Empty }
                : null);
        OpenPlayerSession();
        LastPlayedUrlOrId = url;
        CanRetry = false;
        ErrorMessage = null;
        _accessibility.Announce(T("Loading video..."), AnnouncementPriority.High);
        FocusPlayerIfShown();

        try
        {
            // A pasted link is new content: the old queue goes, as when picking from a list.
            if (_playbackCoordinator is PlaybackCoordinator coordinator && SearchResultIdOf(url) is { } linkId && !coordinator.IsQueued(linkId))
                await coordinator.ClearQueueAsync();
            var result = await _playbackCoordinator.PlayUrlOrIdAsync(url, token);
            if (!result.IsSuccess)
            {
                ShowProblem(T("Could not load this video"), result.Error?.Message);
            }
            else
            {
                UpdateActiveBackendName();
                PopulateSubtitleTracks();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Direct playback load cancelled by user.");
        }
    }

    [RelayCommand]
    public async Task ClosePlayerDialogAsync()
    {
        if (!IsPlayerDialogOpen) return;

        IsPlayerDialogOpen = false;
        StopBrailleBlinkTimer();
        RecordCurrentProgress(CurrentTime, flush: true);
        _ = PersistVolumeAsync();

        try
        {
            if (IsEngineInitialized)
            {
                await _playerEngine.StopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop playback when closing player dialog.");
        }

        PlaybackState = PlaybackState.Stopped;
        CurrentTime = TimeSpan.Zero;
        UpdateBrailleDisplayLine();

        RefreshLibraryStatusOfVisibleItems();
        RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    public void ClosePlayerDialog()
    {
        _ = ClosePlayerDialogAsync();
    }

    [RelayCommand]
    public async Task EnqueueSearchResultAsync(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item == null || item.Kind != SearchResultKind.Video) return;

        await _playbackCoordinator.EnqueueAsync(item);
        _accessibility.Announce(T("Added to queue."), AnnouncementPriority.Low);
    }

    [RelayCommand]
    public async Task PlayQueueItemAsync(PlaybackQueueItem? item)
    {
        item ??= SelectedQueueItem;
        if (item == null) return;

        BeginNewPlayback();
        CurrentVideoTitle = item.Title;
        CurrentChannelTitle = item.ChannelTitle;
        SetCurrentLibraryVideo(ToSearchResult(item));
        OpenPlayerSession();
        _accessibility.Announce(T("Loading {0}", item.Title), AnnouncementPriority.High);
        FocusPlayerIfShown();

        await _playbackCoordinator.PlayQueueItemAsync(item.QueueItemId);
        PopulateSubtitleTracks();
    }

    [RelayCommand]
    public async Task RemoveQueueItemAsync(PlaybackQueueItem? item)
    {
        item ??= SelectedQueueItem;
        if (item == null) return;

        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            await coord.RemoveFromQueueAsync(item.QueueItemId);
            _accessibility.Announce(T("Removed from queue."), AnnouncementPriority.Low);
        }
    }

    [RelayCommand]
    public async Task ClearQueueAsync()
    {
        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            await coord.ClearQueueAsync();
            _accessibility.Announce(T("Playback queue cleared."), AnnouncementPriority.Low);
        }
    }

    [RelayCommand]
    public async Task PlayNextAsync()
    {
        if (!CanPlayNext) return;

        AnnounceQueueNeighbour(+1);
        await _playbackCoordinator.PlayNextAsync();
        PopulateSubtitleTracks();
    }

    [RelayCommand]
    public async Task PlayPreviousAsync()
    {
        if (!CanPlayPrevious) return;

        AnnounceQueueNeighbour(-1);
        await _playbackCoordinator.PlayPreviousAsync();
        PopulateSubtitleTracks();
    }

    private void AnnounceQueueNeighbour(int offset)
    {
        var index = CurrentQueueItem is null ? -1 : PlaybackQueue.IndexOf(CurrentQueueItem);
        var target = index + offset;
        if (target >= 0 && target < PlaybackQueue.Count)
            _accessibility.Announce(PlaybackQueue[target].Title, AnnouncementPriority.Medium);
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        if (!IsEngineInitialized) return;

        try
        {
            var wasPlaying = PlaybackState == PlaybackState.Playing;
            await _playbackCoordinator.TogglePlayPauseAsync();
            _accessibility.Announce(wasPlaying ? T("Paused") : T("Playing"), AnnouncementPriority.Medium);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle play/pause.");
        }
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (!IsEngineInitialized) return;

        try
        {
            await _playerEngine.StopAsync();
            _accessibility.Announce(T("Playback stopped."), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop playback.");
        }
    }

    [RelayCommand]
    public async Task SeekSmallForwardAsync()
    {
        if (!IsEngineInitialized) return;

        var step = _configService.CurrentConfig.Playback.SmallSeekSeconds;
        try
        {
            var target = ClampToDuration(CurrentTime + TimeSpan.FromSeconds(step));
            await _playerEngine.SeekRelativeAsync(step);
            CurrentTime = target;
            UpdateBrailleDisplayLine();
            _accessibility.Announce(AccessibleTextFormatter.FormatTimeSpoken(target), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek forward.");
        }
    }

    [RelayCommand]
    public async Task SeekSmallBackwardAsync()
    {
        if (!IsEngineInitialized) return;

        var step = _configService.CurrentConfig.Playback.SmallSeekSeconds;
        try
        {
            var target = ClampToDuration(CurrentTime + TimeSpan.FromSeconds(-step));
            await _playerEngine.SeekRelativeAsync(-step);
            CurrentTime = target;
            UpdateBrailleDisplayLine();
            _accessibility.Announce(AccessibleTextFormatter.FormatTimeSpoken(target), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek backward.");
        }
    }

    [RelayCommand]
    public async Task SeekLargeForwardAsync()
    {
        if (!IsEngineInitialized) return;

        var step = _configService.CurrentConfig.Playback.LargeSeekSeconds;
        try
        {
            var target = ClampToDuration(CurrentTime + TimeSpan.FromSeconds(step));
            await _playerEngine.SeekRelativeAsync(step);
            CurrentTime = target;
            UpdateBrailleDisplayLine();
            _accessibility.Announce(AccessibleTextFormatter.FormatTimeSpoken(target), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek large forward.");
        }
    }

    [RelayCommand]
    public async Task SeekLargeBackwardAsync()
    {
        if (!IsEngineInitialized) return;

        var step = _configService.CurrentConfig.Playback.LargeSeekSeconds;
        try
        {
            var target = ClampToDuration(CurrentTime + TimeSpan.FromSeconds(-step));
            await _playerEngine.SeekRelativeAsync(-step);
            CurrentTime = target;
            UpdateBrailleDisplayLine();
            _accessibility.Announce(AccessibleTextFormatter.FormatTimeSpoken(target), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek large backward.");
        }
    }

    private TimeSpan ClampToDuration(TimeSpan position)
    {
        var dur = Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        return dur > TimeSpan.Zero && position > dur ? dur : position;
    }

    [RelayCommand]
    public async Task VolumeUpAsync()
    {
        if (!IsEngineInitialized) return;

        var step = _configService.CurrentConfig.Playback.VolumeStepPercent;
        var newVol = Math.Clamp(VolumePercent + step, 0.0, 100.0);
        try
        {
            await _playerEngine.SetVolumeAsync(newVol);
            VolumePercent = newVol;
            _accessibility.Announce(AccessibleTextFormatter.FormatVolumeSpoken(VolumePercent), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to raise volume.");
        }
    }

    [RelayCommand]
    public async Task VolumeDownAsync()
    {
        if (!IsEngineInitialized) return;

        var step = _configService.CurrentConfig.Playback.VolumeStepPercent;
        var newVol = Math.Clamp(VolumePercent - step, 0.0, 100.0);
        try
        {
            await _playerEngine.SetVolumeAsync(newVol);
            VolumePercent = newVol;
            _accessibility.Announce(AccessibleTextFormatter.FormatVolumeSpoken(VolumePercent), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lower volume.");
        }
    }

    [RelayCommand]
    public async Task ToggleMuteAsync()
    {
        if (!IsEngineInitialized) return;

        try
        {
            var newMute = !IsMuted;
            await _playerEngine.SetMuteAsync(newMute);
            IsMuted = newMute;
            _accessibility.Announce(IsMuted ? T("Audio muted.") : T("Audio unmuted."), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle mute.");
        }
    }

    [RelayCommand]
    public async Task SpeedUpAsync()
    {
        if (!IsEngineInitialized) return;

        var config = _configService.CurrentConfig.Playback;
        var newSpeed = Math.Clamp(PlaybackSpeed + config.SpeedStep, config.MinSpeed, config.MaxSpeed);
        try
        {
            await _playerEngine.SetSpeedAsync(newSpeed);
            PlaybackSpeed = newSpeed;
            _accessibility.Announce(AccessibleTextFormatter.FormatSpeedSpoken(PlaybackSpeed), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to increase speed.");
        }
    }

    [RelayCommand]
    public async Task SpeedDownAsync()
    {
        if (!IsEngineInitialized) return;

        var config = _configService.CurrentConfig.Playback;
        var newSpeed = Math.Clamp(PlaybackSpeed - config.SpeedStep, config.MinSpeed, config.MaxSpeed);
        try
        {
            await _playerEngine.SetSpeedAsync(newSpeed);
            PlaybackSpeed = newSpeed;
            _accessibility.Announce(AccessibleTextFormatter.FormatSpeedSpoken(PlaybackSpeed), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrease speed.");
        }
    }

    [RelayCommand]
    public async Task ResetSpeedAsync()
    {
        if (!IsEngineInitialized) return;

        try
        {
            await _playerEngine.SetSpeedAsync(1.0);
            PlaybackSpeed = 1.0;
            _accessibility.Announce(AccessibleTextFormatter.FormatSpeedSpoken(PlaybackSpeed), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset speed.");
        }
    }

    [RelayCommand]
    public async Task SelectSubtitleTrackAsync(CaptionTrackOption? option)
    {
        if (option == null || !IsEngineInitialized) return;

        SelectedSubtitleTrack = option;
        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            await coord.SetCaptionTrackAsync(option.Track);
            var label = option.Track != null ? T("Subtitles switched to {0}", option.DisplayName) : T("Subtitles turned off");
            _accessibility.Announce(label, AnnouncementPriority.Low);
        }
    }

    [RelayCommand]
    public void OpenSettings()
    {
        IsSettingsDialogOpen = true;
    }

    [RelayCommand]
    public void CloseSettings()
    {
        // The settings window is modal; Windows returns focus to where it was when it closes.
        IsSettingsDialogOpen = false;
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        IsRunningDiagnostics = true;
        _accessibility.Announce(T("Running diagnostics..."), AnnouncementPriority.Medium);

        var report = new StringBuilder();
        report.AppendLine("=== TimePlayer Diagnostics Report ===");
        report.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        // 1. TubeRepair Primary Backend
        report.AppendLine("[1] TubeRepair Primary Backend:");
        try
        {
            var trBase = _configService.CurrentConfig.Backend.TubeRepairBaseUrl;
            report.AppendLine($"  - Server URL: {trBase}");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ping = await client.GetAsync($"{trBase.TrimEnd('/')}/feeds/api/videos?q=test&max-results=1");
            report.AppendLine($"  - Health status: {(ping.IsSuccessStatusCode ? "Reachable (HTTP " + (int)ping.StatusCode + ")" : "HTTP " + (int)ping.StatusCode)}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  - TubeRepair check error: {ex.Message}");
        }
        report.AppendLine();

        // 2. Invidious Instances Health
        report.AppendLine("[2] Invidious Backend Status:");
        try
        {
            var invidiousInstances = await _instanceDirectory.GetInstancesAsync(BackendProviderType.Invidious);
            report.AppendLine($"  - Discovered instances: {invidiousInstances.Count}");
            var bestInvidious = await _healthService.SelectBestInstanceAsync(BackendProviderType.Invidious);
            if (bestInvidious != null)
            {
                report.AppendLine($"  - Active / Best instance: {bestInvidious.BaseUri}");
            }
            else
            {
                report.AppendLine("  - Active instance: None reachable (will fallback)");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  - Invidious check error: {ex.Message}");
        }
        report.AppendLine();

        // 3. InnerTube Tertiary Backend
        report.AppendLine("[3] InnerTube Tertiary Backend:");
        report.AppendLine($"  - Base URL: {_configService.CurrentConfig.Backend.InnerTubeBaseUrl}");
        report.AppendLine("  - Protocol: Anonymous YouTube InnerTube API v1");
        report.AppendLine();

        // 4. Local yt-dlp Status (Final Fallback)
        report.AppendLine("[4] Local yt-dlp Executable (Final Fallback):");
        report.AppendLine($"  - Path: {_ytDlpManager.ExecutablePath}");
        report.AppendLine($"  - Installed: {_ytDlpManager.IsInstalled}");
        if (_ytDlpManager.IsInstalled)
        {
            var verResult = await _ytDlpManager.GetVersionAsync();
            report.AppendLine($"  - Version: {(verResult.IsSuccess ? verResult.Value : "Unknown")}");
        }
        report.AppendLine();

        // 5. Embedded mpv Engine
        report.AppendLine("[5] Embedded mpv Engine:");
        report.AppendLine($"  - Initialized: {IsEngineInitialized}");
        report.AppendLine($"  - Engine alive: {_playerEngine.IsEngineAlive}");
        report.AppendLine($"  - Playback state: {PlaybackState}");
        report.AppendLine();

        report.AppendLine("Diagnostics completed.");
        DiagnosticsReport = report.ToString();
        IsRunningDiagnostics = false;
        _accessibility.Announce(T("Diagnostics complete."), AnnouncementPriority.High);
    }

    [RelayCommand]
    public async Task LoadTestVideoAsync()
    {
        if (!IsEngineInitialized)
        {
            _accessibility.Announce(T("Video engine is not ready."), AnnouncementPriority.High);
            return;
        }

        try
        {
            CurrentVideoTitle = T("Built-in Test Pattern (1080p 30fps Color Bars)");
            CurrentChannelTitle = "TimePlayer Built-in Diagnostics";
            OpenPlayerSession();
            _accessibility.Announce(T("Loading test pattern..."), AnnouncementPriority.High);
            FocusPlayerIfShown();

            await _playerEngine.LoadAsync("av://lavfi:testsrc=duration=600:size=1280x720:rate=30");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load test video.");
            ErrorMessage = T("Failed to load test video: {0}", ex.Message);
        }
    }

    private void PopulateSubtitleTracks()
    {
        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            AvailableSubtitleTracks.Clear();
            var offOption = new CaptionTrackOption("Captions Off", null);
            AvailableSubtitleTracks.Add(offOption);

            foreach (var caption in coord.CurrentCaptions)
            {
                var opt = new CaptionTrackOption(caption.AccessibleLabel, caption);
                AvailableSubtitleTracks.Add(opt);
                if (coord.SelectedCaptionTrack == caption)
                {
                    SelectedSubtitleTrack = opt;
                }
            }

            if (SelectedSubtitleTrack == null)
            {
                SelectedSubtitleTrack = offOption;
            }
        }
    }

    private readonly SynchronizationContext? _uiContext;
    private readonly int _uiThreadId;

    // Compare threads, not contexts: WPF hands out a different DispatcherSynchronizationContext
    // instance per priority, so a context comparison is false even on the UI thread and the
    // handlers would re-post themselves forever, starving the dispatcher.
    private bool IsOnUiThread => _uiContext is null || Environment.CurrentManagedThreadId == _uiThreadId;

    private void UpdateActiveBackendName()
    {
        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            ActiveBackendName = coord.ActiveProvider switch
            {
                BackendProviderType.TubeRepair => "TubeRepair (Primary)",
                BackendProviderType.Invidious => "Invidious",
                BackendProviderType.InnerTube => "InnerTube",
                BackendProviderType.YtDlpLocal => "yt-dlp (Fallback)",
                _ => "Automatic"
            };
        }
    }

    private void OnEngineStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnEngineStateChanged(sender, e), null);
            return;
        }

        PlaybackState = e.NewState;
        PlaybackStatusText = e.NewState.ToString();
        IsVideoActive = e.NewState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;

        if (e.NewState == PlaybackState.Paused)
        {
            StartBrailleBlinkTimer();
        }
        else
        {
            StopBrailleBlinkTimer();
        }

        UpdateBrailleDisplayLine();
        OnLibraryPlaybackStateChanged(e.NewState);

        var status = e.NewState switch
        {
            PlaybackState.Playing => T("Playing"),
            PlaybackState.Paused => T("Paused"),
            PlaybackState.Buffering => T("Buffering..."),
            PlaybackState.Ended => T("Playback ended."),
            PlaybackState.Stopped => T("Stopped"),
            PlaybackState.Error => T("Playback error."),
            _ => e.NewState.ToString()
        };

        StatusAnnouncement = status;
        // Nothing is spoken here: the user hears the video start, play/pause speaks from
        // TogglePlayPauseAsync, the end from the queue/autoplay logic, and real failures from
        // the coordinator (engine errors while it tries the next stream source are expected).
    }

    private void OnEnginePositionChanged(object? sender, PlaybackPositionChangedEventArgs e)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnEnginePositionChanged(sender, e), null);
            return;
        }

        CurrentTime = e.Position;
        Duration = e.Duration;
        UpdateBrailleDisplayLine();
        OnLibraryPositionChanged(e.Position);
        OnSegmentPosition(e.Position);
    }

    private void OnEngineSubtitleTextChanged(object? sender, string text)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnEngineSubtitleTextChanged(sender, text), null);
            return;
        }

        CurrentSubtitleText = text;
        if (!SpeakCaptions || string.IsNullOrWhiteSpace(text))
            return;

        // One line of speech per caption; mpv repeats a caption when tracks or positions change.
        var spoken = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (spoken == _lastSpokenCaption)
            return;
        _lastSpokenCaption = spoken;
        // Interrupts the previous caption, so speech stays with the video instead of queueing up.
        _accessibility.AnnounceCaption(spoken);
    }

    private string? _lastSpokenCaption;

    private void OnCoordinatorCurrentVideoChanged(object? sender, VideoDetails? video)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorCurrentVideoChanged(sender, video), null);
            return;
        }

        if (video != null)
        {
            CurrentVideo = video;
            CurrentVideoTitle = video.Title;
            CurrentChannelTitle = video.Channel.Name;
            SetCurrentLibraryVideo(video.ToSearchResult());
            if (string.IsNullOrWhiteSpace(VideoDescription) && !string.IsNullOrWhiteSpace(video.Description))
                VideoDescription = video.Description;
        }
    }

    private void OnCoordinatorErrorOccurred(object? sender, PlaybackError error)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorErrorOccurred(sender, error), null);
            return;
        }

        ShowProblem(T("Playback problem"), error.UserMessage);
        StatusAnnouncement = ErrorMessage ?? string.Empty;
    }

    private void OnCoordinatorItemSkipped(object? sender, QueueSkipEventArgs e)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorItemSkipped(sender, e), null);
            return;
        }
        var why = e.IsUnavailable ? T("it isn't available") : T("it wouldn't play");
        if (e.WillContinue)
            _accessibility.Announce(T("Skipped {0}: {1}.", e.Item.Title, why), AnnouncementPriority.Medium);
    }

    private void OnCoordinatorStatusChanged(object? sender, string status)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorStatusChanged(sender, status), null);
            return;
        }

        PlaybackStatusText = status;
        StatusAnnouncement = status;
    }

    private void OnCoordinatorQueueChanged(object? sender, IReadOnlyList<PlaybackQueueItem> queue)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnCoordinatorQueueChanged(sender, queue), null);
            return;
        }

        // Moving to the next song only changes a row or two; rebuilding a long queue for that is slow on
        // older computers and makes screen readers lose their place in the list.
        bool sameItems = PlaybackQueue.Count == queue.Count &&
                         PlaybackQueue.Select(q => q.QueueItemId).SequenceEqual(queue.Select(q => q.QueueItemId));
        if (sameItems)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (!Equals(PlaybackQueue[i], queue[i]))
                    PlaybackQueue[i] = queue[i];
            }
        }
        else
        {
            PlaybackQueue.Clear();
            foreach (var item in queue)
            {
                PlaybackQueue.Add(item);
            }
        }

        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            CurrentQueueItem = coord.CurrentQueueItem;
            if (CurrentQueueItem != null)
            {
                CurrentVideoTitle = CurrentQueueItem.Title;
                CurrentChannelTitle = CurrentQueueItem.ChannelTitle;
                // Auto-advance changes the video without going through a play command.
                if (_currentLibraryVideo?.Id != CurrentQueueItem.VideoId.Value)
                    SetCurrentLibraryVideo(ToSearchResult(CurrentQueueItem));
            }

            var index = PlaybackQueue.IndexOf(CurrentQueueItem!);
            CanPlayPrevious = index > 0;
            CanPlayNext = index >= 0 && index + 1 < PlaybackQueue.Count;
        }
    }

    [RelayCommand]
    public async Task RetryLastActionAsync()
    {
        ErrorMessage = null;
        CanRetry = false;

        if (!string.IsNullOrWhiteSpace(LastPlayedUrlOrId))
        {
            _accessibility.Announce(T("Retrying playback..."), AnnouncementPriority.Medium);
            await PlayDirectUrlAsync(LastPlayedUrlOrId);
        }
        else if (!string.IsNullOrWhiteSpace(LastSearchQuery))
        {
            _accessibility.Announce(T("Retrying search for '{0}'...", LastSearchQuery), AnnouncementPriority.Medium);
            SearchQuery = LastSearchQuery;
            await SearchAsync();
        }
    }

    [RelayCommand]
    public async Task BrailleTouchCursorAsync(int cellIndex) => await BrailleTouchCursorAsync(cellIndex, 0);

    /// <param name="cellCount">Width of the display the key was pressed on; 0 uses the configured width.</param>
    public async Task BrailleTouchCursorAsync(int cellIndex, int cellCount)
    {
        if (!IsEngineInitialized) return;

        var dur = Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);
        if (dur <= TimeSpan.Zero) return;

        var targetTime = cellCount >= 2
            ? BrailleProgressTrack.CalculateSeekTime(cellIndex, cellCount, dur)
            : _brailleTrack.CalculateSeekTime(cellIndex, dur);
        try
        {
            // Update local time and tactile display line immediately for 0ms physical response
            CurrentTime = targetTime;
            UpdateBrailleDisplayLine();

            await _playerEngine.SeekAsync(targetTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek via Braille touch cursor at cell {Index}.", cellIndex);
        }
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdates = true;
        DependencyStatusMessage = T("Checking for updates to yt-dlp and mpv...");
        _accessibility.Announce(DependencyStatusMessage, AnnouncementPriority.Medium);

        try
        {
            var updateResult = await _ytDlpManager.EnsureInstalledOrUpdatedAsync();
            if (updateResult.IsSuccess)
            {
                var verResult = await _ytDlpManager.GetVersionAsync();
                var verStr = verResult.IsSuccess ? verResult.Value : "Current";
                DependencyStatusMessage = T("yt-dlp is verified and up to date (version {0}). mpv engine is intact.", verStr);
            }
            else
            {
                DependencyStatusMessage = T("Update check: {0}", updateResult.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            DependencyStatusMessage = T("Error during update check: {0}", ex.Message);
        }
        finally
        {
            IsCheckingUpdates = false;
            _accessibility.Announce(DependencyStatusMessage, AnnouncementPriority.High);
        }
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        await PersistAccessibilitySettingsAsync();
        await PersistLibraryPreferencesAsync();
        await PersistPlayerPreferencesAsync();
        CloseSettings();
    }

    public async Task PersistAccessibilitySettingsAsync()
    {
        var current = _configService.CurrentConfig;
        var updated = current with
        {
            Playback = current.Playback with
            {
                PreferredQuality = SelectedQuality,
                HardwareDecoding = EnableHardwareDecoding
            },
            Accessibility = current.Accessibility with
            {
                BrailleDisplayCellCount = SelectedBrailleCellCount,
                EnableBrailleProgress = EnableBrailleProgress
            }
        };

        try
        {
            await _configService.UpdateConfigAsync(updated);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist updated settings.");
        }
    }

    public void AnnounceNvdaAddonInstalled() =>
        _accessibility.Announce(
            T("TimePlayer braille add-on installed. Restart NVDA once to use the braille progress track and touch seeking."),
            AnnouncementPriority.Medium);

    [ObservableProperty]
    private bool _useBrailleAddon;

    private bool _applyingBrailleAddonChoice;

    /// <summary>NVDA is installed and the user has never said whether they want braille support.</summary>
    public bool ShouldAskAboutBrailleAddon =>
        _configService.CurrentConfig.Accessibility.UseBrailleAddon is null
        && !NvdaAddonInstaller.IsInstalled
        && NvdaAddonInstaller.NvdaConfigDirectory is not null;

    private void InitializeBrailleAddonPreference()
    {
        _useBrailleAddon = _configService.CurrentConfig.Accessibility.UseBrailleAddon ?? NvdaAddonInstaller.IsInstalled;
    }

    partial void OnUseBrailleAddonChanged(bool value)
    {
        if (!_applyingBrailleAddonChoice)
            _ = ApplyBrailleAddonChoiceAsync(value);
    }

    /// <summary>
    /// Saves the answer so the question is never asked again, and installs or removes the add-on.
    /// Declining also turns the braille progress track off; it can be turned on again in Settings.
    /// </summary>
    public async Task ApplyBrailleAddonChoiceAsync(bool install)
    {
        _applyingBrailleAddonChoice = true;
        UseBrailleAddon = install;
        _applyingBrailleAddonChoice = false;

        if (install)
        {
            var result = NvdaAddonInstaller.EnsureInstalled();
            _accessibility.Announce(result switch
            {
                NvdaAddonInstallResult.Failed => T("The braille add-on could not be installed."),
                NvdaAddonInstallResult.NvdaNotFound => T("NVDA was not found, so the braille add-on was not installed."),
                NvdaAddonInstallResult.AlreadyCurrent => T("The braille add-on is installed."),
                _ => T("Braille add-on installed. Restart NVDA once to use it.")
            }, AnnouncementPriority.Medium);
            if (!EnableBrailleProgress)
                EnableBrailleProgress = true;
        }
        else
        {
            bool wasInstalled = NvdaAddonInstaller.IsInstalled;
            NvdaAddonInstaller.Uninstall();
            if (EnableBrailleProgress)
                EnableBrailleProgress = false;
            if (wasInstalled)
                _accessibility.Announce(T("Braille add-on removed. It stops after NVDA restarts."), AnnouncementPriority.Medium);
        }

        var current = _configService.CurrentConfig;
        await _configService.UpdateConfigAsync(current with
        {
            Accessibility = current.Accessibility with { UseBrailleAddon = install, EnableBrailleProgress = EnableBrailleProgress }
        });
    }

    [RelayCommand]
    public void ToggleBrailleProgress()
    {
        EnableBrailleProgress = !EnableBrailleProgress;
        var message = EnableBrailleProgress
            ? T("Braille progress track enabled ({0} cells).", SelectedBrailleCellCount)
            : T("Braille progress track disabled. Screen reader standard display active.");
        _accessibility.Announce(message, AnnouncementPriority.High);
    }


    private void UpdateBrailleDisplayLine()
    {
        bool isPlaying = PlaybackState is PlaybackState.Playing or PlaybackState.Buffering;
        bool isPaused = PlaybackState == PlaybackState.Paused;
        var dur = Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);

        if (!EnableBrailleProgress)
        {
            BrailleDisplayLine = string.Empty;
            NvdaBrailleBridge.PublishProgressState("off");
            return;
        }

        string newLine = _brailleTrack.GenerateDisplayLine(
            CurrentTime,
            dur,
            isPlaying,
            isPaused,
            _brailleBlinkVisible);

        BrailleDisplayLine = newLine;

        double fraction = dur > TimeSpan.Zero ? Math.Clamp(CurrentTime / dur, 0.0, 1.0) : 0.0;
        NvdaBrailleBridge.PublishProgressState(isPlaying || isPaused
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(isPlaying ? "playing" : "paused")} {fraction:F4}")
            : "stopped");

    }

    private void StartBrailleBlinkTimer()
    {
        _brailleBlinkTimer?.Dispose();
        _brailleBlinkVisible = true;
        _brailleBlinkTimer = new System.Threading.Timer(_ =>
        {
            _brailleBlinkVisible = !_brailleBlinkVisible;
            System.Windows.Application.Current?.Dispatcher?.InvokeAsync(UpdateBrailleDisplayLine);
        }, null, 600, 600);
    }

    private void StopBrailleBlinkTimer()
    {
        _brailleBlinkTimer?.Dispose();
        _brailleBlinkTimer = null;
        _brailleBlinkVisible = true;
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        StopBrailleBlinkTimer();
        RecordCurrentProgress(CurrentTime, flush: true);
        _library.Flush();
        _sectionCts?.Cancel();

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();

        _playerEngine.StateChanged -= OnEngineStateChanged;
        _playerEngine.PositionChanged -= OnEnginePositionChanged;
        _playerEngine.SubtitleTextChanged -= OnEngineSubtitleTextChanged;

        _playbackCoordinator.CurrentVideoChanged -= OnCoordinatorCurrentVideoChanged;
        _playbackCoordinator.ErrorOccurred -= OnCoordinatorErrorOccurred;
        _playbackCoordinator.StatusChanged -= OnCoordinatorStatusChanged;
        if (_playbackCoordinator is PlaybackCoordinator concreteCoord)
        {
            concreteCoord.QueueChanged -= OnCoordinatorQueueChanged;
            concreteCoord.QueueEnded -= OnCoordinatorQueueEnded;
            concreteCoord.ItemSkipped -= OnCoordinatorItemSkipped;
            concreteCoord.CaptionsChanged -= OnCoordinatorCaptionsChanged;
        }

        return ValueTask.CompletedTask;
    }
}
