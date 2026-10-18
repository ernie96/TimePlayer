using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.Invidious;

/// <summary>
/// Production-quality Invidious backend provider implementing video search, metadata retrieval,
/// and stream resolution as an independent fallback with multi-instance retry and rate-limit handling.
/// </summary>
public sealed class InvidiousBackendProvider : IBackendProvider, IVideoCatalogProvider, IStreamResolver
{
    private readonly HttpClient _httpClient;
    private readonly IInstanceHealthService _healthService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<InvidiousBackendProvider> _logger;

    public BackendProviderType ProviderType => BackendProviderType.Invidious;

    public InvidiousBackendProvider(
        HttpClient httpClient,
        IInstanceHealthService healthService,
        IConfigurationService configService,
        ILogger<InvidiousBackendProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

        var endpointPath = $"api/v1/search?q={Uri.EscapeDataString(query)}&page={page}&type=video";

        var responseResult = await ExecuteWithRetryAsync<List<InvidiousSearchItem>>(
            endpointPath,
            cancellationToken);

        if (responseResult.IsFailure)
            return Result<PagedResult<SearchResult>>.Failure(responseResult.Error);

        var results = MapVideoItems(responseResult.Value);

        var hasNextPage = results.Count > 0;
        var paged = new PagedResult<SearchResult>
        {
            Items = results,
            PageIndex = page,
            HasNextPage = hasNextPage,
            NextPageToken = hasNextPage ? (page + 1).ToString() : null,
            TotalEstimatedResults = results.Count
        };

        return Result<PagedResult<SearchResult>>.Success(paged);
    }

    /// <summary>Recommended videos from the watch page.</summary>
    public async Task<Result<IReadOnlyList<SearchResult>>> GetRelatedAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteWithRetryAsync<InvidiousRecommendedResponse>(
            $"api/v1/videos/{Uri.EscapeDataString(videoId)}?fields=recommendedVideos", cancellationToken, videoId);
        if (response.IsFailure)
            return Result<IReadOnlyList<SearchResult>>.Failure(response.Error);

