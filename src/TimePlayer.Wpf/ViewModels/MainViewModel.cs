using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

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
    private string _currentVideoTitle = "No video loaded";

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
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusAnnouncement = "TimePlayer ready. Focus is in the search box.";

    [ObservableProperty]
    private string _activeBackendName = "Piped (Primary)";

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

    public IReadOnlyList<QualityPreference> AvailableQualities { get; } = Enum.GetValues<QualityPreference>();

    // Braille Display Progress Indicator
    [ObservableProperty]
    private string _brailleDisplayLine = string.Empty;

    [ObservableProperty]
    private int _selectedBrailleCellCount = 40;

    [ObservableProperty]
    private ObservableCollection<int> _availableBrailleCellCounts = new() { 14, 18, 20, 32, 40, 80 };

    [ObservableProperty]
    private bool _enableBrailleProgress = true;

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
    private string _dependencyStatusMessage = "All media components verified.";

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
        ILogger<MainViewModel> logger)
    {
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
        _enableBrailleProgress = config.Accessibility.EnableBrailleProgress;
        _enableHardwareDecoding = config.Playback.HardwareDecoding;
        _brailleTrack = new BrailleProgressTrack(_selectedBrailleCellCount);
        _brailleDisplayLine = _brailleTrack.GenerateDisplayLine(TimeSpan.Zero, TimeSpan.Zero, false, false);

        // Player engine events
        _playerEngine.StateChanged += OnEngineStateChanged;
        _playerEngine.PositionChanged += OnEnginePositionChanged;
        _playerEngine.ErrorOccurred += OnEngineErrorOccurred;
        _playerEngine.SubtitleTextChanged += OnEngineSubtitleTextChanged;

        // Coordinator events
        _playbackCoordinator.CurrentVideoChanged += OnCoordinatorCurrentVideoChanged;
        _playbackCoordinator.ErrorOccurred += OnCoordinatorErrorOccurred;
        _playbackCoordinator.StatusChanged += OnCoordinatorStatusChanged;
        if (_playbackCoordinator is PlaybackCoordinator concreteCoord)
        {
            concreteCoord.QueueChanged += OnCoordinatorQueueChanged;
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
            PlaybackStatusText = "Engine initialized";
            _accessibility.Announce("Playback engine initialized and ready.", AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize playback engine.");
            ErrorMessage = $"Player initialization failed: {ex.Message}";
            PlaybackStatusText = "Engine error";
            _accessibility.Announce("Failed to initialize video engine. Please check dependencies.", AnnouncementPriority.High);
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        var query = SearchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            _accessibility.Announce("Please enter a search query or YouTube link.", AnnouncementPriority.High);
            return;
        }

        // Direct YouTube URL playback check
        if (YouTubeUrl.TryParse(query, out var ytUrl))
        {
            _accessibility.Announce($"Direct YouTube video recognized: {ytUrl.VideoId}. Opening player dialog...", AnnouncementPriority.High);
            await PlayDirectUrlAsync(ytUrl.NormalizedWatchUrl);
            return;
        }

        // Cancel previous in-flight search if any
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var cancellationToken = _searchCts.Token;

        IsSearching = true;
        ErrorMessage = null;
        LastSearchQuery = query;
        CanRetry = false;
        _accessibility.Announce($"Searching YouTube for {query}...", AnnouncementPriority.Medium);

        try
        {
            var searchResult = await _playbackCoordinator.SearchAsync(query, cancellationToken);
            if (searchResult.IsSuccess)
            {
                SearchResults.Clear();
                var seenIds = new HashSet<string>();
                foreach (var item in searchResult.Value.Items)
                {
                    if (seenIds.Add(item.Id))
                    {
                        SearchResults.Add(item);
                    }
                }

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
                ErrorMessage = $"Search failed: {searchResult.Error?.Message}";
                CanRetry = true;
                _accessibility.Announce(ErrorMessage, AnnouncementPriority.High);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Search cancelled by user.");
            _accessibility.Announce("Search cancelled.", AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing search for query '{Query}'", query);
            ErrorMessage = "Failed to search YouTube. Please check network or try a direct link.";
            CanRetry = true;
            _accessibility.Announce(ErrorMessage, AnnouncementPriority.High);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public void CancelSearch()
    {
        if (IsSearching)
        {
            _searchCts?.Cancel();
            IsSearching = false;
            _accessibility.Announce("Search cancelled.", AnnouncementPriority.Medium);
        }
    }

    [RelayCommand]
    public void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        _accessibility.Announce("Search cleared.", AnnouncementPriority.Low);
        RequestFocusSearch?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ClearError()
    {
        ErrorMessage = null;
        CanRetry = false;
        _accessibility.Announce("Error dismissed.", AnnouncementPriority.Low);
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
            _accessibility.Announce("Clipboard is empty or does not contain text.", AnnouncementPriority.High);
            return;
        }

        if (YouTubeUrl.TryParse(text, out var ytUrl))
        {
            SearchQuery = ytUrl.NormalizedWatchUrl;
            _accessibility.Announce($"Pasted YouTube link. Opening video {ytUrl.VideoId}...", AnnouncementPriority.High);
            await PlayDirectUrlAsync(ytUrl.NormalizedWatchUrl);
        }
        else
        {
            SearchQuery = text;
            _accessibility.Announce($"Pasted text '{text}' into search box. Press Enter to search.", AnnouncementPriority.Medium);
            RequestFocusSearch?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    public async Task PlaySearchResultAsync(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item == null) return;

        // Cancel previous in-flight playback loading if any
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();
        _playbackCts = new CancellationTokenSource();
        var token = _playbackCts.Token;

        CurrentVideoTitle = item.Title;
        CurrentChannelTitle = item.ChannelTitle;
        IsPlayerDialogOpen = true;
        LastPlayedUrlOrId = item.Id;
        CanRetry = false;
        ErrorMessage = null;
        _accessibility.Announce($"Opening video player for: {item.Title}. Press Escape at any time to return to search results.", AnnouncementPriority.High);
        RequestFocusPlayer?.Invoke(this, EventArgs.Empty);

        // Add to queue if not already present
        if (!PlaybackQueue.Any(q => q.VideoId.Value == item.Id))
        {
            await _playbackCoordinator.EnqueueAsync(item);
        }

        try
        {
            var result = await _playbackCoordinator.PlayUrlOrIdAsync(item.Id, token);
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error?.Message;
                CanRetry = true;
                _accessibility.Announce($"Could not play video: {ErrorMessage}", AnnouncementPriority.High);
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

        CurrentVideoTitle = "Loading YouTube video...";
        CurrentChannelTitle = string.Empty;
        IsPlayerDialogOpen = true;
        LastPlayedUrlOrId = url;
        CanRetry = false;
        ErrorMessage = null;
        _accessibility.Announce("Opening video player dialog. Press Escape at any time to exit.", AnnouncementPriority.High);
        RequestFocusPlayer?.Invoke(this, EventArgs.Empty);

        try
        {
            var result = await _playbackCoordinator.PlayUrlOrIdAsync(url, token);
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error?.Message;
                CanRetry = true;
                _accessibility.Announce($"Could not load video: {ErrorMessage}", AnnouncementPriority.High);
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
    public void ClosePlayerDialog()
    {
        if (!IsPlayerDialogOpen) return;

        IsPlayerDialogOpen = false;
        _accessibility.Announce("Closed video player. Focus restored to search results.", AnnouncementPriority.Medium);
        RequestFocusResults?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task EnqueueSearchResultAsync(SearchResult? item)
    {
        item ??= SelectedSearchResult;
        if (item == null) return;

        await _playbackCoordinator.EnqueueAsync(item);
        _accessibility.Announce($"Added '{item.Title}' to playback queue.", AnnouncementPriority.Low);
    }

    [RelayCommand]
    public async Task PlayQueueItemAsync(PlaybackQueueItem? item)
    {
        item ??= SelectedQueueItem;
        if (item == null) return;

        CurrentVideoTitle = item.Title;
        CurrentChannelTitle = item.ChannelTitle;
        IsPlayerDialogOpen = true;
        _accessibility.Announce($"Playing queued video: {item.Title}.", AnnouncementPriority.High);
        RequestFocusPlayer?.Invoke(this, EventArgs.Empty);

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
            _accessibility.Announce($"Removed '{item.Title}' from queue.", AnnouncementPriority.Low);
        }
    }

    [RelayCommand]
    public async Task ClearQueueAsync()
    {
        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            await coord.ClearQueueAsync();
            _accessibility.Announce("Playback queue cleared.", AnnouncementPriority.Low);
        }
    }

    [RelayCommand]
    public async Task PlayNextAsync()
    {
        if (!CanPlayNext) return;

        _accessibility.Announce("Playing next item in queue...", AnnouncementPriority.Medium);
        await _playbackCoordinator.PlayNextAsync();
        PopulateSubtitleTracks();
    }

    [RelayCommand]
    public async Task PlayPreviousAsync()
    {
        if (!CanPlayPrevious) return;

        _accessibility.Announce("Playing previous item in queue...", AnnouncementPriority.Medium);
        await _playbackCoordinator.PlayPreviousAsync();
        PopulateSubtitleTracks();
    }

    [RelayCommand]
    public async Task TogglePlayPauseAsync()
    {
        if (!IsEngineInitialized) return;

        try
        {
            await _playbackCoordinator.TogglePlayPauseAsync();
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
            _accessibility.Announce("Playback stopped.", AnnouncementPriority.Low);
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
            await _playerEngine.SeekRelativeAsync(step);
            _accessibility.Announce(AccessibleTextFormatter.FormatRelativeSeekSpoken(step, CurrentTime), AnnouncementPriority.Low);
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
            await _playerEngine.SeekRelativeAsync(-step);
            _accessibility.Announce(AccessibleTextFormatter.FormatRelativeSeekSpoken(-step, CurrentTime), AnnouncementPriority.Low);
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
            await _playerEngine.SeekRelativeAsync(step);
            _accessibility.Announce(AccessibleTextFormatter.FormatRelativeSeekSpoken(step, CurrentTime), AnnouncementPriority.Low);
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
            await _playerEngine.SeekRelativeAsync(-step);
            _accessibility.Announce(AccessibleTextFormatter.FormatRelativeSeekSpoken(-step, CurrentTime), AnnouncementPriority.Low);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seek large backward.");
        }
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
            _accessibility.Announce(IsMuted ? "Audio muted." : "Audio unmuted.", AnnouncementPriority.Low);
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
            var label = option.Track != null ? $"Subtitles switched to {option.DisplayName}" : "Subtitles turned off";
            _accessibility.Announce(label, AnnouncementPriority.Low);
        }
    }

    [RelayCommand]
    public void OpenSettings()
    {
        IsSettingsDialogOpen = true;
        _accessibility.Announce("Settings and Diagnostics opened. Press Escape to close.", AnnouncementPriority.Medium);
    }

    [RelayCommand]
    public void CloseSettings()
    {
        IsSettingsDialogOpen = false;
        _accessibility.Announce("Settings and Diagnostics closed.", AnnouncementPriority.Low);
        if (IsPlayerDialogOpen)
        {
            RequestFocusPlayer?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            RequestFocusSearch?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    public async Task RunDiagnosticsAsync()
    {
        IsRunningDiagnostics = true;
        _accessibility.Announce("Running diagnostics check on backend instances and dependencies...", AnnouncementPriority.Medium);

        var report = new StringBuilder();
        report.AppendLine("=== TimePlayer Diagnostics Report ===");
        report.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        // 1. Piped Instances Health
        report.AppendLine("[1] Piped Backend Status:");
        try
        {
            var pipedInstances = await _instanceDirectory.GetInstancesAsync(BackendProviderType.Piped);
            report.AppendLine($"  - Discovered instances: {pipedInstances.Count}");
            var bestPiped = await _healthService.SelectBestInstanceAsync(BackendProviderType.Piped);
            if (bestPiped != null)
            {
                report.AppendLine($"  - Active / Best instance: {bestPiped.BaseUri}");
            }
            else
            {
                report.AppendLine("  - Active instance: None reachable (will fallback)");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  - Piped check error: {ex.Message}");
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

        // 3. Local yt-dlp Status
        report.AppendLine("[3] Local yt-dlp Executable:");
        report.AppendLine($"  - Path: {_ytDlpManager.ExecutablePath}");
        report.AppendLine($"  - Installed: {_ytDlpManager.IsInstalled}");
        if (_ytDlpManager.IsInstalled)
        {
            var verResult = await _ytDlpManager.GetVersionAsync();
            report.AppendLine($"  - Version: {(verResult.IsSuccess ? verResult.Value : "Unknown")}");
        }
        report.AppendLine();

        // 4. mpv Engine Status
        report.AppendLine("[4] Embedded mpv Engine:");
        report.AppendLine($"  - Initialized: {IsEngineInitialized}");
        report.AppendLine($"  - Engine alive: {_playerEngine.IsEngineAlive}");
        report.AppendLine($"  - Playback state: {PlaybackState}");
        report.AppendLine();

        report.AppendLine("Diagnostics completed.");
        DiagnosticsReport = report.ToString();
        IsRunningDiagnostics = false;
        _accessibility.Announce("Diagnostics check complete. Review results in the diagnostics text box.", AnnouncementPriority.High);
    }

    [RelayCommand]
    public async Task LoadTestVideoAsync()
    {
        if (!IsEngineInitialized)
        {
            _accessibility.Announce("Video engine is not ready.", AnnouncementPriority.High);
            return;
        }

        try
        {
            CurrentVideoTitle = "Built-in Test Pattern (1080p 30fps Color Bars)";
            CurrentChannelTitle = "TimePlayer Built-in Diagnostics";
            IsPlayerDialogOpen = true;
            _accessibility.Announce("Loading test pattern into player dialog...", AnnouncementPriority.High);
            RequestFocusPlayer?.Invoke(this, EventArgs.Empty);

            await _playerEngine.LoadAsync("av://lavfi:testsrc=duration=600:size=1280x720:rate=30");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load test video.");
            ErrorMessage = $"Failed to load test video: {ex.Message}";
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

    private void UpdateActiveBackendName()
    {
        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            ActiveBackendName = coord.ActiveProvider switch
            {
                BackendProviderType.Piped => "Piped (Primary API)",
                BackendProviderType.Invidious => "Invidious (Fallback API)",
                BackendProviderType.YtDlpLocal => "yt-dlp (Local Fallback)",
                _ => "Automatic"
            };
        }
    }

    private void OnEngineStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
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

        var status = e.NewState switch
        {
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Buffering => "Buffering...",
            PlaybackState.Ended => "Playback ended.",
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Error => "Playback error.",
            _ => e.NewState.ToString()
        };

        StatusAnnouncement = status;
        _accessibility.Announce(status, AnnouncementPriority.Medium);
    }

    private void OnEnginePositionChanged(object? sender, PlaybackPositionChangedEventArgs e)
    {
        CurrentTime = e.Position;
        Duration = e.Duration;
        UpdateBrailleDisplayLine();
    }

    private void OnEngineErrorOccurred(object? sender, PlaybackErrorEventArgs e)
    {
        ErrorMessage = e.Message;
        PlaybackStatusText = "Error";
        CanRetry = true;
        StatusAnnouncement = $"Player error: {e.Message}";
        _accessibility.Announce(StatusAnnouncement, AnnouncementPriority.High);
    }

    private void OnEngineSubtitleTextChanged(object? sender, string text)
    {
        CurrentSubtitleText = text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Update live region announcement for screen reader users
            _accessibility.Announce(text, AnnouncementPriority.Low);
        }
    }

    private void OnCoordinatorCurrentVideoChanged(object? sender, VideoDetails? video)
    {
        if (video != null)
        {
            CurrentVideo = video;
            CurrentVideoTitle = video.Title;
            CurrentChannelTitle = video.Channel.Name;
        }
    }

    private void OnCoordinatorErrorOccurred(object? sender, PlaybackError error)
    {
        ErrorMessage = error.UserMessage;
        CanRetry = true;
        StatusAnnouncement = error.UserMessage;
        _accessibility.Announce(error.UserMessage, AnnouncementPriority.High);
    }

    private void OnCoordinatorStatusChanged(object? sender, string status)
    {
        PlaybackStatusText = status;
        StatusAnnouncement = status;
        _accessibility.Announce(status, AnnouncementPriority.Low);
    }

    private void OnCoordinatorQueueChanged(object? sender, IReadOnlyList<PlaybackQueueItem> queue)
    {
        PlaybackQueue.Clear();
        foreach (var item in queue)
        {
            PlaybackQueue.Add(item);
        }

        if (_playbackCoordinator is PlaybackCoordinator coord)
        {
            CurrentQueueItem = coord.CurrentQueueItem;
            if (CurrentQueueItem != null)
            {
                CurrentVideoTitle = CurrentQueueItem.Title;
                CurrentChannelTitle = CurrentQueueItem.ChannelTitle;
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
            _accessibility.Announce("Retrying playback...", AnnouncementPriority.Medium);
            await PlayDirectUrlAsync(LastPlayedUrlOrId);
        }
        else if (!string.IsNullOrWhiteSpace(LastSearchQuery))
        {
            _accessibility.Announce($"Retrying search for '{LastSearchQuery}'...", AnnouncementPriority.Medium);
            SearchQuery = LastSearchQuery;
            await SearchAsync();
        }
    }

    [RelayCommand]
    public async Task BrailleTouchCursorAsync(int cellIndex)
    {
        if (!IsEngineInitialized || Duration <= TimeSpan.Zero) return;

        var targetTime = _brailleTrack.CalculateSeekTime(cellIndex, Duration);
        try
        {
            await _playerEngine.SeekAsync(targetTime);
            CurrentTime = targetTime;
            UpdateBrailleDisplayLine();
            _accessibility.Announce($"Braille seek to cell {cellIndex + 1} of {_brailleTrack.CellCount}, time {AccessibleTextFormatter.FormatTimeSpoken(targetTime)}", AnnouncementPriority.Low);
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
        DependencyStatusMessage = "Checking for updates to yt-dlp and mpv...";
        _accessibility.Announce(DependencyStatusMessage, AnnouncementPriority.Medium);

        try
        {
            var updateResult = await _ytDlpManager.EnsureInstalledOrUpdatedAsync();
            if (updateResult.IsSuccess)
            {
                var verResult = await _ytDlpManager.GetVersionAsync();
                var verStr = verResult.IsSuccess ? verResult.Value : "Current";
                DependencyStatusMessage = $"yt-dlp is verified and up to date (version {verStr}). mpv engine is intact.";
            }
            else
            {
                DependencyStatusMessage = $"Update check: {updateResult.Error?.Message}";
            }
        }
        catch (Exception ex)
        {
            DependencyStatusMessage = $"Error during update check: {ex.Message}";
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
            _accessibility.Announce("Settings saved successfully.", AnnouncementPriority.Medium);
            CloseSettings();
        }
        catch (Exception ex)
        {
            _accessibility.Announce($"Could not save settings: {ex.Message}", AnnouncementPriority.High);
        }
    }

    partial void OnSelectedBrailleCellCountChanged(int value)
    {
        if (value >= 2)
        {
            _brailleTrack = new BrailleProgressTrack(value);
            UpdateBrailleDisplayLine();
        }
    }

    partial void OnEnableBrailleProgressChanged(bool value)
    {
        if (!value)
        {
            StopBrailleBlinkTimer();
            BrailleDisplayLine = string.Empty;
        }
        else
        {
            UpdateBrailleDisplayLine();
        }
    }

    private void UpdateBrailleDisplayLine()
    {
        if (!EnableBrailleProgress) return;

        bool isPlaying = PlaybackState is PlaybackState.Playing or PlaybackState.Buffering;
        bool isPaused = PlaybackState == PlaybackState.Paused;

        BrailleDisplayLine = _brailleTrack.GenerateDisplayLine(
            CurrentTime,
            Duration,
            isPlaying,
            isPaused,
            _brailleBlinkVisible);

        if (isPlaying || isPaused)
        {
            NvdaBrailleBridge.SendBrailleMessage(BrailleDisplayLine);
        }
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

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _playbackCts?.Cancel();
        _playbackCts?.Dispose();

        _playerEngine.StateChanged -= OnEngineStateChanged;
        _playerEngine.PositionChanged -= OnEnginePositionChanged;
        _playerEngine.ErrorOccurred -= OnEngineErrorOccurred;
        _playerEngine.SubtitleTextChanged -= OnEngineSubtitleTextChanged;

        _playbackCoordinator.CurrentVideoChanged -= OnCoordinatorCurrentVideoChanged;
        _playbackCoordinator.ErrorOccurred -= OnCoordinatorErrorOccurred;
        _playbackCoordinator.StatusChanged -= OnCoordinatorStatusChanged;
        if (_playbackCoordinator is PlaybackCoordinator concreteCoord)
        {
            concreteCoord.QueueChanged -= OnCoordinatorQueueChanged;
        }

        return ValueTask.CompletedTask;
    }
}
