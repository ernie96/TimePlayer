using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class InvidiousBackendProviderTests
{
    private sealed class MockHealthService : IInstanceHealthService
    {
        private readonly List<PublicInstance> _instances;
        private int _index;

        public List<(PublicInstance instance, string reason)> RecordedFailures { get; } = new();
        public List<(PublicInstance instance, TimeSpan latency)> RecordedSuccesses { get; } = new();

        public MockHealthService(params string[] instanceUrls)
        {
            _instances = instanceUrls.Select(u => new PublicInstance
            {
                BaseUri = new Uri(u),
                ProviderType = BackendProviderType.Invidious
            }).ToList();
        }

        public Task<InstanceHealthMeasurement> ProbeInstanceAsync(PublicInstance instance, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new InstanceHealthMeasurement
            {
                InstanceUri = instance.BaseUri,
                ProviderType = BackendProviderType.Invidious,
                IsSuccessful = true
            });
        }

        public Task<PublicInstance?> SelectBestInstanceAsync(BackendProviderType providerType, CancellationToken cancellationToken = default)
        {
            if (_instances.Count == 0) return Task.FromResult<PublicInstance?>(null);
            var inst = _instances[_index % _instances.Count];
            _index++;
            return Task.FromResult<PublicInstance?>(inst);
        }

        public void RecordSuccess(PublicInstance instance, TimeSpan latency)
        {
            RecordedSuccesses.Add((instance, latency));
        }

        public void RecordFailure(PublicInstance instance, string reason)
        {
            RecordedFailures.Add((instance, reason));
        }
    }

    private static readonly string SampleInvidiousSearchJson = """
        [
          {
            "type": "video",
            "title": "Never Gonna Give You Up",
            "videoId": "dQw4w9WgXcQ",
            "author": "Rick Astley",
            "authorId": "UCuAXFkgsw1L7xaCfnd5JJOw",
            "authorUrl": "/channel/UCuAXFkgsw1L7xaCfnd5JJOw",
            "authorVerified": true,
            "videoThumbnails": [
              { "quality": "medium", "url": "https://invidious.f5.si/vi/dQw4w9WgXcQ/mqdefault.jpg", "width": 320, "height": 180 },
              { "quality": "high", "url": "https://invidious.f5.si/vi/dQw4w9WgXcQ/hqdefault.jpg", "width": 480, "height": 360 }
            ],
            "description": "Official Music Video for Rick Astley",
            "viewCount": 1500000000,
            "publishedText": "14 years ago",
            "lengthSeconds": 212,
            "liveNow": false
          },
          {
            "type": "channel",
            "title": "Rick Astley",
            "author": "Rick Astley"
          },
          {
            "type": "video",
            "title": "Together Forever",
            "videoId": "yPYZpwSpKmA",
            "author": "Rick Astley",
            "lengthSeconds": 204,
            "viewCount": 200000000
          }
        ]
        """;

    private static readonly string SampleInvidiousVideoJson = """
        {
          "title": "Never Gonna Give You Up",
          "videoId": "dQw4w9WgXcQ",
          "description": "Official Music Video",
          "publishedText": "14 years ago",
          "author": "Rick Astley",
          "authorId": "UCuAXFkgsw1L7xaCfnd5JJOw",
          "authorUrl": "/channel/UCuAXFkgsw1L7xaCfnd5JJOw",
          "authorVerified": true,
          "authorThumbnails": [
            { "url": "https://invidious.f5.si/avatar.jpg", "width": 176, "height": 176 }
          ],
          "lengthSeconds": 212,
          "viewCount": 1500000000,
          "likeCount": 16000000,
          "liveNow": false,
          "formatStreams": [
            {
              "url": "https://invidious.f5.si/videoplayback_720p.mp4",
              "quality": "hd720",
              "qualityLabel": "720p",
              "resolution": "1280x720",
              "container": "mp4",
              "encoding": "avc1.4d401f",
              "bitrate": "2148480"
            },
            {
              "url": "https://invidious.f5.si/videoplayback_360p.mp4",
              "quality": "medium",
              "qualityLabel": "360p",
              "resolution": "640x360",
              "container": "mp4",
              "encoding": "avc1.42001e",
              "bitrate": 850000
            }
          ],
          "adaptiveFormats": [
            {
              "url": "https://invidious.f5.si/audio_128k.m4a",
              "type": "audio/mp4; codecs=\"mp4a.40.2\"",
              "bitrate": "128000",
              "container": "m4a",
              "audioQuality": "AUDIO_QUALITY_MEDIUM"
            },
            {
              "url": "https://invidious.f5.si/video_1080p.mp4",
              "type": "video/mp4; codecs=\"avc1.64002a\"",
              "qualityLabel": "1080p",
              "resolution": "1920x1080",
              "fps": 60,
              "bitrate": "4500000",
              "container": "mp4"
            }
          ],
          "captions": [
            {
              "label": "English",
              "language_code": "en",
              "url": "https://invidious.f5.si/api/v1/captions/dQw4w9WgXcQ?label=English"
            }
          ]
        }
        """;

    [Fact]
    public async Task SearchAsync_ParsesVideoItemsAndComputesPagination()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleInvidiousSearchJson);
        var health = new MockHealthService("https://invidious.f5.si/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new InvidiousBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<InvidiousBackendProvider>.Instance);

        var result = await provider.SearchAsync("rick astley", pageToken: "1");

        Assert.True(result.IsSuccess);
        var paged = result.Value;
        Assert.NotNull(paged);
        Assert.Equal(2, paged.Items.Count); // Channel filtered out, 2 videos remain
        Assert.Equal(1, paged.PageIndex);
        Assert.True(paged.HasNextPage);
        Assert.Equal("2", paged.NextPageToken); // Next page is 2

        var first = paged.Items[0];
        Assert.Equal("dQw4w9WgXcQ", first.Id);
        Assert.Equal("Never Gonna Give You Up", first.Title);
        Assert.Equal("Rick Astley", first.ChannelTitle);
        Assert.Equal(TimeSpan.FromSeconds(212), first.Duration);
        Assert.Equal(1_500_000_000, first.ViewCount);
        Assert.Equal("https://invidious.f5.si/vi/dQw4w9WgXcQ/hqdefault.jpg", first.ThumbnailUrl); // Picked high quality
    }

    [Fact]
    public async Task GetVideoDetailsAsync_ParsesFormatStreamsAndAdaptiveFormats()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleInvidiousVideoJson);
        var health = new MockHealthService("https://invidious.f5.si/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new InvidiousBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<InvidiousBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.GetVideoDetailsAsync(videoId);

        Assert.True(result.IsSuccess);
        var details = result.Value;
        Assert.NotNull(details);
        Assert.Equal("Never Gonna Give You Up", details.Title);
        Assert.Equal("Rick Astley", details.Channel.Name);
        Assert.Equal(TimeSpan.FromSeconds(212), details.Duration);
        Assert.Equal(4, details.Streams.Count); // 2 formatStreams (progressive) + 2 adaptiveFormats
        Assert.Single(details.Captions);
        Assert.Equal("en", details.Captions[0].LanguageCode);
    }

    [Fact]
    public async Task ResolveStreamsAsync_Preferred720p_PicksProgressiveStream()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleInvidiousVideoJson);
        var health = new MockHealthService("https://invidious.f5.si/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new InvidiousBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<InvidiousBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.ResolveStreamsAsync(videoId, QualityPreference.P720);

        Assert.True(result.IsSuccess);
        var res = result.Value;
        Assert.NotNull(res);
        Assert.Equal("720p", res.SelectedVideoStream.QualityLabel);
        Assert.True(res.SelectedVideoStream.IsProgressive);
        Assert.Equal("https://invidious.f5.si/videoplayback_720p.mp4", res.DirectPlaybackUrl);
        Assert.Null(res.AudioTrackUrl); // Progressive stream already has audio!
    }

    [Fact]
    public async Task ResolveStreamsAsync_Preferred1080p_PicksAdaptiveVideoAndPairsAudioTrack()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleInvidiousVideoJson);
        var health = new MockHealthService("https://invidious.f5.si/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new InvidiousBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<InvidiousBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.ResolveStreamsAsync(videoId, QualityPreference.P1080);

        Assert.True(result.IsSuccess);
        var res = result.Value;
        Assert.NotNull(res);
        Assert.Equal("1080p", res.SelectedVideoStream.QualityLabel);
        Assert.False(res.SelectedVideoStream.IsProgressive);
        Assert.Equal("https://invidious.f5.si/video_1080p.mp4", res.DirectPlaybackUrl);
        Assert.NotNull(res.AudioTrackUrl);
        Assert.Equal("https://invidious.f5.si/audio_128k.m4a", res.AudioTrackUrl);
    }

    [Fact]
    public async Task ExecuteWithRetry_SwitchesInstanceOnFailure()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host == "server1.invidious.io")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleInvidiousSearchJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var health = new MockHealthService("https://server1.invidious.io/", "https://server2.invidious.io/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new InvidiousBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<InvidiousBackendProvider>.Instance);

        var result = await provider.SearchAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Single(health.RecordedFailures);
        Assert.Equal("https://server1.invidious.io/", health.RecordedFailures[0].instance.BaseUri.AbsoluteUri);
        Assert.Single(health.RecordedSuccesses);
        Assert.Equal("https://server2.invidious.io/", health.RecordedSuccesses[0].instance.BaseUri.AbsoluteUri);
    }

    [Fact]
    public async Task ExecuteWithRetry_Permanent404ReturnsVideoNotFoundImmediately()
    {
        var handler = MockHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var health = new MockHealthService("https://server1.invidious.io/", "https://server2.invidious.io/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new InvidiousBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<InvidiousBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.GetVideoDetailsAsync(videoId);

        Assert.False(result.IsSuccess);
        Assert.Equal("VIDEO_NOT_FOUND", result.Error?.Code);
        Assert.Single(handler.Requests); // Only 1 attempt, didn't hammer server2
    }
}
