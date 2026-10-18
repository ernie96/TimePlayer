using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public interface IDiscoveryService
{
    /// <summary>Featured videos for a category; <paramref name="pageToken"/> is null for the first page.</summary>
    Task<Result<PagedResult<SearchResult>>> GetFeaturedAsync(FeaturedCategory category, string region, string? pageToken = null, CancellationToken cancellationToken = default);
    Task<Result<ChannelUploads>> GetChannelUploadsAsync(string channelId, CancellationToken cancellationToken = default);
    /// <summary>Older uploads beyond the RSS feed's latest 15, one page at a time (null token = first page).</summary>
    Task<Result<PagedResult<SearchResult>>> GetChannelUploadsPageAsync(string channelId, string? pageToken, CancellationToken cancellationToken = default);
    Task<SubscriptionFeedResult> GetSubscriptionFeedAsync(
        IReadOnlyList<ChannelSubscription> subscriptions,
        IProgress<int>? channelsDone = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Featured videos (YouTube's destination pages, charts and filtered searches) and channel uploads / subscription feed
/// (YouTube's public per-channel RSS feeds, the same source NewPipe's fast feed mode uses).
/// Neither needs an account or API key.
/// </summary>
public sealed class DiscoveryService : IDiscoveryService
{
    private const int MaxParallelChannelFetches = 8;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    // YouTube's own "destination" channels, which replaced the Trending page in 2025.
    private const string MusicChannel = "UC-9-kyTW8ZkZNDHQJ6FgpwQ";
    private const string GamingChannel = "UCOpNcN46UbXVtpKMrmU4Abg";
    private const string NewsChannel = "UCYfdidRxbB8Qhf0Nx7ioOYw";
    private const string SportsChannel = "UCEgdi0XIXXZ-qJOFPf4JSKw";
    private const string LiveChannel = "UC4R8DWoMoI7CAwX8_LjQHig";
    /// <summary>YouTube Charts "Top 100 Music Videos Global", when the region has no chart of its own.</summary>
    private const string GlobalMusicVideosChart = "PL4fGSI1pDJn5kI81J1fYWK5eZRl1zJ5kM";
    // Search filters ("sp"): uploaded this week, videos only; and this week sorted by views.
    private const string ThisWeekVideos = "EgQIAxAB";
    private const string ThisWeekByViews = "CAMSAggD";

    private readonly HttpClient _httpClient;
    private readonly InvidiousBackendProvider _invidious;
    private readonly InnerTubeBackendProvider _innerTube;
    private readonly ILogger<DiscoveryService> _logger;

    public DiscoveryService(HttpClient httpClient, InvidiousBackendProvider invidious, InnerTubeBackendProvider innerTube, ILogger<DiscoveryService> logger)
    {
        _httpClient = httpClient;
        _invidious = invidious;
        _innerTube = innerTube;
        _logger = logger;
    }

    /// <summary>
    /// YouTube retired its Trending page in 2025, and Invidious's trending lists went stale with it. Each
    /// category now reads a source that YouTube still keeps current, as seen from the chosen region.
    /// </summary>
    public async Task<Result<PagedResult<SearchResult>>> GetFeaturedAsync(
        FeaturedCategory category, string region, string? pageToken = null, CancellationToken cancellationToken = default)
    {
        region = string.IsNullOrWhiteSpace(region) ? "US" : region.Trim().ToUpperInvariant();
        var result = category switch
        {
            FeaturedCategory.Music => await GetMusicChartAsync(region, pageToken, cancellationToken),
            FeaturedCategory.Gaming => await _innerTube.SearchFilteredAsync("gaming", ThisWeekVideos, pageToken, region, cancellationToken),
            FeaturedCategory.Movies => await _innerTube.SearchFilteredAsync("official movie trailer", ThisWeekByViews, pageToken, region, cancellationToken),
            FeaturedCategory.News => await GetPageVideosAsync(NewsChannel, region, cancellationToken),
            FeaturedCategory.Sports => await GetPageVideosAsync(SportsChannel, region, cancellationToken),
            FeaturedCategory.Live => await GetPageVideosAsync(LiveChannel, region, cancellationToken),
            _ => await GetTrendingMixAsync(region, cancellationToken)
        };
        if (result.IsSuccess && (result.Value.Items.Count > 0 || pageToken is not null))
            return result;

        if (category == FeaturedCategory.Trending)
        {
            _logger.LogInformation("Featured {Category} unavailable ({Error}); falling back to Invidious popular.", category, result.Error?.Message);
            var popular = await _invidious.GetPopularAsync(cancellationToken);
            if (popular.IsSuccess)
                return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult> { Items = popular.Value.ToList() });
        }
        return result.IsSuccess
            ? Result<PagedResult<SearchResult>>.Failure("FEATURED_EMPTY", "YouTube returned no videos for this category.")
            : result;
    }

    /// <summary>The region's "Top 100 Music Videos" chart, which the Music channel links to.</summary>
    private async Task<Result<PagedResult<SearchResult>>> GetMusicChartAsync(string region, string? pageToken, CancellationToken cancellationToken)
    {
        // Page tokens carry the chart's playlist id: "PLAYLIST|continuation".
        string playlistId;
        string? continuation = null;
        if (pageToken is not null)
        {
            var bar = pageToken.IndexOf('|');
            playlistId = pageToken[..bar];
            continuation = pageToken[(bar + 1)..];
        }
        else
        {
            var page = await _innerTube.BrowseAsync(MusicChannel, region, cancellationToken);
            playlistId = page.IsSuccess
                ? page.Value.Playlists.FirstOrDefault(p => p.Title.StartsWith("Top 100 Music Videos", StringComparison.OrdinalIgnoreCase))?.Id
                  ?? GlobalMusicVideosChart
                : GlobalMusicVideosChart;
        }

        var playlist = await _innerTube.GetPlaylistAsync(playlistId, continuation, cancellationToken);
        if (playlist.IsFailure)
            return Result<PagedResult<SearchResult>>.Failure(playlist.Error);
        var next = playlist.Value.NextPageToken;
        return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
        {
            Items = playlist.Value.Videos.ToList(),
            HasNextPage = next is not null,
            NextPageToken = next is null ? null : $"{playlistId}|{next}"
        });
    }

    private async Task<Result<PagedResult<SearchResult>>> GetPageVideosAsync(string browseId, string region, CancellationToken cancellationToken)
    {
        var page = await _innerTube.BrowseAsync(browseId, region, cancellationToken);
        return page.IsSuccess
            ? Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult> { Items = page.Value.Videos.ToList() })
            : Result<PagedResult<SearchResult>>.Failure(page.Error);
    }

    /// <summary>
    /// Trending: what YouTube's Music, Sports, News and Gaming pages feature now, plus this week's gaming,
    /// taken in turn from each so no single topic fills the list.
    /// </summary>
    private async Task<Result<PagedResult<SearchResult>>> GetTrendingMixAsync(string region, CancellationToken cancellationToken)
    {
        var sources = await Task.WhenAll(
            GetPageVideosAsync(MusicChannel, region, cancellationToken),
            GetPageVideosAsync(SportsChannel, region, cancellationToken),
            GetPageVideosAsync(NewsChannel, region, cancellationToken),
            GetPageVideosAsync(GamingChannel, region, cancellationToken),
            _innerTube.SearchFilteredAsync("gaming", ThisWeekVideos, null, region, cancellationToken));

        var lists = sources.Where(r => r.IsSuccess).Select(r => r.Value!.Items).ToList();
        if (lists.Count == 0)
            return Result<PagedResult<SearchResult>>.Failure(sources[0].Error!);

        // News pages run long; a fair share each keeps the mix varied.
        const int perSource = 25;
        var mixed = new List<SearchResult>();
        var seen = new HashSet<string>();
        for (int i = 0; i < perSource; i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Count && !list[i].IsLive && seen.Add(list[i].Id))
                    mixed.Add(list[i]);
            }
        }
        return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult> { Items = mixed });
    }

    public async Task<Result<ChannelUploads>> GetChannelUploadsAsync(string channelId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var url = $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(channelId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) TimePlayer");
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Result<ChannelUploads>.Failure("CHANNEL_FEED", $"Channel feed returned HTTP {(int)response.StatusCode}.");

            var xml = await response.Content.ReadAsStringAsync(timeout.Token);
            return Result<ChannelUploads>.Success(ParseChannelFeed(xml, channelId));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<ChannelUploads>.Failure("CHANNEL_FEED", "The channel feed timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException)
        {
            return Result<ChannelUploads>.Failure("CHANNEL_FEED", $"Could not read the channel feed: {ex.Message}");
        }
    }

    public Task<Result<PagedResult<SearchResult>>> GetChannelUploadsPageAsync(string channelId, string? pageToken, CancellationToken cancellationToken = default) =>
        _invidious.GetChannelVideosAsync(channelId, pageToken, cancellationToken);

    public async Task<SubscriptionFeedResult> GetSubscriptionFeedAsync(
        IReadOnlyList<ChannelSubscription> subscriptions,
        IProgress<int>? channelsDone = null,
        CancellationToken cancellationToken = default)
    {
        var videos = new List<SearchResult>();
        var failed = new List<ChannelSubscription>();
        int done = 0;
        using var gate = new SemaphoreSlim(MaxParallelChannelFetches);

        var tasks = subscriptions.Select(async sub =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var uploads = await GetChannelUploadsAsync(sub.ChannelId, cancellationToken);
                lock (videos)
                {
                    if (uploads.IsSuccess)
                        videos.AddRange(uploads.Value.Videos);
                    else
                        failed.Add(sub);
                }
            }
            finally
            {
                gate.Release();
                channelsDone?.Report(Interlocked.Increment(ref done));
            }
        });
        await Task.WhenAll(tasks);

        var sorted = videos
            .GroupBy(v => v.Id)
            .Select(g => g.First())
            .OrderByDescending(v => v.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();
        return new SubscriptionFeedResult(sorted, failed, DateTimeOffset.UtcNow);
    }

    internal static ChannelUploads ParseChannelFeed(string xml, string channelId)
    {
        var feed = XDocument.Parse(xml).Root ?? throw new System.Xml.XmlException("Empty channel feed.");
        var channelName = (string?)feed.Element(Atom + "author")?.Element(Atom + "name")
            ?? (string?)feed.Element(Atom + "title")
            ?? channelId;

        var videos = new List<SearchResult>();
        foreach (var entry in feed.Elements(Atom + "entry"))
        {
            var videoId = (string?)entry.Element(Yt + "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
                continue;

            var link = (string?)entry.Element(Atom + "link")?.Attribute("href") ?? string.Empty;
            var group = entry.Element(Media + "group");
            var views = (string?)group?.Element(Media + "community")?.Element(Media + "statistics")?.Attribute("views");

            videos.Add(new SearchResult
            {
                Id = videoId,
                Title = (string?)entry.Element(Atom + "title") ?? "Untitled video",
                ChannelTitle = (string?)entry.Element(Atom + "author")?.Element(Atom + "name") ?? channelName,
                ChannelId = (string?)entry.Element(Yt + "channelId") ?? channelId,
                PublishedAt = DateTimeOffset.TryParse((string?)entry.Element(Atom + "published"), out var published) ? published : null,
                ViewCount = long.TryParse(views, out var v) ? v : null,
                IsShort = link.Contains("/shorts/", StringComparison.OrdinalIgnoreCase),
                ThumbnailUrl = (string?)group?.Element(Media + "thumbnail")?.Attribute("url"),
                DescriptionSnippet = Truncate((string?)group?.Element(Media + "description"), 200)
            });
        }
        return new ChannelUploads(channelId, channelName, videos);
    }

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max];
}
