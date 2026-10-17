using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.Piped;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class PipedBackendProviderTests
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
                ProviderType = BackendProviderType.Piped
            }).ToList();
        }

        public Task<InstanceHealthMeasurement> ProbeInstanceAsync(PublicInstance instance, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new InstanceHealthMeasurement
            {
                InstanceUri = instance.BaseUri,
                ProviderType = BackendProviderType.Piped,
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

    private static readonly string SampleSearchJson = """
        {
          "items": [
            {
              "url": "/watch?v=dQw4w9WgXcQ",
              "type": "stream",
              "title": "Never Gonna Give You Up",
              "thumbnail": "https://pipedproxy/vi/dQw4w9WgXcQ/hqdefault.jpg",
              "uploaderName": "Rick Astley",
              "uploaderUrl": "/channel/UCuAXFkgsw1L7xaCfnd5JJOw",
              "uploadedDate": "14 years ago",
              "duration": 212,
              "views": 1500000000,
              "uploaderVerified": true
            },
            {
              "url": "/channel/UCuAXFkgsw1L7xaCfnd5JJOw",
              "type": "channel",
              "title": "Rick Astley Channel"
            },
            {
              "url": "/watch?v=9bZkp7q19f0",
              "type": "stream",
              "title": "Gangnam Style",
              "uploaderName": "Psy",
              "duration": 253,
              "views": 5000000000
            }
          ],
          "nextpage": "encoded_token_123"
        }
        """;

    private static readonly string SampleStreamsJson = """
        {
          "title": "Never Gonna Give You Up",
          "description": "Official Music Video for Rick Astley",
          "uploadDate": "2009-10-25",
          "uploader": "Rick Astley",
          "uploaderUrl": "/channel/UCuAXFkgsw1L7xaCfnd5JJOw",
          "uploaderAvatar": "https://pipedproxy/avatar.jpg",
          "uploaderVerified": true,
          "duration": 212,
          "views": 1500000000,
          "likes": 16000000,
          "thumbnailUrl": "https://pipedproxy/thumb.jpg",
          "livestream": false,
          "audioStreams": [
            {
              "url": "https://pipedproxy/audio_128k.m4a",
              "format": "M4A",
              "quality": "128 kbps",
              "mimeType": "audio/mp4",
              "codec": "mp4a.40.2",
              "bitrate": 128000,
              "videoOnly": false
            },
            {
              "url": "https://pipedproxy/audio_160k.opus",
              "format": "OPUS",
              "quality": "160 kbps",
              "mimeType": "audio/webm",
              "codec": "opus",
              "bitrate": 160000,
              "videoOnly": false
            }
          ],
          "videoStreams": [
            {
              "url": "https://pipedproxy/video_720p_prog.mp4",
              "format": "MPEG_4",
              "quality": "720p",
              "height": 720,
              "width": 1280,
              "fps": 30,
              "bitrate": 2000000,
              "codec": "avc1.4d401f",
              "videoOnly": false
            },
            {
              "url": "https://pipedproxy/video_1080p_dash.mp4",
              "format": "MPEG_4",
              "quality": "1080p",
              "height": 1080,
              "width": 1920,
              "fps": 60,
              "bitrate": 4500000,
              "codec": "avc1.64002a",
              "videoOnly": true
            }
          ],
          "subtitles": [
            {
              "url": "https://pipedproxy/sub_en.vtt",
              "name": "English",
              "code": "en",
              "mimeType": "text/vtt",
              "autoGenerated": false
            }
          ]
        }
        """;

    [Fact]
    public async Task SearchAsync_ParsesVideoItemsAndContinuationToken()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleSearchJson);
        var health = new MockHealthService("https://piped.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var result = await provider.SearchAsync("rick astley");

        Assert.True(result.IsSuccess);
        var paged = result.Value;
        Assert.NotNull(paged);
        Assert.Equal(2, paged.Items.Count); // Channel filtered out, only 2 videos remain
        Assert.True(paged.HasNextPage);
        Assert.Equal("encoded_token_123", paged.NextPageToken);

        var first = paged.Items[0];
        Assert.Equal("dQw4w9WgXcQ", first.Id);
        Assert.Equal("Never Gonna Give You Up", first.Title);
        Assert.Equal("Rick Astley", first.ChannelTitle);
        Assert.Equal("UCuAXFkgsw1L7xaCfnd5JJOw", first.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(212), first.Duration);
        Assert.Equal(1_500_000_000, first.ViewCount);
        Assert.Contains("Rick Astley", first.AccessibleSummary);
        Assert.Contains("1.5 billion views", first.AccessibleSummary);
    }

    [Fact]
    public async Task GetVideoDetailsAsync_ParsesMetadataAndStreams()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleStreamsJson);
        var health = new MockHealthService("https://piped.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.GetVideoDetailsAsync(videoId);

        Assert.True(result.IsSuccess);
        var details = result.Value;
        Assert.NotNull(details);
        Assert.Equal("Never Gonna Give You Up", details.Title);
        Assert.Equal("Rick Astley", details.Channel.Name);
        Assert.Equal("UCuAXFkgsw1L7xaCfnd5JJOw", details.Channel.Id);
        Assert.True(details.Channel.IsVerified);
        Assert.Equal(TimeSpan.FromSeconds(212), details.Duration);
        Assert.Equal(4, details.Streams.Count); // 2 audio + 2 video
        Assert.Single(details.Captions);
        Assert.Equal("en", details.Captions[0].LanguageCode);
        Assert.Equal("English", details.Captions[0].DisplayName);
    }

    [Fact]
    public async Task ResolveStreamsAsync_AutoQuality_SelectsHighestQualityAndAudioTrack()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleStreamsJson);
        var health = new MockHealthService("https://piped.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.ResolveStreamsAsync(videoId, QualityPreference.Auto);

        Assert.True(result.IsSuccess);
        var res = result.Value;
        Assert.NotNull(res);
        // 1080p DASH is highest quality video
        Assert.Equal("1080p", res.SelectedVideoStream.QualityLabel);
        Assert.True(res.SelectedVideoStream.IsVideoOnly);
        Assert.Equal("https://pipedproxy/video_1080p_dash.mp4", res.DirectPlaybackUrl);
        // Best audio is 160k opus
        Assert.NotNull(res.SelectedAudioStream);
        Assert.Equal("https://pipedproxy/audio_160k.opus", res.AudioTrackUrl);
    }

    [Fact]
    public async Task ResolveStreamsAsync_AudioOnly_ResolvesBestAudioTrack()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleStreamsJson);
        var health = new MockHealthService("https://piped.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.ResolveStreamsAsync(videoId, QualityPreference.AudioOnly, audioOnly: true);

        Assert.True(result.IsSuccess);
        var res = result.Value;
        Assert.NotNull(res);
        Assert.True(res.SelectedVideoStream.IsAudioOnly);
        Assert.Equal("https://pipedproxy/audio_160k.opus", res.DirectPlaybackUrl);
    }

    [Fact]
    public async Task ExecuteWithRetry_RetriesNextInstanceWhenFirstInstanceReturns500()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host == "server1.example.com")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleSearchJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var health = new MockHealthService("https://server1.example.com/", "https://server2.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var result = await provider.SearchAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Single(health.RecordedFailures);
        Assert.Equal("https://server1.example.com/", health.RecordedFailures[0].instance.BaseUri.AbsoluteUri);
        Assert.Single(health.RecordedSuccesses);
        Assert.Equal("https://server2.example.com/", health.RecordedSuccesses[0].instance.BaseUri.AbsoluteUri);
    }

    [Fact]
    public async Task ExecuteWithRetry_DoesNotRetryPermanent404()
    {
        var handler = MockHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var health = new MockHealthService("https://server1.example.com/", "https://server2.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var videoId = new YouTubeVideoId("dQw4w9WgXcQ");
        var result = await provider.GetVideoDetailsAsync(videoId);

        Assert.False(result.IsSuccess);
        Assert.Equal("VIDEO_NOT_FOUND", result.Error?.Code);
        Assert.Single(handler.Requests); // Failed immediately without retrying server2
        Assert.Empty(health.RecordedFailures); // 404 is not an instance server failure
    }

    [Fact]
    public async Task ExecuteWithRetry_Handles429RateLimitByMarkingInstanceAndRetrying()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.Host == "server1.example.com")
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleSearchJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var health = new MockHealthService("https://server1.example.com/", "https://server2.example.com/");
        var config = new ConfigurationService(Options.Create(new AppConfig()));

        var provider = new PipedBackendProvider(
            new HttpClient(handler),
            health,
            config,
            NullLogger<PipedBackendProvider>.Instance);

        var result = await provider.SearchAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Contains(health.RecordedFailures, f => f.reason.Contains("Rate limited"));
    }
}
