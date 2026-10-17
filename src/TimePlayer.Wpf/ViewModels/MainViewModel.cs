using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Main window view model handling search, playlist, player state, and screen reader announcements.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IPlayerEngine _playerEngine;
    private readonly IYtDlpManager _ytDlpManager;
    private readonly IAccessibilityService _accessibility;
    private readonly IConfigurationService _configService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private ObservableCollection<SearchResult> _searchResults = new();

    [ObservableProperty]
    private SearchResult? _selectedSearchResult;

    [ObservableProperty]
    private VideoMetadata? _currentVideo;

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
    private string _statusAnnouncement = "TimePlayer ready.";

    public MainViewModel(
        IPlayerEngine playerEngine,
        IYtDlpManager ytDlpManager,
        IAccessibilityService accessibility,
        IConfigurationService configService,
        ILogger<MainViewModel> logger)
    {
        _playerEngine = playerEngine ?? throw new ArgumentNullException(nameof(playerEngine));
        _ytDlpManager = ytDlpManager ?? throw new ArgumentNullException(nameof(ytDlpManager));
        _accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var config = _configService.CurrentConfig;
        _volumePercent = config.Playback.DefaultVolumePercent;

        _playerEngine.StateChanged += OnEngineStateChanged;
        _playerEngine.PositionChanged += OnEnginePositionChanged;
        _playerEngine.ErrorOccurred += OnEngineErrorOccurred;
    }

    private int _isInitializing;

    public async Task InitializePlayerEngineAsync(nint windowHandle)
    {
        if (IsEngineInitialized || Interlocked.CompareExchange(ref _isInitializing, 1, 0) != 0)
            return;

        try
        {
            _logger.LogInformation("Initializing player engine with HWND {Hwnd}...", windowHandle);
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
    private async Task SearchAsync()
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
            _accessibility.Announce($"Direct YouTube video recognized: {ytUrl.VideoId}. Loading...", AnnouncementPriority.High);
            await LoadDirectUrlAsync(ytUrl.NormalizedWatchUrl);
            return;
        }

        IsSearching = true;
        ErrorMessage = null;
        _accessibility.Announce($"Searching for {query}...", AnnouncementPriority.Medium);

        try
        {
            await Task.Delay(50); // fast async boundary
            var count = SearchResults.Count;
            _accessibility.Announce(AccessibleTextFormatter.FormatSearchResultsSpoken(count, query), AnnouncementPriority.Medium);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing search for query '{Query}'", query);
            ErrorMessage = "Failed to search YouTube. Please try again or paste a direct video URL.";
            _accessibility.Announce(ErrorMessage, AnnouncementPriority.High);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        _accessibility.Announce("Search cleared.", AnnouncementPriority.Low);
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync()
    {
        if (!IsEngineInitialized) return;

        try
        {
            if (PlaybackState == PlaybackState.Playing)
            {
                await _playerEngine.PauseAsync();
            }
            else
            {
                await _playerEngine.PlayAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle play/pause.");
        }
    }

    [RelayCommand]
    private async Task SeekSmallForwardAsync()
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
    private async Task SeekSmallBackwardAsync()
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
    private async Task VolumeUpAsync()
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
    private async Task VolumeDownAsync()
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
    private async Task SpeedUpAsync()
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
    private async Task SpeedDownAsync()
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
    private async Task ResetSpeedAsync()
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
    public async Task LoadTestVideoAsync()
    {
        if (!IsEngineInitialized)
        {
            _accessibility.Announce("Video engine is not ready.", AnnouncementPriority.High);
            return;
        }

        try
        {
            _accessibility.Announce("Loading test video pattern into player...", AnnouncementPriority.High);
            // Self-contained, built-in test pattern with color bars and timer
            await _playerEngine.LoadAsync("av://lavfi:testsrc=duration=600:size=1280x720:rate=30");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load test video.");
            ErrorMessage = $"Failed to load test video: {ex.Message}";
        }
    }

    public async Task LoadDirectUrlAsync(string url)
    {
        if (!IsEngineInitialized || string.IsNullOrWhiteSpace(url)) return;

        try
        {
            _accessibility.Announce("Loading media URL in player...", AnnouncementPriority.Medium);
            await _playerEngine.LoadAsync(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load URL {Url}", url);
            ErrorMessage = $"Failed to load media: {ex.Message}";
        }
    }

    private void OnEngineStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        PlaybackState = e.NewState;
        PlaybackStatusText = e.NewState.ToString();
        IsVideoActive = e.NewState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering;

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

        _accessibility.Announce(status, AnnouncementPriority.Medium);
    }

    private void OnEnginePositionChanged(object? sender, PlaybackPositionChangedEventArgs e)
    {
        CurrentTime = e.Position;
        Duration = e.Duration;
    }

    private void OnEngineErrorOccurred(object? sender, PlaybackErrorEventArgs e)
    {
        ErrorMessage = e.Message;
        PlaybackStatusText = "Error";
        _accessibility.Announce($"Player error: {e.Message}", AnnouncementPriority.High);
    }
}
