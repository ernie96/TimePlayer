using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.TubeRepair;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Playback;

/// <summary>
/// Hermetic end-to-end orchestration tests for the complete search-to-playback workflow.
/// Verifies exact 4-tier ordered priority (TubeRepair -> Invidious -> InnerTube -> yt-dlp),
/// verified playback startup, stream selection safety (no mute video-only streams),
/// cancellation, and actionable error handling.
/// </summary>
public class WorkflowOrchestrationTests
{
    private readonly FakePlayerEngine _playerEngine = new();
    private readonly ConfigurationService _configService;
    private readonly FakeBackendProvider _tubeRepairFake = new(BackendProviderType.TubeRepair);
    private readonly FakeBackendProvider _invidiousFake = new(BackendProviderType.Invidious);
    private readonly FakeBackendProvider _innerTubeFake = new(BackendProviderType.InnerTube);
    private readonly FakeBackendProvider _ytdlpFake = new(BackendProviderType.YtDlpLocal);

    public WorkflowOrchestrationTests()
    {
        var config = new AppConfig
        {
            Backend = new BackendConfig
            {
                RequestTimeoutSeconds = 2,
                MaxRetriesPerProvider = 1
            }
        };
        _configService = new ConfigurationService(Options.Create(config));
    }

    private PlaybackCoordinator CreateCoordinator()
    {
        return new PlaybackCoordinator(
            _tubeRepairFake,
            _invidiousFake,
            _innerTubeFake,
            _ytdlpFake,
            _playerEngine,
            _configService,
            NullLogger<PlaybackCoordinator>.Instance);
    }

    // ==========================================
    // 1. SEARCH WORKFLOW ORCHESTRATION TESTS
    // ==========================================

    [Fact]
    public async Task Search_HappyPath_UsesTubeRepairFirst()
    {
        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("rick roll");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("TubeRepair", result.Value.Items[0].Title);
        Assert.Equal(1, _tubeRepairFake.SearchCallCount);
        Assert.Equal(0, _invidiousFake.SearchCallCount);
        Assert.Equal(0, _innerTubeFake.SearchCallCount);
        Assert.Equal(0, _ytdlpFake.SearchCallCount);
        Assert.Equal(BackendProviderType.TubeRepair, coordinator.ActiveProvider);
    }

    [Fact]
    public async Task Search_TubeRepairFails_FallsBackToInvidious()
    {
        _tubeRepairFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("TubeRepair down")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("classical music");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("Invidious", result.Value.Items[0].Title);
        Assert.Equal(1, _tubeRepairFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
        Assert.Equal(0, _innerTubeFake.SearchCallCount);
        Assert.Equal(0, _ytdlpFake.SearchCallCount);
        Assert.Equal(BackendProviderType.Invidious, coordinator.ActiveProvider);
    }

    [Fact]
    public async Task Search_TubeRepairAndInvidiousFail_FallsBackToInnerTube()
    {
        _tubeRepairFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("TubeRepair down")));
        _invidiousFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Invidious down")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("jazz beats");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("InnerTube", result.Value.Items[0].Title);
        Assert.Equal(1, _tubeRepairFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
        Assert.Equal(1, _innerTubeFake.SearchCallCount);
        Assert.Equal(0, _ytdlpFake.SearchCallCount);
        Assert.Equal(BackendProviderType.InnerTube, coordinator.ActiveProvider);
    }

