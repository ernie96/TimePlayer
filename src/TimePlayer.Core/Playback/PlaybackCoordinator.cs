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
/// with failover (Piped -> Invidious -> yt-dlp), playback startup confirmation, and closed caption synchronization.
/// </summary>
public sealed class PlaybackCoordinator : IPlaybackCoordinator, IAsyncDisposable
{
    private readonly IBackendProvider _piped;
    private readonly IBackendProvider _invidious;
    private readonly IBackendProvider _ytdlp;
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
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IReadOnlyList<PlaybackQueueItem>>? QueueChanged;

    public PlaybackCoordinator(
        IBackendProvider piped,
        IBackendProvider invidious,
        IBackendProvider ytdlp,
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

    public PlaybackCoordinator(
        PipedBackendProvider piped,
        InvidiousBackendProvider invidious,
        YtDlpBackendProvider ytdlp,
        IPlayerEngine playerEngine,
        IConfigurationService configService,
        ILogger<PlaybackCoordinator> logger)
        : this((IBackendProvider)piped, (IBackendProvider)invidious, (IBackendProvider)ytdlp, playerEngine, configService, logger)
    {
    }

    /// <summary>
    /// Searches YouTube across multi-tier providers with automatic fallback (Piped -> Invidious -> yt-dlp)
    /// within a bounded total search deadline.
    /// </summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Bounded overall search deadline (20 seconds total)
        using var overallDeadlineCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, overallDeadlineCts.Token);
        var searchToken = linkedCts.Token;

        // Tier 1: Piped
        try
        {
            _logger.LogInformation("Attempting YouTube search via Piped for '{Query}'...", query);
            StatusChanged?.Invoke(this, "Searching YouTube (Piped)...");
            var pipedRes = await _piped.SearchAsync(query, cancellationToken: searchToken);
            if (pipedRes.IsSuccess && pipedRes.Value.Items.Count > 0)
            {
                ActiveProvider = BackendProviderType.Piped;
                StatusChanged?.Invoke(this, $"Found {pipedRes.Value.Items.Count} videos via Piped.");
                return pipedRes;
            }
            _logger.LogWarning("Piped search returned 0 items or failed: {Error}", pipedRes.Error?.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Search cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during Piped search.");
        }

        // Tier 2: Invidious
        try
        {
            _logger.LogInformation("Falling back to Invidious search for '{Query}'...", query);
            StatusChanged?.Invoke(this, "Piped failed. Searching Invidious...");
            var invidiousRes = await _invidious.SearchAsync(query, cancellationToken: searchToken);
            if (invidiousRes.IsSuccess && invidiousRes.Value.Items.Count > 0)
            {
                ActiveProvider = BackendProviderType.Invidious;
                StatusChanged?.Invoke(this, $"Found {invidiousRes.Value.Items.Count} videos via Invidious.");
                return invidiousRes;
            }
            _logger.LogWarning("Invidious search returned 0 items or failed: {Error}", invidiousRes.Error?.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Search cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during Invidious search.");
        }

        // Tier 3: Local yt-dlp
        _logger.LogInformation("Falling back to local yt-dlp search for '{Query}'...", query);
        StatusChanged?.Invoke(this, "Public APIs failed. Falling back to local yt-dlp search...");
        var ytdlpRes = await _ytdlp.SearchAsync(query, cancellationToken: searchToken);
        if (ytdlpRes.IsSuccess)
        {
            ActiveProvider = BackendProviderType.YtDlpLocal;
            StatusChanged?.Invoke(this, $"Found {ytdlpRes.Value.Items.Count} videos via yt-dlp.");
        }

        return ytdlpRes;
    }

    /// <summary>
    /// Plays a video by YouTube URL or video ID with automatic multi-tier fallback
    /// and verified playback startup.
    /// </summary>
    public async Task<Result<bool>> PlayUrlOrIdAsync(string urlOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlOrId);
        cancellationToken.ThrowIfCancellationRequested();

        YouTubeVideoId videoId;
        TimeSpan? startTime = null;

        if (YouTubeUrl.TryParse(urlOrId, out var ytUrl))
        {
            videoId = ytUrl.VideoId;
            startTime = ytUrl.StartTime;
        }
        else if (YouTubeVideoId.TryParse(urlOrId, out var parsedId))
        {
            videoId = parsedId;
        }
        else
        {
            return Result<bool>.Failure(AppError.InvalidUrl("Invalid YouTube URL or video ID format."));
        }

        return await PlayVideoIdInternalAsync(videoId, startTime, cancellationToken);
    }

