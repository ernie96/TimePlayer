using System.Collections.Specialized;
using System.Globalization;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>Display-only state derived from the rest of the view model (labels, visibility, slider).</summary>
public sealed partial class MainViewModel
{
    private void InitializeViewState()
    {
        SearchResults.CollectionChanged += OnListCountChanged;
        PlaybackQueue.CollectionChanged += OnListCountChanged;
    }

    private void OnListCountChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseListStateChanged();

    private void RaiseListStateChanged()
    {
        OnPropertyChanged(nameof(SectionCountText));
        OnPropertyChanged(nameof(IsSectionEmpty));
        OnPropertyChanged(nameof(IsResultsListVisible));
        OnPropertyChanged(nameof(IsQueueListVisible));
    }

    public string WindowTitle => IsPlayerDialogOpen && !string.IsNullOrWhiteSpace(CurrentVideoTitle)
        ? $"{CurrentVideoTitle} - TimePlayer {ModeName}"
        : $"TimePlayer {ModeName}";

    public bool IsQueueSection => CurrentSection == BrowseSection.Queue;
    public bool IsResultsListVisible => !IsQueueSection && !IsSectionEmpty;
    public bool IsQueueListVisible => IsQueueSection && PlaybackQueue.Count > 0;
    public bool CanRemoveFromSection => CurrentSection is BrowseSection.History or BrowseSection.MusicRecent or BrowseSection.WatchLater or BrowseSection.Channels
        or BrowseSection.Playlists or BrowseSection.ForYou || IsOpenPlaylistInLibrary;

    public bool IsSectionEmpty => IsQueueSection
        ? PlaybackQueue.Count == 0
        : SearchResults.Count == 0 && !IsSectionLoading;

    public string SectionCountText
    {
        get
        {
            int count = IsQueueSection ? PlaybackQueue.Count : SearchResults.Count;
            if (count == 0) return string.Empty;
            var more = CanLoadMore && !IsQueueSection ? ", more load as you go" : string.Empty;
            return $"{count} {SectionNoun(count)}{more}";
        }
    }

    partial void OnCurrentSectionChanged(BrowseSection value)
    {
        OnPropertyChanged(nameof(IsQueueSection));
        OnPropertyChanged(nameof(CanRemoveFromSection));
        RaisePlaylistViewState();
        RaiseListStateChanged();
    }

    partial void OnIsSectionLoadingChanged(bool value) => RaiseListStateChanged();

    partial void OnCurrentVideoTitleChanged(string value) => OnPropertyChanged(nameof(WindowTitle));

    // --- Player labels ---

    public string PlayPauseButtonText => PlaybackState == PlaybackState.Playing ? "Pause" : "Play";
    public string MuteButtonText => IsMuted ? "Unmute" : "Mute";
    public string VideoDescriptionText
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(VideoDescription) ? "No description." : VideoDescription;
            return CurrentVotes is { } votes
                ? $"{votes.Spoken} (dislikes estimated by Return YouTube Dislike).\n\n{description}"
                : description;
        }
    }

    partial void OnPlaybackStateChanged(PlaybackState value) => OnPropertyChanged(nameof(PlayPauseButtonText));
    partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(MuteButtonText));
    partial void OnVideoDescriptionChanged(string value) => OnPropertyChanged(nameof(VideoDescriptionText));

    public int SelectedPlayerPanelIndex
    {
        get => (int)SelectedPlayerPanel;
        set
        {
            if (value >= 0 && value != (int)SelectedPlayerPanel)
                SelectedPlayerPanel = (PlayerPanel)value;
        }
    }

    // --- Time and seek slider ---

    public string CurrentTimeText => FormatClock(CurrentTime);
    public string DurationText => FormatClock(EffectiveDuration);
    public double DurationSeconds => Math.Max(1, EffectiveDuration.TotalSeconds);

    private TimeSpan EffectiveDuration => Duration > TimeSpan.Zero ? Duration : (CurrentVideo?.Duration ?? TimeSpan.Zero);

    /// <summary>Two-way slider value: reading follows playback, writing (arrows, Page keys, clicks) seeks.</summary>
    public double SliderSeconds
    {
        get => CurrentTime.TotalSeconds;
        set
        {
            if (!IsEngineInitialized || Math.Abs(value - CurrentTime.TotalSeconds) < 0.5)
                return;
            var target = ClampToDuration(TimeSpan.FromSeconds(value));
            CurrentTime = target;
            UpdateBrailleDisplayLine();
            _ = _playerEngine.SeekAsync(target);
            _accessibility.Announce(AccessibleTextFormatter.FormatTimeSpoken(target), AnnouncementPriority.Low);
        }
    }

    partial void OnCurrentTimeChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(CurrentTimeText));
        OnPropertyChanged(nameof(SliderSeconds));
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(DurationSeconds));
    }

    private static string FormatClock(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
