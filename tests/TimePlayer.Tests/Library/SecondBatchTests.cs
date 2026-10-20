using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Library;
using TimePlayer.Tests.Helpers;
using TimePlayer.Wpf;
using Xunit;

namespace TimePlayer.Tests.Library;

public class SecondBatchTests
{
    private static InnerTubeBackendProvider InnerTube(Func<string, string> answer, List<string>? bodies = null)
    {
        var http = new MockHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            bodies?.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(body)) };
        });
        var config = new ConfigurationService(Options.Create(new AppConfig()), Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}.json"));
        return new InnerTubeBackendProvider(new HttpClient(http), config, NullLogger<InnerTubeBackendProvider>.Instance);
    }

    private const string ChannelHome = """
        {"contents":{"twoColumnBrowseResultsRenderer":{"tabs":[
          {"tabRenderer":{"title":"Home","endpoint":{"browseEndpoint":{"params":"EghmZWF0dXJlZPIGBAoCMgA%3D"}}}},
          {"tabRenderer":{"title":"Shorts","endpoint":{"browseEndpoint":{"params":"EgZzaG9ydHPyBgUKA5oBAA%3D%3D"}}}}
        ]}}}
        """;

    private const string ShortsTab = """
        {"items":[
          {"shortsLockupViewModel":{"entityId":"shorts-shelf-item-E_MGy41IYVw","accessibilityText":"Still hits, 45 thousand views - play Short",
            "onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"E_MGy41IYVw"}}},
            "overlayMetadata":{"primaryText":{"content":"Still hits"},"secondaryText":{"content":"45K views"}}}},
          {"shortsLockupViewModel":{"entityId":"shorts-shelf-item-AbCdEfGhIjK","accessibilityText":"Second one, 2 thousand views - play Short"}}
        ],"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"NEXT"}}}}
        """;

    [Fact]
    public async Task ChannelTabs_AreReadFromYouTube_AndTheShortsTabLists()
    {
        var bodies = new List<string>();
        var innerTube = InnerTube(body => body.Contains("\"params\"") ? ShortsTab : ChannelHome, bodies);

        var tabs = await innerTube.GetChannelTabsAsync("UCuAXFkgsw1L7xaCfnd5JJOw");
        Assert.True(tabs.IsSuccess);
        Assert.Equal("EgZzaG9ydHPyBgUKA5oBAA==", tabs.Value["Shorts"]); // unescaped, ready to send

        var page = await innerTube.GetChannelTabPageAsync("UCuAXFkgsw1L7xaCfnd5JJOw", tabs.Value["Shorts"], null);
        Assert.True(page.IsSuccess);
        Assert.Equal(new[] { "E_MGy41IYVw", "AbCdEfGhIjK" }, page.Value.Items.Select(i => i.Id));
        Assert.All(page.Value.Items, i => Assert.True(i.IsShort));
        Assert.Equal("Still hits", page.Value.Items[0].Title);
        Assert.Equal(45_000, page.Value.Items[0].ViewCount);
        Assert.Equal("Second one", page.Value.Items[1].Title);   // from the spoken text when there is no overlay

        await innerTube.GetChannelTabsAsync("UCuAXFkgsw1L7xaCfnd5JJOw");
        Assert.Equal(2, bodies.Count); // the tab list is read once per channel
    }

    [Fact]
    public async Task ShortsSearch_UsesYouTubesShortsFilter()
    {
        var bodies = new List<string>();
        var result = await InnerTube(_ => ShortsTab, bodies).SearchShortsAsync("cats", null);
        Assert.Contains("\"params\":\"EgIQCQ==\"", bodies[0]);
        Assert.Equal(2, result.Value!.Items.Count);
    }

    [Theory]
    [InlineData("Lofi: beats / to study?", "Lofi_ beats _ to study_")]
    [InlineData("  ...  ", "TimePlayer list")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("Ends with dot and space. .", "Ends with dot and space")]
    [InlineData("con", "_con")]
    public void ListNames_BecomeSafeFolderNames(string name, string folder) =>
        Assert.Equal(folder, DownloadService.SafeFolderName(name));

    [Fact]
    public void NativeMessages_AreLengthPrefixedJson()
    {
        using var stream = new MemoryStream();
        NativeMessagingHost.WriteMessage(stream, """{"url":"https://youtu.be/dQw4w9WgXcQ"}""");
        Assert.Equal(4 + 38, stream.Length);
        Assert.Equal(38, BitConverter.ToInt32(stream.ToArray(), 0));
        stream.Position = 0;
        Assert.Equal("""{"url":"https://youtu.be/dQw4w9WgXcQ"}""", NativeMessagingHost.ReadMessage(stream));

        using var tooLong = new MemoryStream(BitConverter.GetBytes(10_000_000));
        Assert.Throws<IOException>(() => NativeMessagingHost.ReadMessage(tooLong));
    }

    [Theory]
    [InlineData("chrome-extension://lcfhohlccjpekkeeiggdjbcgbfedjijg/", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", false)]
    public void HostStarts_AreRecognised(string arg, bool host) =>
        Assert.Equal(host, NativeMessagingHost.IsHostStart(new[] { arg }));
}
