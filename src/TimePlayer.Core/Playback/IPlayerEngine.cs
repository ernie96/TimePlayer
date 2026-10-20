using TimePlayer.Core.Contracts;

namespace TimePlayer.Core.Playback;

/// <summary>
/// Abstraction for media playback engine (mpv).
/// Decouples UI and viewmodels from the underlying process and IPC implementation.
/// </summary>
public interface IPlayerEngine : IPlaybackEngine
{
    /// <summary>
    /// Replaces the player's audio effects with an mpv "af" chain (empty for none). Kept across videos
    /// and re-applied if the player restarts.
    /// </summary>
    Task SetAudioFiltersAsync(string filterChain, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Turns video decoding off (music mode) or back on. Kept across videos.</summary>
    Task SetVideoEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Plays through <paramref name="device"/> (a name from <see cref="GetAudioDevicesAsync"/>, or "auto" for
    /// Windows' default), so the screen reader and the video can use different speakers or headphones.
    /// </summary>
    Task SetAudioDeviceAsync(string device, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>The sound devices the player can use.</summary>
    Task<IReadOnlyList<AudioDevice>> GetAudioDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AudioDevice>>(Array.Empty<AudioDevice>());

    /// <summary>Plays each video again and again (Shorts), or once.</summary>
    Task SetLoopAsync(bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>The highest volume allowed, in percent: 100, or up to 200 for quiet videos.</summary>
    Task SetMaxVolumeAsync(double percent, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A sound output: mpv's name for it and the name Windows shows.</summary>
public sealed record AudioDevice(string Name, string Description);
