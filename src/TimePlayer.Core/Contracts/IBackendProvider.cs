namespace TimePlayer.Core.Contracts;

/// <summary>
/// Unified contract for a YouTube backend provider capable of both catalog operations (search/details)
/// and stream resolution (TubeRepair, Invidious, InnerTube, yt-dlp).
/// </summary>
public interface IBackendProvider : IVideoCatalogProvider, IStreamResolver
{
}
