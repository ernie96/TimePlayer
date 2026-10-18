using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

public interface IDiscoveryService
{
    Task<Result<IReadOnlyList<SearchResult>>> GetFeaturedAsync(FeaturedCategory category, string region, CancellationToken cancellationToken = default);
    Task<Result<ChannelUploads>> GetChannelUploadsAsync(string channelId, CancellationToken cancellationToken = default);
    /// <summary>Older uploads beyond the RSS feed's latest 15, one page at a time (null token = first page).</summary>
    Task<Result<PagedResult<SearchResult>>> GetChannelUploadsPageAsync(string channelId, string? pageToken, CancellationToken cancellationToken = default);
    Task<SubscriptionFeedResult> GetSubscriptionFeedAsync(
        IReadOnlyList<ChannelSubscription> subscriptions,
        IProgress<int>? channelsDone = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Featured videos (Invidious trending) and channel uploads / subscription feed
/// (YouTube's public per-channel RSS feeds, the same source NewPipe's fast feed mode uses).
/// Neither needs an account or API key.
/// </summary>
public sealed class DiscoveryService : IDiscoveryService
{
    private const int MaxParallelChannelFetches = 8;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    private readonly HttpClient _httpClient;
    private readonly InvidiousBackendProvider _invidious;
    private readonly ILogger<DiscoveryService> _logger;

    public DiscoveryService(HttpClient httpClient, InvidiousBackendProvider invidious, ILogger<DiscoveryService> logger)
    {
        _httpClient = httpClient;
        _invidious = invidious;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<SearchResult>>> GetFeaturedAsync(
        FeaturedCategory category, string region, CancellationToken cancellationToken = default)
    {
        var type = category switch
        {
            FeaturedCategory.Music => "music",
            FeaturedCategory.Gaming => "gaming",
            FeaturedCategory.Movies => "movies",
            _ => null
        };
        var result = await _invidious.GetTrendingAsync(type, region, cancellationToken);
        if (result.IsSuccess || category != FeaturedCategory.Trending)
            return result;

        _logger.LogInformation("Invidious trending unavailable ({Error}); falling back to popular.", result.Error?.Message);
        return await _invidious.GetPopularAsync(cancellationToken);
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
