using TimePlayer.Core.Contracts;

namespace TimePlayer.Core.Playback;

/// <summary>
/// Abstraction for media playback engine (mpv).
/// Decouples UI and viewmodels from the underlying process and IPC implementation.
/// </summary>
public interface IPlayerEngine : IPlaybackEngine
{
}
