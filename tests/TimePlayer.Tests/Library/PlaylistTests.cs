using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

public class LibraryPlaylistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "timeplayer-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private LibraryService Create() => new(_dir, () => _now);

    private static SearchResult Video(string id, string channel = "Test channel", string? channelId = "UCBR8-60-B28hp2BmDPdntcQ") => new()
    {
        Id = id,
        Title = $"Video {id}",
        ChannelTitle = channel,
        ChannelId = channelId
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Playlists_CreateAddMoveRemove_AndSurviveRestart()
    {
        var library = Create();
        var playlist = library.CreatePlaylist("  Road trip ");
        Assert.Equal("Road trip", playlist.Name);

        Assert.True(library.AddToPlaylist(playlist.Id, Video("aaaaaaaaaaa")));
        Assert.True(library.AddToPlaylist(playlist.Id, Video("bbbbbbbbbbb")));
        Assert.True(library.AddToPlaylist(playlist.Id, Video("ccccccccccc")));
        Assert.False(library.AddToPlaylist(playlist.Id, Video("bbbbbbbbbbb")));

        Assert.Equal(0, library.MoveInPlaylist(playlist.Id, "ccccccccccc", -5));
        library.RemoveFromPlaylist(playlist.Id, "aaaaaaaaaaa");
        library.RenamePlaylist(playlist.Id, "Holiday");

        var reloaded = Create().GetPlaylist(playlist.Id)!;
        Assert.Equal("Holiday", reloaded.Name);
        Assert.Equal(new[] { "ccccccccccc", "bbbbbbbbbbb" }, reloaded.Videos.Select(v => v.VideoId));
    }

    [Fact]
    public void Likes_ArePlaylistNewestFirst_AndListedFirst()
    {
        var library = Create();
        library.CreatePlaylist("Mine");
        Assert.True(library.ToggleLike(Video("aaaaaaaaaaa")));
        Assert.True(library.ToggleLike(Video("bbbbbbbbbbb")));

        Assert.True(library.IsLiked("aaaaaaaaaaa"));
        Assert.Equal(new[] { "bbbbbbbbbbb", "aaaaaaaaaaa" }, library.LikedVideos.Select(v => v.VideoId));
        Assert.Equal(PlaylistKind.Liked, library.Playlists[0].Kind);

        Assert.False(library.ToggleLike(Video("aaaaaaaaaaa")));
        Assert.False(Create().IsLiked("aaaaaaaaaaa"));
        Assert.True(Create().IsLiked("bbbbbbbbbbb"));
    }

    [Fact]
    public void LikedPlaylist_CannotBeRenamed()
    {
        var library = Create();
        library.ToggleLike(Video("aaaaaaaaaaa"));
        library.RenamePlaylist(LibraryService.LikedPlaylistId, "Something else");
        Assert.Equal("Liked videos", library.GetPlaylist(LibraryService.LikedPlaylistId)!.Name);
    }

    [Fact]
    public void SavedYouTubePlaylist_RefreshKeepsUsersName()
    {
        var library = Create();
        var saved = library.SaveYouTubePlaylist("PLabcdefghijk", "Original", "Owner", new[] { Video("aaaaaaaaaaa") });
        library.RenamePlaylist(saved.Id, "My name");

        library.SaveYouTubePlaylist("PLabcdefghijk", "Original", "Owner", new[] { Video("aaaaaaaaaaa"), Video("bbbbbbbbbbb") });

        var playlist = library.FindSavedYouTubePlaylist("PLabcdefghijk")!;
        Assert.Equal("My name", playlist.Name);
        Assert.Equal(2, playlist.Videos.Count);
        Assert.Single(library.Playlists, p => p.Kind == PlaylistKind.YouTube);
        Assert.Equal("PLabcdefghijk", playlist.ToSearchResult().Id);
        Assert.Equal(saved.Id, playlist.ToSearchResult().LibraryPlaylistId);
    }

    [Fact]
    public void Hidden_VideosAndChannels_ArePersisted()
    {
        var library = Create();
        library.HideVideo("aaaaaaaaaaa");
        library.BlockChannel("UC_x5XG1OV2P6uZZ5FSM9Ttw", "Loud Channel");

        var reloaded = Create();
        Assert.True(reloaded.IsHidden(Video("aaaaaaaaaaa")));
        Assert.True(reloaded.IsHidden(Video("bbbbbbbbbbb", "Loud Channel", null)));
        Assert.True(reloaded.IsHidden(Video("ccccccccccc", "Renamed", "UC_x5XG1OV2P6uZZ5FSM9Ttw")));
        Assert.False(reloaded.IsHidden(Video("ddddddddddd")));

        reloaded.UnblockChannel("UC_x5XG1OV2P6uZZ5FSM9Ttw");
        Assert.False(reloaded.IsHidden(Video("ccccccccccc", "Renamed", "UC_x5XG1OV2P6uZZ5FSM9Ttw")));
    }

    [Fact]
    public void ImportHistory_KeepsExistingAndSortsNewestFirst()
    {
        var library = Create();
        library.RecordProgress(Video("aaaaaaaaaaa"), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), flush: true);

        int added = library.ImportHistory(new[]
        {
            new HistoryEntry { VideoId = "aaaaaaaaaaa", Title = "Old copy", LastWatchedUtc = _now.AddDays(-30) },
            new HistoryEntry { VideoId = "bbbbbbbbbbb", Title = "Older", LastWatchedUtc = _now.AddDays(-2) },
        });

        Assert.Equal(1, added);
        Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" }, library.History.Select(h => h.VideoId));
        Assert.Equal("Video aaaaaaaaaaa", library.History[0].Title);
    }
}

