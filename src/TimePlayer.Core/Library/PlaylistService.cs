using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public interface IPlaylistService
{
    /// <summary>One page of a YouTube playlist; <paramref name="pageToken"/> is null for the first page.</summary>
    Task<Result<YouTubePlaylistPage>> GetPageAsync(string playlistId, string? pageToken, CancellationToken cancellationToken = default);

    /// <summary>Every video of a YouTube playlist, up to <paramref name="maxVideos"/>.</summary>
    Task<Result<YouTubePlaylistPage>> GetAllAsync(string playlistId, int maxVideos = 5000, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, string? pageToken, CancellationToken cancellationToken = default);

    /// <summary>Titles and channels for bare video ids (Takeout gives only ids). Ids that can't be looked up keep a placeholder.</summary>
    Task<IReadOnlyList<SearchResult>> DescribeVideosAsync(IReadOnlyList<string> videoIds, IProgress<int>? done = null, CancellationToken cancellationToken = default);
}

/// <summary>YouTube playlists without an account: InnerTube first, Invidious when it fails.</summary>
public sealed class PlaylistService : IPlaylistService
{
    private const string InvidiousTokenPrefix = "inv:";
    private const int MaxParallelLookups = 8;

    private readonly InnerTubeBackendProvider _innerTube;
    private readonly InvidiousBackendProvider _invidious;
    private readonly HttpClient _httpClient;
    private readonly ILogger<PlaylistService> _logger;

    public PlaylistService(InnerTubeBackendProvider innerTube, InvidiousBackendProvider invidious, HttpClient httpClient, ILogger<PlaylistService> logger)
    {
        _innerTube = innerTube;
        _invidious = invidious;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<Result<YouTubePlaylistPage>> GetPageAsync(string playlistId, string? pageToken, CancellationToken cancellationToken = default)
    {
        if (pageToken is not null && pageToken.StartsWith(InvidiousTokenPrefix, StringComparison.Ordinal))
            return await GetInvidiousPageAsync(playlistId, int.Parse(pageToken[InvidiousTokenPrefix.Length..], CultureInfo.InvariantCulture), cancellationToken);

        var page = await _innerTube.GetPlaylistAsync(playlistId, pageToken, cancellationToken);
        if (page.IsSuccess || pageToken is not null)
            return page;

        _logger.LogInformation("InnerTube playlist {Id} failed ({Error}); trying Invidious.", playlistId, page.Error?.Message);
        var fallback = await GetInvidiousPageAsync(playlistId, 1, cancellationToken);
        return fallback.IsSuccess ? fallback : page;
    }

    private async Task<Result<YouTubePlaylistPage>> GetInvidiousPageAsync(string playlistId, int page, CancellationToken cancellationToken)
    {
        var result = await _invidious.GetPlaylistAsync(playlistId, page, cancellationToken);
        if (result.IsFailure)
            return result;
        var value = result.Value;
        return Result<YouTubePlaylistPage>.Success(value with
        {
            NextPageToken = value.NextPageToken is null ? null : InvidiousTokenPrefix + value.NextPageToken
        });
    }

    public async Task<Result<YouTubePlaylistPage>> GetAllAsync(string playlistId, int maxVideos = 5000, CancellationToken cancellationToken = default)
    {
        var first = await GetPageAsync(playlistId, null, cancellationToken);
        if (first.IsFailure)
            return first;

        var videos = new List<SearchResult>(first.Value.Videos);
        var seen = new HashSet<string>(videos.Select(v => v.Id));
        var token = first.Value.NextPageToken;
        while (token is not null && videos.Count < maxVideos)
        {
            var next = await GetPageAsync(playlistId, token, cancellationToken);
            if (next.IsFailure)
            {
                _logger.LogWarning("Playlist {Id} stopped after {Count} videos: {Error}", playlistId, videos.Count, next.Error?.Message);
                break;
            }
            var added = 0;
            foreach (var v in next.Value.Videos)
            {
                if (seen.Add(v.Id))
                {
                    videos.Add(v);
                    added++;
                }
            }
            token = added > 0 ? next.Value.NextPageToken : null;
        }
        return Result<YouTubePlaylistPage>.Success(first.Value with { Videos = videos.Take(maxVideos).ToList(), NextPageToken = null });
    }

    public Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, string? pageToken, CancellationToken cancellationToken = default) =>
        _innerTube.SearchPlaylistsAsync(query, pageToken, cancellationToken);

    public async Task<IReadOnlyList<SearchResult>> DescribeVideosAsync(IReadOnlyList<string> videoIds, IProgress<int>? done = null, CancellationToken cancellationToken = default)
    {
        var results = new SearchResult[videoIds.Count];
        int finished = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, videoIds.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                results[i] = await DescribeAsync(videoIds[i], ct) ?? TakeoutImport.PlaceholderVideo(videoIds[i]);
                done?.Report(Interlocked.Increment(ref finished));
            });
        return results;
    }

    /// <summary>YouTube's public oEmbed endpoint: title and channel name, no key needed.</summary>
    private async Task<SearchResult?> DescribeAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var url = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString($"https://www.youtube.com/watch?v={videoId}");
            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null; // deleted or private
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(title))
                return null;
            var author = root.TryGetProperty("author_name", out var a) ? a.GetString() : null;
            return new SearchResult
            {
                Id = videoId,
                Title = title,
                ChannelTitle = author ?? string.Empty
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }
}
