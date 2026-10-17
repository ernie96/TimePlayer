using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Backends.Piped;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Playback;

/// <summary>
/// Production coordinator managing queue progression, multi-tier backend stream resolution
/// with failover (Piped -> Invidious -> yt-dlp), closed caption synchronization, and player state.
/// </summary>
public sealed class PlaybackCoordinator : IPlaybackCoordinator, IAsyncDisposable
{
    private readonly PipedBackendProvider _piped;
    private readonly InvidiousBackendProvider _invidious;
    private readonly YtDlpBackendProvider _ytdlp;
    private readonly IPlayerEngine _playerEngine;
    private readonly IConfigurationService _configService;
    private readonly ILogger<PlaybackCoordinator> _logger;

    private readonly List<PlaybackQueueItem> _queue = new();
    private int _currentIndex = -1;
    private bool _isDisposed;

    public PlaybackState State => _playerEngine.State;
    public VideoDetails? CurrentVideo { get; private set; }
    public IReadOnlyList<PlaybackQueueItem> Queue => _queue.AsReadOnly();
    public PlaybackQueueItem? CurrentQueueItem => (_currentIndex >= 0 && _currentIndex < _queue.Count) ? _queue[_currentIndex] : null;

    public BackendProviderType ActiveProvider { get; private set; } = BackendProviderType.Piped;
    public IReadOnlyList<CaptionTrack> CurrentCaptions { get; private set; } = Array.Empty<CaptionTrack>();
    public CaptionTrack? SelectedCaptionTrack { get; private set; }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<VideoDetails?>? CurrentVideoChanged;
    public event EventHandler<PlaybackError>? ErrorOccurred;
    public event EventHandler<IReadOnlyList<PlaybackQueueItem>>? QueueChanged;

    public PlaybackCoordinator(
        PipedBackendProvider piped,
        InvidiousBackendProvider invidious,
        YtDlpBackendProvider ytdlp,
        IPlayerEngine playerEngine,
        IConfigurationService configService,
        ILogger<PlaybackCoordinator> logger)
    {
        _piped = piped ?? throw new ArgumentNullException(nameof(piped));
        _invidious = invidious ?? throw new ArgumentNullException(nameof(invidious));
        _ytdlp = ytdlp ?? throw new ArgumentNullException(nameof(ytdlp));
        _playerEngine = playerEngine ?? throw new ArgumentNullException(nameof(playerEngine));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _playerEngine.StateChanged += OnEngineStateChanged;
        _playerEngine.ErrorOccurred += OnEngineErrorOccurred;
    }

    /// <summary>
    /// Searches YouTube across multi-tier providers with automatic fallback (Piped -> Invidious -> yt-dlp).
    /// </summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // Tier 1: Piped
        try
        {
            _logger.LogInformation("Attempting YouTube search via Piped for '{Query}'...", query);
            var pipedRes = await _piped.SearchAsync(query, cancellationToken: cancellationToken);
            if (pipedRes.IsSuccess && pipedRes.Value.Items.Count > 0)
            {
                ActiveProvider = BackendProviderType.Piped;
                return pipedRes;
            }
            _logger.LogWarning("Piped search returned 0 items or failed: {Error}", pipedRes.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during Piped search.");
        }

        // Tier 2: Invidious
        try
        {
            _logger.LogInformation("Falling back to Invidious search for '{Query}'...", query);
            var invidiousRes = await _invidious.SearchAsync(query, cancellationToken: cancellationToken);
            if (invidiousRes.IsSuccess && invidiousRes.Value.Items.Count > 0)
            {
                ActiveProvider = BackendProviderType.Invidious;
                return invidiousRes;
            }
            _logger.LogWarning("Invidious search returned 0 items or failed: {Error}", invidiousRes.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during Invidious search.");
        }

        // Tier 3: Local yt-dlp
        _logger.LogInformation("Falling back to local yt-dlp search for '{Query}'...", query);
        var ytdlpRes = await _ytdlp.SearchAsync(query, cancellationToken: cancellationToken);
        if (ytdlpRes.IsSuccess)
        {
            ActiveProvider = BackendProviderType.YtDlpLocal;
        }

        return ytdlpRes;
    }

    /// <summary>
    /// Plays a video by YouTube URL or video ID with automatic backend failover.
    /// </summary>
    public async Task<Result<bool>> PlayUrlOrIdAsync(string urlOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlOrId);

        YouTubeVideoId videoId;
        if (YouTubeUrl.TryParse(urlOrId, out var ytUrl))
        {
            videoId = ytUrl.VideoId;
        }
        else if (YouTubeVideoId.TryParse(urlOrId, out var parsedId))
        {
            videoId = parsedId;
        }
        else
        {
            return Result<bool>.Failure(AppError.InvalidUrl("Invalid YouTube URL or video ID format."));
        }

        return await PlayVideoIdInternalAsync(videoId, cancellationToken);
    }

