using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Library;

public class SubscriptionFormatsTests
{
    private const string ChannelA = "UCBR8-60-B28hp2BmDPdntcQ";
    private const string ChannelB = "UC_x5XG1OV2P6uZZ5FSM9Ttw";

    [Fact]
    public void Parse_NewPipeJson_ImportsYouTubeAndSkipsOtherServices()
    {
        var json = $$"""
        {"app_version":"0.27.6","app_version_int":999,"subscriptions":[
          {"service_id":0,"url":"https://www.youtube.com/channel/{{ChannelA}}","name":"YouTube"},
          {"service_id":1,"url":"https://soundcloud.com/someone","name":"SoundCloud artist"},
          {"service_id":0,"url":"https://www.youtube.com/channel/{{ChannelB}}","name":"Google for Developers"}
        ]}
        """;

        var result = SubscriptionFormats.Parse(json);

        Assert.Equal("NewPipe", result.FormatName);
        Assert.Equal(new[] { ChannelA, ChannelB }, result.Channels.Select(c => c.ChannelId));
        Assert.Equal("Google for Developers", result.Channels[1].Name);
        Assert.Equal(1, result.SkippedEntries);
    }

    [Fact]
    public void Parse_TakeoutCsv_HandlesHeaderAndQuotedNames()
    {
        var csv = $"Channel Id,Channel Url,Channel Title\n{ChannelA},http://www.youtube.com/channel/{ChannelA},YouTube\n{ChannelB},http://www.youtube.com/channel/{ChannelB},\"Google, for Developers\"\n";

        var result = SubscriptionFormats.Parse(csv);

        Assert.Equal(2, result.Channels.Count);
        Assert.Equal("Google, for Developers", result.Channels[1].Name);
        Assert.Equal(0, result.SkippedEntries);
    }

    [Fact]
    public void Parse_Opml_ReadsFeedUrls()
    {
        var opml = $"""
        <?xml version="1.0"?>
        <opml version="1.1"><body><outline text="YouTube Subscriptions">
          <outline text="YouTube" title="YouTube" type="rss" xmlUrl="https://www.youtube.com/feeds/videos.xml?channel_id={ChannelA}"/>
          <outline text="Blog" type="rss" xmlUrl="https://example.com/feed.xml"/>
        </outline></body></opml>
        """;

        var result = SubscriptionFormats.Parse(opml);

        Assert.Equal("OPML", result.FormatName);
        Assert.Single(result.Channels);
        Assert.Equal(1, result.SkippedEntries);
    }

    [Fact]
    public void Parse_FreeTubeProfilesDb_ReadsEveryProfileOnce()
    {
        var db = $$"""
        {"name":"All Channels","subscriptions":[{"id":"{{ChannelA}}","name":"YouTube"},{"id":"{{ChannelB}}","name":"Google"}],"_id":"allChannels"}
        {"name":"Tech","subscriptions":[{"id":"{{ChannelB}}","name":"Google"}],"_id":"x1"}
        """;

        var result = SubscriptionFormats.Parse(db);

        Assert.Equal("FreeTube", result.FormatName);
        Assert.Equal(2, result.Channels.Count);
    }

    [Fact]
    public void NewPipeExport_RoundTrips()
    {
        var channels = new[] { new ChannelSubscription(ChannelA, "YouTube"), new ChannelSubscription(ChannelB, "Google") };

        var reparsed = SubscriptionFormats.Parse(SubscriptionFormats.ToNewPipeJson(channels));

        Assert.Equal(channels, reparsed.Channels);
        Assert.Contains("\"service_id\": 0", SubscriptionFormats.ToNewPipeJson(channels));
    }

    [Fact]
    public void OpmlExport_RoundTrips()
    {
        var channels = new[] { new ChannelSubscription(ChannelA, "YouTube & Friends") };
        Assert.Equal(channels, SubscriptionFormats.Parse(SubscriptionFormats.ToOpml(channels)).Channels);
    }

