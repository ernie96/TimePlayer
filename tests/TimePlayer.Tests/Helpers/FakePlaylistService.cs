using TimePlayer.Core.Common;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Tests.Helpers;

public sealed class FakePlaylistService : IPlaylistService
{
    /// <summary>YouTube playlists by id: title, owner and videos.</summary>
    public Dictionary<string, YouTubePlaylistPage> Playlists { get; } = new();
    public List<SearchResult> SearchResults { get; } = new();

    public Task<Result<YouTubePlaylistPage>> GetPageAsync(string playlistId, string? pageToken, CancellationToken cancellationToken = default) =>
        GetAllAsync(playlistId, cancellationToken: cancellationToken);

    public Task<Result<YouTubePlaylistPage>> GetAllAsync(string playlistId, int maxVideos = 5000, CancellationToken cancellationToken = default) =>
        Task.FromResult(Playlists.TryGetValue(playlistId, out var page)
            ? Result<YouTubePlaylistPage>.Success(page)
            : Result<YouTubePlaylistPage>.Failure("PLAYLIST_EMPTY", "No such playlist."));

    public Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, string? pageToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult> { Items = SearchResults.ToList() }));

    public Task<IReadOnlyList<SearchResult>> DescribeVideosAsync(IReadOnlyList<string> videoIds, IProgress<int>? done = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>(videoIds
            .Select(id => new SearchResult { Id = id, Title = $"Title of {id}", ChannelTitle = "Someone" })
            .ToList());
}

public sealed class FakeRecommendationService : IRecommendationService
{
    public List<SearchResult> Videos { get; } = new();
    public bool IsPersonal { get; set; } = true;
    public int Requests { get; private set; }

    public bool IsCacheFresh => false;

    public Task<Recommendations> GetAsync(RecommendationOptions options, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        Requests++;
        return Task.FromResult(new Recommendations(Videos.ToList(), DateTimeOffset.UtcNow, IsPersonal));
    }

    public void Forget(Func<SearchResult, bool> predicate) => Videos.RemoveAll(v => predicate(v));
}
