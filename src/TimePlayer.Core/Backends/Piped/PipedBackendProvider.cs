using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.Piped;

/// <summary>
/// Production-quality Piped backend provider implementing video search, metadata retrieval,
/// and stream resolution with multi-instance retry, failover, and rate-limit handling.
/// </summary>
public sealed class PipedBackendProvider : IBackendProvider, IVideoCatalogProvider, IStreamResolver
{
    private readonly HttpClient _httpClient;
    private readonly IInstanceHealthService _healthService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<PipedBackendProvider> _logger;

    public BackendProviderType ProviderType => BackendProviderType.Piped;

    public PipedBackendProvider(
        HttpClient httpClient,
        IInstanceHealthService healthService,
        IConfigurationService configService,
        ILogger<PipedBackendProvider> logger)
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

        var endpointPath = string.IsNullOrWhiteSpace(pageToken)
            ? $"search?q={Uri.EscapeDataString(query)}&filter=videos"
            : $"nextpage/search?nextpage={Uri.EscapeDataString(pageToken)}&q={Uri.EscapeDataString(query)}&filter=videos";

        var responseResult = await ExecuteWithRetryAsync<PipedSearchResponse>(
            endpointPath,
            cancellationToken);

        if (responseResult.IsFailure)
            return Result<PagedResult<SearchResult>>.Failure(responseResult.Error);

        var searchResponse = responseResult.Value;
        var results = new List<SearchResult>();