    [Fact]
    public void ParseZip_FindsTakeoutSubscriptionsEvenWithTranslatedNames()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
            Add("Takeout/archive_browser.html", "<html></html>");
            Add("Takeout/YouTube y YouTube Music/historial/historial-de-busqueda.json", "[]");
            Add("Takeout/YouTube y YouTube Music/suscripciones/suscripciones.csv",
                string.Join("\n",
                    "ID del canal,URL del canal,Titulo del canal",
                    $"{ChannelA},http://www.youtube.com/channel/{ChannelA},YouTube",
                    $"{ChannelB},http://www.youtube.com/channel/{ChannelB},Google"));
        }
        buffer.Position = 0;

        var result = SubscriptionFormats.ParseZip(buffer);

        Assert.Equal("Google Takeout", result.FormatName);
        Assert.Equal(new[] { ChannelA, ChannelB }, result.Channels.Select(c => c.ChannelId));
    }

    [Fact]
    public void Parse_UnknownText_ThrowsHelpfulFormatException()
    {
        var ex = Assert.Throws<FormatException>(() => SubscriptionFormats.Parse("hello world"));
        Assert.Contains("NewPipe", ex.Message);
    }
}

public class LibraryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "timeplayer-tests", Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private LibraryService Create() => new(_dir, () => _now);

    private static SearchResult Video(string id = "dQw4w9WgXcQ") => new()
    {
        Id = id,
        Title = "Test video",
        ChannelTitle = "Test channel",
        ChannelId = "UCBR8-60-B28hp2BmDPdntcQ"
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Subscriptions_PersistAsNewPipeJson()
    {
        var library = Create();
        Assert.True(library.ToggleSubscription("UCBR8-60-B28hp2BmDPdntcQ", "YouTube"));

        var reloaded = Create();
        Assert.True(reloaded.IsSubscribed("UCBR8-60-B28hp2BmDPdntcQ"));
        Assert.Equal("NewPipe", SubscriptionFormats.Parse(File.ReadAllText(Path.Combine(_dir, "subscriptions.json"))).FormatName);

        Assert.False(reloaded.ToggleSubscription("UCBR8-60-B28hp2BmDPdntcQ", "YouTube"));
        Assert.Empty(Create().Subscriptions);
    }

    [Fact]
    public void Import_SkipsChannelsAlreadySubscribed()
    {
        var library = Create();
        library.ToggleSubscription("UCBR8-60-B28hp2BmDPdntcQ", "YouTube");

        int added = library.ImportSubscriptions(new[]
        {
            new ChannelSubscription("UCBR8-60-B28hp2BmDPdntcQ", "YouTube"),
            new ChannelSubscription("UC_x5XG1OV2P6uZZ5FSM9Ttw", "Google")
        });

        Assert.Equal(1, added);
        Assert.Equal(2, library.Subscriptions.Count);
    }

    [Fact]
    public void History_ResumePositionOnlyForPartlyWatchedVideos()
    {
        var library = Create();
        var video = Video();

        library.RecordProgress(video, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), flush: true);
        Assert.Null(library.GetHistory(video.Id)!.ResumePosition); // too early to bother

        library.RecordProgress(video, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(10), flush: true);
        Assert.Equal(TimeSpan.FromMinutes(4), Create().GetHistory(video.Id)!.ResumePosition);

        library.RecordProgress(video, TimeSpan.FromSeconds(590), TimeSpan.FromMinutes(10), flush: true);
        var finished = Create().GetHistory(video.Id)!;
        Assert.True(finished.IsFinished);
        Assert.Null(finished.ResumePosition);
    }

    [Fact]
    public void History_IsNewestFirstAndCountsSessions()
    {
        var library = Create();
        library.RecordProgress(Video("aaaaaaaaaaa"), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        library.RecordProgress(Video("bbbbbbbbbbb"), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        library.RecordProgress(Video("aaaaaaaaaaa"), TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(5));
        Assert.Equal(1, library.GetHistory("aaaaaaaaaaa")!.WatchCount);

        _now = _now.AddDays(1);
        library.RecordProgress(Video("aaaaaaaaaaa"), TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" }, library.History.Select(h => h.VideoId));
        Assert.Equal(2, library.GetHistory("aaaaaaaaaaa")!.WatchCount);
    }

    [Fact]
    public void History_ProgressIsFlushedOnRequest()
    {
        var library = Create();
        library.RecordProgress(Video(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        _now = _now.AddSeconds(2);
        library.RecordProgress(Video(), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)); // throttled
        library.Flush();

        Assert.Equal(120, Create().GetHistory(Video().Id)!.PositionSeconds);
    }

    [Fact]
    public void WatchLater_Toggles()
    {
        var library = Create();
        Assert.True(library.ToggleWatchLater(Video()));
        Assert.True(Create().IsInWatchLater(Video().Id));
        Assert.False(library.ToggleWatchLater(Video()));
        Assert.Empty(Create().WatchLater);
    }

    [Fact]
    public void CorruptFile_IsKeptAsideNotLost()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), "{ not json");

        var library = Create();

        Assert.Empty(library.History);
        Assert.True(File.Exists(Path.Combine(_dir, "history.json.unreadable")));
    }
}