public class TakeoutImportTests
{
    [Fact]
    public void PlaylistCsv_NewAndOldFormats()
    {
        var newer = "Video ID,Playlist Video Creation Timestamp\r\naaaaaaaaaaa,2024-01-01T00:00:00+00:00\r\nbbbbbbbbbbb,2024-01-02T00:00:00+00:00\r\n";
        Assert.Equal(new[] { "aaaaaaaaaaa", "bbbbbbbbbbb" }, TakeoutImport.ParsePlaylistCsv(newer));

        var older = "Playlist Id,Channel Id,Time Created,Time Updated,Title,Description,Visibility\nPLx,UCx,2020,2021,Mix,,Private\n\nVideo Id,Time Added\nccccccccccc,2020-01-01 00:00:00 UTC\n\n";
        Assert.Equal(new[] { "ccccccccccc" }, TakeoutImport.ParsePlaylistCsv(older));

        Assert.Empty(TakeoutImport.ParsePlaylistCsv("Channel Id,Channel Url,Channel Title\nUCx,http://x,Name\n"));
    }

    [Theory]
    [InlineData("Road trip-videos.csv", "Road trip")]
    [InlineData("Road trip.csv", "Road trip")]
    [InlineData("Liked videos-videos.csv", "Liked videos")]
    public void PlaylistName_FromFileName(string file, string expected) =>
        Assert.Equal(expected, TakeoutImport.PlaylistNameFromFile(file));

    [Fact]
    public void WatchHistoryJson_GroupsRepeatsAndReadsChannel()
    {
        var json = """
        [
          {"header":"YouTube","title":"Watched Song A","titleUrl":"https://www.youtube.com/watch?v=aaaaaaaaaaa","subtitles":[{"name":"Band","url":"https://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ"}],"time":"2026-10-01T10:00:00.000Z"},
          {"header":"YouTube","title":"Visited YouTube Music","time":"2026-09-30T10:00:00.000Z"},
          {"header":"YouTube","title":"Watched Song A","titleUrl":"https://www.youtube.com/watch?v=aaaaaaaaaaa","time":"2026-09-01T10:00:00.000Z"},
          {"header":"YouTube","title":"Watched Song B","titleUrl":"https://www.youtube.com/watch?v=bbbbbbbbbbb","time":"2026-08-01T10:00:00.000Z"}
        ]
        """;
        var entries = TakeoutImport.ParseWatchHistoryJson(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(2, entries.Count);
        Assert.Equal("Song A", entries[0].Title);
        Assert.Equal("Band", entries[0].ChannelTitle);
        Assert.Equal("UCBR8-60-B28hp2BmDPdntcQ", entries[0].ChannelId);
        Assert.Equal(2, entries[0].WatchCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), entries[0].LastWatchedUtc);
    }

