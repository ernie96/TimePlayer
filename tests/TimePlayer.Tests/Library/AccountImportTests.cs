using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

/// <summary>Reading a YouTube account's lists after signing in, the yt-dlp JavaScript runtime setting and YouTube's search filters.</summary>
public class AccountImportTests
{
    private sealed class SignedIn : IYouTubeAccount
    {
        public bool IsSignedIn => true;
        public AccountUse Use => AccountUse.Off; // importing works whatever the setting is
        public Task<Result<int>> ImportFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<int>> ImportFromBrowserAsync(string browser, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Result<int> ImportCookies(IEnumerable<SignInCookie> cookies) => throw new NotSupportedException();
        public void SignOut() { }
        public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders(DateTimeOffset now) => new[] { new KeyValuePair<string, string>("Cookie", "SAPISID=x") };
        public string? WriteTemporaryCookiesFile() => null;
    }

    private static InnerTubeBackendProvider InnerTube(Func<string, string> answer, List<string> bodies, IYouTubeAccount? account = null)
    {
        var http = new MockHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(body)) };
        });
        var config = new ConfigurationService(Options.Create(new AppConfig()), Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}.json"));
        return new InnerTubeBackendProvider(new HttpClient(http), config, NullLogger<InnerTubeBackendProvider>.Instance, account);
    }

    private static string Video(string id, string title) =>
        "{\"videoRenderer\":{\"videoId\":\"" + id + "\",\"title\":{\"runs\":[{\"text\":\"" + title + "\"}]},\"ownerText\":{\"runs\":[{\"text\":\"Someone\"}]}}}";

    private static string Answer(string body)
    {
        if (body.Contains("\"continuation\":\"CHANNELS2\""))
            return """{"items":[{"channelRenderer":{"channelId":"UCbbbbbbbbbbbbbbbbbbbbbb","title":{"simpleText":"Second channel"}}}]}""";
        if (body.Contains("\"FEchannels\""))
            return """{"items":[{"channelRenderer":{"channelId":"UCaaaaaaaaaaaaaaaaaaaaaa","title":{"simpleText":"First channel"}}}],"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"CHANNELS2"}}}}""";
        if (body.Contains("\"FEhistory\""))
            return "{\"items\":[" + Video("aaaaaaaaaaa", "Watched last") + "," + Video("bbbbbbbbbbb", "Watched before") + "]}";
        if (body.Contains("\"VLLL\""))
            return "{\"items\":[" + Video("ccccccccccc", "Liked") + "]}";
        if (body.Contains("\"VLWL\""))
            return "{\"items\":[]}";
        if (body.Contains("\"FEplaylist_aggregation\""))
            return """{"items":[{"gridPlaylistRenderer":{"playlistId":"PLmine","title":{"simpleText":"Road trip"}}},{"gridPlaylistRenderer":{"playlistId":"LL","title":{"simpleText":"Liked videos"}}}]}""";
        if (body.Contains("\"VLPLmine\""))
            return "{\"items\":[" + Video("ddddddddddd", "Song for the road") + "]}";
        return "{}";
    }

    [Fact]
    public async Task ReadAccount_CollectsEveryList_FollowingPages()
    {
        var bodies = new List<string>();
        var data = await InnerTube(Answer, bodies, new SignedIn()).ReadAccountAsync(null);

        Assert.True(data.IsSuccess);
        Assert.Equal(new[] { "First channel", "Second channel" }, data.Value!.Channels.Select(c => c.Name));
        Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" }, data.Value.History.Select(v => v.Id)); // newest first, as YouTube lists them
        Assert.Equal("Liked", Assert.Single(data.Value.Liked).Title);
        Assert.Empty(data.Value.WatchLater);
        var (name, videos) = Assert.Single(data.Value.Playlists); // Liked videos isn't a playlist of its own
        Assert.Equal("Road trip", name);
        Assert.Equal("ddddddddddd", Assert.Single(videos).Id);
        Assert.All(bodies, b => Assert.Contains("\"context\"", b));
    }

    [Fact]
    public async Task ReadAccount_NeedsASignIn()
    {
        var bodies = new List<string>();
        var data = await InnerTube(Answer, bodies).ReadAccountAsync(null);
        Assert.False(data.IsSuccess);
        Assert.Empty(bodies); // nothing is asked of YouTube
    }

    [Fact]
    public async Task ReadAccount_StopsWhenYouTubeSaysSignedOut()
    {
        var bodies = new List<string>();
        var data = await InnerTube(_ => """{"responseContext":{"mainAppWebResponseContext":{"loggedOut":true}}}""", bodies, new SignedIn()).ReadAccountAsync(null);
        Assert.False(data.IsSuccess);
        Assert.Equal("SIGNED_OUT", data.Error!.Code);
    }

    [Theory]
    [InlineData(InnerTubeBackendProvider.LiveFilter)]
    [InlineData(InnerTubeBackendProvider.NewestFilter)]
    [InlineData(InnerTubeBackendProvider.MostViewedFilter)]
    public async Task FilteredSearch_SendsYouTubesFilter(string filter)
    {
        var bodies = new List<string>();
        var result = await InnerTube(_ => "{\"items\":[" + Video("eeeeeeeeeee", "Found") + "]}", bodies).SearchFilteredAsync("news", filter, null);
        Assert.Contains($"\"params\":\"{filter}\"", bodies[0]);
        Assert.Equal("eeeeeeeeeee", Assert.Single(result.Value!.Items).Id);
    }

    [Fact]
    public void YtDlpConfig_NamesTheJavaScriptRuntime()
    {
        Assert.Equal("--extractor-args \"youtube:player_client=mweb,default\"\n", YtDlpManager.ConfigText(null));
        Assert.EndsWith("\n--js-runtimes node\n", YtDlpManager.ConfigText("--js-runtimes node"));
    }
}
