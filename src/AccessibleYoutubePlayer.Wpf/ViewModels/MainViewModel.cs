using System.Collections.ObjectModel;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccessibleYoutubePlayer.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IYouTubeService _youtubeService;
    private readonly IAccessibilityService _accessibilityService;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _screenReaderAnnouncement = string.Empty;

    [ObservableProperty]
    private ProviderType _currentProvider = ProviderType.Piped;

    [ObservableProperty]
    private VideoMetadata? _selectedVideo;

    [ObservableProperty]
    private StreamInfo? _selectedStream;

    [ObservableProperty]
    private PlaybackState _playbackState = PlaybackState.Idle;

    [ObservableProperty]
    private TimeSpan _currentPosition = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private double _volume = 80;

    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    public ObservableCollection<VideoMetadata> SearchResults { get; } = new();
    public ObservableCollection<StreamInfo> AvailableStreams { get; } = new();

    public string FormattedPosition => $"{FormatTime(CurrentPosition)} / {FormatTime(Duration)}";

    public MainViewModel(IYouTubeService youtubeService, IAccessibilityService accessibilityService)
    {
        _youtubeService = youtubeService;
        _accessibilityService = accessibilityService;

        _youtubeService.StatusChanged += (_, status) =>
        {
            StatusMessage = status;
        };

        _youtubeService.ProviderSwitched += (_, provider) =>
        {
            CurrentProvider = provider;
            Announce($"Active backend switched to {provider}.");
        };
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            Announce("Please enter a search term or video link.", AnnouncementPriority.High);
            return;
        }

        IsBusy = true;
        StatusMessage = "Searching YouTube...";
        Announce($"Searching for {SearchQuery}...");

        try
        {
            SearchResults.Clear();
            var result = await _youtubeService.SearchAsync(SearchQuery);

            foreach (var item in result.Items)
            {
                SearchResults.Add(item);
            }

            StatusMessage = $"{SearchResults.Count} videos found via {result.ProviderUsed}.";
            Announce($"{SearchResults.Count} videos found for {SearchQuery}. Use down arrow to navigate results.", AnnouncementPriority.High);
        }
        catch (Exception ex)
        {
            StatusMessage = "Search failed.";
            Announce($"Search failed: {ex.Message}", AnnouncementPriority.High);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task PlaySelectedAsync()
    {
        if (SelectedVideo == null) return;

        IsBusy = true;
        StatusMessage = $"Loading {SelectedVideo.Title}...";
        Announce($"Loading {SelectedVideo.Title} by {SelectedVideo.Author}...");

        try
        {
            AvailableStreams.Clear();
            var streams = await _youtubeService.GetStreamsAsync(SelectedVideo.Id);

            foreach (var s in streams)
            {
                AvailableStreams.Add(s);
            }

            // Pick highest quality stream or audio stream
            SelectedStream = AvailableStreams.FirstOrDefault(s => !s.IsAudioOnly) ?? AvailableStreams.FirstOrDefault();

            PlaybackState = PlaybackState.Playing;
            StatusMessage = $"Playing: {SelectedVideo.Title}";
            Announce($"Now playing: {SelectedVideo.Title}. Press Space to pause, Left and Right arrows to seek.", AnnouncementPriority.High);
        }
        catch (Exception ex)
        {
            PlaybackState = PlaybackState.Error;
            StatusMessage = "Playback failed.";
            Announce($"Playback failed: {ex.Message}", AnnouncementPriority.High);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (PlaybackState == PlaybackState.Playing)
        {
            PlaybackState = PlaybackState.Paused;
            Announce("Paused.");
        }
        else if (PlaybackState == PlaybackState.Paused)
        {
            PlaybackState = PlaybackState.Playing;
            Announce("Playing.");
        }
    }

    [RelayCommand]
    public void SeekBackward5s()
    {
        CurrentPosition = TimeSpan.FromSeconds(Math.Max(0, CurrentPosition.TotalSeconds - 5));
        OnPropertyChanged(nameof(FormattedPosition));
        Announce($"Seek {FormatTime(CurrentPosition)}");
    }

    [RelayCommand]
    public void SeekForward5s()
    {
        CurrentPosition = TimeSpan.FromSeconds(CurrentPosition.TotalSeconds + 5);
        OnPropertyChanged(nameof(FormattedPosition));
        Announce($"Seek {FormatTime(CurrentPosition)}");
    }

    [RelayCommand]
    public void VolumeUp()
    {
        Volume = Math.Min(100, Volume + 5);
        Announce($"Volume {(int)Volume} percent");
    }

    [RelayCommand]
    public void VolumeDown()
    {
        Volume = Math.Max(0, Volume - 5);
        Announce($"Volume {(int)Volume} percent");
    }

    [RelayCommand]
    public void SpeedUp()
    {
        PlaybackSpeed = Math.Min(3.0, Math.Round(PlaybackSpeed + 0.1, 2));
        Announce($"Speed {PlaybackSpeed:F1}x");
    }

    [RelayCommand]
    public void SpeedDown()
    {
        PlaybackSpeed = Math.Max(0.5, Math.Round(PlaybackSpeed - 0.1, 2));
        Announce($"Speed {PlaybackSpeed:F1}x");
    }

    private void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium)
    {
        ScreenReaderAnnouncement = message;
        _accessibilityService.Announce(message, priority);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
        return $"{time.Minutes}:{time.Seconds:D2}";
    }
}
