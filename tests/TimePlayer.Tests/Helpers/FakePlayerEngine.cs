using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

namespace TimePlayer.Tests.Helpers;

public class FakePlayerEngine : IPlayerEngine
{
    public PlaybackState State { get; set; } = PlaybackState.Stopped;
    public TimeSpan CurrentTime { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);
    public double Volume { get; set; } = 80.0;
    public double Speed { get; set; } = 1.0;
    public bool IsMuted { get; set; }
    public string? CurrentSubtitleTrack { get; set; }
    public string? CurrentSubtitleText { get; set; }
    public bool IsEngineAlive { get; set; } = true;
    public bool AutoTransitionToPlaying { get; set; } = true;
    public Func<string, string?, Task<bool>>? LoadHandler { get; set; }

    public List<string> LoadedUrls { get; } = new();
    public List<string?> LoadedAudioUrls { get; } = new();
    public List<string> LoadedSubtitleUrls { get; } = new();

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;
    public event EventHandler<string>? SubtitleTextChanged;

    public Task InitializeAsync(nint windowHandle, PlayerOptions? options = null, CancellationToken cancellationToken = default)
    {
        IsEngineAlive = true;
        return Task.CompletedTask;
    }

    public async Task LoadAsync(string mediaUrl, string? audioTrackUrl = null, CancellationToken cancellationToken = default)
    {
        LoadedUrls.Add(mediaUrl);
        LoadedAudioUrls.Add(audioTrackUrl);

        if (LoadHandler != null)
        {
            var success = await LoadHandler(mediaUrl, audioTrackUrl);
            if (success)
            {
                var old = State;
                State = PlaybackState.Playing;
                StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, PlaybackState.Playing));
            }
            else
            {
                var old = State;
                State = PlaybackState.Error;
                StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, PlaybackState.Error));
                ErrorOccurred?.Invoke(this, new PlaybackErrorEventArgs("Failed to load stream in player engine."));
            }
            return;
        }

        if (AutoTransitionToPlaying)
        {
            var old = State;
            State = PlaybackState.Playing;
            StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, PlaybackState.Playing));
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        var old = State;
        State = PlaybackState.Playing;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, PlaybackState.Playing));
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        var old = State;
        State = PlaybackState.Paused;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, PlaybackState.Paused));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        var old = State;
        State = PlaybackState.Stopped;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, PlaybackState.Stopped));
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan targetTime, CancellationToken cancellationToken = default)
    {
        CurrentTime = targetTime;
        PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(CurrentTime, Duration));
        return Task.CompletedTask;
    }

    public Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default)
    {
        CurrentTime = TimeSpan.FromSeconds(Math.Max(0, CurrentTime.TotalSeconds + deltaSeconds));
        PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(CurrentTime, Duration));
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default)
    {
        Volume = Math.Clamp(volumePercent, 0, 100);
        return Task.CompletedTask;
    }

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        Speed = speed;
        return Task.CompletedTask;
    }

    public string AudioFilters { get; private set; } = string.Empty;
    public bool VideoEnabled { get; private set; } = true;

    public Task SetAudioFiltersAsync(string filterChain, CancellationToken cancellationToken = default)
    {
        AudioFilters = filterChain;
        return Task.CompletedTask;
    }

    public Task SetVideoEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        VideoEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        IsMuted = isMuted;
        return Task.CompletedTask;
    }

    public Task LoadSubtitleTrackAsync(string subtitleUrl, bool selectImmediately = true, CancellationToken cancellationToken = default)
    {
        LoadedSubtitleUrls.Add(subtitleUrl);
        CurrentSubtitleTrack = subtitleUrl;
        return Task.CompletedTask;
    }

    public Task SetSubtitleTrackAsync(string subtitleTrackId, CancellationToken cancellationToken = default)
    {
        CurrentSubtitleTrack = subtitleTrackId;
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        State = PlaybackState.Stopped;
        return Task.CompletedTask;
    }

    public void RaiseError(string message)
    {
        ErrorOccurred?.Invoke(this, new PlaybackErrorEventArgs(message));
    }

    public void RaiseSubtitleText(string text)
    {
        CurrentSubtitleText = text;
        SubtitleTextChanged?.Invoke(this, text);
    }

    public void RaiseState(PlaybackState newState)
    {
        var old = State;
        State = newState;
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, newState));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
