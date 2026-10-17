using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Backends.Piped;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Playback;

public class PlaybackCoordinatorTests
{
    private sealed class MockHealthService : IInstanceHealthService
    {
        private readonly List<PublicInstance> _instances;
        private int _index;

        public MockHealthService(params string[] instanceUrls)
        {
            _instances = instanceUrls.Select(u => new PublicInstance
            {
                BaseUri = new Uri(u),
                ProviderType = BackendProviderType.Piped
            }).ToList();
        }

        public Task<InstanceHealthMeasurement> ProbeInstanceAsync(PublicInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceHealthMeasurement { InstanceUri = instance.BaseUri, ProviderType = BackendProviderType.Piped, IsSuccessful = true });

        public Task<PublicInstance?> SelectBestInstanceAsync(BackendProviderType providerType, CancellationToken cancellationToken = default)
        {
            if (_instances.Count == 0) return Task.FromResult<PublicInstance?>(null);
            var inst = _instances[_index % _instances.Count];
            _index++;
            return Task.FromResult<PublicInstance?>(inst);
        }

        public void RecordSuccess(PublicInstance instance, TimeSpan latency) { }
        public void RecordFailure(PublicInstance instance, string reason) { }
    }

    private readonly FakePlayerEngine _playerEngine = new();
    private readonly ConfigurationService _configService;
    private readonly MockHealthService _healthService = new("https://piped.video");
    private readonly FakeProcessRunner _processRunner = new();
    private readonly FakeYtDlpManager _ytDlpManager = new();

    private readonly PipedBackendProvider _piped;
    private readonly InvidiousBackendProvider _invidious;
    private readonly YtDlpBackendProvider _ytdlp;

    public PlaybackCoordinatorTests()
    {
        _configService = new ConfigurationService(Options.Create(new AppConfig()));
        var httpHandler = MockHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(httpHandler);

        _piped = new PipedBackendProvider(
            httpClient,
            _healthService,
            _configService,
            NullLogger<PipedBackendProvider>.Instance);

        _invidious = new InvidiousBackendProvider(
            httpClient,
            _healthService,
            _configService,
            NullLogger<InvidiousBackendProvider>.Instance);

        _ytdlp = new YtDlpBackendProvider(
            _ytDlpManager,
            _processRunner,
            _configService,
            NullLogger<YtDlpBackendProvider>.Instance);
    }

    private PlaybackCoordinator CreateCoordinator()
    {
        return new PlaybackCoordinator(
            _piped,
            _invidious,
            _ytdlp,
            _playerEngine,
            _configService,
            NullLogger<PlaybackCoordinator>.Instance);
    }

    [Fact]
    public async Task EnqueueAsync_AddsItemAndFiresQueueChanged()
    {
        await using var coordinator = CreateCoordinator();

        bool eventFired = false;
        coordinator.QueueChanged += (s, q) => eventFired = true;

        var result = new SearchResult
        {
            Id = "dQw4w9WgXcQ",
            Title = "Rick Astley - Never Gonna Give You Up",
            ChannelTitle = "RickAstleyVEVO",
            Duration = TimeSpan.FromSeconds(212)
        };

        await coordinator.EnqueueAsync(result);

        Assert.True(eventFired);
        Assert.Single(coordinator.Queue);
        Assert.Equal("dQw4w9WgXcQ", coordinator.Queue[0].VideoId.Value);
        Assert.Equal("Rick Astley - Never Gonna Give You Up", coordinator.Queue[0].Title);
    }

    [Fact]
    public async Task PlayUrlOrIdAsync_InvalidFormat_ReturnsFailureResult()
    {
        await using var coordinator = CreateCoordinator();

        var res = await coordinator.PlayUrlOrIdAsync("not_a_valid_youtube_video");
        Assert.False(res.IsSuccess);
        Assert.Equal("INVALID_YOUTUBE_URL", res.Error?.Code);
    }

    [Fact]
    public async Task QueueNavigation_PlayNextAndPrevious_UpdatesCurrentItem()
    {
        await using var coordinator = CreateCoordinator();

        var item1 = new SearchResult { Id = "dQw4w9WgXcQ", Title = "Video 1", ChannelTitle = "Chan 1", Duration = TimeSpan.FromMinutes(3) };
        var item2 = new SearchResult { Id = "jNQXAC9IVRw", Title = "Video 2", ChannelTitle = "Chan 2", Duration = TimeSpan.FromMinutes(1) };

        await coordinator.EnqueueAsync(item1);
        await coordinator.EnqueueAsync(item2);

        // Play first queue item
        await coordinator.PlayQueueItemAsync(coordinator.Queue[0].QueueItemId);
        Assert.NotNull(coordinator.CurrentQueueItem);
        Assert.Equal("dQw4w9WgXcQ", coordinator.CurrentQueueItem.VideoId.Value);

        // Advance to next
        await coordinator.PlayNextAsync();
        Assert.NotNull(coordinator.CurrentQueueItem);
        Assert.Equal("jNQXAC9IVRw", coordinator.CurrentQueueItem.VideoId.Value);

        // Back to previous
        await coordinator.PlayPreviousAsync();
        Assert.NotNull(coordinator.CurrentQueueItem);
        Assert.Equal("dQw4w9WgXcQ", coordinator.CurrentQueueItem.VideoId.Value);
    }

    [Fact]
    public async Task AutoAdvance_WhenEngineReachesEnded_AdvancesQueue()
    {
        await using var coordinator = CreateCoordinator();

        var item1 = new SearchResult { Id = "dQw4w9WgXcQ", Title = "Video 1", ChannelTitle = "Chan 1", Duration = TimeSpan.FromMinutes(3) };
        var item2 = new SearchResult { Id = "jNQXAC9IVRw", Title = "Video 2", ChannelTitle = "Chan 2", Duration = TimeSpan.FromMinutes(1) };

        await coordinator.EnqueueAsync(item1);
        await coordinator.EnqueueAsync(item2);

        await coordinator.PlayQueueItemAsync(coordinator.Queue[0].QueueItemId);
        Assert.Equal("dQw4w9WgXcQ", coordinator.CurrentQueueItem?.VideoId.Value);

        // Engine notifies video ended
        _playerEngine.RaiseState(PlaybackState.Ended);

        // Wait brief async completion
        await Task.Delay(100);

        // Coordinator should have advanced to item 2
        Assert.Equal("jNQXAC9IVRw", coordinator.CurrentQueueItem?.VideoId.Value);
    }

    [Fact]
    public async Task SetCaptionTrackAsync_LoadsSubtitleInEngine()
    {
        await using var coordinator = CreateCoordinator();

        var caption = new CaptionTrack
        {
            DisplayName = "English",
            LanguageCode = "en",
            Url = "https://subtitles.vtt"
        };

        await coordinator.SetCaptionTrackAsync(caption);

        Assert.Equal(caption, coordinator.SelectedCaptionTrack);
        Assert.Contains("https://subtitles.vtt", _playerEngine.LoadedSubtitleUrls);
    }
}
