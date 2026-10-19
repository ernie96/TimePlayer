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
}