        var related = (response.Value.RecommendedVideos ?? new())
            .Where(v => !string.IsNullOrWhiteSpace(v.VideoId))
            .Select(v => new SearchResult
            {
                Id = v.VideoId!,
                Title = v.Title ?? "Untitled video",
                ChannelTitle = v.Author ?? "Unknown channel",
                ChannelId = v.AuthorId,
                Duration = v.LengthSeconds is > 0 ? TimeSpan.FromSeconds(v.LengthSeconds.Value) : null,
                UploadDateText = v.PublishedText
            })
            .ToList();
        return Result<IReadOnlyList<SearchResult>>.Success(related);
    }

    /// <summary>A page of comments, or of replies when <paramref name="continuation"/> is a reply token.</summary>
    public async Task<Result<CommentPage>> GetCommentsAsync(string videoId, string? continuation, bool isReplyPage, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/comments/{Uri.EscapeDataString(videoId)}";
        if (!string.IsNullOrWhiteSpace(continuation))
            path += $"?continuation={Uri.EscapeDataString(continuation)}";

        var response = await ExecuteWithRetryAsync<InvidiousCommentsResponse>(path, cancellationToken, videoId);
        if (response.IsFailure)
            return Result<CommentPage>.Failure(response.Error);

        var comments = (response.Value.Comments ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .Select(c => new Comment
            {
                Id = c.CommentId ?? Guid.NewGuid().ToString("N"),
                Author = c.Author ?? "Unknown",
                AuthorChannelId = c.AuthorId,
                Text = c.Content!.Trim(),
                PublishedText = c.PublishedText,
                LikesText = c.LikeCount is > 0 ? FormatLikes(c.LikeCount.Value) : null,
                ReplyCount = c.Replies?.ReplyCount ?? 0,
                RepliesToken = c.Replies?.Continuation,
                IsPinned = c.IsPinned == true,
                IsCreator = c.AuthorIsChannelOwner == true,
                IsReply = isReplyPage,
                Source = CommentSource.Invidious
            })
            .ToList();
        return Result<CommentPage>.Success(new CommentPage(comments, response.Value.Continuation, CommentSource.Invidious));
    }

    private static string FormatLikes(long count) => count switch
    {
        1 => "1 like",
        >= 1_000_000 => $"{count / 1_000_000.0:0.#} million likes",
        >= 1_000 => $"{count / 1_000.0:0.#} thousand likes",
        _ => $"{count} likes"
    };

    /// <summary>A channel's uploads, newest first, one page per call.</summary>
    public async Task<Result<PagedResult<SearchResult>>> GetChannelVideosAsync(string channelId, string? continuation, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/channels/{Uri.EscapeDataString(channelId)}/videos";
        if (!string.IsNullOrWhiteSpace(continuation))
            path += $"?continuation={Uri.EscapeDataString(continuation)}";

        var response = await ExecuteWithRetryAsync<InvidiousChannelVideosResponse>(path, cancellationToken);
        if (response.IsFailure)
            return Result<PagedResult<SearchResult>>.Failure(response.Error);

        var items = MapVideoItems(response.Value.Videos);
        var next = items.Count > 0 ? response.Value.Continuation : null;
        return Result<PagedResult<SearchResult>>.Success(new PagedResult<SearchResult>
        {
            Items = items,
            HasNextPage = !string.IsNullOrEmpty(next),
            NextPageToken = next
        });
    }

    /// <summary>One page (about 100 videos) of a playlist; <paramref name="page"/> starts at 1.</summary>
    public async Task<Result<YouTubePlaylistPage>> GetPlaylistAsync(string playlistId, int page, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteWithRetryAsync<InvidiousPlaylistResponse>(
            $"api/v1/playlists/{Uri.EscapeDataString(playlistId)}?page={page}", cancellationToken);
        if (response.IsFailure)
            return Result<YouTubePlaylistPage>.Failure(response.Error);
        var videos = MapVideoItems(response.Value.Videos);
        var total = response.Value.VideoCount ?? 0;
        // Invidious pages hold up to 100 (sometimes 200) videos; keep going while a page is full and more remain.
        var next = videos.Count >= 100 && page * videos.Count < total ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Result<YouTubePlaylistPage>.Success(new YouTubePlaylistPage(
            playlistId, response.Value.Title ?? string.Empty, response.Value.Author, videos, next));
    }

    /// <summary>Trending videos; <paramref name="type"/> is null, "music", "gaming" or "movies".</summary>
    public async Task<Result<IReadOnlyList<SearchResult>>> GetTrendingAsync(
        string? type,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(type)) query.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrWhiteSpace(region)) query.Add($"region={Uri.EscapeDataString(region)}");
        var path = "api/v1/trending" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return await GetVideoListAsync(path, cancellationToken);
    }

    /// <summary>The instance's "popular" page (recent uploads popular with its users).</summary>
    public Task<Result<IReadOnlyList<SearchResult>>> GetPopularAsync(CancellationToken cancellationToken = default) =>
        GetVideoListAsync("api/v1/popular", cancellationToken);

    private async Task<Result<IReadOnlyList<SearchResult>>> GetVideoListAsync(string path, CancellationToken cancellationToken)
    {
        var response = await ExecuteWithRetryAsync<List<InvidiousSearchItem>>(path, cancellationToken);
        if (response.IsFailure)
            return Result<IReadOnlyList<SearchResult>>.Failure(response.Error);

        var results = MapVideoItems(response.Value);
        if (results.Count == 0)
            return Result<IReadOnlyList<SearchResult>>.Failure("EMPTY_FEED", "The featured list is empty right now.");
        return Result<IReadOnlyList<SearchResult>>.Success(results);
    }

    private static List<SearchResult> MapVideoItems(List<InvidiousSearchItem>? items)
    {
        var results = new List<SearchResult>();
        var seen = new HashSet<string>();
        foreach (var item in items ?? new List<InvidiousSearchItem>())
        {
            if (string.IsNullOrWhiteSpace(item.VideoId) || !seen.Add(item.VideoId))
                continue;

            // Invidious returns type="video", "playlist", "channel", "shortVideo"
            if (!string.IsNullOrEmpty(item.Type) &&
                !string.Equals(item.Type, "video", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Type, "shortVideo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bestThumbnail = item.VideoThumbnails?.OrderByDescending(t => t.Width ?? 0).FirstOrDefault()?.Url;
            bool isLive = item.LiveNow == true;

            results.Add(new SearchResult
            {
                Id = item.VideoId,
                Title = item.Title ?? "Untitled Video",
                ChannelTitle = item.Author ?? "Unknown Channel",
                ChannelId = item.AuthorId,
                Duration = !isLive && item.LengthSeconds is > 0 ? TimeSpan.FromSeconds(item.LengthSeconds.Value) : null,
                // Live streams report 0 views; leave it out rather than say "0 views".
                ViewCount = item.ViewCount is > 0 ? item.ViewCount : null,
                // Live streams report "0 seconds ago", which is noise for a screen reader.
                UploadDateText = isLive ? null : item.PublishedText,
                IsLive = isLive,
                IsShort = string.Equals(item.Type, "shortVideo", StringComparison.OrdinalIgnoreCase),
                ThumbnailUrl = bestThumbnail,
                DescriptionSnippet = item.Description
            });
        }
        return results;
    }

    public async Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var endpointPath = $"api/v1/videos/{videoId.Value}";
        var responseResult = await ExecuteWithRetryAsync<InvidiousVideoResponse>(
            endpointPath,
            cancellationToken,
            videoId.Value);

        if (responseResult.IsFailure)
            return Result<VideoDetails>.Failure(responseResult.Error);

        var videoResp = responseResult.Value;
        if (videoResp == null)
            return Result<VideoDetails>.Failure(AppError.VideoNotFound(videoId.Value));

        var avatarUrl = videoResp.AuthorThumbnails?.OrderByDescending(t => t.Width ?? 0).FirstOrDefault()?.Url;
        var channel = new ChannelInfo
        {
            Id = videoResp.AuthorId ?? videoId.Value,
            Name = videoResp.Author ?? "Unknown Channel",
            Url = videoResp.AuthorUrl,
            AvatarUrl = avatarUrl,
            IsVerified = videoResp.AuthorVerified == true
        };

        var bestThumbnail = videoResp.VideoThumbnails?.OrderByDescending(t => t.Width ?? 0).FirstOrDefault()?.Url;
        var allStreams = MapStreams(videoResp);
        var captions = MapCaptions(videoResp.Captions);

        var details = new VideoDetails
        {
            Id = videoId,
            Title = videoResp.Title ?? "Untitled Video",
            Description = videoResp.Description,
            Channel = channel,
            Duration = TimeSpan.FromSeconds(videoResp.LengthSeconds ?? 0),
            ViewCount = videoResp.ViewCount,
            LikeCount = videoResp.LikeCount,
            ThumbnailUrl = bestThumbnail,
            Streams = allStreams,
            Captions = captions,
            IsLive = videoResp.LiveNow == true
        };

        return Result<VideoDetails>.Success(details);
    }

    public async Task<Result<StreamResolutionResult>> ResolveStreamsAsync(
        YouTubeVideoId videoId,
        QualityPreference qualityPreference = QualityPreference.Auto,
        bool audioOnly = false,
        CancellationToken cancellationToken = default)
    {
        var detailsResult = await GetVideoDetailsAsync(videoId, cancellationToken);
        if (detailsResult.IsFailure)
            return Result<StreamResolutionResult>.Failure(detailsResult.Error);

        var details = detailsResult.Value;
        if (details.Streams.Count == 0)
        {
            return Result<StreamResolutionResult>.Failure(
                AppError.StreamUnavailable(videoId.Value, "Invidious response contained no playable streams."));
        }

        // Live stream handling
        if (details.IsLive)
        {
            var liveStream = details.Streams.FirstOrDefault(s => s.Format.Equals("hls", StringComparison.OrdinalIgnoreCase)) ??
                             details.Streams.FirstOrDefault();

            if (liveStream != null)
            {
                return Result<StreamResolutionResult>.Success(new StreamResolutionResult
                {
                    VideoId = videoId,
                    SelectedVideoStream = liveStream,
                    DirectPlaybackUrl = liveStream.Url,
                    IsLiveStream = true,
                    SourceProvider = BackendProviderType.Invidious
                });
            }
        }

        // 1. Audio-only mode
        if (audioOnly || qualityPreference == QualityPreference.AudioOnly)
        {
            var audioStream = details.Streams
                .Where(s => s.IsAudioOnly)
                .OrderByDescending(s => s.Bitrate ?? 0)
                .FirstOrDefault() ?? details.Streams.FirstOrDefault();

            if (audioStream == null)
            {
                return Result<StreamResolutionResult>.Failure(
                    AppError.StreamUnavailable(videoId.Value, "No audio stream found."));
            }

            return Result<StreamResolutionResult>.Success(new StreamResolutionResult
            {
                VideoId = videoId,
                SelectedVideoStream = audioStream,
                SelectedAudioStream = audioStream,
                DirectPlaybackUrl = audioStream.Url,
                Captions = details.Captions,
                AlternativeStreams = details.Streams,
                SourceProvider = BackendProviderType.Invidious
            });
        }

        // 2. Video mode: determine desired quality height
        int? targetHeight = qualityPreference switch
        {
            QualityPreference.P1080 => 1080,
            QualityPreference.P720 => 720,
            QualityPreference.P480 => 480,
            QualityPreference.P360 => 360,
            _ => null // Auto = highest
        };

        // Best audio candidate for DASH combining
        var bestAudio = details.Streams
            .Where(s => s.IsAudioOnly)
            .OrderByDescending(s => s.Bitrate ?? 0)
            .FirstOrDefault();

        // Prefer progressive streams or DASH video with companion audio; do not blindly choose mute video-only
        var videoCandidates = details.Streams
            .Where(s => !s.IsAudioOnly && (s.IsProgressive || bestAudio != null))
            .ToList();

        if (videoCandidates.Count == 0)
        {
            // Fallback to any non-audio stream if none matched
            videoCandidates = details.Streams.Where(s => !s.IsAudioOnly).ToList();
            if (videoCandidates.Count == 0)
            {
                videoCandidates = details.Streams.ToList();
            }
        }

        StreamCandidate? selectedVideo = null;

        if (targetHeight.HasValue)
        {
            // Prefer progressive stream first if it matches target height
            selectedVideo = videoCandidates
                .Where(s => s.Height == targetHeight.Value && s.IsProgressive)
                .OrderByDescending(s => s.Fps ?? 0)
                .FirstOrDefault() ??
                videoCandidates
                .Where(s => s.Height == targetHeight.Value)
                .OrderByDescending(s => s.Fps ?? 0)
                .FirstOrDefault() ??
                videoCandidates
                .Where(s => s.Height <= targetHeight.Value)
                .OrderByDescending(s => s.Height ?? 0)
                .FirstOrDefault();
        }

        // If no match or Auto, choose highest resolution
        selectedVideo ??= videoCandidates
            .OrderByDescending(s => s.Height ?? 0)
            .ThenByDescending(s => s.Fps ?? 0)
            .ThenByDescending(s => s.Bitrate ?? 0)
            .FirstOrDefault() ?? details.Streams.First();

        string? audioTrackUrl = null;
        if (!selectedVideo.IsProgressive && bestAudio != null)
        {
            audioTrackUrl = bestAudio.Url;
        }

        return Result<StreamResolutionResult>.Success(new StreamResolutionResult
        {
            VideoId = videoId,
            SelectedVideoStream = selectedVideo,
            SelectedAudioStream = selectedVideo.IsProgressive ? null : bestAudio,
            DirectPlaybackUrl = selectedVideo.Url,
            AudioTrackUrl = audioTrackUrl,
            Captions = details.Captions,
            AlternativeStreams = details.Streams,
            SourceProvider = BackendProviderType.Invidious
        });
    }

    private async Task<Result<T>> ExecuteWithRetryAsync<T>(
        string endpointPath,
        CancellationToken cancellationToken,
        string? targetVideoId = null) where T : class
    {
        var config = _configService.CurrentConfig.Backend;
        int maxAttempts = Math.Max(1, config.MaxRetriesPerProvider + 1);
        int attempts = 0;
        var triedInstances = new HashSet<Uri>();

        while (attempts < maxAttempts)
        {
            attempts++;
            var instance = await _healthService.SelectBestInstanceAsync(BackendProviderType.Invidious, cancellationToken);
            if (instance == null || !triedInstances.Add(instance.BaseUri))
            {
                break;
            }

            var requestUri = new Uri(instance.BaseUri, endpointPath);
            _logger.LogInformation("Invidious API attempt {Attempt}/{Max}: GET {Uri}", attempts, maxAttempts, requestUri);

            var stopwatch = Stopwatch.StartNew();
            int timeoutSec = endpointPath.StartsWith("api/v1/search", StringComparison.OrdinalIgnoreCase)
                ? Math.Min(7, config.RequestTimeoutSeconds)
                : config.RequestTimeoutSeconds;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                stopwatch.Stop();

                // 1. Permanent Not Found (404)
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Invidious instance {Uri} returned 404 Not Found for {Path}", instance.BaseUri, endpointPath);
                    return Result<T>.Failure(AppError.VideoNotFound(targetVideoId ?? endpointPath));
                }

                // 2. Rate Limited (429)
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("Invidious instance {Uri} rate limited request (HTTP 429).", instance.BaseUri);
                    _healthService.RecordFailure(instance, "Rate limited (HTTP 429)");
                    continue; // Retry with next instance
                }

                // 3. Server Errors (5xx, etc.)
                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    _logger.LogWarning("Invidious instance {Uri} failed with {Error}", instance.BaseUri, errorMsg);
                    _healthService.RecordFailure(instance, errorMsg);
                    continue; // Retry with next instance
                }

                // 4. Success: Deserialize JSON
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                var parsed = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: timeoutCts.Token);
                if (parsed != null)
                {
                    _healthService.RecordSuccess(instance, stopwatch.Elapsed);
                    return Result<T>.Success(parsed);
                }

                _healthService.RecordFailure(instance, "Empty or invalid JSON body returned");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning("Invidious instance {Uri} timed out after {Sec}s.", instance.BaseUri, config.RequestTimeoutSeconds);
                _healthService.RecordFailure(instance, "Request timed out");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Exception calling Invidious instance {Uri}: {Message}", instance.BaseUri, ex.Message);
                _healthService.RecordFailure(instance, ex.Message);
            }
        }

        return Result<T>.Failure(AppError.AllInstancesFailed("Invidious", attempts));
    }

    private static List<StreamCandidate> MapStreams(InvidiousVideoResponse videoResp)
    {
        var list = new List<StreamCandidate>();

        // 1. Progressive streams (formatStreams: combined audio + video)
        if (videoResp.FormatStreams != null)
        {
            foreach (var f in videoResp.FormatStreams)
            {
                if (string.IsNullOrWhiteSpace(f.Url)) continue;

                var (height, width) = ParseResolution(f.Resolution);
                var bitrate = ParseBitrate(f.Bitrate);

                list.Add(new StreamCandidate
                {
                    Url = f.Url,
                    Format = f.Container?.ToLowerInvariant() ?? "mp4",
                    QualityLabel = f.QualityLabel ?? (height.HasValue ? $"{height.Value}p" : f.Quality),
                    Height = height,
                    Width = width,
                    Bitrate = bitrate,
                    VideoCodec = f.Encoding,
                    IsAudioOnly = false,
                    IsVideoOnly = false,
                    IsProgressive = true
                });
            }
        }

        // 2. Adaptive DASH streams (adaptiveFormats: separate audio or video)
        if (videoResp.AdaptiveFormats != null)
        {
            foreach (var a in videoResp.AdaptiveFormats)
            {
                if (string.IsNullOrWhiteSpace(a.Url)) continue;

                var isAudio = a.Type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
                              !string.IsNullOrEmpty(a.AudioQuality);

                var (height, width) = ParseResolution(a.Resolution);
                var bitrate = ParseBitrate(a.Bitrate);

                if (isAudio)
                {
                    list.Add(new StreamCandidate
                    {
                        Url = a.Url,
                        Format = a.Container?.ToLowerInvariant() ?? "m4a",
                        QualityLabel = a.AudioQuality,
                        Bitrate = bitrate,
                        AudioBitrate = bitrate,
                        AudioCodec = a.Encoding,
                        IsAudioOnly = true,
                        IsVideoOnly = false,
                        IsProgressive = false
                    });
                }
                else
                {
                    list.Add(new StreamCandidate
                    {
                        Url = a.Url,
                        Format = a.Container?.ToLowerInvariant() ?? "mp4",
                        QualityLabel = a.QualityLabel ?? (height.HasValue ? $"{height.Value}p" : null),
                        Height = height,
                        Width = width,
                        Fps = a.Fps,
                        Bitrate = bitrate,
                        VideoCodec = a.Encoding,
                        IsAudioOnly = false,
                        IsVideoOnly = true,
                        IsProgressive = false
                    });
                }
            }
        }

        // 3. Live HLS manifest
        if (!string.IsNullOrWhiteSpace(videoResp.HlsUrl))
        {
            list.Add(new StreamCandidate
            {
                Url = videoResp.HlsUrl,
                Format = "hls",
                QualityLabel = "Live HLS",
                IsAudioOnly = false,
                IsVideoOnly = false,
                IsProgressive = true
            });
        }

        return list;
    }

    private static List<CaptionTrack> MapCaptions(List<InvidiousCaption>? captions)
    {
        var list = new List<CaptionTrack>();
        if (captions == null) return list;

        foreach (var c in captions)
        {
            if (string.IsNullOrWhiteSpace(c.Url) || string.IsNullOrWhiteSpace(c.LanguageCode))
                continue;

            list.Add(new CaptionTrack
            {
                Url = c.Url,
                LanguageCode = c.LanguageCode,
                DisplayName = c.Label ?? c.LanguageCode,
                Format = "vtt"
            });
        }

        return list;
    }

    private static (int? height, int? width) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return (null, null);

        var parts = resolution.Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var w) &&
            int.TryParse(parts[1], out var h))
        {
            return (h, w);
        }

        // Sometimes resolution is just "720p" or "1080"
        var digitsOnly = new string(resolution.Where(char.IsDigit).ToArray());
        if (int.TryParse(digitsOnly, out var height))
        {
            return (height, null);
        }

        return (null, null);
    }

    private static long? ParseBitrate(JsonElement? bitrateElement)
    {
        if (!bitrateElement.HasValue) return null;

        var el = bitrateElement.Value;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var num))
            return num;

        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var parsed))
            return parsed;

        return null;
    }
}
