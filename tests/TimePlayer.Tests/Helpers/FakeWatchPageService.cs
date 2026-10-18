using TimePlayer.Core.Common;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Tests.Helpers;

public sealed class FakeWatchPageService : IWatchPageService
{
    public Dictionary<string, List<SearchResult>> Related { get; } = new();

    /// <summary>Comment pages served in order; each page's token is its index.</summary>
    public List<List<Comment>> CommentPages { get; } = new();

    public Dictionary<string, List<Comment>> Replies { get; } = new();

    public int CommentRequests { get; private set; }

    public Task<Result<WatchNextInfo>> GetWatchNextAsync(string videoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<WatchNextInfo>.Success(new WatchNextInfo(
            Related.TryGetValue(videoId, out var related) ? related : new List<SearchResult>(), "token", "A description")));

    public Task<Result<CommentPage>> GetCommentsAsync(string videoId, CommentPage? previous, CancellationToken cancellationToken = default)
    {
        CommentRequests++;
        int index = previous?.NextPageToken is { } token ? int.Parse(token) : 0;
        if (index >= CommentPages.Count)
            return Task.FromResult(Result<CommentPage>.Success(new CommentPage(Array.Empty<Comment>(), null, CommentSource.InnerTube)));
        var next = index + 1 < CommentPages.Count ? (index + 1).ToString() : null;
        return Task.FromResult(Result<CommentPage>.Success(new CommentPage(CommentPages[index], next, CommentSource.InnerTube)));
    }

    public Task<Result<CommentPage>> GetRepliesAsync(string videoId, Comment parent, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<CommentPage>.Success(new CommentPage(
            Replies.TryGetValue(parent.Id, out var replies) ? replies : new List<Comment>(), null, parent.Source)));
}
