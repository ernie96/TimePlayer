using Microsoft.Extensions.Logging;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Backends.TubeRepair;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Playback;

/// <summary>
/// Central ordered fallback coordinator managing queue progression, multi-tier backend stream resolution
/// with strict priority (TubeRepair -> Invidious -> InnerTube -> yt-dlp), playback startup confirmation,
/// and closed caption synchronization.
/// </summary>
public sealed class PlaybackCoordinator : IPlaybackCoordinator, IAsyncDisposable
{
    private readonly IBackendProvider _tubeRepair;
    private readonly IBackendProvider _invidious;
    private readonly IBackendProvider _innerTube;
    private readonly IBackendProvider _ytdlp;
    private readonly IPlayerEngine _playerEngine;
    private readonly IConfigurationService _configService;
    private readonly ILogger<PlaybackCoordinator> _logger;

    private readonly List<PlaybackQueueItem> _queue = new();
    private int _currentIndex = -1;
    private bool _isDisposed;
    private volatile bool _isAttemptingPlayback;
    private string? _playingVideoId;

    // Paging state for the last successful search: later pages must come from the same backend.
    private string? _lastQuery;
    private IBackendProvider? _lastSearchProvider;
    private string? _nextSearchPageToken;

    public PlaybackState State => _playerEngine.State;
    public VideoDetails? CurrentVideo { get; private set; }
    public IReadOnlyList<PlaybackQueueItem> Queue => _queue.AsReadOnly();
    public PlaybackQueueItem? CurrentQueueItem => (_currentIndex >= 0 && _currentIndex < _queue.Count) ? _queue[_currentIndex] : null;

    public BackendProviderType ActiveProvider { get; private set; } = BackendProviderType.TubeRepair;
    public IReadOnlyList<CaptionTrack> CurrentCaptions { get; private set; } = Array.Empty<CaptionTrack>();
    public CaptionTrack? SelectedCaptionTrack { get; private set; }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<VideoDetails?>? CurrentVideoChanged;
    public event EventHandler<PlaybackError>? ErrorOccurred;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IReadOnlyList<PlaybackQueueItem>>? QueueChanged;

    /// <summary>A video ended and the queue has nothing after it (the moment for autoplay).</summary>
    public event EventHandler? QueueEnded;

    /// <summary>A queued video couldn't be played; <see cref="QueueSkipEventArgs.WillContinue"/> says whether the next one is being tried.</summary>
    public event EventHandler<QueueSkipEventArgs>? ItemSkipped;

    /// <summary>After this many unplayable videos in a row, the queue stops instead of skipping on.</summary>
    public const int MaxConsecutiveSkips = 3;

    /// <summary>Told to users when YouTube itself won't play a video.</summary>
    public const string UnavailableMessage =
        "YouTube says this video isn't available. It may have been removed, made private, or blocked where you are.";

    public bool CanLoadMoreSearchResults => _lastSearchProvider is not null && !string.IsNullOrEmpty(_nextSearchPageToken);

    public PlaybackCoordinator(
        IBackendProvider tubeRepair,
        IBackendProvider invidious,
        IBackendProvider innerTube,
        IBackendProvider ytdlp,
        IPlayerEngine playerEngine,
        IConfigurationService configService,
        ILogger<PlaybackCoordinator> logger)
    {
        _tubeRepair = tubeRepair ?? throw new ArgumentNullException(nameof(tubeRepair));
        _invidious = invidious ?? throw new ArgumentNullException(nameof(invidious));
        _innerTube = innerTube ?? throw new ArgumentNullException(nameof(innerTube));
        _ytdlp = ytdlp ?? throw new ArgumentNullException(nameof(ytdlp));
        _playerEngine = playerEngine ?? throw new ArgumentNullException(nameof(playerEngine));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _playerEngine.StateChanged += OnEngineStateChanged;
        _playerEngine.ErrorOccurred += OnEngineErrorOccurred;
    }

    public PlaybackCoordinator(
        TubeRepairBackendProvider tubeRepair,
        InvidiousBackendProvider invidious,
        InnerTubeBackendProvider innerTube,
        YtDlpBackendProvider ytdlp,
        IPlayerEngine playerEngine,
        IConfigurationService configService,
        ILogger<PlaybackCoordinator> logger)
        : this((IBackendProvider)tubeRepair, (IBackendProvider)invidious, (IBackendProvider)innerTube, (IBackendProvider)ytdlp, playerEngine, configService, logger)
    {
    }