    private async Task<Result<bool>> PlayVideoIdInternalAsync(YouTubeVideoId videoId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Resolving streams for video ID {Id}...", videoId.Value);

        var qualityPref = _configService.CurrentConfig.Playback.PreferredQuality;

        StreamResolutionResult? streamResult = null;

        // Tier 1: Piped
        try
        {
            var pipedStream = await _piped.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (pipedStream.IsSuccess && !string.IsNullOrWhiteSpace(pipedStream.Value.DirectPlaybackUrl))
            {
                streamResult = pipedStream.Value;
                ActiveProvider = BackendProviderType.Piped;
            }
            else
            {
                _logger.LogWarning("Piped stream resolution failed: {Error}", pipedStream.Error?.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception resolving stream with Piped.");
        }

        // Tier 2: Invidious
        if (streamResult == null)
        {
            try
            {
                _logger.LogInformation("Falling back to Invidious stream resolution for {Id}...", videoId.Value);
                var invidiousStream = await _invidious.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
                if (invidiousStream.IsSuccess && !string.IsNullOrWhiteSpace(invidiousStream.Value.DirectPlaybackUrl))
                {
                    streamResult = invidiousStream.Value;
                    ActiveProvider = BackendProviderType.Invidious;
                }
                else
                {
                    _logger.LogWarning("Invidious stream resolution failed: {Error}", invidiousStream.Error?.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception resolving stream with Invidious.");
            }
        }

        // Tier 3: Local yt-dlp
        if (streamResult == null)
        {
            try
            {
                _logger.LogInformation("Falling back to yt-dlp stream resolution for {Id}...", videoId.Value);
                var ytdlpStream = await _ytdlp.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
                if (ytdlpStream.IsSuccess && !string.IsNullOrWhiteSpace(ytdlpStream.Value.DirectPlaybackUrl))
                {
                    streamResult = ytdlpStream.Value;
                    ActiveProvider = BackendProviderType.YtDlpLocal;
                }
                else
                {
                    _logger.LogWarning("yt-dlp stream resolution failed: {Error}", ytdlpStream.Error?.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception resolving stream with yt-dlp.");
            }
        }

        // Tier 4: Direct mpv yt-dlp hook playback
        string playbackUrl;
        string? audioUrl = null;
        IReadOnlyList<CaptionTrack> captions = Array.Empty<CaptionTrack>();

        if (streamResult != null)
        {
            playbackUrl = streamResult.DirectPlaybackUrl;
            audioUrl = streamResult.AudioTrackUrl;
            captions = streamResult.Captions;
        }
        else
        {
            _logger.LogInformation("Attempting direct mpv yt-dlp fallback playback URL for {Id}...", videoId.Value);
            playbackUrl = $"https://www.youtube.com/watch?v={videoId.Value}";
            ActiveProvider = BackendProviderType.YtDlpLocal;
        }

        try
        {
            await _playerEngine.LoadAsync(playbackUrl, audioUrl, cancellationToken);
            CurrentCaptions = captions;
            SelectedCaptionTrack = null;

            // Load default subtitle track if available
            if (captions.Count > 0)
            {
                var defaultCaption = captions.FirstOrDefault(c => c.IsDefault)
                    ?? captions.FirstOrDefault(c => c.LanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    ?? captions.FirstOrDefault();

                if (defaultCaption != null)
                {
                    try
                    {
                        await _playerEngine.LoadSubtitleTrackAsync(defaultCaption.Url, selectImmediately: true, cancellationToken);
                        SelectedCaptionTrack = defaultCaption;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load default subtitle track {Lang}.", defaultCaption.LanguageCode);
                    }
                }
            }

            // Asynchronously retrieve video details to populate CurrentVideo
            _ = Task.Run(async () =>
            {
                try
                {
                    var detailsResult = await _piped.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    if (!detailsResult.IsSuccess)
                    {
                        detailsResult = await _invidious.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    }
                    if (!detailsResult.IsSuccess)
                    {
                        detailsResult = await _ytdlp.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    }

                    if (detailsResult.IsSuccess)
                    {
                        CurrentVideo = detailsResult.Value;
                        CurrentVideoChanged?.Invoke(this, CurrentVideo);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not fetch detailed metadata for video {Id}.", videoId.Value);
                }
            }, CancellationToken.None);

            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load media in player engine for {Id}.", videoId.Value);
            var err = new PlaybackError
            {
                ErrorType = PlaybackErrorType.Unknown,
                UserMessage = $"Failed to play video: {ex.Message}",
                TechnicalDetails = ex.ToString()
            };
            ErrorOccurred?.Invoke(this, err);
            return Result<bool>.Failure(AppError.PlaybackFailed(err.UserMessage, ex));
        }
    }

    public async Task<Result<bool>> PlayQueueItemAsync(Guid queueItemId, CancellationToken cancellationToken = default)
    {
        var index = _queue.FindIndex(i => i.QueueItemId == queueItemId);
        if (index < 0)
        {
            return Result<bool>.Failure(new AppError
            {
                Code = "QUEUE_ITEM_NOT_FOUND",
                Message = "Queue item was not found."
            });
        }

        _currentIndex = index;
        var item = _queue[index];

        // Mark previous items
        _queue[index] = item with { IsPlayed = true };
        QueueChanged?.Invoke(this, Queue);

        return await PlayVideoIdInternalAsync(item.VideoId, cancellationToken);
    }

    public Task EnqueueAsync(SearchResult item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var queueItem = PlaybackQueueItem.FromSearchResult(item);
        _queue.Add(queueItem);
        QueueChanged?.Invoke(this, Queue);
        _logger.LogInformation("Enqueued item '{Title}'. Queue count: {Count}", item.Title, _queue.Count);
        return Task.CompletedTask;
    }

    public Task RemoveFromQueueAsync(Guid queueItemId)
    {
        var index = _queue.FindIndex(i => i.QueueItemId == queueItemId);
        if (index >= 0)
        {
            _queue.RemoveAt(index);
            if (_currentIndex >= index && _currentIndex > 0)
            {
                _currentIndex--;
            }
            QueueChanged?.Invoke(this, Queue);
        }
        return Task.CompletedTask;
    }

    public Task ClearQueueAsync()
    {
        _queue.Clear();
        _currentIndex = -1;
        QueueChanged?.Invoke(this, Queue);
        return Task.CompletedTask;
    }

    public async Task PlayNextAsync(CancellationToken cancellationToken = default)
    {
        if (_currentIndex + 1 < _queue.Count)
        {
            _currentIndex++;
            var nextItem = _queue[_currentIndex];
            _queue[_currentIndex] = nextItem with { IsPlayed = true };
            QueueChanged?.Invoke(this, Queue);
            await PlayVideoIdInternalAsync(nextItem.VideoId, cancellationToken);
        }
        else
        {
            _logger.LogInformation("End of queue reached.");
        }
    }

    public async Task PlayPreviousAsync(CancellationToken cancellationToken = default)
    {
        if (_currentIndex > 0)
        {
            _currentIndex--;
            var prevItem = _queue[_currentIndex];
            QueueChanged?.Invoke(this, Queue);
            await PlayVideoIdInternalAsync(prevItem.VideoId, cancellationToken);
        }
    }

    public async Task SetCaptionTrackAsync(CaptionTrack? captionTrack, CancellationToken cancellationToken = default)
    {
        if (captionTrack == null)
        {
            SelectedCaptionTrack = null;
            await _playerEngine.SetSubtitleTrackAsync("no", cancellationToken);
            return;
        }

        SelectedCaptionTrack = captionTrack;
        await _playerEngine.LoadSubtitleTrackAsync(captionTrack.Url, selectImmediately: true, cancellationToken);
    }

    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        if (State == PlaybackState.Playing)
        {
            await _playerEngine.PauseAsync(cancellationToken);
        }
        else
        {
            await _playerEngine.PlayAsync(cancellationToken);
        }
    }

    public Task SeekRelativeAsync(double seconds, CancellationToken cancellationToken = default) =>
        _playerEngine.SeekRelativeAsync(seconds, cancellationToken);

    public Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default) =>
        _playerEngine.SetVolumeAsync(volumePercent, cancellationToken);

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default) =>
        _playerEngine.SetSpeedAsync(speed, cancellationToken);

    private void OnEngineStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, e.NewState);

        // Auto-advance to next queue item when video ends
        if (e.NewState == PlaybackState.Ended && _currentIndex + 1 < _queue.Count)
        {
            _logger.LogInformation("Current video ended, auto-advancing to next queue item...");
            _ = PlayNextAsync(CancellationToken.None);
        }
    }

    private void OnEngineErrorOccurred(object? sender, PlaybackErrorEventArgs e)
    {
        ErrorOccurred?.Invoke(this, new PlaybackError
        {
            ErrorType = PlaybackErrorType.Unknown,
            UserMessage = e.Message
        });
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        _playerEngine.StateChanged -= OnEngineStateChanged;
        _playerEngine.ErrorOccurred -= OnEngineErrorOccurred;

        return ValueTask.CompletedTask;
    }
}
