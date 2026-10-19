using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public interface IWatchPageService
{
    /// <summary>Related videos ("Up next") and description.</summary>
    Task<Result<WatchNextInfo>> GetWatchNextAsync(string videoId, CancellationToken cancellationToken = default);

    /// <summary>First page when <paramref name="previous"/> is null, otherwise the page after it.</summary>
    Task<Result<CommentPage>> GetCommentsAsync(string videoId, CommentPage? previous, CancellationToken cancellationToken = default);

    Task<Result<CommentPage>> GetRepliesAsync(string videoId, Comment parent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Watch page data from YouTube's InnerTube API, falling back to Invidious when InnerTube fails.
/// </summary>
public sealed class WatchPageService : IWatchPageService
{
    private readonly InnerTubeBackendProvider _innerTube;
    private readonly InvidiousBackendProvider _invidious;
    private readonly ILogger<WatchPageService> _logger;
    private readonly ConcurrentDictionary<string, string> _commentTokens = new();

    public WatchPageService(InnerTubeBackendProvider innerTube, InvidiousBackendProvider invidious, ILogger<WatchPageService> logger)
    {
        _innerTube = innerTube;
        _invidious = invidious;
        _logger = logger;
    }

    public async Task<Result<WatchNextInfo>> GetWatchNextAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var innerTube = await _innerTube.GetWatchNextAsync(videoId, cancellationToken);
        if (innerTube.IsSuccess)
        {
            if (innerTube.Value.CommentsToken is { } token)
            {
                // One entry per video watched; a long session shouldn't keep them all.
                if (_commentTokens.Count > 200)
                    _commentTokens.Clear();
                _commentTokens[videoId] = token;
            }
            if (innerTube.Value.Related.Count > 0)
                return innerTube;
        }
        else
        {
            _logger.LogInformation("InnerTube watch page failed for {Id}: {Error}", videoId, innerTube.Error?.Message);
        }

        var related = await _invidious.GetRelatedAsync(videoId, cancellationToken);
        if (related.IsSuccess)
        {
            return Result<WatchNextInfo>.Success(new WatchNextInfo(
                related.Value,
                innerTube.IsSuccess ? innerTube.Value.CommentsToken : null,
                innerTube.IsSuccess ? innerTube.Value.Description : null));
        }
        return innerTube.IsSuccess ? innerTube : Result<WatchNextInfo>.Failure(related.Error);
    }

    public async Task<Result<CommentPage>> GetCommentsAsync(string videoId, CommentPage? previous, CancellationToken cancellationToken = default)
    {
        if (previous is not null)
        {
            if (string.IsNullOrEmpty(previous.NextPageToken))
                return Result<CommentPage>.Success(new CommentPage(Array.Empty<Comment>(), null, previous.Source));
            return previous.Source == CommentSource.InnerTube
                ? await _innerTube.GetCommentsAsync(previous.NextPageToken, isReplyPage: false, cancellationToken)
                : await _invidious.GetCommentsAsync(videoId, previous.NextPageToken, isReplyPage: false, cancellationToken);
        }

        if (!_commentTokens.TryGetValue(videoId, out var token))
        {
            var next = await _innerTube.GetWatchNextAsync(videoId, cancellationToken);
            if (next.IsSuccess && next.Value.CommentsToken is { } t)
                _commentTokens[videoId] = token = t;
        }

        if (token is not null)
        {
            var page = await _innerTube.GetCommentsAsync(token, isReplyPage: false, cancellationToken);
            if (page.IsSuccess && page.Value.Comments.Count > 0)
                return page;
            _logger.LogInformation("InnerTube comments unavailable for {Id}; trying Invidious.", videoId);
        }

        return await _invidious.GetCommentsAsync(videoId, null, isReplyPage: false, cancellationToken);
    }

    public Task<Result<CommentPage>> GetRepliesAsync(string videoId, Comment parent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(parent.RepliesToken))
            return Task.FromResult(Result<CommentPage>.Success(new CommentPage(Array.Empty<Comment>(), null, parent.Source)));
        return parent.Source == CommentSource.InnerTube
            ? _innerTube.GetCommentsAsync(parent.RepliesToken, isReplyPage: true, cancellationToken)
            : _invidious.GetCommentsAsync(videoId, parent.RepliesToken, isReplyPage: true, cancellationToken);
    }
}
