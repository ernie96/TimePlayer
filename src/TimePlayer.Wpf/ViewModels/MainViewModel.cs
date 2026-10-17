using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Main window view model handling search, playlist, player state, and screen reader announcements.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
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
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusAnnouncement = "TimePlayer ready.";

    public MainViewModel(
        IAccessibilityService accessibility,
        IConfigurationService configService,
        ILogger<MainViewModel> logger)
    {
        _accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var config = _configService.CurrentConfig;
        _volumePercent = config.Playback.DefaultVolumePercent;
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

        IsSearching = true;
        ErrorMessage = null;
        _accessibility.Announce($"Searching for {query}...", AnnouncementPriority.Medium);

        try
        {
            // Placeholder for network search (implemented in backend step)
            await Task.Delay(50); // Simulates fast async boundary
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
    private void TogglePlayPause()
    {
        if (PlaybackState == PlaybackState.Playing)
        {
            PlaybackState = PlaybackState.Paused;
            PlaybackStatusText = "Paused";
            _accessibility.Announce("Paused", AnnouncementPriority.Medium);
        }
        else
        {
            PlaybackState = PlaybackState.Playing;
            PlaybackStatusText = "Playing";
            _accessibility.Announce("Playing", AnnouncementPriority.Medium);
        }
    }

    [RelayCommand]
    private void SeekSmallForward()
    {
        var step = _configService.CurrentConfig.Playback.SmallSeekSeconds;
        CurrentTime = CurrentTime.Add(TimeSpan.FromSeconds(step));
        if (Duration > TimeSpan.Zero && CurrentTime > Duration)
            CurrentTime = Duration;

        _accessibility.Announce(AccessibleTextFormatter.FormatRelativeSeekSpoken(step, CurrentTime), AnnouncementPriority.Low);
    }

    [RelayCommand]
    private void SeekSmallBackward()
    {
        var step = _configService.CurrentConfig.Playback.SmallSeekSeconds;
        CurrentTime = CurrentTime.Subtract(TimeSpan.FromSeconds(step));
        if (CurrentTime < TimeSpan.Zero)
            CurrentTime = TimeSpan.Zero;

        _accessibility.Announce(AccessibleTextFormatter.FormatRelativeSeekSpoken(-step, CurrentTime), AnnouncementPriority.Low);
    }

    [RelayCommand]
    private void VolumeUp()
    {
        var step = _configService.CurrentConfig.Playback.VolumeStepPercent;
        VolumePercent = Math.Clamp(VolumePercent + step, 0.0, 100.0);
        _accessibility.Announce(AccessibleTextFormatter.FormatVolumeSpoken(VolumePercent), AnnouncementPriority.Low);
    }

    [RelayCommand]
    private void VolumeDown()
    {
        var step = _configService.CurrentConfig.Playback.VolumeStepPercent;
        VolumePercent = Math.Clamp(VolumePercent - step, 0.0, 100.0);
        _accessibility.Announce(AccessibleTextFormatter.FormatVolumeSpoken(VolumePercent), AnnouncementPriority.Low);
    }

    [RelayCommand]
    private void SpeedUp()
    {
        var config = _configService.CurrentConfig.Playback;
        PlaybackSpeed = Math.Clamp(PlaybackSpeed + config.SpeedStep, config.MinSpeed, config.MaxSpeed);
        _accessibility.Announce(AccessibleTextFormatter.FormatSpeedSpoken(PlaybackSpeed), AnnouncementPriority.Low);
    }

    [RelayCommand]
    private void SpeedDown()
    {
        var config = _configService.CurrentConfig.Playback;
        PlaybackSpeed = Math.Clamp(PlaybackSpeed - config.SpeedStep, config.MinSpeed, config.MaxSpeed);
        _accessibility.Announce(AccessibleTextFormatter.FormatSpeedSpoken(PlaybackSpeed), AnnouncementPriority.Low);
    }

    [RelayCommand]
    private void ResetSpeed()
    {
        PlaybackSpeed = 1.0;
        _accessibility.Announce(AccessibleTextFormatter.FormatSpeedSpoken(PlaybackSpeed), AnnouncementPriority.Low);
    }
}
