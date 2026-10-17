using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Common.Process;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.YtDlp;

/// <summary>
/// Production-grade yt-dlp backend provider implementing video search, metadata retrieval,
/// format extraction, and media downloading as an application-managed local fallback.
/// </summary>
public sealed class YtDlpBackendProvider : IVideoCatalogProvider, IStreamResolver
{
    private static readonly Regex DownloadProgressRegex = new(
        @"\[download\]\s+(?<pct>\d+(\.\d+)?)%\s+of\s+(?<size>[^\s]+)\s+(?:at\s+(?<speed>[^\s]+))?\s*(?:ETA\s+(?<eta>[^\s]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DestinationRegex = new(
        @"\[(?:download|ExtractAudio)\]\s+Destination:\s+(?<dest>.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IYtDlpManager _ytDlpManager;
    private readonly IProcessRunner _processRunner;
    private readonly IConfigurationService _configService;
    private readonly ILogger<YtDlpBackendProvider> _logger;

    public BackendProviderType ProviderType => BackendProviderType.YtDlpLocal;

    public YtDlpBackendProvider(
        IYtDlpManager ytDlpManager,
        IProcessRunner processRunner,
        IConfigurationService configService,
        ILogger<YtDlpBackendProvider> logger)
    {
        _ytDlpManager = ytDlpManager ?? throw new ArgumentNullException(nameof(ytDlpManager));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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

        if (!_ytDlpManager.IsInstalled)
        {
            var initResult = await _ytDlpManager.EnsureInstalledOrUpdatedAsync(cancellationToken);
            if (!initResult.IsSuccess)
                return Result<PagedResult<SearchResult>>.Failure(initResult.Error!);
        }

        var maxResults = _configService.CurrentConfig.Backend.YtDlpMaxSearchResults;
        if (maxResults <= 0) maxResults = 20;

        var args = new List<string>
        {
            "--dump-single-json",
            "--default-search", "ytsearch",
            "--flat-playlist",
            "--skip-download",
            "--no-warnings"
        };

        AppendCookieArgs(args);
        args.Add($"ytsearch{maxResults}:{query}");

        var timeout = TimeSpan.FromSeconds(_configService.CurrentConfig.Backend.YtDlpTimeoutSeconds);
        var options = new ProcessRunOptions { Timeout = timeout };

        _logger.LogInformation("Executing yt-dlp search for query: {Query}", query);

        try
        {
            var result = await _processRunner.RunAsync(
                _ytDlpManager.ExecutablePath,
                args,
                options,
                cancellationToken: cancellationToken);

            if (!result.IsSuccess)
            {
                var error = YtDlpErrorClassifier.Classify(result.ExitCode, result.StandardError, result.StandardOutput);
                _logger.LogWarning("yt-dlp search failed with code {Code}: {Message}", error.Code, error.Message);
                return Result<PagedResult<SearchResult>>.Failure(error);
            }

            if (string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return Result<PagedResult<SearchResult>>.Success(PagedResult<SearchResult>.Empty());
            }

            var searchResult = JsonSerializer.Deserialize<YtDlpSearchResult>(result.StandardOutput);
            var items = new List<SearchResult>();

            if (searchResult?.Entries != null)
            {
                foreach (var entry in searchResult.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id)) continue;

                    TimeSpan? duration = entry.DurationSeconds.HasValue
                        ? TimeSpan.FromSeconds(entry.DurationSeconds.Value)
                        : null;

                    string? thumbUrl = entry.Thumbnails?.LastOrDefault()?.Url;

                    items.Add(new SearchResult
                    {
                        Id = entry.Id,
                        Title = entry.Title ?? "Untitled Video",
                        ChannelTitle = entry.Channel ?? entry.Uploader ?? "Unknown Channel",
                        ChannelId = entry.ChannelId ?? entry.UploaderId,
                        Duration = duration,
                        ViewCount = entry.ViewCount,
                        UploadDateText = FormatUploadDate(entry.UploadDate),
                        ThumbnailUrl = thumbUrl,
                        DescriptionSnippet = entry.Description
                    });
                }
            }

            _logger.LogInformation("yt-dlp search returned {Count} results.", items.Count);

            return Result<PagedResult<SearchResult>>.Success(PagedResult<SearchResult>.SinglePage(items));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing yt-dlp search.");
            return Result<PagedResult<SearchResult>>.Failure("YTDLP_SEARCH_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

    public async Task<Result<VideoDetails>> GetVideoDetailsAsync(
        YouTubeVideoId videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId.Value))
            return Result<VideoDetails>.Failure("INVALID_ID", "Video ID cannot be empty.");

        if (!_ytDlpManager.IsInstalled)
        {
            var initResult = await _ytDlpManager.EnsureInstalledOrUpdatedAsync(cancellationToken);
            if (!initResult.IsSuccess)
                return Result<VideoDetails>.Failure(initResult.Error!);
        }

        var args = new List<string>
        {
            "--dump-single-json",
            "--skip-download",
            "--no-warnings"
        };

        AppendCookieArgs(args);
        args.Add(videoId.ToWatchUrl());

        var timeout = TimeSpan.FromSeconds(_configService.CurrentConfig.Backend.YtDlpTimeoutSeconds);
        var options = new ProcessRunOptions { Timeout = timeout };

        _logger.LogInformation("Extracting video details via yt-dlp for {Id}...", videoId.Value);

        try
        {
            var result = await _processRunner.RunAsync(
                _ytDlpManager.ExecutablePath,
                args,
                options,
                cancellationToken: cancellationToken);

            if (!result.IsSuccess)
            {
                var error = YtDlpErrorClassifier.Classify(result.ExitCode, result.StandardError, result.StandardOutput, videoId.Value);
                _logger.LogWarning("yt-dlp video extraction failed for {Id}: {Error}", videoId.Value, error.Message);
                return Result<VideoDetails>.Failure(error);
            }

            var videoInfo = JsonSerializer.Deserialize<YtDlpVideoInfo>(result.StandardOutput);
            if (videoInfo == null || string.IsNullOrWhiteSpace(videoInfo.Id))
            {
                return Result<VideoDetails>.Failure("INVALID_YTDLP_JSON", "yt-dlp returned invalid or empty video JSON.");
            }

            var streams = MapStreams(videoInfo.Formats);
            var captions = MapCaptions(videoInfo.Subtitles, videoInfo.AutomaticCaptions);

            var details = new VideoDetails
            {
                Id = videoId,
                Title = videoInfo.Title ?? "Untitled Video",
                Description = videoInfo.Description,
                Channel = new ChannelInfo
                {
                    Id = videoInfo.ChannelId ?? videoInfo.UploaderId ?? string.Empty,
                    Name = videoInfo.Channel ?? videoInfo.Uploader ?? "Unknown Channel"
                },
                Duration = videoInfo.DurationSeconds.HasValue ? TimeSpan.FromSeconds(videoInfo.DurationSeconds.Value) : TimeSpan.Zero,
                ViewCount = videoInfo.ViewCount,
                LikeCount = videoInfo.LikeCount,
                UploadDate = ParseUploadDate(videoInfo.UploadDate),
                ThumbnailUrl = videoInfo.Thumbnails?.LastOrDefault()?.Url,
                Streams = streams,
                Captions = captions,
                Tags = (IReadOnlyList<string>?)videoInfo.Tags ?? Array.Empty<string>(),
                IsLive = videoInfo.IsLive ?? false
            };

            return Result<VideoDetails>.Success(details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying video details for {Id} via yt-dlp.", videoId.Value);
            return Result<VideoDetails>.Failure("YTDLP_DETAILS_ERROR", ex.Message, ex.ToString(), ex);
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

        var detailsResult = await GetVideoDetailsAsync(videoId, cancellationToken);
        if (!detailsResult.IsSuccess)
            return Result<StreamResolutionResult>.Failure(detailsResult.Error!);

        var details = detailsResult.Value!;
        var streams = details.Streams;

        if (streams.Count == 0)
        {
            return Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(videoId.Value, "No stream formats found."));
        }

        // Case 1: Audio only requested
        if (audioOnly || qualityPreference == QualityPreference.AudioOnly)
        {
            var audioStream = streams
                .Where(s => s.IsAudioOnly)
                .OrderByDescending(s => s.AudioBitrate ?? s.Bitrate ?? 0)
                .FirstOrDefault()
                ?? streams.FirstOrDefault(s => s.IsProgressive);

            if (audioStream == null)
            {
                return Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(videoId.Value, "No audio stream available."));
            }

            return Result<StreamResolutionResult>.Success(new StreamResolutionResult
            {
                VideoId = videoId,
                SelectedVideoStream = audioStream,
                SelectedAudioStream = null,
                DirectPlaybackUrl = audioStream.Url,
                AudioTrackUrl = null,
                AlternativeStreams = streams.Where(s => s.IsAudioOnly && s.Url != audioStream.Url).ToList(),
                Captions = details.Captions,
                IsLiveStream = details.IsLive,
                SourceProvider = BackendProviderType.YtDlpLocal
            });
        }

        // Case 2: Standard video playback
        // Pick best progressive stream or best video+audio DASH streams
        var progressiveStreams = streams
            .Where(s => s.IsProgressive)
            .OrderByDescending(s => s.Height ?? 0)
            .ToList();

        var videoOnlyStreams = streams
            .Where(s => s.IsVideoOnly)
            .OrderByDescending(s => s.Height ?? 0)
            .ToList();

        var bestAudio = streams
            .Where(s => s.IsAudioOnly)
            .OrderByDescending(s => s.AudioBitrate ?? s.Bitrate ?? 0)
            .FirstOrDefault();

        StreamCandidate? selectedVideo = null;
        StreamCandidate? selectedAudio = null;

        int targetHeight = qualityPreference switch
        {
            QualityPreference.P1080 => 1080,
            QualityPreference.P720 => 720,
            QualityPreference.P480 => 480,
            QualityPreference.P360 => 360,
            _ => 1080
        };

        // Prefer progressive stream if available and matches quality preference
        var matchedProgressive = progressiveStreams.FirstOrDefault(s => s.Height == targetHeight)
                               ?? progressiveStreams.FirstOrDefault();

        var matchedVideoOnly = videoOnlyStreams.FirstOrDefault(s => s.Height == targetHeight)
                             ?? videoOnlyStreams.FirstOrDefault();

        if (qualityPreference != QualityPreference.Auto && matchedVideoOnly?.Height == targetHeight && bestAudio != null)
        {
            selectedVideo = matchedVideoOnly;
            selectedAudio = bestAudio;
        }
        else if (matchedProgressive != null)
        {
            selectedVideo = matchedProgressive;
            selectedAudio = null;
        }
        else if (matchedVideoOnly != null && bestAudio != null)
        {
            selectedVideo = matchedVideoOnly;
            selectedAudio = bestAudio;
        }
        else
        {
            selectedVideo = streams.FirstOrDefault();
        }

        if (selectedVideo == null)
        {
            return Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(videoId.Value, "No suitable video stream could be resolved."));
        }

