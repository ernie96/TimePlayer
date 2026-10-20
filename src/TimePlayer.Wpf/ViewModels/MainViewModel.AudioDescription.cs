using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Playback;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Audio description: some YouTube videos have a second soundtrack where a narrator describes what is on
/// screen. TimePlayer plays it when there is one (Settings, Playback), says so once, and D switches it.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _useAudioDescription = true;

    // The video whose audio description was last spoken about, so it is said once per video.
    private string? _descriptionAnnouncedFor;

    private void InitializeAudioDescription()
    {
        _useAudioDescription = _configService.CurrentConfig.Playback.AudioDescription;
        if (_playbackCoordinator is PlaybackCoordinator coordinator)
            coordinator.AudioDescriptionAvailable += OnAudioDescriptionAvailable;
    }

    private void DisposeAudioDescription()
    {
        if (_playbackCoordinator is PlaybackCoordinator coordinator)
            coordinator.AudioDescriptionAvailable -= OnAudioDescriptionAvailable;
    }

    partial void OnUseAudioDescriptionChanged(bool value)
    {
        var current = _configService.CurrentConfig;
        if (current.Playback.AudioDescription != value)
            _ = _configService.UpdateConfigAsync(current with { Playback = current.Playback with { AudioDescription = value } });
    }

    private AudioDescriptionState DescriptionState =>
        (_playbackCoordinator as PlaybackCoordinator)?.AudioDescription ?? AudioDescriptionState.None;

    /// <summary>Called when a video starts playing: says once that it is the audio-described version.</summary>
    private void AnnounceAudioDescriptionOnPlay()
    {
        var id = CurrentVideo?.Id.Value;
        if (id is null || DescriptionState != AudioDescriptionState.Playing || _descriptionAnnouncedFor == id)
            return;
        _descriptionAnnouncedFor = id;
        _accessibility.Announce(T("With audio description."), AnnouncementPriority.Low);
    }

    private void OnAudioDescriptionAvailable(object? sender, EventArgs e)
    {
        if (!IsOnUiThread)
        {
            _uiContext!.Post(_ => OnAudioDescriptionAvailable(sender, e), null);
            return;
        }
        var id = CurrentVideo?.Id.Value;
        if (id is null || _descriptionAnnouncedFor == id)
            return;
        _descriptionAnnouncedFor = id;
        _accessibility.Announce(T("Audio description available: press D."), AnnouncementPriority.Low);
    }

    /// <summary>D in the player: audio description on or off, switching the video playing now.</summary>
    [RelayCommand]
    public async Task ToggleAudioDescriptionAsync()
    {
        UseAudioDescription = !UseAudioDescription;
        var state = DescriptionState;
        if (state == AudioDescriptionState.None)
        {
            _accessibility.Announce(UseAudioDescription
                ? T("Audio description on. This video doesn't have it.")
                : T("Audio description off."), AnnouncementPriority.High);
            return;
        }

        _accessibility.Announce(UseAudioDescription ? T("Audio description on.") : T("Audio description off."), AnnouncementPriority.High);
        var needsSwitch = UseAudioDescription ? state == AudioDescriptionState.Available : state == AudioDescriptionState.Playing;
        if (!needsSwitch || _playbackCoordinator is not PlaybackCoordinator coordinator)
            return;

        _descriptionAnnouncedFor = CurrentVideo?.Id.Value; // already said
        var result = await coordinator.ReplayCurrentAsync(CurrentTime);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Switching audio description failed: {Message}", result.Error?.Message);
            ShowProblem(T("Could not play this video"), result.Error?.Message);
        }
    }
}
