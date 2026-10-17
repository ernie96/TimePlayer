using TimePlayer.Core.Models;

namespace TimePlayer.Core.Contracts;

/// <summary>
/// Unified contract for a YouTube backend provider capable of both catalog operations (search/details)
/// and stream resolution (Piped, Invidious, yt-dlp).
/// </summary>
public interface IBackendProvider : IVideoCatalogProvider, IStreamResolver
{
}
