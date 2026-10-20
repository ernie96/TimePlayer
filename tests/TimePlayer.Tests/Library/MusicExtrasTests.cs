using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

public class MusicExtrasTests
{
    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Music Video)", "RickAstleyVEVO", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Never Gonna Give You Up", "Rick Astley - Topic", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Daft Punk – Get Lucky (feat. Pharrell Williams) [Official Audio]", "Daft Punk", "Daft Punk", "Get Lucky")]
    [InlineData("Bohemian Rhapsody (Remastered 2011)", "Queen Official", "Queen", "Bohemian Rhapsody")]
    public void TheSong_IsWorkedOutFromTheVideo(string title, string channel, string artist, string track)
    {
        var (a, t) = LyricsService.GuessSong(title, channel);
        Assert.Equal(artist, a);
        Assert.Equal(track, t);
    }

    [Fact]
    public void TimedLyrics_AreReadInOrder_WithRepeatedLinesAndBreaksHandled()
    {
        var lines = LyricsService.ParseLrc("""
            [ar:Someone]
            [00:12.50]First line
            [00:20.00]
            [00:25.10][01:05.00]Chorus line
            [00:30,5]Third line
            """);
        Assert.Equal(new[] { "First line", "Chorus line", "Third line", "Chorus line" }, lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(12.5), lines[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(65), lines[3].Start);
    }

    [Fact]
    public async Task Radio_ListsTheSongsOfYouTubesMix()
    {
        const string next = """
            {"contents":{"twoColumnWatchNextResults":{"playlist":{"playlist":{"contents":[
              {"playlistPanelVideoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"simpleText":"Never Gonna Give You Up"},"longBylineText":{"runs":[{"text":"Rick Astley"}]},"lengthText":{"simpleText":"3:33"}}},
              {"playlistPanelVideoRenderer":{"videoId":"yPYZpwSpKmA","title":{"simpleText":"Together Forever"},"longBylineText":{"runs":[{"text":"Rick Astley"}]},"lengthText":{"simpleText":"3:25"}}},
              {"playlistPanelVideoRenderer":{"videoId":"yPYZpwSpKmA","title":{"simpleText":"Together Forever"}}}
            ]}}}}}
            """;
        string? sent = null;
        var http = new MockHttpMessageHandler(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(next) };
        });
        var config = new ConfigurationService(Options.Create(new AppConfig()), Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}.json"));
        var innerTube = new InnerTubeBackendProvider(new HttpClient(http), config, NullLogger<InnerTubeBackendProvider>.Instance);

        var radio = await innerTube.GetRadioAsync("dQw4w9WgXcQ");

        Assert.True(radio.IsSuccess);
        Assert.Equal(new[] { "Never Gonna Give You Up", "Together Forever" }, radio.Value.Select(s => s.Title));
        Assert.Contains("\"playlistId\":\"RDAMVMdQw4w9WgXcQ\"", sent);
        Assert.Equal(TimeSpan.FromSeconds(213), radio.Value[0].Duration);
    }

    [Fact]
    public async Task AccountPages_NeedTheAccount()
    {
        var config = new ConfigurationService(Options.Create(new AppConfig()), Path.Combine(Path.GetTempPath(), $"tp-{Guid.NewGuid():N}.json"));
        var http = MockHttpMessageHandler.Status(HttpStatusCode.OK);
        var innerTube = new InnerTubeBackendProvider(new HttpClient(http), config, NullLogger<InnerTubeBackendProvider>.Instance);

        var home = await innerTube.GetAccountPageAsync("FEwhat_to_watch");

        Assert.False(home.IsSuccess);
        Assert.Empty(http.Requests); // nothing is sent without a sign-in
    }
}
