using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class YtDlpBackendProviderTests
{
    private readonly FakeProcessRunner _processRunner = new();
    private readonly FakeYtDlpManager _ytDlpManager = new();
    private readonly ConfigurationService _configService;

    public YtDlpBackendProviderTests()
    {
        var config = new AppConfig();
        _configService = new ConfigurationService(Options.Create(config));
    }

    private YtDlpBackendProvider CreateProvider()
    {
        return new YtDlpBackendProvider(
            _ytDlpManager,
            _processRunner,
            _configService,
            NullLogger<YtDlpBackendProvider>.Instance);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsFailure()
    {
        var provider = CreateProvider();
        var result = await provider.SearchAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Equal("EMPTY_QUERY", result.Error!.Code);
        Assert.Empty(_processRunner.Invocations);
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_BuildsCorrectArgumentsAndParsesEntries()
    {
        var jsonOutput = """
        {
            "_type": "playlist",
            "id": "ytsearch20:queen",
            "title": "ytsearch20:queen",
            "entries": [
                {
                    "id": "fJ9rUzIMcZQ",
                    "title": "Queen – Bohemian Rhapsody (Official Video)",
                    "uploader": "Queen Official",
                    "channel": "Queen Official",
                    "channel_id": "UC2Jv2oQG6N3x5o7gZ0V1u5A",
                    "duration": 359,
                    "view_count": 1700000000,
                    "upload_date": "20080801",
                    "description": "Bohemian Rhapsody official music video remastered.",
                    "thumbnails": [
                        { "url": "https://i.ytimg.com/vi/fJ9rUzIMcZQ/default.jpg", "width": 120, "height": 90 },
                        { "url": "https://i.ytimg.com/vi/fJ9rUzIMcZQ/maxresdefault.jpg", "width": 1280, "height": 720 }
                    ]
                }
            ]
        }
        """;

        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = jsonOutput,
            StandardError = ""
        };

        var provider = CreateProvider();
        var result = await provider.SearchAsync("queen bohemian");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Items);

        var item = result.Value.Items[0];
        Assert.Equal("fJ9rUzIMcZQ", item.Id);
        Assert.Equal("Queen – Bohemian Rhapsody (Official Video)", item.Title);
        Assert.Equal("Queen Official", item.ChannelTitle);
        Assert.Equal(TimeSpan.FromSeconds(359), item.Duration);
        Assert.Equal(1700000000, item.ViewCount);
        Assert.Equal("August 1, 2008", item.UploadDateText);
        Assert.Equal("https://i.ytimg.com/vi/fJ9rUzIMcZQ/maxresdefault.jpg", item.ThumbnailUrl);

        // Verify arguments
        var invocation = Assert.Single(_processRunner.Invocations);
        Assert.Contains("--dump-single-json", invocation.Arguments);
        Assert.Contains("--default-search", invocation.Arguments);
        Assert.Contains("ytsearch", invocation.Arguments);
        Assert.Contains("--flat-playlist", invocation.Arguments);
        Assert.Contains("--skip-download", invocation.Arguments);
        Assert.Contains("--no-warnings", invocation.Arguments);
        Assert.Contains("ytsearch20:queen bohemian", invocation.Arguments);
    }

    [Fact]
    public async Task SearchAsync_WithCookiesConfigured_AppendsCookieArguments()
    {
        var config = new AppConfig
        {
            Backend = new BackendConfig { YtDlpBrowserCookies = "firefox" }
        };
        await _configService.UpdateConfigAsync(config);

        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = "{\"entries\":[]}",
            StandardError = ""
        };

        var provider = CreateProvider();
        await provider.SearchAsync("test query");

        var invocation = Assert.Single(_processRunner.Invocations);
        Assert.Contains("--cookies-from-browser", invocation.Arguments);
        Assert.Contains("firefox", invocation.Arguments);
    }

    [Fact]
    public async Task SearchAsync_ExitCodeNonZero_ClassifiesError()
    {
        _processRunner.NextResult = new()
        {
            ExitCode = 1,
            StandardOutput = "",
            StandardError = "ERROR: [youtube] HTTP Error 429: Too Many Requests"
        };

        var provider = CreateProvider();
        var result = await provider.SearchAsync("test query");

        Assert.False(result.IsSuccess);
        Assert.Equal("RATE_LIMITED", result.Error!.Code);
        Assert.True(result.Error.IsRecoverable);
    }

    [Fact]
    public async Task GetVideoDetailsAsync_ValidJson_ParsesMetadataAndStreams()
    {
        var jsonOutput = """
        {
            "id": "fJ9rUzIMcZQ",
            "title": "Queen – Bohemian Rhapsody",
            "description": "Full description here",
            "uploader": "Queen",
            "channel": "Queen Official",
            "channel_id": "UC2Jv2oQG6N3x5o7gZ0V1u5A",
            "duration": 354,
            "view_count": 1500000,
            "like_count": 80000,
            "upload_date": "20080801",
            "is_live": false,
            "tags": ["rock", "queen", "freddie mercury"],
            "thumbnails": [{ "url": "https://thumb.jpg" }],
            "formats": [
                {
                    "format_id": "140",
                    "ext": "m4a",
                    "url": "https://stream.audio.140",
                    "vcodec": "none",
                    "acodec": "mp4a.40.2",
                    "abr": 128
                },
                {
                    "format_id": "18",
                    "ext": "mp4",
                    "url": "https://stream.prog.360p",
                    "vcodec": "avc1.42001E",
                    "acodec": "mp4a.40.2",
                    "height": 360,
                    "width": 640,
                    "fps": 30,
                    "tbr": 600
                },
                {
                    "format_id": "137",
                    "ext": "mp4",
                    "url": "https://stream.dash.1080p",
                    "vcodec": "avc1.640028",
                    "acodec": "none",
                    "height": 1080,
                    "width": 1920,
                    "fps": 30,
                    "vbr": 4000
                }
            ],
            "subtitles": {
                "en": [
                    { "ext": "vtt", "url": "https://sub.vtt", "name": "English" }
                ]
            }
        }
        """;

        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = jsonOutput,
            StandardError = ""
        };

        var provider = CreateProvider();
        var videoId = new YouTubeVideoId("fJ9rUzIMcZQ");
        var result = await provider.GetVideoDetailsAsync(videoId);

        Assert.True(result.IsSuccess);
        var details = result.Value!;
        Assert.Equal("fJ9rUzIMcZQ", details.Id.Value);
        Assert.Equal("Queen – Bohemian Rhapsody", details.Title);
        Assert.Equal("Queen Official", details.Channel.Name);
        Assert.Equal(TimeSpan.FromSeconds(354), details.Duration);
        Assert.Equal(1500000, details.ViewCount);
        Assert.Equal(80000, details.LikeCount);
        Assert.Equal(3, details.Tags.Count);
        Assert.Equal(3, details.Streams.Count);

        var audio = details.Streams.FirstOrDefault(s => s.IsAudioOnly);
        Assert.NotNull(audio);
        Assert.Equal("https://stream.audio.140", audio.Url);

        var prog = details.Streams.FirstOrDefault(s => s.IsProgressive);
        Assert.NotNull(prog);
        Assert.Equal(360, prog.Height);

        var dash = details.Streams.FirstOrDefault(s => s.IsVideoOnly);
        Assert.NotNull(dash);
        Assert.Equal(1080, dash.Height);

        Assert.Single(details.Captions);
        Assert.Equal("en", details.Captions[0].LanguageCode);
        Assert.Equal("English", details.Captions[0].DisplayName);
    }

    [Fact]
    public async Task ResolveStreamsAsync_AudioOnly_PicksBestAudioStream()
    {
        var jsonOutput = """
        {
            "id": "fJ9rUzIMcZQ",
            "title": "Audio Test",
            "formats": [
                { "format_id": "251", "ext": "webm", "url": "https://stream.audio.opus", "vcodec": "none", "acodec": "opus", "abr": 160 },
                { "format_id": "140", "ext": "m4a", "url": "https://stream.audio.m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 128 },
                { "format_id": "18", "ext": "mp4", "url": "https://stream.prog.360p", "vcodec": "avc1", "acodec": "mp4a", "height": 360 }
            ]
        }
        """;

        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = jsonOutput,
            StandardError = ""
        };

        var provider = CreateProvider();
        var videoId = new YouTubeVideoId("fJ9rUzIMcZQ");
        var result = await provider.ResolveStreamsAsync(videoId, audioOnly: true);

        Assert.True(result.IsSuccess);
        var resolved = result.Value!;
        Assert.Equal(BackendProviderType.YtDlpLocal, resolved.SourceProvider);
        Assert.Equal("https://stream.audio.opus", resolved.DirectPlaybackUrl);
        Assert.Null(resolved.AudioTrackUrl);
    }

    [Fact]
    public async Task ResolveStreamsAsync_Quality1080p_PicksVideoAndAudioTracks()
    {
        var jsonOutput = """
        {
            "id": "fJ9rUzIMcZQ",
            "title": "Video Quality Test",
            "formats": [
                { "format_id": "140", "ext": "m4a", "url": "https://stream.audio", "vcodec": "none", "acodec": "mp4a", "abr": 128 },
                { "format_id": "18", "ext": "mp4", "url": "https://stream.prog.360", "vcodec": "avc1", "acodec": "mp4a", "height": 360 },
                { "format_id": "137", "ext": "mp4", "url": "https://stream.dash.1080", "vcodec": "avc1", "acodec": "none", "height": 1080 }
            ]
        }
        """;

        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = jsonOutput,
            StandardError = ""
        };

        var provider = CreateProvider();
        var videoId = new YouTubeVideoId("fJ9rUzIMcZQ");
        var result = await provider.ResolveStreamsAsync(videoId, qualityPreference: QualityPreference.P1080);

        Assert.True(result.IsSuccess);
        var resolved = result.Value!;
        Assert.Equal("https://stream.dash.1080", resolved.DirectPlaybackUrl);
        Assert.Equal("https://stream.audio", resolved.AudioTrackUrl);
        Assert.NotNull(resolved.SelectedAudioStream);
    }

    [Fact]
    public async Task DownloadAsync_VideoDownload_EmitsProgressAndReturnsDestination()
    {
        _processRunner.Handler = (exe, args, progress) =>
        {
            // Simulate yt-dlp progress output
            progress?.Report("[download] Destination: C:\\Downloads\\Bohemian Rhapsody.mp4");
            progress?.Report("[download]  25.0% of 20.00MiB at 5.00MiB/s ETA 00:03");
            progress?.Report("[download]  75.0% of 20.00MiB at 5.00MiB/s ETA 00:01");
            progress?.Report("[download] 100% of 20.00MiB in 00:04");

            return new()
            {
                ExitCode = 0,
                StandardOutput = "",
                StandardError = ""
            };
        };

        var provider = CreateProvider();
        var videoId = new YouTubeVideoId("fJ9rUzIMcZQ");
        var downloadOptions = new DownloadOptions
        {
            OutputDirectory = @"C:\Downloads",
            QualityPreference = QualityPreference.P1080
        };

        var reportedProgress = new List<DownloadProgress>();
        var progressHandler = new Progress<DownloadProgress>(p => reportedProgress.Add(p));

        var result = await provider.DownloadAsync(videoId, downloadOptions, progressHandler);

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\Downloads\Bohemian Rhapsody.mp4", result.Value);

        // Verify arguments
        var invocation = Assert.Single(_processRunner.Invocations);
        Assert.Contains("--newline", invocation.Arguments);
        Assert.Contains("--no-playlist", invocation.Arguments);
        Assert.Contains("-o", invocation.Arguments);
        Assert.Contains("-f", invocation.Arguments);
        Assert.Contains("https://www.youtube.com/watch?v=fJ9rUzIMcZQ", invocation.Arguments);
    }

    [Fact]
    public async Task DownloadAsync_AudioOnly_PassesAudioExtractionArguments()
    {
        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = "[ExtractAudio] Destination: C:\\Music\\Song.mp3",
            StandardError = ""
        };

        var provider = CreateProvider();
        var videoId = new YouTubeVideoId("fJ9rUzIMcZQ");
        var options = new DownloadOptions
        {
            OutputDirectory = @"C:\Music",
            AudioOnly = true,
            AudioFormat = "mp3"
        };

        var result = await provider.DownloadAsync(videoId, options);

        Assert.True(result.IsSuccess);
        var invocation = Assert.Single(_processRunner.Invocations);
        Assert.Contains("-x", invocation.Arguments);
        Assert.Contains("--audio-format", invocation.Arguments);
        Assert.Contains("mp3", invocation.Arguments);
        Assert.Contains("--audio-quality", invocation.Arguments);
    }

    [Theory]
    [InlineData(QualityPreference.P1080, "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=1080]+bestaudio/best[height<=1080]/best")]
    [InlineData(QualityPreference.P720, "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=720]+bestaudio/best[height<=720]/best")]
    [InlineData(QualityPreference.Auto, "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best")]
    public void BuildFormatSelector_ReturnsExpectedFormatStrings(QualityPreference pref, string expected)
    {
        var selector = YtDlpBackendProvider.BuildFormatSelector(pref);
        Assert.Equal(expected, selector);
    }
}
