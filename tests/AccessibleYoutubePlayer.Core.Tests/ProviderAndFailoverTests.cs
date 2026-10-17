using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Models;
using AccessibleYoutubePlayer.Core.Services;
using AccessibleYoutubePlayer.Core.Services.Providers;
using Xunit;

namespace AccessibleYoutubePlayer.Core.Tests;

public class ProviderAndFailoverTests
{
    [Fact]
    public void Piped_ExtractVideoId_HandlesVariousFormats()
    {
        Assert.Equal("dQw4w9WgXcQ", PipedBackendProvider.ExtractVideoId("/watch?v=dQw4w9WgXcQ"));
        Assert.Equal("dQw4w9WgXcQ", PipedBackendProvider.ExtractVideoId("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s"));
        Assert.Equal("dQw4w9WgXcQ", PipedBackendProvider.ExtractVideoId("https://youtu.be/dQw4w9WgXcQ?si=123"));
        Assert.Equal("dQw4w9WgXcQ", PipedBackendProvider.ExtractVideoId("dQw4w9WgXcQ"));
    }

    [Fact]
    public void Piped_ParseSearchJson_ReturnsValidVideoMetadata()
    {
        var sampleJson = @"{
            ""items"": [
                {
                    ""url"": ""/watch?v=test12345"",
                    ""title"": ""Accessible Coding Tutorial"",
                    ""uploaderName"": ""TrypSynth"",
                    ""duration"": 300,
                    ""views"": 1500,
                    ""uploadedDate"": ""2 days ago"",
                    ""thumbnail"": ""https://img.youtube.com/vi/test12345/default.jpg""
                }
            ]
        }";

