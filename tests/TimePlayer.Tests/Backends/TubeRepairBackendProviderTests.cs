using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.TubeRepair;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class TubeRepairBackendProviderTests
{
    private const string SampleAtomFeed = """
        <?xml version='1.0' encoding='utf-8'?>
        <feed xmlns='http://www.w3.org/2005/Atom'
              xmlns:yt='http://tuberepair.uptimetrackers.com/schemas/2007'
              xmlns:media='http://search.yahoo.com/mrss/'>
            <entry>
                <id>tag:youtube.com,2008:video:k2OhavWYZ6w</id>
                <title>TEST - Omar Baliw (Official Visualizer)</title>
                <author>
                    <name>OMAR BALIW</name>
                </author>
                <media:group>
                    <yt:videoid>k2OhavWYZ6w</yt:videoid>
                    <yt:duration seconds="169"/>
                    <yt:uploaderId>UCxPiInO4_sDJf3A8WqTJJoA</yt:uploaderId>
                    <media:thumbnail url="https://i.ytimg.com/vi/k2OhavWYZ6w/hqdefault.jpg"/>
                    <media:content url="http://tuberepair.uptimetrackers.com/getvideo/k2OhavWYZ6w" type="video/mp4"/>
                </media:group>
                <yt:statistics viewCount="26489"/>
            </entry>
        </feed>
        """;

    private const string SampleCommentsAtom = """
        <?xml version='1.0' encoding='utf-8'?>
        <feed xmlns='http://www.w3.org/2005/Atom'>
            <entry>
                <author><name>@listener1</name></author>
                <content>Great track!</content>
            </entry>
            <entry>
                <author><name>@listener2</name></author>
                <content>Love this sound</content>
            </entry>
        </feed>
        """;

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task SearchAsync_ParsesEntriesCorrectly()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("/feeds/api/videos", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleAtomFeed, Encoding.UTF8, "application/atom+xml")
            };
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.SearchAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        var item = result.Value.Items[0];
        Assert.Equal("k2OhavWYZ6w", item.Id);
        Assert.Equal("TEST - Omar Baliw (Official Visualizer)", item.Title);
        Assert.Equal("OMAR BALIW", item.ChannelTitle);
        Assert.Equal(TimeSpan.FromSeconds(169), item.Duration);
        Assert.Equal(26489, item.ViewCount);
    }

    [Fact]
    public async Task ResolveStreamsAsync_Returns307RedirectLocation()
    {
        const string redirectLocation = "https://rr3---sn-vgqsrn6k.googlevideo.com/videoplayback?id=123";
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("/getvideo/k2OhavWYZ6w", req.RequestUri!.ToString());
            var resp = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            resp.Headers.Location = new Uri(redirectLocation);
            return resp;
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.ResolveStreamsAsync(new YouTubeVideoId("k2OhavWYZ6w"));

        Assert.True(result.IsSuccess);
        Assert.Equal(redirectLocation, result.Value.DirectPlaybackUrl);
        Assert.Equal(BackendProviderType.TubeRepair, result.Value.SourceProvider);
    }

    [Fact]
    public async Task GetCommentsAsync_ParsesAtomComments()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("/feeds/api/videos/k2OhavWYZ6w/comments", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleCommentsAtom, Encoding.UTF8, "application/atom+xml")
            };
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.GetCommentsAsync("k2OhavWYZ6w");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains("@listener1: Great track!", result.Value);
        Assert.Contains("@listener2: Love this sound", result.Value);
    }

    [Fact]
    public async Task GetStandardFeedAsync_FetchesPopularFeed()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("/feeds/api/standardfeeds/most_popular", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleAtomFeed, Encoding.UTF8, "application/atom+xml")
            };
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.GetStandardFeedAsync("most_popular");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("k2OhavWYZ6w", result.Value[0].Id);
    }

    [Fact]
    public async Task GetUserUploadsAsync_FetchesUserUploadsFeed()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Contains("/feeds/api/users/UCxPiInO4_sDJf3A8WqTJJoA/uploads", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleAtomFeed, Encoding.UTF8, "application/atom+xml")
            };
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.GetUserUploadsAsync("UCxPiInO4_sDJf3A8WqTJJoA");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("k2OhavWYZ6w", result.Value[0].Id);
    }

    [Fact]
    public async Task SearchAsync_SanitizesMalformedXmlWithDuplicateAttributesAndMissingFeedClosure()
    {
        // Notice duplicate type='video/3gpp' and unclosed <feed> tag (exactly what live TubeRepair produces)
        const string malformedFeed = """
            <?xml version='1.0' encoding='utf-8'?>
            <feed xmlns='http://www.w3.org/2005/Atom'
                  xmlns:yt='http://tuberepair.uptimetrackers.com/schemas/2007'
                  xmlns:media='http://search.yahoo.com/mrss/'>
                <entry>
                    <id>tag:youtube.com,2008:video:FIHoJokj8-o</id>
                    <title>Live Test Video</title>
                    <author><name>Author</name></author>
                    <media:group>
                        <yt:videoid>FIHoJokj8-o</yt:videoid>
                        <yt:duration seconds="300"/>
                        <media:content url="http://tuberepair.uptimetrackers.com/getvideo/FIHoJokj8-o" type='video/3gpp' type='video/3gpp' medium='video'/>
                    </media:group>
                </entry>
            """;

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(malformedFeed, Encoding.UTF8, "application/atom+xml")
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.SearchAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Equal("FIHoJokj8-o", result.Value.Items[0].Id);
    }

    [Fact]
    public async Task SearchAsync_SanitizesUnescapedAmpersandsInTitles()
    {
        const string feedWithAmpersands = """
            <?xml version='1.0' encoding='utf-8'?>
            <feed xmlns='http://www.w3.org/2005/Atom'
                  xmlns:yt='http://tuberepair.uptimetrackers.com/schemas/2007'
                  xmlns:media='http://search.yahoo.com/mrss/'>
                <entry>
                    <id>tag:youtube.com,2008:video:abc123xyz89</id>
                    <title>Hypothesis Testing Problems - Z Test & T Statistics - One & Two Tailed Tests</title>
                    <author><name>Author & Co</name></author>
                    <media:group>
                        <yt:videoid>abc123xyz89</yt:videoid>
                        <yt:duration seconds="200"/>
                        <media:title type='plain'>Feedback & Fixes</media:title>
                    </media:group>
                </entry>
            </feed>
            """;

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(feedWithAmpersands, Encoding.UTF8, "application/atom+xml")
        });

        var client = new HttpClient(handler);
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.SearchAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Equal("abc123xyz89", result.Value.Items[0].Id);
        Assert.Equal("Feedback & Fixes", result.Value.Items[0].Title);
    }

    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task LiveNetwork_TubeRepair_SearchReturnsResults()
    {
        using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var result = await provider.SearchAsync("cavetown");

        Assert.True(result.IsSuccess, $"TubeRepair search failed: {result.Error?.Message}");
        Assert.NotNull(result.Value);
        Assert.NotEmpty(result.Value.Items);

        // Also test query 'test' which returns feeds with raw ampersands and unclosed feed tags
        var testResult = await provider.SearchAsync("test");
        Assert.True(testResult.IsSuccess, $"TubeRepair search for 'test' failed: {testResult.Error?.Message}");
        Assert.NotNull(testResult.Value);
        Assert.NotEmpty(testResult.Value.Items);
    }

    [Fact]
    [Trait("Category", "LiveNetwork")]
    public async Task LiveNetwork_TubeRepair_ResolveStreamsReturnsPlayableUrl()
    {
        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var config = new ConfigurationService(Options.Create(new AppConfig()));
        var provider = new TubeRepairBackendProvider(client, config, NullLogger<TubeRepairBackendProvider>.Instance);

        var streamResult = await provider.ResolveStreamsAsync(new YouTubeVideoId("dQw4w9WgXcQ"));
        Assert.True(streamResult.IsSuccess, $"Stream resolution failed: {streamResult.Error?.Message}");
        Assert.NotNull(streamResult.Value.DirectPlaybackUrl);
        Assert.StartsWith("https://", streamResult.Value.DirectPlaybackUrl);
        Assert.Contains("googlevideo.com", streamResult.Value.DirectPlaybackUrl);
    }
}