    [Fact]
    public void Zip_FindsEverything()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
            Add("Takeout/YouTube and YouTube Music/subscriptions/subscriptions.csv",
                "Channel Id,Channel Url,Channel Title\nUCBR8-60-B28hp2BmDPdntcQ,http://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ,YouTube\n");
            Add("Takeout/YouTube and YouTube Music/playlists/playlists.csv",
                "Playlist ID,Add new videos to top,Playlist Title (Original)\nPLx,False,Road trip\n");
            Add("Takeout/YouTube and YouTube Music/playlists/Road trip-videos.csv",
                "Video ID,Playlist Video Creation Timestamp\naaaaaaaaaaa,2024-01-01T00:00:00+00:00\n");
            Add("Takeout/YouTube and YouTube Music/playlists/Liked videos-videos.csv",
                "Video ID,Playlist Video Creation Timestamp\nbbbbbbbbbbb,2024-01-01T00:00:00+00:00\n");
            Add("Takeout/YouTube and YouTube Music/playlists/Watch later-videos.csv",
                "Video ID,Playlist Video Creation Timestamp\nccccccccccc,2024-01-01T00:00:00+00:00\n");
            Add("Takeout/YouTube and YouTube Music/history/watch-history.json",
                """[{"title":"Watched X","titleUrl":"https://www.youtube.com/watch?v=ddddddddddd","time":"2026-01-01T00:00:00Z"}]""");
        }
        buffer.Position = 0;

        var contents = TakeoutImport.ParseZip(buffer);

        Assert.Single(contents.Subscriptions!.Channels);
        var playlist = Assert.Single(contents.Playlists);
        Assert.Equal("Road trip", playlist.Name);
        Assert.Equal(new[] { "aaaaaaaaaaa" }, playlist.VideoIds);
        Assert.Equal(new[] { "bbbbbbbbbbb" }, contents.LikedVideoIds);
        Assert.Equal(new[] { "ccccccccccc" }, contents.WatchLaterIds);
        Assert.Equal("ddddddddddd", Assert.Single(contents.History).VideoId);
    }
}

public class PlaylistParsingTests
{
    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", true)]
    [InlineData("https://youtube.com/playlist?si=abc&list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", false)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("lofi playlist", false)]
    public void PlaylistLinks(string input, bool isPlaylistLink)
    {
        Assert.Equal(isPlaylistLink, YouTubeUrl.IsPlaylistLink(input));
        if (isPlaylistLink)
        {
            Assert.True(YouTubeUrl.TryGetPlaylistId(input, out var id));
            Assert.Equal("PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", id);
        }
    }

    [Fact]
    public void ParsePlaylistPage_ReadsTitleOwnerVideosAndContinuation()
    {
        var json = """
        {
          "header": { "pageHeaderRenderer": { "content": { "pageHeaderViewModel": {
            "title": { "dynamicTextViewModel": { "text": { "content": "Popular Music Videos" } } },
            "metadata": { "contentMetadataViewModel": { "metadataRows": [ { "metadataParts": [
              { "avatarStack": { "avatarStackViewModel": { "text": { "content": "by Music" } } } } ] } ] } } } } } },
          "contents": { "items": [
            { "lockupViewModel": { "contentType": "LOCKUP_CONTENT_TYPE_VIDEO", "contentId": "aaaaaaaaaaa",
              "metadata": { "lockupMetadataViewModel": { "title": { "content": "Song" },
                "metadata": { "contentMetadataViewModel": { "metadataRows": [
                  { "metadataParts": [ { "text": { "content": "Singer" } } ] },
                  { "metadataParts": [ { "text": { "content": "1.2M views" } }, { "text": { "content": "2 years ago" } } ] } ] } } } },
              "contentImage": { "thumbnailBadgeViewModel": { "text": "3:45" } } } },
            { "continuationItemRenderer": { "continuationEndpoint": { "continuationCommand": { "token": "NEXT" } } } }
          ] },
          "metadata": { "playlistMetadataRenderer": { "title": "Popular Music Videos" } }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var page = InnerTubeJson.ParsePlaylistPage(doc.RootElement, "PLx");

        Assert.Equal("Popular Music Videos", page.Title);
        Assert.Equal("Music", page.Owner);
        var video = Assert.Single(page.Videos);
        Assert.Equal("Song", video.Title);
        Assert.Equal(TimeSpan.FromSeconds(225), video.Duration);
        Assert.Equal("NEXT", page.NextPageToken);
    }

    [Fact]
    public void ListContinuationToken_IgnoresFilterChips()
    {
        // Search pages put the list's own token first and the filter chips' tokens after it.
        var json = """
        {
          "contents": { "sectionListRenderer": { "contents": [
            { "itemSectionRenderer": { "contents": [] } },
            { "continuationItemRenderer": { "continuationEndpoint": { "continuationCommand": { "token": "MORE" } } } } ] } },
          "header": { "chipCloudRenderer": { "chips": [
            { "chipCloudChipRenderer": { "navigationEndpoint": { "continuationCommand": { "token": "SHORTS_CHIP" } } } } ] } }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("MORE", InnerTubeJson.ListContinuationToken(doc.RootElement));
    }

    [Fact]
    public void ParsePlaylistLockup_ReadsSearchResultAndSkipsMixes()
    {
        var json = """
        [
          { "contentType": "LOCKUP_CONTENT_TYPE_PLAYLIST", "contentId": "PLXIclLvfETS3AgCnZg4N6QqHu_T27XKIq",
            "contentImage": { "thumbnailBadgeViewModel": { "text": "76 videos" } },
            "metadata": { "lockupMetadataViewModel": { "title": { "content": "Lofi beats" },
              "metadata": { "contentMetadataViewModel": { "metadataRows": [ { "metadataParts": [
                { "text": { "content": "Mimi Lofi Chill", "commandRuns": [ { "onTap": { "innertubeCommand": { "browseEndpoint": { "browseId": "UCq9J_CUURRfbP7vm4VN_3xg" } } } } ] } },
                { "text": { "content": "Playlist" } } ] } ] } } } } },
          { "contentType": "LOCKUP_CONTENT_TYPE_PLAYLIST", "contentId": "RDdQw4w9WgXcQ",
            "metadata": { "lockupMetadataViewModel": { "title": { "content": "Mix" } } } }
        ]
        """;
        using var doc = JsonDocument.Parse(json);
        var playlist = InnerTubeJson.ParsePlaylistLockup(doc.RootElement[0])!;

        Assert.Equal(SearchResultKind.Playlist, playlist.Kind);
        Assert.Equal("PLXIclLvfETS3AgCnZg4N6QqHu_T27XKIq", playlist.Id);
        Assert.Equal("Mimi Lofi Chill", playlist.ChannelTitle);
        Assert.Equal("UCq9J_CUURRfbP7vm4VN_3xg", playlist.ChannelId);
        Assert.Equal(76, playlist.PlaylistVideoCount);
        Assert.Equal("Playlist: Lofi beats, 76 videos, by Mimi Lofi Chill", playlist.AccessibleSummary);
        Assert.Null(InnerTubeJson.ParsePlaylistLockup(doc.RootElement[1]));
    }
}

public class RecommendationServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "timeplayer-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeWatchPageService _watchPage = new();
    private readonly FakeDiscoveryService _discovery = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SearchResult Video(string id, string channel) => new()
    {
        Id = id,
        Title = $"Video {id}",
        ChannelTitle = channel,
        ChannelId = "UC" + channel.PadRight(22, 'x')[..22]
    };