        var results = PipedBackendProvider.ParsePipedSearchJson(sampleJson);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("test12345", item.Id);
        Assert.Equal("Accessible Coding Tutorial", item.Title);
        Assert.Equal("TrypSynth", item.Author);
        Assert.Equal(TimeSpan.FromSeconds(300), item.Duration);
        Assert.Equal(1500, item.ViewCount);
        Assert.Equal("5:00", item.FormattedDuration);
        Assert.Contains("Accessible Coding Tutorial, by TrypSynth, duration 5:00, 1,500 views, uploaded 2 days ago", item.AccessibleSummary);
    }

    [Fact]
    public void Invidious_ParseSearchJson_ReturnsValidVideoMetadata()
    {
        var sampleJson = @"[
            {
                ""type"": ""video"",
                ""title"": ""Invidious Music Stream"",
                ""videoId"": ""inv987"",
                ""author"": ""Beethoven"",
                ""lengthSeconds"": 600,
                ""viewCount"": 50000,
                ""publishedText"": ""1 month ago""
            }
        ]";

        var results = InvidiousBackendProvider.ParseInvidiousSearchJson(sampleJson);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("inv987", item.Id);
        Assert.Equal("Invidious Music Stream", item.Title);
        Assert.Equal("Beethoven", item.Author);
        Assert.Equal(TimeSpan.FromSeconds(600), item.Duration);
        Assert.Equal("10:00", item.FormattedDuration);
    }

    [Fact]
    public void LocalYtDlp_ParseJson_ExtractsMetadataAndStreams()
    {
        var sampleJson = @"{
            ""id"": ""local123"",
            ""title"": ""Local Extraction Video"",
            ""uploader"": ""Offline Dev"",
            ""duration"": 125,
            ""view_count"": 42,
            ""formats"": [
                {
                    ""url"": ""https://rr.googlevideo.com/videoplayback?id=audio_only"",
                    ""ext"": ""m4a"",
                    ""vcodec"": ""none"",
                    ""acodec"": ""mp4a.40.2"",
                    ""tbr"": 128,
                    ""format_note"": ""medium""
                },
                {
                    ""url"": ""https://rr.googlevideo.com/videoplayback?id=video_720"",
                    ""ext"": ""mp4"",
                    ""vcodec"": ""avc1.4d401f"",
                    ""acodec"": ""none"",
                    ""height"": 720,
                    ""tbr"": 1200,
                    ""format_note"": ""720p""
                }
            ]
        }";

        var meta = LocalYtDlpBackendProvider.ParseSingleVideoJson(sampleJson);
        Assert.NotNull(meta);
        Assert.Equal("local123", meta.Id);
        Assert.Equal("Local Extraction Video", meta.Title);
        Assert.Equal("2:05", meta.FormattedDuration);

        var streams = LocalYtDlpBackendProvider.ParseYtDlpStreamsJson(sampleJson);
        Assert.Equal(2, streams.Count);
        Assert.True(streams[0].IsAudioOnly);
        Assert.Equal("m4a", streams[0].Format);
        Assert.False(streams[1].IsAudioOnly);
        Assert.Equal(720, streams[1].Height);
    }

    [Fact]
    public async Task FailoverService_FallsBackToInvidious_WhenPipedFails()
    {
        var mockPiped = new MockBackend(ProviderType.Piped, shouldFail: true);
        var mockInvidious = new MockBackend(ProviderType.Invidious, shouldFail: false);
        var mockLocal = new MockBackend(ProviderType.LocalFallback, shouldFail: false);

        var service = new FailoverYouTubeService(mockPiped, mockInvidious, mockLocal);

        var result = await service.SearchAsync("test query");

        Assert.Equal(ProviderType.Invidious, result.ProviderUsed);
        Assert.Single(result.Items);
        Assert.Equal("Invidious Result", result.Items[0].Title);
    }

    [Fact]
    public async Task FailoverService_FallsBackToLocal_WhenPipedAndInvidiousFail()
    {
        var mockPiped = new MockBackend(ProviderType.Piped, shouldFail: true);
        var mockInvidious = new MockBackend(ProviderType.Invidious, shouldFail: true);
        var mockLocal = new MockBackend(ProviderType.LocalFallback, shouldFail: false);

        var service = new FailoverYouTubeService(mockPiped, mockInvidious, mockLocal);

        var result = await service.SearchAsync("test query");

        Assert.Equal(ProviderType.LocalFallback, result.ProviderUsed);
        Assert.Single(result.Items);
        Assert.Equal("LocalFallback Result", result.Items[0].Title);
    }

    [Fact]
    public void FailoverService_DetectsYouTubeUrls()
    {
        Assert.True(FailoverYouTubeService.IsYouTubeUrl("https://www.youtube.com/watch?v=abc12345", out var id1));
        Assert.Equal("abc12345", id1);

        Assert.True(FailoverYouTubeService.IsYouTubeUrl("https://youtu.be/xyz987", out var id2));
        Assert.Equal("xyz987", id2);

        Assert.True(FailoverYouTubeService.IsYouTubeUrl("https://www.youtube.com/shorts/short123", out var id3));
        Assert.Equal("short123", id3);

        Assert.False(FailoverYouTubeService.IsYouTubeUrl("not a url", out _));
    }
}

public class MockBackend : IYouTubeBackendProvider
{
    private readonly bool _shouldFail;
    public ProviderType ProviderType { get; }

    public MockBackend(ProviderType providerType, bool shouldFail)
    {
        ProviderType = providerType;
        _shouldFail = shouldFail;
    }

    public Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (_shouldFail) throw new HttpRequestException($"{ProviderType} simulated failure");

        return Task.FromResult(new SearchResult
        {
            Items = new List<VideoMetadata>
            {
                new() { Id = "mock1", Title = $"{ProviderType} Result", Author = "Artist", Duration = TimeSpan.FromMinutes(3) }
            },
            Query = query,
            ProviderUsed = ProviderType
        });
    }

    public Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        if (_shouldFail) throw new HttpRequestException($"{ProviderType} simulated failure");
        return Task.FromResult(new VideoMetadata { Id = videoId, Title = $"{ProviderType} Detail" });
    }

    public Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        if (_shouldFail) throw new HttpRequestException($"{ProviderType} simulated failure");
        return Task.FromResult<IReadOnlyList<StreamInfo>>(new List<StreamInfo>
        {
            new() { Url = "https://mock.stream", Format = "mp4" }
        });
    }

    public Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SubtitleTrack>>(Array.Empty<SubtitleTrack>());
    }
}
