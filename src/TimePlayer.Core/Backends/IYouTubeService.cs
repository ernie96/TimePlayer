using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends;

/// <summary>
/// High-level YouTube orchestration service that coordinates multi-tier backend failover
/// (Piped -> Invidious -> local yt-dlp) transparently for the UI.
/// </summary>
public interface IYouTubeService
{
    BackendProviderType ActiveProvider { get; }

    event EventHandler<BackendProviderType>? ActiveProviderChanged;

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<VideoMetadata> GetVideoDetailsAsync(string videoIdOrUrl, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default);
    Task InitializeBackendsAsync(CancellationToken cancellationToken = default);
}
