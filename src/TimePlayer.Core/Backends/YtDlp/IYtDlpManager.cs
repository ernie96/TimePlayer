using TimePlayer.Core.Common;

namespace TimePlayer.Core.Backends.YtDlp;

/// <summary>
/// Manages the application-bundled yt-dlp standalone binary lifecycle,
/// including discovery, installation, SHA256 verification, and automatic updates.
/// </summary>
public interface IYtDlpManager
{
    /// <summary>
    /// Gets the absolute path to the managed or configured yt-dlp executable.
    /// </summary>
    string ExecutablePath { get; }

    /// <summary>
    /// Gets whether the yt-dlp executable currently exists on disk.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Ensures that yt-dlp is installed in the managed directory and updated to the latest version.
    /// </summary>
    Task<Result<string>> EnsureInstalledOrUpdatedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current version string reported by the yt-dlp binary or local metadata.
    /// </summary>
    Task<Result<string>> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks for updates against the official releases and updates the binary if a newer version is available.
    /// </summary>
    Task<Result<bool>> CheckAndUpdateAsync(CancellationToken cancellationToken = default);
}
