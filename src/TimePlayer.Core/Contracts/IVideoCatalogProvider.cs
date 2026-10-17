using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// Contract for video catalog providers (Piped, Invidious, local yt-dlp).
/// Searches YouTube catalog with pagination and loads full video metadata.
/// </summary>
public interface IVideoCatalogProvider
{
    BackendProviderType ProviderType { get; }

    Task<Result<PagedResult<SearchResult>>> SearchAsync(
        string query,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default);
}