    /// <summary>
    /// Searches YouTube across multi-tier providers with automatic fallback:
    /// 1. TubeRepair -> 2. Invidious -> 3. InnerTube -> 4. yt-dlp
    /// </summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();

        // 30-second bounded total search deadline across public web tiers
        using var publicDeadlineCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, publicDeadlineCts.Token);
        var publicSearchToken = linkedCts.Token;

        // Tier 1: TubeRepair (legacy YouTube API)
        try
        {
            _logger.LogInformation("Attempting YouTube search via TubeRepair for '{Query}'...", query);
            StatusChanged?.Invoke(this, "Searching YouTube (TubeRepair)...");
            var tubeRepairRes = await _tubeRepair.SearchAsync(query, cancellationToken: publicSearchToken);
            if (tubeRepairRes.IsSuccess && tubeRepairRes.Value.Items.Count > 0)
            {
                RememberSearchPaging(query, _tubeRepair, tubeRepairRes.Value);
                ActiveProvider = BackendProviderType.TubeRepair;
                StatusChanged?.Invoke(this, $"Found {tubeRepairRes.Value.Items.Count} videos via TubeRepair.");
                return tubeRepairRes;
            }
            _logger.LogWarning("TubeRepair search returned 0 items or failed: {Error}", tubeRepairRes.Error?.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Search cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during TubeRepair search.");
        }

        // Tier 2: Invidious
        try
        {
            _logger.LogInformation("Falling back to Invidious search for '{Query}'...", query);
            StatusChanged?.Invoke(this, "TubeRepair failed. Searching Invidious...");
            var invidiousRes = await _invidious.SearchAsync(query, cancellationToken: publicSearchToken);
            if (invidiousRes.IsSuccess && invidiousRes.Value.Items.Count > 0)
            {
                RememberSearchPaging(query, _invidious, invidiousRes.Value);
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

        // Tier 3: InnerTube
        try
        {
            _logger.LogInformation("Falling back to InnerTube search for '{Query}'...", query);
            StatusChanged?.Invoke(this, "Invidious failed. Searching InnerTube...");
            var innerTubeRes = await _innerTube.SearchAsync(query, cancellationToken: publicSearchToken);
            if (innerTubeRes.IsSuccess && innerTubeRes.Value.Items.Count > 0)
            {
                RememberSearchPaging(query, _innerTube, innerTubeRes.Value);
                ActiveProvider = BackendProviderType.InnerTube;
                StatusChanged?.Invoke(this, $"Found {innerTubeRes.Value.Items.Count} videos via InnerTube.");
                return innerTubeRes;
            }
            _logger.LogWarning("InnerTube search returned 0 items or failed: {Error}", innerTubeRes.Error?.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Search cancelled by user.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during InnerTube search.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Tier 4: Local yt-dlp (uses caller cancellationToken)
        _logger.LogInformation("Falling back to local yt-dlp search for '{Query}'...", query);
        StatusChanged?.Invoke(this, "Public APIs failed. Falling back to local yt-dlp search...");
        var ytdlpRes = await _ytdlp.SearchAsync(query, cancellationToken: cancellationToken);
        if (ytdlpRes.IsSuccess)
        {
            RememberSearchPaging(query, _ytdlp, ytdlpRes.Value);
            ActiveProvider = BackendProviderType.YtDlpLocal;
            StatusChanged?.Invoke(this, $"Found {ytdlpRes.Value.Items.Count} videos via yt-dlp.");
        }

        return ytdlpRes;
    }

    private void RememberSearchPaging(string query, IBackendProvider provider, PagedResult<SearchResult> page)
    {
        _lastQuery = query;
        _lastSearchProvider = provider;
        _nextSearchPageToken = page.HasNextPage ? page.NextPageToken : null;
    }

    /// <summary>The next page of the last search, from the backend that served the first page.</summary>
    public async Task<Result<PagedResult<SearchResult>>> SearchMoreAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadMoreSearchResults || _lastQuery is null || _lastSearchProvider is null)
            return Result<PagedResult<SearchResult>>.Success(PagedResult<SearchResult>.Empty());

        var provider = _lastSearchProvider;
        var query = _lastQuery;
        var result = await provider.SearchAsync(query, _nextSearchPageToken, cancellationToken);
        if (result.IsSuccess && ReferenceEquals(provider, _lastSearchProvider) && query == _lastQuery)
        {
            _nextSearchPageToken = result.Value.HasNextPage && result.Value.Items.Count > 0 ? result.Value.NextPageToken : null;
        }
        return result;
    }

    /// <summary>
    /// Plays a video by YouTube URL or video ID with automatic multi-tier fallback
    /// and verified playback startup.
    /// </summary>
    public async Task<Result<bool>> PlayUrlOrIdAsync(string urlOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlOrId);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = YouTubeUrl.Validate(urlOrId);
        if (!validation.IsSuccess)
        {
            return Result<bool>.Failure(validation.Error!);
        }

        var videoId = validation.Value.VideoId;
        var startTime = validation.Value.StartTime;

        // Keep the queue position in step, so auto-advance continues after this video
        // rather than after whatever was current before.
        var queueIndex = _queue.FindIndex(i => i.VideoId.Value == videoId.Value);
        if (queueIndex >= 0 && startTime is null)
            return await PlayQueueFromAsync(queueIndex, raiseErrors: false, cancellationToken);

        return await PlayVideoIdInternalAsync(videoId, startTime, cancellationToken, raiseErrors: false);
    }

    /// <summary>
    /// Plays the queue from <paramref name="index"/>, skipping videos that can't be played (removed,
    /// private or blocked where the user is) so one bad song doesn't stop a whole playlist.
    /// </summary>
    private async Task<Result<bool>> PlayQueueFromAsync(int index, bool raiseErrors, CancellationToken cancellationToken)
    {
        Result<bool> last = Result<bool>.Failure(AppError.PlaybackFailed("The queue is empty."));
        int failures = 0;
        while (index >= 0 && index < _queue.Count)
        {
            _currentIndex = index;
            var item = _queue[index] with { IsPlayed = true };
            _queue[index] = item;
            QueueChanged?.Invoke(this, Queue);

            last = await PlayVideoIdInternalAsync(item.VideoId, startTime: null, cancellationToken, raiseErrors: false);
            if (last.IsSuccess)
                return last;

            failures++;
            bool willContinue = failures < MaxConsecutiveSkips && index + 1 < _queue.Count;
            ItemSkipped?.Invoke(this, new QueueSkipEventArgs(item, last.Error?.Message ?? string.Empty, willContinue));
            if (!willContinue)
                break;
            index++;
        }

        if (failures >= MaxConsecutiveSkips)
        {
            last = Result<bool>.Failure(AppError.PlaybackFailed(
                $"{failures} videos in a row couldn't be played, so the queue stopped. {last.Error?.Message}"));
        }
        if (raiseErrors && last.IsFailure)
        {
            ErrorOccurred?.Invoke(this, new PlaybackError
            {
                ErrorType = PlaybackErrorType.StreamUnreachable,
                UserMessage = last.Error?.Message ?? "This video couldn't be played."
            });
        }
        return last;
    }

    private async Task<Result<bool>> PlayVideoIdInternalAsync(
        YouTubeVideoId videoId,
        TimeSpan? startTime,
        CancellationToken cancellationToken,
        bool raiseErrors = true)
    {
        // Set when a source reports that YouTube won't play this video at all.
        string? unavailable = null;
        _logger.LogInformation("Starting resilient playback workflow for video ID {Id}...", videoId.Value);
        _playingVideoId = videoId.Value;
        var qualityPref = _configService.CurrentConfig.Playback.PreferredQuality;

        // Tier 1: TubeRepair Stream Resolution & Playback Confirmation
        try
        {
            StatusChanged?.Invoke(this, "Resolving stream via TubeRepair...");
            _logger.LogInformation("Resolving stream via TubeRepair for {Id}...", videoId.Value);
            var trStream = await _tubeRepair.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (trStream.IsSuccess && !string.IsNullOrWhiteSpace(trStream.Value.DirectPlaybackUrl))
            {
                StatusChanged?.Invoke(this, "Connecting to TubeRepair stream...");
                var started = await AttemptPlaybackAsync(
                    trStream.Value.DirectPlaybackUrl,
                    trStream.Value.AudioTrackUrl,
                    trStream.Value.Captions,
                    videoId,
                    startTime,
                    BackendProviderType.TubeRepair,
                    TimeSpan.FromSeconds(7),
                    cancellationToken);

                if (started)
                {
                    return Result<bool>.Success(true);
                }
                _logger.LogWarning("TubeRepair stream did not start playing. Falling back to Invidious...");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception attempting TubeRepair playback for {Id}.", videoId.Value);
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
                _logger.LogWarning("Invidious stream did not start playing. Falling back to InnerTube...");
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

        // Tier 3: InnerTube Stream Resolution & Playback Confirmation
        try
        {
            StatusChanged?.Invoke(this, "Falling back to InnerTube stream...");
            _logger.LogInformation("Resolving stream via InnerTube for {Id}...", videoId.Value);
            var innerTubeStream = await _innerTube.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (innerTubeStream.IsFailure && innerTubeStream.Error?.Message?.Contains("unavailable", StringComparison.OrdinalIgnoreCase) == true)
                unavailable = innerTubeStream.Error.Message;
            if (innerTubeStream.IsSuccess && !string.IsNullOrWhiteSpace(innerTubeStream.Value.DirectPlaybackUrl))
            {
                StatusChanged?.Invoke(this, "Connecting to InnerTube stream...");
                var started = await AttemptPlaybackAsync(
                    innerTubeStream.Value.DirectPlaybackUrl,
                    innerTubeStream.Value.AudioTrackUrl,
                    innerTubeStream.Value.Captions,
                    videoId,
                    startTime,
                    BackendProviderType.InnerTube,
                    TimeSpan.FromSeconds(7),
                    cancellationToken);

                if (started)
                {
                    return Result<bool>.Success(true);
                }
                _logger.LogWarning("InnerTube stream did not start playing. Falling back to yt-dlp...");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception attempting InnerTube playback for {Id}.", videoId.Value);
        }

        // Tier 4: Local yt-dlp Stream Resolution & Direct Watch URL Fallback
        try
        {
            StatusChanged?.Invoke(this, "Resolving stream via local yt-dlp...");
            _logger.LogInformation("Resolving stream via yt-dlp for {Id}...", videoId.Value);
            var ytdlpStream = await _ytdlp.ResolveStreamsAsync(videoId, qualityPref, cancellationToken: cancellationToken);
            if (ytdlpStream.IsFailure && ytdlpStream.Error?.Code == "VIDEO_NOT_FOUND")
                unavailable ??= ytdlpStream.Error.Message;
            else if (ytdlpStream.IsFailure)
                unavailable = null; // yt-dlp failed for another reason; the direct attempt may still work
            if (ytdlpStream.IsSuccess && !string.IsNullOrWhiteSpace(ytdlpStream.Value.DirectPlaybackUrl))
            {
                var started = await AttemptPlaybackAsync(
                    ytdlpStream.Value.DirectPlaybackUrl,
                    ytdlpStream.Value.AudioTrackUrl,
                    ytdlpStream.Value.Captions,
                    videoId,
                    startTime,
                    BackendProviderType.YtDlpLocal,
                    TimeSpan.FromSeconds(10),
                    cancellationToken);

                if (started)
                {
                    return Result<bool>.Success(true);
                }
                _logger.LogWarning("yt-dlp stream did not start playing. Falling back to direct URL...");
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

        // Tier 4 Fallback: Direct watch URL hook via mpv's bundled yt-dlp hook
        if (unavailable is not null)
        {
            _logger.LogWarning("YouTube reports {Id} as unavailable ({Reason}); not trying the direct URL.", videoId.Value, unavailable);
            return Fail(UnavailableMessage, raiseErrors, "VIDEO_UNAVAILABLE");
        }
        try
        {
            StatusChanged?.Invoke(this, "Playing via yt-dlp direct watch URL...");
            _logger.LogInformation("Attempting direct mpv yt-dlp hook playback for {Id}...", videoId.Value);
            var directWatchUrl = $"https://www.youtube.com/watch?v={videoId.Value}";

            var started = await AttemptPlaybackAsync(
                directWatchUrl,
                audioUrl: null,
                captions: Array.Empty<CaptionTrack>(),
                videoId,
                startTime,
                BackendProviderType.YtDlpLocal,
                TimeSpan.FromSeconds(25),
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
            _logger.LogWarning(ex, "Exception during direct mpv yt-dlp playback for {Id}.", videoId.Value);
        }

        // All tiers failed
        _logger.LogError("All playback tiers failed for video ID {Id}.", videoId.Value);
        return Fail("None of TimePlayer's sources could play this video. It may be private, age-restricted or blocked, or the sources may be having trouble; try again later.", raiseErrors);

        Result<bool> Fail(string message, bool raise, string code = "PLAYBACK_ENGINE_ERROR")
        {
            if (raise)
                ErrorOccurred?.Invoke(this, new PlaybackError { ErrorType = PlaybackErrorType.StreamUnreachable, UserMessage = message });
            StatusChanged?.Invoke(this, "Playback failed.");
            return Result<bool>.Failure(new AppError { Code = code, Message = message, IsRecoverable = code != "VIDEO_UNAVAILABLE" });
        }
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
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void StateHandler(object? sender, PlaybackStateChangedEventArgs e)
        {
            if (e.NewState == PlaybackState.Playing)
            {
                tcs.TrySetResult(true);
            }
            else if (e.NewState is PlaybackState.Error or PlaybackState.Stopped or PlaybackState.Ended)
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
            _isAttemptingPlayback = true;
            await _playerEngine.LoadAsync(mediaUrl, audioUrl, cancellationToken);

            if (_playerEngine.State == PlaybackState.Playing)
            {
                tcs.TrySetResult(true);
            }
            else if (_playerEngine.State is PlaybackState.Error or PlaybackState.Stopped or PlaybackState.Ended)
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
            captions = UsefulCaptions(captions);
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

            // Load default captions if available; some sources (TubeRepair) give none, so ask yt-dlp.
            if (captions.Count > 0)
            {
                await LoadDefaultCaptionAsync(captions, cancellationToken);
            }
            else
            {
                CaptionsLookupInProgress = true;
                _ = FetchCaptionsInBackgroundAsync(videoId);
            }

            StatusChanged?.Invoke(this, $"Playing via {provider}.");
            _logger.LogInformation("Playback successfully confirmed via {Provider}.", provider);

            // Asynchronously fetch full video details across providers
            _ = Task.Run(async () =>
            {
                try
                {
                    var detailsResult = await _tubeRepair.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    if (!detailsResult.IsSuccess)
                    {
                        detailsResult = await _invidious.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    }
                    if (!detailsResult.IsSuccess)
                    {
                        detailsResult = await _innerTube.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    }
                    if (!detailsResult.IsSuccess)
                    {
                        detailsResult = await _ytdlp.GetVideoDetailsAsync(videoId, CancellationToken.None);
                    }

                    // The user may have moved on while details were loading.
                    if (detailsResult.IsSuccess && _playingVideoId == videoId.Value)
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
            _isAttemptingPlayback = false;
            _playerEngine.StateChanged -= StateHandler;
            _playerEngine.ErrorOccurred -= ErrorHandler;
        }
    }

    /// <summary>True while captions are being looked up for a video whose stream came without them.</summary>
    public bool CaptionsLookupInProgress { get; private set; }

    /// <summary>Raised when captions arrive after playback started.</summary>
    public event EventHandler? CaptionsChanged;

    private async Task FetchCaptionsInBackgroundAsync(YouTubeVideoId videoId)
    {
        try
        {
            var details = await _ytdlp.GetVideoDetailsAsync(videoId, CancellationToken.None);
            if (_playingVideoId != videoId.Value)
                return; // the user moved on
            var captions = details.IsSuccess ? UsefulCaptions(details.Value.Captions) : Array.Empty<CaptionTrack>();
            _logger.LogInformation("Found {Count} caption tracks for {Id} via yt-dlp.", captions.Count, videoId.Value);
            CurrentCaptions = captions;
            if (captions.Count > 0)
                await LoadDefaultCaptionAsync(captions, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up captions for {Id}.", videoId.Value);
        }
        finally
        {
            if (_playingVideoId == videoId.Value)
            {
                CaptionsLookupInProgress = false;
                CaptionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task LoadDefaultCaptionAsync(IReadOnlyList<CaptionTrack> captions, CancellationToken cancellationToken)
    {
        var defaultCaption = ChooseDefaultCaption(captions);
        if (defaultCaption is null)
            return;
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

    /// <summary>
    /// Real captions in the user's language, then any real captions, then automatic captions in the
    /// video's own language ("-orig"), then automatic captions in the user's language.
    /// </summary>
    internal static CaptionTrack? ChooseDefaultCaption(IReadOnlyList<CaptionTrack> captions)
    {
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        bool IsLang(CaptionTrack c, string l) => c.LanguageCode.Equals(l, StringComparison.OrdinalIgnoreCase)
            || c.LanguageCode.StartsWith(l + "-", StringComparison.OrdinalIgnoreCase);
        var manual = captions.Where(c => !c.IsAutoGenerated).ToList();
        var auto = captions.Where(c => c.IsAutoGenerated).ToList();
        return manual.FirstOrDefault(c => c.IsDefault)
            ?? manual.FirstOrDefault(c => IsLang(c, lang))
            ?? manual.FirstOrDefault(c => IsLang(c, "en"))
            ?? manual.FirstOrDefault()
            ?? auto.FirstOrDefault(c => c.LanguageCode.EndsWith("-orig", StringComparison.OrdinalIgnoreCase))
            ?? auto.FirstOrDefault(c => IsLang(c, lang))
            ?? auto.FirstOrDefault();
    }

    /// <summary>
    /// YouTube lists automatic translations into about 150 languages; keep only the video's own
    /// language and the user's, so the captions list stays short enough to arrow through.
    /// </summary>
    internal static IReadOnlyList<CaptionTrack> UsefulCaptions(IReadOnlyList<CaptionTrack> captions)
    {
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return captions
            .Where(c => !c.IsAutoGenerated
                || c.LanguageCode.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)
                || c.LanguageCode.Equals(lang, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

        return await PlayQueueFromAsync(index, raiseErrors: true, cancellationToken);
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

    /// <summary>
    /// Starts a new queue with these videos, in one change: adding a long playlist item by item
    /// rebuilt the on-screen queue once per video and froze the window.
    /// </summary>
    public Task ReplaceQueueAsync(IEnumerable<SearchResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _queue.Clear();
        _queue.AddRange(items.Select(PlaybackQueueItem.FromSearchResult));
        _currentIndex = -1;
        QueueChanged?.Invoke(this, Queue);
        _logger.LogInformation("Queue replaced with {Count} items.", _queue.Count);
        return Task.CompletedTask;
    }

    public bool IsQueued(string videoId) => _queue.Any(i => i.VideoId.Value == videoId);

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
            await PlayQueueFromAsync(_currentIndex + 1, raiseErrors: true, cancellationToken);
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

    public async Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        switch (_playerEngine.State)
        {
            case PlaybackState.Playing:
                await _playerEngine.PauseAsync(cancellationToken);
                break;
            case PlaybackState.Paused:
                await _playerEngine.PlayAsync(cancellationToken);
                break;
            case PlaybackState.Ended:
                // Play after the end starts the video over.
                await _playerEngine.SeekAsync(TimeSpan.Zero, cancellationToken);
                await _playerEngine.PlayAsync(cancellationToken);
                break;
        }
    }

    public async Task SetCaptionTrackAsync(CaptionTrack? track, CancellationToken cancellationToken = default)
    {
        if (track == null)
        {
            await _playerEngine.SetSubtitleTrackAsync("no", cancellationToken);
            SelectedCaptionTrack = null;
        }
        else
        {
            await _playerEngine.LoadSubtitleTrackAsync(track.Url, selectImmediately: true, cancellationToken);
            SelectedCaptionTrack = track;
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
        if (e.NewState == PlaybackState.Ended && !_isAttemptingPlayback)
        {
            if (_currentIndex + 1 < _queue.Count)
            {
                _logger.LogInformation("Current video ended, auto-advancing to next queue item...");
                _ = PlayNextAsync(CancellationToken.None);
            }
            else
            {
                QueueEnded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnEngineErrorOccurred(object? sender, PlaybackErrorEventArgs e)
    {
        if (_isAttemptingPlayback)
        {
            _logger.LogWarning("Suppressed transient engine error during startup trial: {Error}", e.Message);
            return;
        }

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

/// <summary>A queued video was skipped because it couldn't be played.</summary>
public sealed class QueueSkipEventArgs : EventArgs
{
    public QueueSkipEventArgs(PlaybackQueueItem item, string reason, bool willContinue)
    {
        Item = item;
        Reason = reason;
        WillContinue = willContinue;
    }

    public PlaybackQueueItem Item { get; }
    public string Reason { get; }
    public bool WillContinue { get; }
}
