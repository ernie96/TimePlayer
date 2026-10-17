using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends;

/// <summary>
/// Pluggable backend provider contract for YouTube metadata and stream retrieval.
/// Implementations include Piped, Invidious, and yt-dlp local fallback.
/// </summary>
public interface IYouTubeBackendProvider
{
    BackendProviderType ProviderType { get; }
    bool IsAvailable { get; }

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default);
}
