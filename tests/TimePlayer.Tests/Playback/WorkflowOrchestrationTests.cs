using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.Piped;
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
/// Verifies multi-tier fallback (Piped -> Invidious -> yt-dlp -> direct hook),
/// verified playback startup, stream selection safety (no mute video-only streams),
/// cancellation, and actionable error handling.
/// </summary>
public class WorkflowOrchestrationTests
{
    private readonly FakePlayerEngine _playerEngine = new();
    private readonly ConfigurationService _configService;
    private readonly FakeBackendProvider _pipedFake = new(BackendProviderType.Piped);
    private readonly FakeBackendProvider _invidiousFake = new(BackendProviderType.Invidious);
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
            _pipedFake,
            _invidiousFake,
            _ytdlpFake,
            _playerEngine,
            _configService,
            NullLogger<PlaybackCoordinator>.Instance);
    }

    // ==========================================
    // 1. SEARCH WORKFLOW ORCHESTRATION TESTS
    // ==========================================

    [Fact]
    public async Task Search_HappyPath_UsesPipedFirst()
    {
        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("rick roll");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("Piped", result.Value.Items[0].Title);
        Assert.Equal(1, _pipedFake.SearchCallCount);
        Assert.Equal(0, _invidiousFake.SearchCallCount);
        Assert.Equal(0, _ytdlpFake.SearchCallCount);
        Assert.Equal(BackendProviderType.Piped, coordinator.ActiveProvider);
    }

    [Fact]
    public async Task Search_PipedFails_FallsBackToInvidious()
    {
        _pipedFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Piped down")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("classical music");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("Invidious", result.Value.Items[0].Title);
        Assert.Equal(1, _pipedFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
        Assert.Equal(0, _ytdlpFake.SearchCallCount);
        Assert.Equal(BackendProviderType.Invidious, coordinator.ActiveProvider);
    }

    [Fact]
    public async Task Search_PipedAndInvidiousFail_FallsBackToYtDlp()
    {
        _pipedFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Piped down")));
        _invidiousFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Invidious down")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("indie folk");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Contains("YtDlpLocal", result.Value.Items[0].Title);
        Assert.Equal(1, _pipedFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
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

        Assert.Equal(0, _pipedFake.SearchCallCount);
    }

    [Fact]
    public async Task Search_AllBackendsFail_ReturnsFailureResult()
    {
        _pipedFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Piped error")));
        _invidiousFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.NetworkError("Invidious error")));
        _ytdlpFake.SearchHandler = (q, token, ct) =>
            Task.FromResult(Result<PagedResult<SearchResult>>.Failure(AppError.PlaybackFailed("yt-dlp failed")));

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.SearchAsync("anything");

        Assert.False(result.IsSuccess);
        Assert.Equal(1, _pipedFake.SearchCallCount);
        Assert.Equal(1, _invidiousFake.SearchCallCount);
        Assert.Equal(1, _ytdlpFake.SearchCallCount);
    }

    // ==========================================
    // 2. PLAYBACK WORKFLOW ORCHESTRATION TESTS
    // ==========================================

    [Fact]
    public async Task Playback_HappyPath_PipedStreamStartsPlaying()
    {
        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(1, _pipedFake.ResolveStreamsCallCount);
        Assert.Equal(0, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(0, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.Piped, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("Piped", _playerEngine.LoadedUrls[0]);
    }

    [Fact]
    public async Task Playback_PipedFailsStartup_FallsBackToInvidious()
    {
        // Piped stream resolves, but player engine fails to start when loading Piped URL
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) =>
        {
            if (mediaUrl.Contains("Piped"))
            {
                // Simulate HTTP 403 or decode failure on Piped stream
                return Task.FromResult(false);
            }
            return Task.FromResult(true); // Invidious stream succeeds
        };

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _pipedFake.ResolveStreamsCallCount);
        Assert.Equal(1, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(0, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.Invidious, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("Invidious", _playerEngine.LoadedUrls[1]);
    }

    [Fact]
    public async Task Playback_PipedAndInvidiousFailStartup_FallsBackToYtDlpResolved()
    {
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) =>
        {
            if (mediaUrl.Contains("Piped") || mediaUrl.Contains("Invidious"))
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true); // yt-dlp resolved stream succeeds
        };

        await using var coordinator = CreateCoordinator();

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _pipedFake.ResolveStreamsCallCount);
        Assert.Equal(1, _invidiousFake.ResolveStreamsCallCount);
        Assert.Equal(1, _ytdlpFake.ResolveStreamsCallCount);
        Assert.Equal(BackendProviderType.YtDlpLocal, coordinator.ActiveProvider);
        Assert.Equal(PlaybackState.Playing, _playerEngine.State);
        Assert.Contains("YtDlpLocal", _playerEngine.LoadedUrls[2]);
    }

    [Fact]
    public async Task Playback_AllResolversFail_DirectWatchUrlFallbackPlays()
    {
        // Both API resolvers fail stream resolution completely
        _pipedFake.ResolveStreamsHandler = (id, q, a, ct) =>
            Task.FromResult(Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(id.Value, "Piped streams unavailable")));
        _invidiousFake.ResolveStreamsHandler = (id, q, a, ct) =>
            Task.FromResult(Result<StreamResolutionResult>.Failure(AppError.StreamUnavailable(id.Value, "Invidious streams unavailable")));
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
        // Every stream and direct URL fails
        _playerEngine.LoadHandler = (mediaUrl, audioUrl) => Task.FromResult(false);

        await using var coordinator = CreateCoordinator();

        PlaybackError? receivedError = null;
        coordinator.ErrorOccurred += (s, e) => receivedError = e;

        var result = await coordinator.PlayUrlOrIdAsync("dQw4w9WgXcQ");

        Assert.False(result.IsSuccess);
        Assert.NotNull(receivedError);
        Assert.Equal(PlaybackErrorType.StreamUnreachable, receivedError.ErrorType);
        Assert.Contains("Unable to play video after trying Piped, Invidious, and local yt-dlp fallbacks", receivedError.UserMessage);
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

    // ==========================================
    // 3. STREAM SELECTION SAFETY (NO MUTE VIDEO-ONLY)
    // ==========================================

    [Fact]
    public async Task StreamSelection_SafeRejectionOfMuteVideoOnlyStream_WhenCompanionAudioMissing()
    {
        // JSON response from Piped with a 1080p DASH video-only stream and a 720p progressive stream, NO audio-only streams
        var pipedStreamsJson = """
        {
          "title": "Test Video",
          "description": "Desc",
          "uploader": "Uploader",
          "uploaderUrl": "/channel/123",
          "duration": 200,
          "videoStreams": [
            {
              "url": "https://piped.video/mute1080p.mp4",
              "format": "mp4",
              "quality": "1080p",
              "mimeType": "video/mp4",
              "codec": "avc1",
              "videoOnly": true,
              "height": 1080,
              "width": 1920,
              "fps": 30,
              "bitrate": 4000000
            },
            {
              "url": "https://piped.video/prog720p.mp4",
              "format": "mp4",
              "quality": "720p",
              "mimeType": "video/mp4",
              "codec": "avc1",
              "videoOnly": false,
              "height": 720,
              "width": 1280,
              "fps": 30,
              "bitrate": 2000000
            }
          ],
          "audioStreams": []
        }
        """;

        var mockHandler = MockHttpMessageHandler.StringResponse(pipedStreamsJson);
        var httpClient = new HttpClient(mockHandler);
        var healthService = new MockInstanceHealthService("https://piped.video");

        var pipedProvider = new PipedBackendProvider(
            httpClient,
            healthService,
            _configService,
            NullLogger<PipedBackendProvider>.Instance);

        var resolution = await pipedProvider.ResolveStreamsAsync(
            new YouTubeVideoId("dQw4w9WgXcQ"),
            QualityPreference.Auto);

        Assert.True(resolution.IsSuccess);
        // Must NOT pick the 1080p mute stream since no audio stream exists to companion it!
        // It must select the 720p progressive stream that has embedded audio.
        Assert.Equal("https://piped.video/prog720p.mp4", resolution.Value.DirectPlaybackUrl);
        Assert.True(resolution.Value.SelectedVideoStream.IsProgressive);
        Assert.Null(resolution.Value.AudioTrackUrl);
    }

    [Fact]
    public async Task StreamSelection_AllowsVideoOnlyStream_WhenCompanionAudioIsPresent()
    {
        // JSON response with 1080p video-only stream AND companion audio stream
        var pipedStreamsJson = """
        {
          "title": "Test Video",
          "description": "Desc",
          "uploader": "Uploader",
          "uploaderUrl": "/channel/123",
          "duration": 200,
          "videoStreams": [
            {
              "url": "https://piped.video/video1080p.mp4",
              "format": "mp4",
              "quality": "1080p",
              "mimeType": "video/mp4",
              "codec": "avc1",
              "videoOnly": true,
              "height": 1080,
              "width": 1920,
              "fps": 30,
              "bitrate": 4000000
            }
          ],
          "audioStreams": [
            {
              "url": "https://piped.video/audio128k.m4a",
              "format": "m4a",
              "quality": "128 kbps",
              "mimeType": "audio/mp4",
              "codec": "mp4a",
              "bitrate": 128000
            }
          ]
        }
        """;

        var mockHandler = MockHttpMessageHandler.StringResponse(pipedStreamsJson);
        var httpClient = new HttpClient(mockHandler);
        var healthService = new MockInstanceHealthService("https://piped.video");

        var pipedProvider = new PipedBackendProvider(
            httpClient,
            healthService,
            _configService,
            NullLogger<PipedBackendProvider>.Instance);

        var resolution = await pipedProvider.ResolveStreamsAsync(
            new YouTubeVideoId("dQw4w9WgXcQ"),
            QualityPreference.Auto);

        Assert.True(resolution.IsSuccess);
        // When audio companion is available, it can pick the 1080p DASH video track and provide the companion audio URL
        Assert.Equal("https://piped.video/video1080p.mp4", resolution.Value.DirectPlaybackUrl);
        Assert.Equal("https://piped.video/audio128k.m4a", resolution.Value.AudioTrackUrl);
        Assert.NotNull(resolution.Value.SelectedAudioStream);
    }

    // ==========================================
    // HELPER MOCK HEALTH SERVICE
    // ==========================================
    private sealed class MockInstanceHealthService : IInstanceHealthService
    {
        private readonly PublicInstance _instance;

        public MockInstanceHealthService(string url)
        {
            _instance = new PublicInstance
            {
                BaseUri = new Uri(url),
                ProviderType = BackendProviderType.Piped
            };
        }

        public Task<InstanceHealthMeasurement> ProbeInstanceAsync(PublicInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceHealthMeasurement { InstanceUri = instance.BaseUri, ProviderType = BackendProviderType.Piped, IsSuccessful = true });

        public Task<PublicInstance?> SelectBestInstanceAsync(BackendProviderType providerType, CancellationToken cancellationToken = default) =>
            Task.FromResult<PublicInstance?>(_instance);

        public void RecordSuccess(PublicInstance instance, TimeSpan latency) { }
        public void RecordFailure(PublicInstance instance, string reason) { }
    }
}