public class ChannelFeedParserTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns:media="http://search.yahoo.com/mrss/" xmlns="http://www.w3.org/2005/Atom">
         <title>YouTube</title>
         <author><name>YouTube</name><uri>https://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ</uri></author>
         <entry>
          <yt:videoId>XiW4sNNLFBY</yt:videoId>
          <yt:channelId>UCBR8-60-B28hp2BmDPdntcQ</yt:channelId>
          <title>A short</title>
          <link rel="alternate" href="https://www.youtube.com/shorts/XiW4sNNLFBY"/>
          <author><name>YouTube</name></author>
          <published>2026-10-17T19:00:28+00:00</published>
          <media:group>
           <media:thumbnail url="https://i1.ytimg.com/vi/XiW4sNNLFBY/hqdefault.jpg" width="480" height="360"/>
           <media:description>Description</media:description>
           <media:community><media:statistics views="12345"/></media:community>
          </media:group>
         </entry>
         <entry>
          <yt:videoId>dQw4w9WgXcQ</yt:videoId>
          <title>A regular video</title>
          <link rel="alternate" href="https://www.youtube.com/watch?v=dQw4w9WgXcQ"/>
          <published>2026-10-10T08:00:00+00:00</published>
         </entry>
        </feed>
        """;

    [Fact]
    public void ParseChannelFeed_ReadsVideosShortsAndViews()
    {
        var uploads = DiscoveryService.ParseChannelFeed(Feed, "UCBR8-60-B28hp2BmDPdntcQ");

        Assert.Equal("YouTube", uploads.ChannelName);
        Assert.Equal(2, uploads.Videos.Count);
        var shortVideo = uploads.Videos[0];
        Assert.True(shortVideo.IsShort);
        Assert.Equal(12345, shortVideo.ViewCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 19, 0, 28, TimeSpan.Zero), shortVideo.PublishedAt);
        Assert.False(uploads.Videos[1].IsShort);
        Assert.Equal("UCBR8-60-B28hp2BmDPdntcQ", uploads.Videos[1].ChannelId);
    }

    [Fact]
    public void AccessibleSummary_SpeaksStatusShortAndAge()
    {
        var video = DiscoveryService.ParseChannelFeed(Feed, "UCBR8-60-B28hp2BmDPdntcQ").Videos[0] with { StatusPrefix = "New" };

        Assert.StartsWith("New. A short. by YouTube. Short.", video.AccessibleSummary);
        Assert.Contains("ago", video.AccessibleSummary);
    }
}
