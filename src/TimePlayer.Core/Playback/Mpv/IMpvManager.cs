using TimePlayer.Core.Common;

namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Manages the application-bundled mpv standalone binary lifecycle,
/// including discovery, installation, and path resolution.
/// </summary>
public interface IMpvManager
{
    /// <summary>
    /// Gets the absolute path to the managed or configured mpv executable.
    /// </summary>
    string ExecutablePath { get; }

    /// <summary>
    /// Gets whether the mpv executable currently exists on disk.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Ensures that mpv is present in the application's managed dependency directory.
    /// If missing, downloads the standalone release from official GitHub distribution and extracts it.
    /// </summary>
    Task<Result<string>> EnsureInstalledAsync(CancellationToken cancellationToken = default);
}