    private async Task<Result<bool>> PlayVideoIdInternalAsync(
        YouTubeVideoId videoId,
        TimeSpan? startTime,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting resilient playback workflow for video ID {Id}...", videoId.Value);
        var qualityPref = _configService.CurrentConfig.Playback.PreferredQuality;

        // Tier 1: Piped Stream Resolution & Playback Confirmation
        try
        {
            StatusChanged?.Invoke(this, "Resolving stream via Piped...");
            _logger.LogInformation("Resolving stream via Piped for {Id}...", videoId.Value);
            var pipedStream = await _piped.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (pipedStream.IsSuccess && !string.IsNullOrWhiteSpace(pipedStream.Value.DirectPlaybackUrl))
            {
                StatusChanged?.Invoke(this, "Connecting to Piped stream...");
                var started = await AttemptPlaybackAsync(
                    pipedStream.Value.DirectPlaybackUrl,
                    pipedStream.Value.AudioTrackUrl,
                    pipedStream.Value.Captions,
                    videoId,
                    startTime,
                    BackendProviderType.Piped,
                    TimeSpan.FromSeconds(7),
                    cancellationToken);

                if (started)
                {
                    return Result<bool>.Success(true);
                }
                _logger.LogWarning("Piped stream did not start playing. Falling back to Invidious...");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception attempting Piped playback for {Id}.", videoId.Value);
        }

        // Tier 2: Invidious Stream Resolution & Playback Confirmation
        try
        {
            StatusChanged?.Invoke(this, "Falling back to Invidious stream...");
            _logger.LogInformation("Resolving stream via Invidious for {Id}...", videoId.Value);
            var invidiousStream = await _invidious.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (invidiousStream.IsSuccess && !string.IsNullOrWhiteSpace(invidiousStream.Value.DirectPlaybackUrl))
            {
                StatusChanged?.Invoke(this, "Connecting to Invidious stream...");
                var started = await AttemptPlaybackAsync(
                    invidiousStream.Value.DirectPlaybackUrl,
                    invidiousStream.Value.AudioTrackUrl,
                    invidiousStream.Value.Captions,
                    videoId,
                    startTime,
                    BackendProviderType.Invidious,
                    TimeSpan.FromSeconds(7),
                    cancellationToken);

                if (started)
                {
                    return Result<bool>.Success(true);
                }
                _logger.LogWarning("Invidious stream did not start playing. Falling back to yt-dlp...");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception attempting Invidious playback for {Id}.", videoId.Value);
        }

        // Tier 3: Local yt-dlp Stream Resolution
        try
        {
            StatusChanged?.Invoke(this, "Resolving stream via local yt-dlp...");
            _logger.LogInformation("Resolving stream via yt-dlp for {Id}...", videoId.Value);
            var ytdlpStream = await _ytdlp.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (ytdlpStream.IsSuccess && !string.IsNullOrWhiteSpace(ytdlpStream.Value.DirectPlaybackUrl))
            {
                StatusChanged?.Invoke(this, "Connecting to yt-dlp extracted stream...");
                var started = await AttemptPlaybackAsync(
                    ytdlpStream.Value.DirectPlaybackUrl,
                    ytdlpStream.Value.AudioTrackUrl,
                    ytdlpStream.Value.Captions,
                    videoId,
                    startTime,
                    BackendProviderType.YtDlpLocal,
                    TimeSpan.FromSeconds(8),
                    cancellationToken);

                if (started)
                {
                    return Result<bool>.Success(true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception attempting yt-dlp resolved playback for {Id}.", videoId.Value);
        }

        // Tier 4: Direct mpv yt-dlp hook playback (mpv loads original YouTube URL directly)
        try
        {
            StatusChanged?.Invoke(this, "Attempting direct player playback via yt-dlp hook...");
            _logger.LogInformation("Attempting direct mpv yt-dlp hook playback for {Id}...", videoId.Value);
            var directWatchUrl = $"https://www.youtube.com/watch?v={videoId.Value}";

            var started = await AttemptPlaybackAsync(
                directWatchUrl,
                audioUrl: null,
                captions: Array.Empty<CaptionTrack>(),
                videoId,
                startTime,
                BackendProviderType.YtDlpLocal,
                TimeSpan.FromSeconds(15),
                cancellationToken);

            if (started)
            {
                return Result<bool>.Success(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during direct mpv yt-dlp fallback playback for {Id}.", videoId.Value);
        }

        // All tiers failed
        _logger.LogError("All playback tiers failed for video ID {Id}.", videoId.Value);
        var failureErr = new PlaybackError
        {
            ErrorType = PlaybackErrorType.StreamUnreachable,
            UserMessage = "Unable to play video after trying Piped, Invidious, and local yt-dlp fallbacks. The video may be private, age-restricted, or blocked."
        };
        ErrorOccurred?.Invoke(this, failureErr);
        StatusChanged?.Invoke(this, "Playback failed across all providers.");
        return Result<bool>.Failure(AppError.PlaybackFailed(failureErr.UserMessage));
    }

    private async Task<bool> AttemptPlaybackAsync(
        string mediaUrl,
        string? audioUrl,
        IReadOnlyList<CaptionTrack> captions,
        YouTubeVideoId videoId,
        TimeSpan? startTime,
        BackendProviderType provider,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void StateHandler(object? sender, PlaybackStateChangedEventArgs e)
        {
            if (e.NewState is PlaybackState.Playing or PlaybackState.Buffering)
            {
                tcs.TrySetResult(true);
            }
            else if (e.NewState == PlaybackState.Error)
            {
                tcs.TrySetResult(false);
            }
        }

        void ErrorHandler(object? sender, PlaybackErrorEventArgs e)
        {
            tcs.TrySetResult(false);
        }

        _playerEngine.StateChanged += StateHandler;
        _playerEngine.ErrorOccurred += ErrorHandler;

        try
        {
            await _playerEngine.LoadAsync(mediaUrl, audioUrl, cancellationToken);

            if (_playerEngine.State is PlaybackState.Playing or PlaybackState.Buffering)
            {
                tcs.TrySetResult(true);
            }
            else if (_playerEngine.State == PlaybackState.Error)
            {
                tcs.TrySetResult(false);
            }

            using var timeoutCts = new CancellationTokenSource(startupTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(false));

            var playbackStarted = await tcs.Task;
            if (!playbackStarted)
            {
                _logger.LogWarning("Playback did not start within {Timeout} for {Provider}.", startupTimeout, provider);
                return false;
            }

            ActiveProvider = provider;
            CurrentCaptions = captions;
            SelectedCaptionTrack = null;

            // Seek to start timestamp if present in URL
            if (startTime.HasValue && startTime.Value > TimeSpan.Zero)
            {
                try
                {
                    await _playerEngine.SeekAsync(startTime.Value, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to apply starting timestamp {Time}.", startTime.Value);
                }
            }

            // Load default captions if available
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

            StatusChanged?.Invoke(this, $"Playing via {provider}.");
            _logger.LogInformation("Playback successfully confirmed via {Provider}.", provider);

            // Asynchronously fetch full video details
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

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Engine load failed for {Provider}: {Message}", provider, ex.Message);
            return false;
        }
        finally
        {
            _playerEngine.StateChanged -= StateHandler;
            _playerEngine.ErrorOccurred -= ErrorHandler;
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

        _queue[index] = item with { IsPlayed = true };
        QueueChanged?.Invoke(this, Queue);

        return await PlayVideoIdInternalAsync(item.VideoId, startTime: null, cancellationToken);
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
            await PlayVideoIdInternalAsync(nextItem.VideoId, startTime: null, cancellationToken);
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
            await PlayVideoIdInternalAsync(prevItem.VideoId, startTime: null, cancellationToken);
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