        if (searchResponse?.Items != null)
        {
            foreach (var item in searchResponse.Items)
            {
                // Map items (filter type == stream or any item having video URL)
                if (string.Equals(item.Type, "channel", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Type, "playlist", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var videoId = ExtractVideoId(item.Url);
                if (videoId == null)
                    continue;

                results.Add(new SearchResult
                {
                    Id = videoId.Value.Value,
                    Title = item.Title ?? "Untitled Video",
                    ChannelTitle = item.UploaderName ?? "Unknown Channel",
                    ChannelId = ExtractChannelId(item.UploaderUrl),
                    Duration = item.DurationSeconds.HasValue ? TimeSpan.FromSeconds(item.DurationSeconds.Value) : null,
                    ViewCount = item.Views,
                    UploadDateText = item.UploadedDate,
                    ThumbnailUrl = item.Thumbnail
                });
            }
        }

        var paged = new PagedResult<SearchResult>
        {
            Items = results,
            PageIndex = 1,
            HasNextPage = !string.IsNullOrEmpty(searchResponse?.NextPage),
            NextPageToken = searchResponse?.NextPage,
            TotalEstimatedResults = results.Count
        };

        return Result<PagedResult<SearchResult>>.Success(paged);
    }

    public async Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        var endpointPath = $"streams/{videoId.Value}";
        var responseResult = await ExecuteWithRetryAsync<PipedStreamsResponse>(
            endpointPath,
            cancellationToken,
            videoId.Value);

        if (responseResult.IsFailure)
            return Result<VideoDetails>.Failure(responseResult.Error);

        var streamResp = responseResult.Value;
        if (streamResp == null)
            return Result<VideoDetails>.Failure(AppError.VideoNotFound(videoId.Value));

        var channel = new ChannelInfo
        {
            Id = ExtractChannelId(streamResp.UploaderUrl) ?? videoId.Value,
            Name = streamResp.Uploader ?? "Unknown Channel",
            Url = streamResp.UploaderUrl,
            AvatarUrl = streamResp.UploaderAvatar,
            IsVerified = streamResp.UploaderVerified == true
        };

        var allStreams = MapStreams(streamResp);
        var captions = MapSubtitles(streamResp.Subtitles);

        var details = new VideoDetails
        {
            Id = videoId,
            Title = streamResp.Title ?? "Untitled Video",
            Description = streamResp.Description,
            Channel = channel,
            Duration = TimeSpan.FromSeconds(streamResp.Duration ?? 0),
            ViewCount = streamResp.Views,
            LikeCount = streamResp.Likes,
            ThumbnailUrl = streamResp.ThumbnailUrl,
            Streams = allStreams,
            Captions = captions,
            IsLive = streamResp.Livestream == true
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
                AppError.StreamUnavailable(videoId.Value, "Piped response contained no playable streams."));
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
                    SourceProvider = BackendProviderType.Piped
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
                SourceProvider = BackendProviderType.Piped
            });
        }

        // 2. Video mode: determine desired resolution height
        int? targetHeight = qualityPreference switch
        {
            QualityPreference.P1080 => 1080,
            QualityPreference.P720 => 720,
            QualityPreference.P480 => 480,
            QualityPreference.P360 => 360,
            _ => null // Auto = highest quality
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
            // Try exact match or closest lower
            selectedVideo = videoCandidates
                .Where(s => s.Height == targetHeight.Value)
                .OrderByDescending(s => s.Fps ?? 0)
                .ThenByDescending(s => s.Bitrate ?? 0)
                .FirstOrDefault() ??
                videoCandidates
                .Where(s => s.Height <= targetHeight.Value)
                .OrderByDescending(s => s.Height ?? 0)
                .ThenByDescending(s => s.Fps ?? 0)
                .FirstOrDefault();
        }

        // If no match or Auto, pick highest resolution and bitrate
        selectedVideo ??= videoCandidates
            .OrderByDescending(s => s.Height ?? 0)
            .ThenByDescending(s => s.Fps ?? 0)
            .ThenByDescending(s => s.Bitrate ?? 0)
            .FirstOrDefault() ?? details.Streams.First();

        // Check if stream is progressive (contains both audio and video)
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
            SourceProvider = BackendProviderType.Piped
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
            var instance = await _healthService.SelectBestInstanceAsync(BackendProviderType.Piped, cancellationToken);
            if (instance == null || !triedInstances.Add(instance.BaseUri))
            {
                // No more distinct instances available
                break;
            }

            var requestUri = new Uri(instance.BaseUri, endpointPath);
            _logger.LogInformation("Piped API attempt {Attempt}/{Max}: GET {Uri}", attempts, maxAttempts, requestUri);

            var stopwatch = Stopwatch.StartNew();
            int timeoutSec = endpointPath.StartsWith("search", StringComparison.OrdinalIgnoreCase) || endpointPath.StartsWith("nextpage", StringComparison.OrdinalIgnoreCase)
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
                    _logger.LogWarning("Piped instance {Uri} returned 404 Not Found for {Path}", instance.BaseUri, endpointPath);
                    return Result<T>.Failure(AppError.VideoNotFound(targetVideoId ?? endpointPath));
                }

                // 2. Rate Limited (429)
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("Piped instance {Uri} rate limited request (HTTP 429).", instance.BaseUri);
                    _healthService.RecordFailure(instance, "Rate limited (HTTP 429)");
                    continue; // Retry with next instance
                }

                // 3. Server Errors (5xx, 502, 503, 526, etc.)
                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    _logger.LogWarning("Piped instance {Uri} failed with {Error}", instance.BaseUri, errorMsg);
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
                _logger.LogWarning("Piped instance {Uri} timed out after {Sec}s.", instance.BaseUri, config.RequestTimeoutSeconds);
                _healthService.RecordFailure(instance, "Request timed out");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Exception calling Piped instance {Uri}: {Message}", instance.BaseUri, ex.Message);
                _healthService.RecordFailure(instance, ex.Message);
            }
        }

        return Result<T>.Failure(AppError.AllInstancesFailed("Piped", attempts));
    }

    private static List<StreamCandidate> MapStreams(PipedStreamsResponse streamResp)
    {
        var list = new List<StreamCandidate>();

        // Video streams
        if (streamResp.VideoStreams != null)
        {
            foreach (var v in streamResp.VideoStreams)
            {
                if (string.IsNullOrWhiteSpace(v.Url)) continue;

                var isProgressive = v.VideoOnly == false;
                list.Add(new StreamCandidate
                {
                    Url = v.Url,
                    Format = v.Format?.ToLowerInvariant() ?? "mp4",
                    QualityLabel = v.Quality ?? (v.Height.HasValue ? $"{v.Height}p" : null),
                    Height = v.Height,
                    Width = v.Width,
                    Fps = v.Fps,
                    Bitrate = v.Bitrate,
                    VideoCodec = v.Codec,
                    IsAudioOnly = false,
                    IsVideoOnly = v.VideoOnly ?? true,
                    IsProgressive = isProgressive
                });
            }
        }

        // Audio streams
        if (streamResp.AudioStreams != null)
        {
            foreach (var a in streamResp.AudioStreams)
            {
                if (string.IsNullOrWhiteSpace(a.Url)) continue;

                list.Add(new StreamCandidate
                {
                    Url = a.Url,
                    Format = a.Format?.ToLowerInvariant() ?? "m4a",
                    QualityLabel = a.Quality,
                    Bitrate = a.Bitrate,
                    AudioBitrate = a.Bitrate,
                    AudioCodec = a.Codec,
                    IsAudioOnly = true,
                    IsVideoOnly = false,
                    IsProgressive = false
                });
            }
        }

        // HLS live stream
        if (!string.IsNullOrWhiteSpace(streamResp.Hls))
        {
            list.Add(new StreamCandidate
            {
                Url = streamResp.Hls,
                Format = "hls",
                QualityLabel = "Live HLS",
                IsAudioOnly = false,
                IsVideoOnly = false,
                IsProgressive = true
            });
        }

        return list;
    }

    private static List<CaptionTrack> MapSubtitles(List<PipedSubtitle>? subtitles)
    {
        var list = new List<CaptionTrack>();
        if (subtitles == null) return list;

        foreach (var sub in subtitles)
        {
            if (string.IsNullOrWhiteSpace(sub.Url) || string.IsNullOrWhiteSpace(sub.Code))
                continue;

            list.Add(new CaptionTrack
            {
                Url = sub.Url,
                LanguageCode = sub.Code,
                DisplayName = sub.Name ?? sub.Code,
                Format = sub.MimeType?.Contains("vtt", StringComparison.OrdinalIgnoreCase) == true ? "vtt" : "ttml",
                IsAutoGenerated = sub.AutoGenerated == true
            });
        }

        return list;
    }

    private static YouTubeVideoId? ExtractVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (YouTubeUrl.TryExtractVideoId(url, out var vid, out _))
            return vid;

        // Piped sometimes returns "/watch?v=ID"
        if (url.StartsWith("/watch?v=", StringComparison.OrdinalIgnoreCase))
        {
            var rawId = url[9..];
            if (rawId.Contains('&')) rawId = rawId[..rawId.IndexOf('&')];
            if (YouTubeVideoId.TryParse(rawId, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string? ExtractChannelId(string? channelUrl)
    {
        if (string.IsNullOrWhiteSpace(channelUrl)) return null;
        if (channelUrl.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase))
            return channelUrl[9..];
        if (channelUrl.StartsWith("/c/", StringComparison.OrdinalIgnoreCase))
            return channelUrl[3..];
        if (channelUrl.StartsWith("/user/", StringComparison.OrdinalIgnoreCase))
            return channelUrl[6..];

        return channelUrl.TrimStart('/');
    }
}
