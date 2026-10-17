using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// Low-level media player engine contract (e.g. mpv embedded via HwndHost and controlled via JSON IPC).
/// </summary>
public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackState State { get; }
    TimeSpan CurrentTime { get; }
    TimeSpan Duration { get; }
    double Volume { get; }
    double Speed { get; }
    bool IsMuted { get; }
    string? CurrentSubtitleTrack { get; }
    string? CurrentSubtitleText { get; }
    bool IsEngineAlive { get; }

    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;
    event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;
    event EventHandler<string>? SubtitleTextChanged;

    Task InitializeAsync(nint windowHandle, PlayerOptions? options = null, CancellationToken cancellationToken = default);
    Task LoadAsync(string mediaUrl, string? audioTrackUrl = null, CancellationToken cancellationToken = default);
    Task PlayAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan targetTime, CancellationToken cancellationToken = default);
    Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default);
    Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default);
    Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default);
    Task LoadSubtitleTrackAsync(string subtitleUrl, bool selectImmediately = true, CancellationToken cancellationToken = default);
    Task SetSubtitleTrackAsync(string subtitleTrackId, CancellationToken cancellationToken = default);
    Task RestartAsync(CancellationToken cancellationToken = default);
}
