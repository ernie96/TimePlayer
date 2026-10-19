using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class InnerTubeBackendProviderTests
{
    private const string SampleSearchJsonResponse = """
    {
      "contents": {
        "twoColumnSearchResultsRenderer": {
          "primaryContents": {
            "sectionListRenderer": {
              "contents": [
                {
                  "itemSectionRenderer": {
                    "contents": [
                      {
                        "videoRenderer": {
                          "videoId": "dQw4w9WgXcQ",
                          "title": {
                            "runs": [
                              { "text": "Rick Astley - Never Gonna Give You Up (Official Music Video)" }
                            ]
                          },
                          "ownerText": {
                            "runs": [
                              {
                                "text": "Rick Astley",
                                "navigationEndpoint": {
                                  "browseEndpoint": {
                                    "browseId": "UCuAXFkgsw1L7xaCfnd5JJOw"
                                  }
                                }
                              }
                            ]
                          },
                          "lengthText": {
                            "simpleText": "3:33"
                          },
                          "viewCountText": {
                            "simpleText": "1,500,000,000 views"
                          },
                          "thumbnail": {
                            "thumbnails": [
                              { "url": "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg" }
                            ]
                          }
                        }
                      }
                    ]
                  }
                }
              ]
            }
          }
        }
      }
    }
    """;

    private const string SamplePlayerJsonResponseWithStreams = """
    {
      "playabilityStatus": {
        "status": "OK"
      },
      "videoDetails": {
        "videoId": "dQw4w9WgXcQ",
        "title": "Never Gonna Give You Up",
        "lengthSeconds": "213",
        "author": "Rick Astley",
        "channelId": "UCuAXFkgsw1L7xaCfnd5JJOw"
      },
      "streamingData": {
        "formats": [
          {
            "itag": 18,
            "url": "https://rr3---sn-vgqsrn6k.googlevideo.com/videoplayback?itag=18&id=1",
            "mimeType": "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
            "bitrate": 600000,
            "width": 640,
            "height": 360,
            "qualityLabel": "360p"
          }
        ]
      }
    }
    """;

    private const string SamplePlayerJsonBotCheck = """
    {
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm you’re not a bot"
      }
    }
    """;

    private readonly ConfigurationService _configService;

    public InnerTubeBackendProviderTests()
    {
        var config = new AppConfig
        {
            Backend = new BackendConfig
            {
                InnerTubeBaseUrl = "https://www.youtube.com/youtubei/v1/",
                InnerTubeTimeoutSeconds = 5
            }
        };
        _configService = new ConfigurationService(Options.Create(config));
    }

    [Fact]
    public async Task SearchAsync_ValidJsonResponse_ParsesResultsCorrectly()
    {
        var handler = MockHttpMessageHandler.StringResponse(SampleSearchJsonResponse);
        var client = new HttpClient(handler);
        var provider = new InnerTubeBackendProvider(client, _configService, NullLogger<InnerTubeBackendProvider>.Instance);

        var result = await provider.SearchAsync("rick astley");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        var item = result.Value.Items[0];
        Assert.Equal("dQw4w9WgXcQ", item.Id);
        Assert.Equal("Rick Astley - Never Gonna Give You Up (Official Music Video)", item.Title);
        Assert.Equal("Rick Astley", item.ChannelTitle);
        Assert.Equal("UCuAXFkgsw1L7xaCfnd5JJOw", item.ChannelId);
        Assert.Equal(TimeSpan.FromSeconds(213), item.Duration);
        Assert.Equal(1500000000L, item.ViewCount);
        Assert.Contains("hqdefault.jpg", item.ThumbnailUrl);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsFailure()
    {
        var handler = MockHttpMessageHandler.Status(HttpStatusCode.OK);
        var client = new HttpClient(handler);
        var provider = new InnerTubeBackendProvider(client, _configService, NullLogger<InnerTubeBackendProvider>.Instance);

        var result = await provider.SearchAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Equal("EMPTY_QUERY", result.Error?.Code);
    }

    [Fact]
    public async Task ResolveStreamsAsync_WithProgressiveStream_ReturnsDirectPlaybackUrl()
    {
        var handler = MockHttpMessageHandler.StringResponse(SamplePlayerJsonResponseWithStreams);
        var client = new HttpClient(handler);
        var provider = new InnerTubeBackendProvider(client, _configService, NullLogger<InnerTubeBackendProvider>.Instance);

        var result = await provider.ResolveStreamsAsync(YouTubeVideoId.Parse("dQw4w9WgXcQ"));

        Assert.True(result.IsSuccess);
        Assert.Equal("https://rr3---sn-vgqsrn6k.googlevideo.com/videoplayback?itag=18&id=1", result.Value.DirectPlaybackUrl);
        Assert.Equal(BackendProviderType.InnerTube, result.Value.SourceProvider);
        Assert.True(result.Value.SelectedVideoStream.IsProgressive);
    }

    [Fact]
    public async Task ResolveStreamsAsync_BotCheckLoginRequired_ReturnsStreamUnavailableToAllowCascade()
    {
        var handler = MockHttpMessageHandler.StringResponse(SamplePlayerJsonBotCheck);
        var client = new HttpClient(handler);
        var provider = new InnerTubeBackendProvider(client, _configService, NullLogger<InnerTubeBackendProvider>.Instance);

        var result = await provider.ResolveStreamsAsync(YouTubeVideoId.Parse("dQw4w9WgXcQ"));

        Assert.False(result.IsSuccess);
        Assert.Equal("STREAM_UNAVAILABLE", result.Error?.Code);
        Assert.Contains("Sign in to confirm you’re not a bot", result.Error?.TechnicalDetails);
    }
}