    private RecommendationService Create(LibraryService library) =>
        new(library, _watchPage, _discovery, NullLogger<RecommendationService>.Instance, () => _now);

    [Fact]
    public async Task NewUser_GetsTrending()
    {
        _discovery.Featured.Add(Video("ttttttttttt", "Trend"));
        var service = Create(new LibraryService(_dir, () => _now));

        var result = await service.GetAsync(new RecommendationOptions());

        Assert.False(result.IsPersonal);
        Assert.Equal("ttttttttttt", Assert.Single(result.Videos).Id);
    }

    [Fact]
    public async Task FromHistoryAndLikes_ExcludesWatchedHiddenAndLimitsEachChannel()
    {
        var library = new LibraryService(_dir, () => _now);
        library.RecordProgress(Video("seed1111111", "A"), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4), flush: true);
        library.ToggleLike(Video("seed2222222", "B"));
        library.HideVideo("hidden00000");
        library.BlockChannel("UCBlockedxxxxxxxxxxxxxxx", "Blocked");

        _watchPage.Related["seed1111111"] = new()
        {
            Video("common00000", "C"), Video("seed2222222", "B"), Video("hidden00000", "D"),
            Video("same1111111", "Same"), Video("same2222222", "Same"), Video("same3333333", "Same"),
            Video("blocked0000", "Blocked")
        };
        _watchPage.Related["seed2222222"] = new() { Video("common00000", "C"), Video("other000000", "E") };

        var result = await Create(library).GetAsync(new RecommendationOptions());
        var ids = result.Videos.Select(v => v.Id).ToList();

        Assert.True(result.IsPersonal);
        Assert.Equal("common00000", ids[0]); // up next for both seeds
        Assert.DoesNotContain("seed2222222", ids);
        Assert.DoesNotContain("hidden00000", ids);
        Assert.DoesNotContain("blocked0000", ids);
        Assert.Equal(2, result.Videos.Count(v => v.ChannelTitle == "Same"));
        Assert.Contains("other000000", ids);
    }

    [Fact]
    public async Task Cache_IsUsedUntilRefresh_AndForgetRemoves()
    {
        var library = new LibraryService(_dir, () => _now);
        library.ToggleLike(Video("seed1111111", "A"));
        _watchPage.Related["seed1111111"] = new() { Video("first000000", "C") };
        var service = Create(library);

        await service.GetAsync(new RecommendationOptions());
        _watchPage.Related["seed1111111"] = new() { Video("second00000", "C") };

        Assert.True(service.IsCacheFresh);
        Assert.Equal("first000000", (await service.GetAsync(new RecommendationOptions())).Videos[0].Id);
        Assert.Equal("second00000", (await service.GetAsync(new RecommendationOptions(Refresh: true))).Videos[0].Id);

        service.Forget(v => v.Id == "second00000");
        Assert.False(service.IsCacheFresh);
    }
}
