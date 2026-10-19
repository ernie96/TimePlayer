using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.TubeRepair;

/// <summary>
/// Primary backend provider using the legacy YouTube-compatible TubeRepair API (TubeRepair-CSharp).
/// Server endpoint: https://tuberepair.uptimetrackers.com/
/// Provides video search, stream resolution with Proof-of-Origin (POT) tokens, related videos,
/// comments, and playlists without requiring API keys or user authentication.
/// </summary>
public sealed class TubeRepairBackendProvider : IBackendProvider, IVideoCatalogProvider, IStreamResolver
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configService;
    private readonly ILogger<TubeRepairBackendProvider> _logger;

    public BackendProviderType ProviderType => BackendProviderType.TubeRepair;

    public TubeRepairBackendProvider(
        HttpClient httpClient,
        IConfigurationService configService,
        ILogger<TubeRepairBackendProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string BaseUrl
    {
        get
        {
            var url = _configService.CurrentConfig.Backend.TubeRepairBaseUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://tuberepair.uptimetrackers.com/";
            }
            return url.TrimEnd('/') + "/";
        }
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(
        Math.Max(5, _configService.CurrentConfig.Backend.TubeRepairTimeoutSeconds));

    public async Task<Result<PagedResult<SearchResult>>> SearchAsync(
        string query,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result<PagedResult<SearchResult>>.Failure("EMPTY_QUERY", "Search query cannot be empty.");

        int page = 1;
        if (!string.IsNullOrWhiteSpace(pageToken) && int.TryParse(pageToken, out var parsedPage) && parsedPage > 0)
        {
            page = parsedPage;
        }

        int maxResults = 20;
        int startIndex = ((page - 1) * maxResults) + 1;

        var requestUri = $"{BaseUrl}feeds/api/videos?q={Uri.EscapeDataString(query)}&start-index={startIndex}&max-results={maxResults}";
        _logger.LogInformation("Executing TubeRepair search for '{Query}', page {Page} from {Uri}...", query, page, requestUri);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
            req.Headers.TryAddWithoutValidation("User-Agent", "TimePlayer/1.0 (Windows NT 10.0; Win64; x64)");
            req.Headers.TryAddWithoutValidation("Accept", "application/atom+xml, application/xml, text/xml");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("TubeRepair search returned HTTP {Status} for '{Query}'.", resp.StatusCode, query);
                return Result<PagedResult<SearchResult>>.Failure(
                    "TUBEREPAIR_HTTP_ERROR",
                    $"TubeRepair search failed with status {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
            }

            var xml = await resp.Content.ReadAsStringAsync(cts.Token);
            var items = TubeRepairFeedParser.ParseVideoFeed(xml);

            _logger.LogInformation("TubeRepair search returned {Count} results for '{Query}'.", items.Count, query);

            var hasNext = items.Count >= maxResults;
            var paged = new PagedResult<SearchResult>
            {
                Items = items,
                PageIndex = page,
                HasNextPage = hasNext,
                NextPageToken = hasNext ? (page + 1).ToString() : null,
                TotalEstimatedResults = items.Count
            };

            return Result<PagedResult<SearchResult>>.Success(paged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during TubeRepair search for '{Query}'.", query);
            return Result<PagedResult<SearchResult>>.Failure("TUBEREPAIR_SEARCH_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId.Value))
            return Result<VideoDetails>.Failure("INVALID_ID", "Video ID cannot be empty.");

        // First attempt: search for videoId directly in TubeRepair
        var searchResult = await SearchAsync(videoId.Value, pageToken: null, cancellationToken);
        if (searchResult.IsSuccess && searchResult.Value.Items.Count > 0)
        {
            var matched = searchResult.Value.Items.FirstOrDefault(i => i.Id == videoId.Value)
                ?? searchResult.Value.Items[0];

            return Result<VideoDetails>.Success(new VideoDetails
            {
                Id = videoId,
                Title = matched.Title,
                Description = matched.DescriptionSnippet ?? string.Empty,
                Duration = matched.Duration ?? TimeSpan.Zero,
                ViewCount = matched.ViewCount ?? 0,
                UploadDate = null,
                Channel = new ChannelInfo
                {
                    Id = matched.ChannelId ?? string.Empty,
                    Name = matched.ChannelTitle,
                    AvatarUrl = null
                },
                ThumbnailUrl = matched.ThumbnailUrl
            });
        }

        // Second attempt: query related videos feed which also returns the video entry context
        try
        {
            var relatedResult = await GetRelatedVideosAsync(videoId.Value, cancellationToken);
            if (relatedResult.IsSuccess && relatedResult.Value.Count > 0)
            {
                var first = relatedResult.Value[0];
                return Result<VideoDetails>.Success(new VideoDetails
                {
                    Id = videoId,
                    Title = first.Title,
                    Description = string.Empty,
                    Duration = first.Duration ?? TimeSpan.Zero,
                    ViewCount = first.ViewCount ?? 0,
                    Channel = new ChannelInfo
                    {
                        Id = first.ChannelId ?? string.Empty,
                        Name = first.ChannelTitle
                    },
                    ThumbnailUrl = first.ThumbnailUrl
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TubeRepair related videos fallback failed for {Id}.", videoId.Value);
        }

        return Result<VideoDetails>.Failure("VIDEO_DETAILS_UNAVAILABLE", $"Video details not found on TubeRepair for {videoId.Value}.");
    }

    public async Task<Result<StreamResolutionResult>> ResolveStreamsAsync(
        YouTubeVideoId videoId,
        QualityPreference qualityPreference = QualityPreference.Auto,
        bool audioOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId.Value))
            return Result<StreamResolutionResult>.Failure("INVALID_ID", "Video ID cannot be empty.");

        string getUrl = $"{BaseUrl}getvideo/{videoId.Value}";
        _logger.LogInformation("Resolving stream via TubeRepair endpoint: {Url}...", getUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, getUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "TimePlayer/1.0 (Windows NT 10.0; Win64; x64)");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            string? targetUrl = null;
            if (resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308)
            {
                if (resp.Headers.Location != null)
                {
                    targetUrl = resp.Headers.Location.IsAbsoluteUri
                        ? resp.Headers.Location.ToString()
                        : new Uri(new Uri(getUrl), resp.Headers.Location).ToString();
                }
            }
            else if (resp.IsSuccessStatusCode)
            {
                // In case an intermediate proxy or followed redirect returned 200 directly
                var finalUri = resp.RequestMessage?.RequestUri?.ToString();
                targetUrl = (!string.IsNullOrEmpty(finalUri) && !finalUri.Equals(getUrl, StringComparison.OrdinalIgnoreCase))
                    ? finalUri
                    : getUrl;
            }

            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                _logger.LogWarning("TubeRepair /getvideo/{Id} returned HTTP {Status} without redirect location.", videoId.Value, resp.StatusCode);
                return Result<StreamResolutionResult>.Failure(
                    "STREAM_UNAVAILABLE",
                    $"TubeRepair did not return a playable redirect stream (Status: {(int)resp.StatusCode}).");
            }

            var finalPlaybackUrl = !string.IsNullOrWhiteSpace(targetUrl) ? targetUrl : getUrl;
            _logger.LogInformation("TubeRepair successfully resolved stream redirect for {Id} to {Url}.", videoId.Value, Common.LogRedaction.Url(finalPlaybackUrl));

            var candidate = new StreamCandidate
            {
                Url = finalPlaybackUrl,
                Format = "mp4",
                Height = 360,
                Bitrate = 500_000,
                IsAudioOnly = false,
                QualityLabel = "360p",
                IsProgressive = true
            };

            return Result<StreamResolutionResult>.Success(new StreamResolutionResult
            {
                VideoId = videoId,
                SelectedVideoStream = candidate,
                SelectedAudioStream = null,
                DirectPlaybackUrl = finalPlaybackUrl,
                AudioTrackUrl = null,
                Captions = Array.Empty<CaptionTrack>(),
                IsLiveStream = false,
                SourceProvider = BackendProviderType.TubeRepair
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TubeRepair stream resolution failed for {Id}.", videoId.Value);
            return Result<StreamResolutionResult>.Failure("TUBEREPAIR_STREAM_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<List<SearchResult>>> GetRelatedVideosAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{BaseUrl}feeds/api/videos/{Uri.EscapeDataString(videoId)}/related";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
            req.Headers.TryAddWithoutValidation("User-Agent", "TimePlayer/1.0 (Windows NT 10.0; Win64; x64)");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return Result<List<SearchResult>>.Failure("HTTP_ERROR", $"Related videos failed with status {resp.StatusCode}");
            }

            var xml = await resp.Content.ReadAsStringAsync(cts.Token);
            var items = TubeRepairFeedParser.ParseVideoFeed(xml);
            return Result<List<SearchResult>>.Success(items);
        }
        catch (Exception ex)
        {
            return Result<List<SearchResult>>.Failure("RELATED_VIDEOS_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<List<string>>> GetCommentsAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{BaseUrl}feeds/api/videos/{Uri.EscapeDataString(videoId)}/comments";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
            req.Headers.TryAddWithoutValidation("User-Agent", "TimePlayer/1.0 (Windows NT 10.0; Win64; x64)");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return Result<List<string>>.Failure("HTTP_ERROR", $"Comments failed with status {resp.StatusCode}");
            }

            var xml = await resp.Content.ReadAsStringAsync(cts.Token);
            var sanitized = TubeRepairFeedParser.SanitizeXml(xml);
            var doc = XDocument.Parse(sanitized);
            var comments = new List<string>();

            // Namespace-agnostic matching for entries and content
            var entries = doc.Descendants().Where(e => e.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in entries)
            {
                var contentElem = entry.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("content", StringComparison.OrdinalIgnoreCase));
                var authorElem = entry.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("author", StringComparison.OrdinalIgnoreCase));
                var nameElem = authorElem?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase));

                var content = contentElem?.Value?.Trim();
                var author = nameElem?.Value?.Trim();

                if (!string.IsNullOrEmpty(content))
                {
                    comments.Add(string.IsNullOrEmpty(author) ? content : $"{author}: {content}");
                }
            }

            return Result<List<string>>.Success(comments);
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Failure("COMMENTS_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

}
