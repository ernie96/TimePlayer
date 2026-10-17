using TimePlayer.Core.Models;

namespace TimePlayer.Core.Playback;

/// <summary>
/// Abstraction for media playback engine (mpv).
/// Decouples UI and viewmodels from the underlying process and IPC implementation.
/// </summary>
public interface IPlayerEngine : IAsyncDisposable
{
    PlaybackState State { get; }
    TimeSpan CurrentTime { get; }
    TimeSpan Duration { get; }
    double Volume { get; }
    double Speed { get; }
    bool IsMuted { get; }

    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;
    event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    Task InitializeAsync(nint windowHandle, PlayerOptions? options = null, CancellationToken cancellationToken = default);
    Task LoadAsync(string mediaUrl, CancellationToken cancellationToken = default);
    Task PlayAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan targetTime, CancellationToken cancellationToken = default);
    Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default);
    Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default);
    Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default);
}