        return Result<StreamResolutionResult>.Success(new StreamResolutionResult
        {
            VideoId = videoId,
            SelectedVideoStream = selectedVideo,
            SelectedAudioStream = selectedAudio,
            DirectPlaybackUrl = selectedVideo.Url,
            AudioTrackUrl = selectedAudio?.Url,
            AlternativeStreams = streams.Where(s => s.Url != selectedVideo.Url).ToList(),
            Captions = details.Captions,
            IsLiveStream = details.IsLive,
            SourceProvider = BackendProviderType.YtDlpLocal
        });
    }

    /// <summary>
    /// Downloads media to disk with real-time progress reporting, safe process execution,
    /// and audio extraction if requested.
    /// </summary>
    public async Task<Result<string>> DownloadAsync(
        YouTubeVideoId videoId,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId.Value))
            return Result<string>.Failure("INVALID_ID", "Video ID cannot be empty.");

        ArgumentNullException.ThrowIfNull(options);

        if (!_ytDlpManager.IsInstalled)
        {
            var initResult = await _ytDlpManager.EnsureInstalledOrUpdatedAsync(cancellationToken);
            if (!initResult.IsSuccess)
                return Result<string>.Failure(initResult.Error!);
        }

        Directory.CreateDirectory(options.OutputDirectory);

        var args = new List<string>
        {
            "--newline",
            "--no-playlist",
            "--no-warnings"
        };

        // Output destination pattern
        var outputPath = Path.Combine(options.OutputDirectory, options.FileNamePattern);
        args.Add("-o");
        args.Add(outputPath);

        // Format selection
        if (options.AudioOnly)
        {
            args.Add("-x");
            args.Add("--audio-format");
            args.Add(string.IsNullOrWhiteSpace(options.AudioFormat) ? "mp3" : options.AudioFormat);
            args.Add("--audio-quality");
            args.Add("0");
        }
        else
        {
            args.Add("-f");
            var formatSelector = BuildFormatSelector(options.QualityPreference);
            args.Add(formatSelector);
        }

        AppendCookieArgs(args, options.BrowserCookies);
        args.Add(videoId.ToWatchUrl());

        var processOptions = new ProcessRunOptions
        {
            WorkingDirectory = options.OutputDirectory,
            Timeout = TimeSpan.FromHours(2) // generous for long downloads
        };

        string? destinationFile = null;

        var lineProgress = new SynchronousProgress<string>(line =>
        {
            // Check for destination file
            var destMatch = DestinationRegex.Match(line);
            if (destMatch.Success)
            {
                destinationFile = destMatch.Groups["dest"].Value.Trim();
            }

            // Check for progress
            var progMatch = DownloadProgressRegex.Match(line);
            if (progMatch.Success && progress != null)
            {
                if (double.TryParse(progMatch.Groups["pct"].Value, out var pct))
                {
                    progress.Report(new DownloadProgress
                    {
                        Percent = pct,
                        TotalSize = progMatch.Groups["size"].Value,
                        Speed = progMatch.Groups["speed"].Value,
                        Eta = progMatch.Groups["eta"].Value,
                        StatusMessage = line.Trim()
                    });
                }
            }
        });

        _logger.LogInformation("Starting yt-dlp download for video {Id} to {Dir}...", videoId.Value, options.OutputDirectory);

        try
        {
            var result = await _processRunner.RunAsync(
                _ytDlpManager.ExecutablePath,
                args,
                processOptions,
                standardOutputLineProgress: lineProgress,
                cancellationToken: cancellationToken);

            if (!result.IsSuccess)
            {
                var error = YtDlpErrorClassifier.Classify(result.ExitCode, result.StandardError, result.StandardOutput, videoId.Value);
                _logger.LogWarning("yt-dlp download failed for {Id}: {Error}", videoId.Value, error.Message);
                return Result<string>.Failure(error);
            }

            // Fallback: check stdout lines if destination wasn't caught by progress
            if (destinationFile == null && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                foreach (var line in result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = DestinationRegex.Match(line);
                    if (match.Success)
                    {
                        destinationFile = match.Groups["dest"].Value.Trim();
                        break;
                    }
                }
            }

            progress?.Report(new DownloadProgress
            {
                Percent = 100,
                StatusMessage = "Download completed successfully."
            });

            var finalPath = destinationFile ?? outputPath;
            _logger.LogInformation("yt-dlp download completed for {Id}: {Path}", videoId.Value, finalPath);
            return Result<string>.Success(finalPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download canceled by user for {Id}.", videoId.Value);
            return Result<string>.Failure("DOWNLOAD_CANCELED", "The media download was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading media for {Id}.", videoId.Value);
            return Result<string>.Failure("DOWNLOAD_ERROR", ex.Message, ex.ToString(), ex);
        }
    }

    private void AppendCookieArgs(List<string> args, string? explicitBrowser = null)
    {
        var browser = explicitBrowser ?? _configService.CurrentConfig.Backend.YtDlpBrowserCookies;
        if (!string.IsNullOrWhiteSpace(browser))
        {
            args.Add("--cookies-from-browser");
            args.Add(browser);
        }
    }

    public static string BuildFormatSelector(QualityPreference preference)
    {
        return preference switch
        {
            QualityPreference.P1080 => "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=1080]+bestaudio/best[height<=1080]/best",
            QualityPreference.P720 => "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=720]+bestaudio/best[height<=720]/best",
            QualityPreference.P480 => "bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=480]+bestaudio/best[height<=480]/best",
            QualityPreference.P360 => "bestvideo[height<=360][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=360]+bestaudio/best[height<=360]/best",
            _ => "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best"
        };
    }

    private static IReadOnlyList<StreamCandidate> MapStreams(List<YtDlpFormat>? formats)
    {
        if (formats == null) return Array.Empty<StreamCandidate>();

        var list = new List<StreamCandidate>();
        foreach (var f in formats)
        {
            if (string.IsNullOrWhiteSpace(f.Url)) continue;

            var formatExt = f.Ext ?? "mp4";
            long bitrate = (long)((f.TotalBitrateKbps ?? f.VideoBitrateKbps ?? f.AudioBitrateKbps ?? 0) * 1000);

            if (f.IsAudioOnly)
            {
                list.Add(StreamCandidate.CreateAudio(f.Url, formatExt, bitrate, f.ACodec));
            }
            else if (f.IsProgressive)
            {
                list.Add(StreamCandidate.CreateVideo(
                    f.Url,
                    formatExt,
                    f.Height ?? 0,
                    f.Width ?? 0,
                    (int)(f.Fps ?? 30),
                    bitrate,
                    f.VCodec,
                    isProgressive: true));
            }
            else if (f.IsVideoOnly)
            {
                list.Add(StreamCandidate.CreateVideo(
                    f.Url,
                    formatExt,
                    f.Height ?? 0,
                    f.Width ?? 0,
                    (int)(f.Fps ?? 30),
                    bitrate,
                    f.VCodec,
                    isProgressive: false));
            }
        }

        return list;
    }

    private static IReadOnlyList<CaptionTrack> MapCaptions(
        Dictionary<string, List<YtDlpSubtitleTrack>>? manual,
        Dictionary<string, List<YtDlpSubtitleTrack>>? auto)
    {
        var list = new List<CaptionTrack>();

        void ProcessDict(Dictionary<string, List<YtDlpSubtitleTrack>>? dict, bool isAuto)
        {
            if (dict == null) return;

            foreach (var (langCode, tracks) in dict)
            {
                // Prefer vtt or ttml format
                var chosen = tracks.FirstOrDefault(t => t.Ext == "vtt")
                           ?? tracks.FirstOrDefault(t => t.Ext == "ttml")
                           ?? tracks.FirstOrDefault();

                if (chosen != null && !string.IsNullOrWhiteSpace(chosen.Url))
                {
                    list.Add(new CaptionTrack
                    {
                        LanguageCode = langCode,
                        DisplayName = chosen.Name ?? langCode,
                        Url = chosen.Url,
                        Format = chosen.Ext ?? "vtt",
                        IsAutoGenerated = isAuto
                    });
                }
            }
        }

        ProcessDict(manual, false);
        ProcessDict(auto, true);

        return list;
    }

    private static string? FormatUploadDate(string? yyyymmdd)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd.Length != 8)
            return null;

        if (int.TryParse(yyyymmdd[..4], out var year) &&
            int.TryParse(yyyymmdd.Substring(4, 2), out var month) &&
            int.TryParse(yyyymmdd.Substring(6, 2), out var day))
        {
            try
            {
                var dt = new DateTime(year, month, day);
                return dt.ToString("MMMM d, yyyy");
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static DateTimeOffset? ParseUploadDate(string? yyyymmdd)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd.Length != 8)
            return null;

        if (int.TryParse(yyyymmdd[..4], out var year) &&
            int.TryParse(yyyymmdd.Substring(4, 2), out var month) &&
            int.TryParse(yyyymmdd.Substring(6, 2), out var day))
        {
            try
            {
                return new DateTimeOffset(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        public void Report(T value) => _handler(value);
    }
}