    [Fact]
    public async Task Search_FirstThreeFail_FallsBackToYtDlp()
    {
        _tubeRepairFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("TubeRepair down")));
        _invidiousFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Invidious down")));
        _innerTubeFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("InnerTube down")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("indie folk");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("YtDlpLocal", result.Value.Items[0].Title);
        Assert.Equal(1, _tubeRepairFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
        Assert.Equal(1, _innerTubeFake.SearchCallCount);
        Assert.Equal(1, _ytdlpFake.SearchCallCount);
        Assert.Equal(BackendProviderType.YtDlpLocal, coordinator.ActiveProvider);
    }

    [Fact]
    public async Task Search_Cancellation_AbortsPromptly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        await using var coordinator = CreateCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await coordinator.SearchAsync("test query", cts.Token);
        });

        Assert.Equal(0, _tubeRepairFake.SearchCallCount);
    }

    [Fact]
    public async Task Search_AllBackendsFail_ReturnsFailureResult()
    {
        _tubeRepairFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("TubeRepair error")));
        _invidiousFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Invidious error")));
        _innerTubeFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("InnerTube error")));
        _ytdlpFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.PlaybackFailed("yt-dlp failed")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("anything");

        Assert.False(result.IsSuccess);
        Assert.Equal(1, _tubeRepairFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
        Assert.Equal(1, _innerTubeFake.SearchCallCount);
        Assert.Equal(1, _ytdlpFake.SearchCallCount);
    }

    // ==========================================
    // 2. PLAYBACK WORKFLOW ORCHESTRATION TESTS
    // ==========================================

    [Fact]
    public async Task Playback_HappyPath_TubeRepairStreamStartsPlaying()
    {
        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(1, _tubeRepairFake.ResolveStreamsCallCount);
        Assert.Equal(0, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(0, _innerTubeFake.ResolveStreamsCallCount);
        Assert.Equal(0, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.TubeRepair, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("TubeRepair", _playerEngine.LoadedUrls[0]);
    }

    [Fact]
    public async Task Playback_TubeRepairFailsStartup_FallsBackToInvidious()
    {
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) =>
        {
            if (mediaUrl.Contains("TubeRepair"))
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true); // Invidious stream succeeds
        };

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _tubeRepairFake.ResolveStreamsCallCount);
        Assert.Equal(1, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(0, _innerTubeFake.ResolveStreamsCallCount);
        Assert.Equal(0, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.Invidious, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("Invidious", _playerEngine.LoadedUrls[1]);
    }

    [Fact]
    public async Task Playback_TubeRepairAndInvidiousFailStartup_FallsBackToInnerTube()
    {
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) =>
        {
            if (mediaUrl.Contains("TubeRepair") || mediaUrl.Contains("Invidious"))
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true); // InnerTube stream succeeds
        };

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _tubeRepairFake.ResolveStreamsCallCount);
        Assert.Equal(1, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(1, _innerTubeFake.ResolveStreamsCallCount);
        Assert.Equal(0, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.InnerTube, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("InnerTube", _playerEngine.LoadedUrls[2]);
    }

    [Fact]
    public async Task Playback_FirstThreeFailStartup_FallsBackToYtDlpResolved()
    {
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) =>
        {
            if (mediaUrl.Contains("TubeRepair") || mediaUrl.Contains("Invidious") || mediaUrl.Contains("InnerTube"))
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true); // yt-dlp resolved stream succeeds
        };

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _tubeRepairFake.ResolveStreamsCallCount);
        Assert.Equal(1, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(1, _innerTubeFake.ResolveStreamsCallCount);
        Assert.Equal(1, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.YtDlpLocal, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("YtDlpLocal", _playerEngine.LoadedUrls[3]);
    }

    [Fact]
    public async Task Playback_AllResolversFail_DirectWatchUrlFallbackPlays()
    {
        _tubeRepairFake.ResolveStreamsHandler = (id, q, a, ct) =>
            Task.FromResult(Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(id.Value, "TubeRepair streams unavailable")));
        _invidiousFake.ResolveStreamsHandler = (id, q, a, ct) =>
            Task.FromResult(Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(id.Value, "Invidious streams unavailable")));
        _innerTubeFake.ResolveStreamsHandler = (id, q, a, ct) =>
            Task.FromResult(Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(id.Value, "InnerTube streams unavailable")));
        _ytdlpFake.ResolveStreamsHandler = (id, q, a, ct) =>
            Task.FromResult(Result<StreamResolutionResult>.Failure(AppError.PlaybackFailed("yt-dlp extraction failed")));

        // Engine succeeds on the direct watch URL
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) =>
        {
            if (mediaUrl.StartsWith("https://www.youtube.com/watch?v="))
            {
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        };

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.Equal(BackendProviderType.YtDlpLocal, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("https://www.youtube.com/watch?v=dQw4w9WgXcQ", _playerEngine.LoadedUrls);
    }

    [Fact]
    public async Task Playback_AllFallbacksFail_FiresErrorAndReturnsFailureWithoutInfiniteLoop()
    {
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) => Task.FromResult(false);

        await using var coordinator = CreateCoordinator();

        PlaybackError? receivedError = null;
        coordinator.ErrorOccurred += (s, e) => receivedError = e;

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        // A direct play reports its failure to the caller only; the event too would make it speak twice.
        Assert.False(result.IsSuccess);
        Assert.Null(receivedError);
        Assert.Contains("None of TimePlayer's sources could play this video", result.Error!.Message);

        // Playing from the queue has no caller waiting, so the event carries the problem.
        await coordinator.EnqueueAsync(new SearchResult { Id = "jNQXAC9IVRw", Title = "Queued", ChannelTitle = "c" });
        await coordinator.PlayQueueItemAsync(coordinator.Queue[^1].QueueItemId);
        Assert.NotNull(receivedError);
        Assert.Equal(PlaybackErrorType.StreamUnreachable, receivedError.ErrorType);
        Assert.Contains("None of TimePlayer's sources could play this video", receivedError.UserMessage);
    }

    [Fact]
    public async Task Playback_UrlWithTimestamp_AppliesSeekToStartPosition()
    {
        await using var coordinator = CreateCoordinator();

        // 1m25s = 85 seconds
        var result = await coordinator.PlayUrlOrIdAsync("https://youtu.be/dQw4w9WgXcQ?t=85");

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(85), _playerEngine.CurrentTime);
    }
}
