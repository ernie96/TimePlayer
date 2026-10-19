using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.InnerTube;

/// <summary>
/// Tertiary backend provider implementing anonymous YouTube InnerTube API calls.
/// Requires no Google API key, no OAuth, and no user login.
/// Provides direct YouTube catalog search, metadata via /next, and player format inspection via /player.
/// </summary>
public sealed class InnerTubeBackendProvider : IBackendProvider, IVideoCatalogProvider, IStreamResolver
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configService;
    private readonly ILogger<InnerTubeBackendProvider> _logger;

    public BackendProviderType ProviderType => BackendProviderType.InnerTube;

    public InnerTubeBackendProvider(
        HttpClient httpClient,
        IConfigurationService configService,
        ILogger<InnerTubeBackendProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string BaseUrl
    {
        get
        {
            var url = _configService.CurrentConfig.Backend.InnerTubeBaseUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://www.youtube.com/youtubei/v1/";
            }
            return url.TrimEnd('/') + "/";
        }
    }

    private TimeSpan Timeout => TimeSpan.FromSeconds(
        Math.Max(5, _configService.CurrentConfig.Backend.InnerTubeTimeoutSeconds));

    private static InnerTubeContext CreateWebContext(string? region = null) => new()
    {
        Client = new InnerTubeClientInfo
        {
            ClientName = "WEB",
            ClientVersion = "2.20261015.00.00",
            Hl = "en",
            Gl = string.IsNullOrWhiteSpace(region) ? "US" : region.Trim().ToUpperInvariant()
        }
    };

    /// <param name="pageToken">InnerTube continuation token from the previous page, or null for page 1.</param>
    public Task<Result<PagedResult<SearchResult>>> SearchAsync(
        string query,
        string? pageToken = null,
        CancellationToken cancellationToken = default) =>
        SearchFilteredAsync(query, null, pageToken, null, cancellationToken);

    /// <summary>
    /// Search with one of YouTube's search filters (<paramref name="searchParams"/>, the "sp" value of a
    /// filtered search link, e.g. "this week, sorted by views") as seen from <paramref name="region"/>.
    /// </summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchFilteredAsync(
        string query,
        string? searchParams,
        string? pageToken,
        string? region,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result<PagedResult<SearchResult>>.Failure("EMPTY_QUERY", "Search query cannot be empty.");

        var body = new JsonObject();
        if (string.IsNullOrWhiteSpace(pageToken))
        {
            body["query"] = query;
            if (!string.IsNullOrEmpty(searchParams))
                body["params"] = searchParams;
        }
        else
        {
            body["continuation"] = pageToken;
        }

        var response = await PostAsync("search", body, cancellationToken, region);
        if (response.IsFailure)
            return Result<PagedResult<SearchResult>>.Failure(response.Error);

        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            var results = new List<SearchResult>();
            var seen = new HashSet<string>();
            foreach (var renderer in InnerTubeJson.FindAll(doc.RootElement, "videoRenderer"))
            {
                if (InnerTubeJson.ParseVideoRenderer(renderer) is { } item && seen.Add(item.Id))
                    results.Add(item);
            }
            var next = InnerTubeJson.ListContinuationToken(doc.RootElement);
            _logger.LogInformation("InnerTube search returned {Count} items for '{Query}'.", results.Count, query);

            return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
            {
                Items = results,
                PageIndex = 1,
                HasNextPage = results.Count > 0 && next is not null,
                NextPageToken = results.Count > 0 ? next : null,
                TotalEstimatedResults = results.Count
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "InnerTube search returned unreadable JSON for '{Query}'.", query);
            return Result<PagedResult<SearchResult>>.Failure("INNERTUBE_SEARCH_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    /// <summary>Related videos, the comments token and the description of a video's watch page.</summary>
    public async Task<Result<WatchNextInfo>> GetWatchNextAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("next", new JsonObject { ["videoId"] = videoId }, cancellationToken);
        if (response.IsFailure)
            return Result<WatchNextInfo>.Failure(response.Error);
        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            return Result<WatchNextInfo>.Success(InnerTubeJson.ParseWatchNext(doc.RootElement));
        }
        catch (JsonException ex)
        {
            return Result<WatchNextInfo>.Failure("INNERTUBE_NEXT_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    /// <summary>A page of comments (or of replies, when <paramref name="isReplyPage"/>) for a continuation token.</summary>
    public async Task<Result<CommentPage>> GetCommentsAsync(string continuationToken, bool isReplyPage, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("next", new JsonObject { ["continuation"] = continuationToken }, cancellationToken);
        if (response.IsFailure)
            return Result<CommentPage>.Failure(response.Error);
        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            return Result<CommentPage>.Success(InnerTubeJson.ParseComments(doc.RootElement, isReplyPage));
        }
        catch (JsonException ex)
        {
            return Result<CommentPage>.Failure("INNERTUBE_COMMENTS_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    /// <summary>
    /// The videos on a YouTube page such as a channel's home (the Music, Sports, News, Gaming and Live
    /// destination channels), as seen from <paramref name="region"/>, and the playlists it links to.
    /// </summary>
    public async Task<Result<(IReadOnlyList<SearchResult> Videos, IReadOnlyList<SearchResult> Playlists)>> BrowseAsync(
        string browseId, string? region, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("browse", new JsonObject { ["browseId"] = browseId }, cancellationToken, region);
        if (response.IsFailure)
            return Result<(IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>)>.Failure(response.Error);
        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            var videos = new List<SearchResult>();
            var playlists = new List<SearchResult>();
            var seen = new HashSet<string>();
            foreach (var lockup in InnerTubeJson.FindAll(doc.RootElement, "lockupViewModel"))
            {
                if (InnerTubeJson.ParseLockup(lockup) is { } video && seen.Add(video.Id))
                    videos.Add(video);
                else if (InnerTubeJson.ParsePlaylistLockup(lockup) is { } playlist && seen.Add(playlist.Id))
                    playlists.Add(playlist);
            }
            foreach (var name in new[] { "videoRenderer", "gridVideoRenderer" })
            {
                foreach (var renderer in InnerTubeJson.FindAll(doc.RootElement, name))
                {
                    if (InnerTubeJson.ParseVideoRenderer(renderer) is { } video && seen.Add(video.Id))
                        videos.Add(video);
                }
            }
            return Result<(IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>)>.Success((videos, playlists));
        }
        catch (JsonException ex)
        {
            return Result<(IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>)>.Failure("INNERTUBE_BROWSE_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    /// <summary>One page of a YouTube playlist: the first when <paramref name="continuation"/> is null.</summary>
    public async Task<Result<YouTubePlaylistPage>> GetPlaylistAsync(string playlistId, string? continuation, CancellationToken cancellationToken = default)
    {
        var body = string.IsNullOrEmpty(continuation)
            ? new JsonObject { ["browseId"] = "VL" + playlistId }
            : new JsonObject { ["continuation"] = continuation };
        var response = await PostAsync("browse", body, cancellationToken);
        if (response.IsFailure)
            return Result<YouTubePlaylistPage>.Failure(response.Error);
        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            var page = InnerTubeJson.ParsePlaylistPage(doc.RootElement, playlistId);
            if (continuation is null && page.Videos.Count == 0)
                return Result<YouTubePlaylistPage>.Failure("PLAYLIST_EMPTY", "This playlist is private, empty or no longer exists.");
            return Result<YouTubePlaylistPage>.Success(page);
        }
        catch (JsonException ex)
        {
            return Result<YouTubePlaylistPage>.Failure("INNERTUBE_PLAYLIST_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    /// <summary>Search for playlists instead of videos.</summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchPlaylistsAsync(string query, string? pageToken, CancellationToken cancellationToken = default)
    {
        var body = string.IsNullOrWhiteSpace(pageToken)
            // "EgIQAw==" is YouTube's "Type: Playlist" search filter.
            ? new JsonObject { ["query"] = query, ["params"] = "EgIQAw==" }
            : new JsonObject { ["continuation"] = pageToken };
        var response = await PostAsync("search", body, cancellationToken);
        if (response.IsFailure)
            return Result<PagedResult<SearchResult>>.Failure(response.Error);
        try
        {
            using var doc = JsonDocument.Parse(response.Value);
            var results = new List<SearchResult>();
            var seen = new HashSet<string>();
            foreach (var lockup in InnerTubeJson.FindAll(doc.RootElement, "lockupViewModel"))
            {
                if (InnerTubeJson.ParsePlaylistLockup(lockup) is { } item && seen.Add(item.Id))
                    results.Add(item);
            }
            var next = results.Count > 0 ? InnerTubeJson.ListContinuationToken(doc.RootElement) : null;
            return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
            {
                Items = results,
                PageIndex = 1,
                HasNextPage = next is not null,
                NextPageToken = next,
                TotalEstimatedResults = results.Count
            });
        }
        catch (JsonException ex)
        {
            return Result<PagedResult<SearchResult>>.Failure("INNERTUBE_SEARCH_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    private async Task<Result<string>> PostAsync(string endpoint, JsonObject body, CancellationToken cancellationToken, string? region = null)
    {
        body["context"] = JsonSerializer.SerializeToNode(CreateWebContext(region));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{endpoint}?prettyPrint=false")
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("InnerTube {Endpoint} returned HTTP {Status}.", endpoint, resp.StatusCode);
                return Result<string>.Failure("INNERTUBE_HTTP_ERROR", $"InnerTube {endpoint} returned status {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
            }
            return Result<string>.Success(await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure("INNERTUBE_TIMEOUT", $"InnerTube {endpoint} timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "InnerTube {Endpoint} request failed.", endpoint);
            return Result<string>.Failure("INNERTUBE_NETWORK", ex.Message, ex.ToString(), ex);
        }
    }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    public async Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId.Value))
            return Result<VideoDetails>.Failure("INVALID_ID", "Video ID cannot be empty.");

        var requestUri = $"{BaseUrl}next?prettyPrint=false";
        _logger.LogInformation("Fetching InnerTube video details for {Id}...", videoId.Value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            var payload = new InnerTubeNextRequest
            {
                Context = CreateWebContext(),
                VideoId = videoId.Value
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return Result<VideoDetails>.Failure("INNERTUBE_HTTP_ERROR", $"Details request failed with {resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string title = "Untitled Video";
            string author = "Unknown Channel";
            string channelId = string.Empty;
            string description = string.Empty;

            if (root.TryGetProperty("contents", out var contents) &&
                contents.TryGetProperty("twoColumnWatchNextResults", out var watchNext) &&
                watchNext.TryGetProperty("results", out var resultsElem) &&
                resultsElem.TryGetProperty("results", out var innerResults) &&
                innerResults.TryGetProperty("contents", out var contentArray) &&
                contentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentArray.EnumerateArray())
                {
                    if (item.TryGetProperty("videoPrimaryInfoRenderer", out var primary))
                    {
                        if (primary.TryGetProperty("title", out var titleProp) &&
                            titleProp.TryGetProperty("runs", out var titleRuns) &&
                            titleRuns.ValueKind == JsonValueKind.Array &&
                            titleRuns.GetArrayLength() > 0)
                        {
                            title = titleRuns[0].GetProperty("text").GetString() ?? title;
                        }
                    }

                    if (item.TryGetProperty("videoSecondaryInfoRenderer", out var secondary))
                    {
                        if (secondary.TryGetProperty("owner", out var owner) &&
                            owner.TryGetProperty("videoOwnerRenderer", out var vor))
                        {
                            if (vor.TryGetProperty("title", out var ownerTitle) &&
                                ownerTitle.TryGetProperty("runs", out var ownerRuns) &&
                                ownerRuns.ValueKind == JsonValueKind.Array &&
                                ownerRuns.GetArrayLength() > 0)
                            {
                                author = ownerRuns[0].GetProperty("text").GetString() ?? author;
                            }

                            if (vor.TryGetProperty("navigationEndpoint", out var nav) &&
                                nav.TryGetProperty("browseEndpoint", out var browse) &&
                                browse.TryGetProperty("browseId", out var bId))
                            {
                                channelId = bId.GetString() ?? string.Empty;
                            }
                        }

                        if (secondary.TryGetProperty("description", out var descProp) &&
                            descProp.TryGetProperty("runs", out var descRuns) &&
                            descRuns.ValueKind == JsonValueKind.Array)
                        {
                            var parts = new List<string>();
                            foreach (var run in descRuns.EnumerateArray())
                            {
                                if (run.TryGetProperty("text", out var t))
                                {
                                    parts.Add(t.GetString() ?? "");
                                }
                            }
                            description = string.Concat(parts);
                        }
                    }
                }
            }

            return Result<VideoDetails>.Success(new VideoDetails
            {
                Id = videoId,
                Title = title,
                Description = description,
                Duration = TimeSpan.Zero,
                ViewCount = 0,
                Channel = new ChannelInfo
                {
                    Id = channelId,
                    Name = author
                },
                ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId.Value}/hqdefault.jpg"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load InnerTube video details for {Id}.", videoId.Value);
            return Result<VideoDetails>.Failure("INNERTUBE_DETAILS_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<StreamResolutionResult>> ResolveStreamsAsync(
        YouTubeVideoId videoId,
        QualityPreference qualityPreference = QualityPreference.Auto,
        bool audioOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId.Value))
            return Result<StreamResolutionResult>.Failure("INVALID_ID", "Video ID cannot be empty.");

        var requestUri = $"{BaseUrl}player?prettyPrint=false";
        _logger.LogInformation("Attempting InnerTube player format extraction for {Id}...", videoId.Value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            var payload = new InnerTubePlayerRequest
            {
                Context = CreateWebContext(),
                VideoId = videoId.Value
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(payload)
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("InnerTube /player endpoint returned HTTP {Status} for {Id}.", resp.StatusCode, videoId.Value);
                return Result<StreamResolutionResult>.Failure("INNERTUBE_HTTP_ERROR", $"Player endpoint returned {resp.StatusCode}");
            }

            var playerResponse = await resp.Content.ReadFromJsonAsync<InnerTubePlayerResponse>(cancellationToken: cts.Token);
            if (playerResponse == null)
            {
                return Result<StreamResolutionResult>.Failure("INVALID_PLAYER_JSON", "Empty player response.");
            }

            var playStatus = playerResponse.PlayabilityStatus?.Status;
            if (!string.Equals(playStatus, "OK", StringComparison.OrdinalIgnoreCase))
            {
                var reason = playerResponse.PlayabilityStatus?.Reason ?? playStatus ?? "Playability check not OK";
                _logger.LogWarning("InnerTube player playability status '{Status}' for {Id}: {Reason}", playStatus, videoId.Value, reason);
                return Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(videoId.Value, reason));
            }

            // Find playable progressive or adaptive formats with direct URLs
            var formats = playerResponse.StreamingData?.Formats ?? new List<InnerTubeFormat>();
            var progressive = formats.FirstOrDefault(f => !string.IsNullOrEmpty(f.Url) && (f.MimeType?.Contains("mp4") ?? false));

            if (progressive != null && !string.IsNullOrEmpty(progressive.Url))
            {
                var candidate = new StreamCandidate
                {
                    Url = progressive.Url,
                    Format = "mp4",
                    Height = progressive.Height ?? 360,
                    Bitrate = progressive.Bitrate ?? 500_000,
                    IsAudioOnly = false,
                    QualityLabel = progressive.QualityLabel ?? $"{progressive.Height ?? 360}p",
                    IsProgressive = true
                };

                return Result<StreamResolutionResult>.Success(new StreamResolutionResult
                {
                    VideoId = videoId,
                    SelectedVideoStream = candidate,
                    SelectedAudioStream = null,
                    DirectPlaybackUrl = progressive.Url,
                    AudioTrackUrl = null,
                    Captions = ExtractCaptions(playerResponse.Captions),
                    IsLiveStream = false,
                    SourceProvider = BackendProviderType.InnerTube
                });
            }

            _logger.LogWarning("InnerTube player response for {Id} did not contain directly playable progressive URLs.", videoId.Value);
            return Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(videoId.Value, "No direct progressive media URLs returned by InnerTube."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InnerTube stream resolution error for {Id}.", videoId.Value);
            return Result<StreamResolutionResult>.Failure("INNERTUBE_STREAM_FAILED", ex.Message, ex.ToString(), ex);
        }
    }

    private static IReadOnlyList<CaptionTrack> ExtractCaptions(InnerTubeCaptions? captions)
    {
        var result = new List<CaptionTrack>();
        var tracks = captions?.PlayerCaptionsTracklistRenderer?.CaptionTracks;
        if (tracks == null) return result;

        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.BaseUrl) || string.IsNullOrWhiteSpace(t.LanguageCode))
                continue;

            string name = t.Name?.SimpleText
                ?? t.Name?.Runs?.FirstOrDefault()?.Text
                ?? t.LanguageCode;

            result.Add(new CaptionTrack
            {
                Url = t.BaseUrl,
                DisplayName = name,
                LanguageCode = t.LanguageCode,
                IsDefault = false
            });
        }

        return result;
    }
}
