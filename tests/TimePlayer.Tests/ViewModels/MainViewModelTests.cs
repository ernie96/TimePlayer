using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Backends.TubeRepair;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Library;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;
using TimePlayer.Tests.Helpers;
using TimePlayer.Wpf.ViewModels;
using Xunit;

namespace TimePlayer.Tests.ViewModels;

public class MainViewModelTests
{
    private sealed class MockHealthService : IInstanceHealthService
    {
        public Task<InstanceHealthMeasurement> ProbeInstanceAsync(PublicInstance instance, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstanceHealthMeasurement { InstanceUri = instance.BaseUri, ProviderType = BackendProviderType.Piped, IsSuccessful = true });

        public Task<PublicInstance?> SelectBestInstanceAsync(BackendProviderType providerType, CancellationToken cancellationToken = default) =>
            Task.FromResult<PublicInstance?>(new PublicInstance { BaseUri = new Uri("https://piped.video"), ProviderType = BackendProviderType.Piped });

        public void RecordSuccess(PublicInstance instance, TimeSpan latency) { }
        public void RecordFailure(PublicInstance instance, string reason) { }
    }

    private sealed class MockInstanceDirectory : IPublicInstanceDirectory
    {
        public Task<IReadOnlyList<PublicInstance>> GetInstancesAsync(BackendProviderType providerType, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicInstance>>(new List<PublicInstance>
            {
                new() { BaseUri = new Uri("https://piped.video"), ProviderType = providerType }
            });

        public Task SaveHealthyInstancesAsync(BackendProviderType providerType, IReadOnlyList<PublicInstance> instances, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MockAccessibilityService : IAccessibilityService
    {
        public List<(string message, AnnouncementPriority priority)> Announcements { get; } = new();
        public event EventHandler<AccessibilityAnnouncementEventArgs>? AnnouncementRequested;

        public void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium)
        {
            Announcements.Add((message, priority));
            AnnouncementRequested?.Invoke(this, new AccessibilityAnnouncementEventArgs(message, priority));
        }
    }

    private readonly FakePlayerEngine _playerEngine = new();
    private readonly ConfigurationService _configService;
    private readonly FakeYtDlpManager _ytDlpManager = new();
    private readonly FakeProcessRunner _processRunner = new();
    private readonly MockAccessibilityService _accessibility = new();
    private readonly MockHealthService _healthService = new();
    private readonly MockInstanceDirectory _instanceDirectory = new();

    private readonly TubeRepairBackendProvider _tubeRepair;
    private readonly InvidiousBackendProvider _invidious;
    private readonly InnerTubeBackendProvider _innerTube;
    private readonly YtDlpBackendProvider _ytdlp;
    private readonly PlaybackCoordinator _coordinator;
    private readonly LibraryService _library = new(Path.Combine(Path.GetTempPath(), "timeplayer-tests", Guid.NewGuid().ToString("N")));
    private readonly FakeDiscoveryService _discovery = new();
    private readonly FakeWatchPageService _watchPage = new();
    private readonly FakeDownloadService _downloads = new();
    private readonly FakePlaylistService _playlists = new();
    private readonly FakeRecommendationService _recommendations = new();
    private readonly FakeSponsorBlockService _sponsorBlock = new();
    private readonly FakeDislikeService _dislikes = new();

    public MainViewModelTests()
    {
        _configService = new ConfigurationService(Options.Create(new AppConfig()));
        var httpHandler = MockHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(httpHandler);

        _tubeRepair = new TubeRepairBackendProvider(httpClient, _configService, NullLogger<TubeRepairBackendProvider>.Instance);
        _invidious = new InvidiousBackendProvider(httpClient, _healthService, _configService, NullLogger<InvidiousBackendProvider>.Instance);
        _innerTube = new InnerTubeBackendProvider(httpClient, _configService, NullLogger<InnerTubeBackendProvider>.Instance);
        _ytdlp = new YtDlpBackendProvider(_ytDlpManager, _processRunner, _configService, NullLogger<YtDlpBackendProvider>.Instance);

        _coordinator = new PlaybackCoordinator(
            _tubeRepair,
            _invidious,
            _innerTube,
            _ytdlp,
            _playerEngine,
            _configService,
            NullLogger<PlaybackCoordinator>.Instance);
    }

    private MainViewModel CreateViewModel()
    {
        return new MainViewModel(
            _coordinator,
            _playerEngine,
            _ytDlpManager,
            _accessibility,
            _configService,
            _healthService,
            _instanceDirectory,
            _library,
            _discovery,
            _watchPage,
            _downloads,
            _playlists,
            _recommendations,
            _sponsorBlock,
            _dislikes,
            NullLogger<MainViewModel>.Instance);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_AnnouncesValidationMessage()
    {
        await using var vm = CreateViewModel();
        vm.SearchQuery = "   ";

        await vm.SearchAsync();

        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Please enter a search query"));
    }

    [Fact]
    public async Task SearchAsync_WithDirectYouTubeUrl_RecognizesUrlAndOpensPlayerDialog()
    {
        await using var vm = CreateViewModel();
        vm.SearchQuery = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

        await vm.SearchAsync();

        Assert.True(vm.IsPlayerDialogOpen);
        Assert.Single(_playerEngine.LoadedUrls);
        Assert.Contains("dQw4w9WgXcQ", _playerEngine.LoadedUrls[0]);
    }

    [Fact]
    public async Task PlaySearchResultAsync_OpensPlayerDialogAndFocuses()
    {
        await using var vm = CreateViewModel();
        bool playerFocusRequested = false;
        vm.RequestFocusPlayer += (s, e) => playerFocusRequested = true;

        var result = new SearchResult
        {
            Id = "jNQXAC9IVRw",
            Title = "Me at the zoo",
            ChannelTitle = "jawed",
            Duration = TimeSpan.FromSeconds(19)
        };

        await vm.PlaySearchResultAsync(result);

        Assert.True(vm.IsPlayerDialogOpen);
        Assert.True(playerFocusRequested);
        Assert.Equal("Me at the zoo", vm.CurrentVideoTitle);
        Assert.Equal("jawed", vm.CurrentChannelTitle);
        Assert.Contains(_playerEngine.LoadedUrls, u => u.Contains("jNQXAC9IVRw"));
    }

    [Fact]
    public async Task ClosePlayerDialog_ClosesDialogAndRequestsFocusOnResults()
    {
        await using var vm = CreateViewModel();
        bool resultsFocusRequested = false;
        vm.RequestFocusResults += (s, e) => resultsFocusRequested = true;

        vm.IsPlayerDialogOpen = true;
        await vm.ClosePlayerDialogAsync();

        Assert.False(vm.IsPlayerDialogOpen);
        Assert.True(resultsFocusRequested);
        Assert.Equal(PlaybackState.Stopped, _playerEngine.State);
    }

    [Fact]
    public async Task SubtitleTextChanged_UpdatesCurrentSubtitleTextAndAnnouncesToScreenReader()
    {
        await using var vm = CreateViewModel();

        vm.SpeakCaptions = true;
        _playerEngine.RaiseSubtitleText("Welcome to the video screen reader caption test");

        Assert.Equal("Welcome to the video screen reader caption test", vm.CurrentSubtitleText);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Welcome to the video screen reader caption test");
    }

    [Fact]
    public async Task Captions_RepeatsAreSkipped_AndLinesAreJoined()
    {
        await using var vm = CreateViewModel();
        vm.SpeakCaptions = true;

        _playerEngine.RaiseSubtitleText("first line" + System.Environment.NewLine + "second line");
        _playerEngine.RaiseSubtitleText("first line second line");
        _playerEngine.RaiseSubtitleText("");
        _playerEngine.RaiseSubtitleText("next caption");

        var spoken = _accessibility.Announcements.Select(a => a.message).ToList();
        Assert.Equal(1, spoken.Count(m => m == "first line second line"));
        Assert.Contains("next caption", spoken);
    }

    [Fact]
    public async Task Captions_AreNotSpokenUnlessTurnedOn()
    {
        await using var vm = CreateViewModel();

        _playerEngine.RaiseSubtitleText("A caption line");

        Assert.Equal("A caption line", vm.CurrentSubtitleText);
        Assert.DoesNotContain(_accessibility.Announcements, a => a.message == "A caption line");
    }

    [Fact]
    public async Task QueueManagement_Enqueue_PlayNext_ClearQueue()
    {
        await using var vm = CreateViewModel();

        var item1 = new SearchResult { Id = "dQw4w9WgXcQ", Title = "Song 1", ChannelTitle = "Artist 1", Duration = TimeSpan.FromMinutes(3) };
        var item2 = new SearchResult { Id = "jNQXAC9IVRw", Title = "Video 2", ChannelTitle = "Artist 2", Duration = TimeSpan.FromMinutes(1) };

        await vm.EnqueueSearchResultAsync(item1);
        await vm.EnqueueSearchResultAsync(item2);

        Assert.Equal(2, vm.PlaybackQueue.Count);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Added to queue.");

        // Play queued item 1
        await vm.PlayQueueItemAsync(vm.PlaybackQueue[0]);
        Assert.True(vm.IsPlayerDialogOpen);
        Assert.True(vm.CanPlayNext);
        Assert.False(vm.CanPlayPrevious);

        // Next item
        await vm.PlayNextAsync();
        Assert.Equal("Video 2", vm.CurrentVideoTitle);

        // Clear queue
        await vm.ClearQueueAsync();
        Assert.Empty(vm.PlaybackQueue);
    }

    [Fact]
    public async Task VolumeAndSpeedControls_UpdatePropertiesAndAnnounce()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)12345);

        // Volume
        vm.VolumePercent = 50.0;
        await vm.VolumeUpAsync();
        Assert.Equal(55.0, vm.VolumePercent);

        await vm.VolumeDownAsync();
        Assert.Equal(50.0, vm.VolumePercent);

        // Mute
        await vm.ToggleMuteAsync();
        Assert.True(vm.IsMuted);
        await vm.ToggleMuteAsync();
        Assert.False(vm.IsMuted);

        // Speed
        vm.PlaybackSpeed = 1.0;
        await vm.SpeedUpAsync();
        Assert.Equal(1.25, vm.PlaybackSpeed);

        await vm.ResetSpeedAsync();
        Assert.Equal(1.0, vm.PlaybackSpeed);
    }

    [Fact]
    public async Task SettingsAndDiagnostics_ToggleAndRunDiagnostics()
    {
        await using var vm = CreateViewModel();

        // Open settings
        vm.OpenSettings();
        Assert.True(vm.IsSettingsDialogOpen);

        // Run diagnostics
        await vm.RunDiagnosticsAsync();
        Assert.False(vm.IsRunningDiagnostics);
        Assert.Contains("TubeRepair Primary Backend", vm.DiagnosticsReport);
        Assert.Contains("Invidious Backend Status", vm.DiagnosticsReport);
        Assert.Contains("InnerTube Tertiary Backend", vm.DiagnosticsReport);
        Assert.Contains("Local yt-dlp Executable", vm.DiagnosticsReport);
        Assert.Contains("Embedded mpv Engine", vm.DiagnosticsReport);

        // Close settings
        vm.CloseSettings();
        Assert.False(vm.IsSettingsDialogOpen);
    }

    [Fact]
    public async Task CancelSearch_CancelsOngoingSearchAndAnnounces()
    {
        await using var vm = CreateViewModel();

        vm.SearchQuery = "ambient sleep music";
        vm.IsSearching = true;

        vm.CancelSearch();

        Assert.False(vm.IsSearching);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Search cancelled."));
    }

    [Fact]
    public async Task ClearSearch_CancelsSearchAndClearsResults()
    {
        await using var vm = CreateViewModel();

        vm.SearchQuery = "some query";
        vm.CurrentSection = BrowseSection.Search;
        vm.SearchResults.Add(new SearchResult
        {
            Id = "dQw4w9WgXcQ",
            Title = "Rick",
            ChannelTitle = "VEVO",
            Duration = TimeSpan.FromMinutes(3)
        });

        vm.ClearSearch();

        Assert.Empty(vm.SearchQuery);
        Assert.Empty(vm.SearchResults);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Search cleared"));
    }

    [Fact]
    public async Task ErrorBanner_ClearError_ClearsMessageAndCanRetry()
    {
        await using var vm = CreateViewModel();

        vm.ErrorMessage = "Network connection lost. Please check internet.";
        vm.CanRetry = true;

        vm.ClearError();

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.CanRetry);
    }

    [Fact]
    public async Task BrailleTouchCursor_SeeksEngineToCalculatedPosition()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)12345);

        _playerEngine.Duration = TimeSpan.FromSeconds(390);
        _playerEngine.State = PlaybackState.Playing;
        vm.Duration = TimeSpan.FromSeconds(390);
        vm.SelectedBrailleCellCount = 40;
        vm.PlaybackState = PlaybackState.Playing;

        // Touch cell 10 of 40 (0-indexed 10): 10 / 39 * 390 = 100 seconds
        await vm.BrailleTouchCursorAsync(10);

        Assert.Equal(TimeSpan.FromSeconds(100), vm.CurrentTime);
        Assert.Equal(BrailleProgressTrack.LowerG, vm.BrailleDisplayLine[10]);

        // Touch cell 0 (beginning)
        await vm.BrailleTouchCursorAsync(0);
        Assert.Equal(TimeSpan.Zero, vm.CurrentTime);
        Assert.Equal(BrailleProgressTrack.LowerG, vm.BrailleDisplayLine[0]);

        // Touch cell 39 (end)
        await vm.BrailleTouchCursorAsync(39);
        Assert.Equal(TimeSpan.FromSeconds(390), vm.CurrentTime);
        Assert.Equal(BrailleProgressTrack.LowerG, vm.BrailleDisplayLine[39]);
    }

    [Fact]
    public async Task BrailleSettings_PersistsCellCountCorrectly()
    {
        await using var vm = CreateViewModel();
        vm.SelectedBrailleCellCount = 20;

        await vm.SaveSettingsAsync();

        Assert.Equal(20, _configService.CurrentConfig.Accessibility.BrailleDisplayCellCount);
    }

    [Fact]
    public async Task CheckForUpdates_VerifiesYtDlpAndAnnouncesStatus()
    {
        await using var vm = CreateViewModel();

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.IsCheckingUpdates);
        Assert.Contains("yt-dlp is verified", vm.DependencyStatusMessage);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("yt-dlp is verified"));
    }

    [Fact]
    public async Task SaveSettings_PersistsChangesToConfigurationService()
    {
        await using var vm = CreateViewModel();

        vm.OpenSettings();
        vm.SelectedQuality = QualityPreference.P720;
        vm.EnableHardwareDecoding = false;
        vm.SelectedBrailleCellCount = 18;
        vm.EnableBrailleProgress = true;

        await vm.SaveSettingsAsync();

        var config = _configService.CurrentConfig;
        Assert.Equal(QualityPreference.P720, config.Playback.PreferredQuality);
        Assert.False(config.Playback.HardwareDecoding);
        Assert.Equal(18, config.Accessibility.BrailleDisplayCellCount);
        Assert.True(config.Accessibility.EnableBrailleProgress);
        Assert.False(vm.IsSettingsDialogOpen);
    }

    [Fact]
    public async Task ToggleBrailleProgress_TogglesStateAndAnnounces()
    {
        await using var vm = CreateViewModel();

        Assert.True(vm.EnableBrailleProgress);

        vm.ToggleBrailleProgress();
        Assert.False(vm.EnableBrailleProgress);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Braille progress track disabled"));

        vm.ToggleBrailleProgress();
        Assert.True(vm.EnableBrailleProgress);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Braille progress track enabled"));
    }

    // --- Library & discovery ---

    private static SearchResult Video(string id, string title, string channelId = "UCBR8-60-B28hp2BmDPdntcQ", DateTimeOffset? published = null) => new()
    {
        Id = id,
        Title = title,
        ChannelTitle = "YouTube",
        ChannelId = channelId,
        Duration = TimeSpan.FromMinutes(10),
        PublishedAt = published
    };

    [Fact]
    public async Task PlayingAPartlyWatchedVideo_ResumesAndSaysHowToStartOver()
    {
        var video = Video("jNQXAC9IVRw", "Me at the zoo");
        _library.RecordProgress(video, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(10), flush: true);
        await using var vm = CreateViewModel();

        await vm.PlaySearchResultAsync(video);

        Assert.Equal(TimeSpan.FromMinutes(4), _playerEngine.CurrentTime);
        Assert.Contains(_accessibility.Announcements, a => a.message.StartsWith("Resumed at 4 minutes") && a.message.Contains("Home"));
    }

    [Fact]
    public async Task ReplayingInTheSameSession_ResumesWhereItWasClosed()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        var video = Video("jNQXAC9IVRw", "Me at the zoo");

        await vm.PlaySearchResultAsync(video);
        await _playerEngine.SeekAsync(TimeSpan.FromMinutes(2)); // watched 2 of the fake's 3 minutes
        await vm.ClosePlayerDialogAsync();
        _playerEngine.CurrentTime = TimeSpan.Zero;

        await vm.PlaySearchResultAsync(video);

        Assert.Equal(TimeSpan.FromMinutes(2), _playerEngine.CurrentTime);
        Assert.Contains(_accessibility.Announcements, a => a.message.StartsWith("Resumed at 2 minutes"));
    }

    [Fact]
    public async Task ResumeSetting_Off_StartsFromTheBeginning()
    {
        var video = Video("jNQXAC9IVRw", "Me at the zoo");
        _library.RecordProgress(video, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(10), flush: true);
        await using var vm = CreateViewModel();
        vm.ResumePlayback = false;

        await vm.PlaySearchResultAsync(video);

        Assert.Equal(TimeSpan.Zero, _playerEngine.CurrentTime);
    }

    [Fact]
    public async Task WatchLater_ToggleThenDeleteFromSection()
    {
        await using var vm = CreateViewModel();
        var video = Video("jNQXAC9IVRw", "Me at the zoo");

        vm.ToggleWatchLaterForItem(video);
        await vm.ShowSectionByNumberAsync(6);

        Assert.Equal(BrowseSection.WatchLater, vm.CurrentSection);
        Assert.Single(vm.SearchResults);
        Assert.Contains(_accessibility.Announcements, a => a.message == "1 video");

        vm.RemoveSelectedItem(null);

        Assert.Empty(vm.SearchResults);
        Assert.Empty(_library.WatchLater);
    }

    [Fact]
    public async Task Featured_AsksForTheChosenCategory_AndLoadsMorePages()
    {
        await using var vm = CreateViewModel();
        _discovery.Featured.AddRange(Enumerable.Range(0, 12).Select(i => Video($"aaaaaaaa{i:000}", $"Song {i}")));
        _discovery.FeaturedPages.Add(Enumerable.Range(0, 12).Select(i => Video($"bbbbbbbb{i:000}", $"More {i}")).ToList());

        await vm.ShowSectionByNumberAsync(1);
        vm.SelectedFeaturedCategory = FeaturedCategory.Music;
        await vm.RefreshSectionAsync();

        Assert.Contains(_discovery.FeaturedRequests, r => r.Category == FeaturedCategory.Music && r.Region == "US" && r.Token is null);
        Assert.Equal("Featured: top music videos", vm.SectionTitle);
        Assert.True(vm.CanLoadMore);

        await vm.LoadMoreAsync();

        Assert.Equal(24, vm.SearchResults.Count);
        Assert.Contains(_discovery.FeaturedRequests, r => r.Category == FeaturedCategory.Music && r.Token == "1");
        Assert.False(vm.CanLoadMore);
    }

    [Fact]
    public async Task SponsorBlock_SkipsSponsorAnnouncesSelfPromoAndShowsVotes()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        _sponsorBlock.Segments["jNQXAC9IVRw"] = new()
        {
            new(30, 60, TimePlayer.Core.Community.SegmentCategory.Sponsor, TimePlayer.Core.Community.SegmentKind.Skip, "a"),
            new(90, 100, TimePlayer.Core.Community.SegmentCategory.SelfPromotion, TimePlayer.Core.Community.SegmentKind.Skip, "b"),
            new(150, 150, TimePlayer.Core.Community.SegmentCategory.Highlight, TimePlayer.Core.Community.SegmentKind.Highlight, "h")
        };
        _dislikes.Votes["jNQXAC9IVRw"] = new TimePlayer.Core.Community.VideoVotes(1200, 34);

        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));
        Assert.StartsWith("1,200 likes, 34 dislikes", vm.VideoDescriptionText.Replace(' ', ','));

        await _playerEngine.SeekAsync(TimeSpan.FromSeconds(35));
        Assert.Equal(TimeSpan.FromSeconds(60), _playerEngine.CurrentTime);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Skipped sponsor, 30 seconds.");

        await _playerEngine.SeekAsync(TimeSpan.FromSeconds(91));
        Assert.Equal(TimeSpan.FromSeconds(91), _playerEngine.CurrentTime);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Self-promotion, 10 seconds. S skips it.");

        await vm.SkipCurrentSegmentAsync();
        Assert.Equal(TimeSpan.FromSeconds(100), _playerEngine.CurrentTime);

        await vm.JumpToHighlightAsync();
        Assert.Equal(TimeSpan.FromSeconds(150), _playerEngine.CurrentTime);
    }

    [Fact]
    public async Task ShuffleALongPlaylist_ChangesTheQueueOnceAndStartsPlaying()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        var playlist = _library.CreatePlaylist("Long", Enumerable.Range(0, 300).Select(i => Video($"vid{i:00000000}", $"Song {i}")));
        int queueChanges = 0;
        _coordinator.QueueChanged += (_, _) => queueChanges++;

        await vm.ShowSectionByNumberAsync(5);
        vm.SelectedSearchResult = vm.SearchResults.Single(r => r.LibraryPlaylistId == playlist.Id);
        await vm.ShufflePlayAsync();

        Assert.Equal(300, _coordinator.Queue.Count);
        Assert.True(queueChanges <= 2, $"the queue changed {queueChanges} times"); // replace, then the playing position
        Assert.True(vm.IsPlayerDialogOpen);
        Assert.NotEmpty(_playerEngine.LoadedUrls);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Shuffling 300 videos.");
    }

    [Fact]
    public async Task PlayingSomethingNew_StartsAFreshQueue_ButAutoplayContinuesIt()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        var playlist = _library.CreatePlaylist("Two", new[] { Video("jNQXAC9IVRw", "One"), Video("dQw4w9WgXcQ", "Two") });
        await vm.ShowSectionByNumberAsync(5);
        vm.SelectedSearchResult = vm.SearchResults.Single(r => r.LibraryPlaylistId == playlist.Id);
        await vm.PlayAllAsync();
        Assert.Equal(2, _coordinator.Queue.Count);

        // Picking a video that's already queued plays it from its place.
        await vm.PlaySearchResultAsync(Video("dQw4w9WgXcQ", "Two"));
        Assert.Equal(2, _coordinator.Queue.Count);

        // Something new replaces the queue.
        await vm.PlaySearchResultAsync(Video("kJQP7kiw5Fk", "New pick"));
        Assert.Equal(new[] { "kJQP7kiw5Fk" }, _coordinator.Queue.Select(q => q.VideoId.Value));
    }

    [Fact]
    public async Task AProblemWithoutAReason_StillSaysSomethingUseful()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        _playerEngine.RaiseError("");

        Assert.StartsWith("Playback problem: No reason was given.", vm.ErrorMessage);
        Assert.Contains(_accessibility.Announcements, a => a.message.EndsWith("F6 reaches Retry and Dismiss."));

        vm.ClearError();
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Sound_PresetMusicModeAndCustom_ReachThePlayer()
    {
        await using var vm = CreateViewModel();
        Assert.Equal(string.Empty, _playerEngine.AudioFilters);

        vm.VideoSoundPreset = "Bass boost";
        await Task.Delay(500);
        Assert.Contains("bass=g=6", _playerEngine.AudioFilters);
        Assert.Equal("Bass boost", _configService.CurrentConfig.Audio.Preset);

        vm.ToggleMusicMode();
        await Task.Delay(500);
        Assert.False(_playerEngine.VideoEnabled);
        Assert.Contains("extrastereo", _playerEngine.AudioFilters); // the Music preset
        Assert.Contains(_accessibility.Announcements, a => a.message == "Music mode on: no video, Music sound.");

        // Editing the custom sound makes it what you hear in the current mode.
        vm.CustomCrossfeed = true;
        await Task.Delay(500);
        Assert.Equal("Custom", vm.MusicSoundPreset);
        Assert.Equal("Bass boost", vm.VideoSoundPreset);
        Assert.Equal("@timeplayer:lavfi=[bs2b=profile=default]", _playerEngine.AudioFilters);

        vm.ToggleMusicMode();
        await Task.Delay(500);
        Assert.True(_playerEngine.VideoEnabled);
        Assert.Contains("bass=g=6", _playerEngine.AudioFilters);
    }

    [Fact]
    public async Task Sound_CyclePreset_SpeaksTheNewPreset()
    {
        await using var vm = CreateViewModel();
        vm.CycleSoundPreset(1);
        Assert.Equal("Music", vm.VideoSoundPreset);
        vm.CycleSoundPreset(-1);
        vm.CycleSoundPreset(-1);
        Assert.Equal("Custom", vm.VideoSoundPreset);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Custom sound.");
    }

    [Fact]
    public async Task SponsorBlock_OffInSettings_DoesNotSkip()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        _sponsorBlock.Segments["jNQXAC9IVRw"] = new()
        {
            new(30, 60, TimePlayer.Core.Community.SegmentCategory.Sponsor, TimePlayer.Core.Community.SegmentKind.Skip, "a")
        };
        vm.SponsorBlockEnabled = false;

        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));
        await _playerEngine.SeekAsync(TimeSpan.FromSeconds(35));

        Assert.Equal(TimeSpan.FromSeconds(35), _playerEngine.CurrentTime);
        Assert.False(_configService.CurrentConfig.SponsorBlock.Enabled);
    }

    [Fact]
    public async Task SettingsSteps_ChangeHowFarArrowsSeek()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));
        vm.SmallSeekSeconds = 10;

        await vm.SeekSmallForwardAsync();

        Assert.Equal(10, _configService.CurrentConfig.Playback.SmallSeekSeconds);
        Assert.Equal(TimeSpan.FromSeconds(10), _playerEngine.CurrentTime);
        Assert.Contains(vm.RegionChoices, r => r.Value == "GB" && r.Label == "United Kingdom (GB)");
    }

    [Fact]
    public async Task PlaylistLink_OpensPlaylist_SaveAndPlayAll()
    {
        await using var vm = CreateViewModel();
        _playlists.Playlists["PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI"] = new YouTubePlaylistPage(
            "PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI", "Road songs", "Music",
            new[] { Video("jNQXAC9IVRw", "One"), Video("dQw4w9WgXcQ", "Two") }, null);

        vm.SearchQuery = "https://www.youtube.com/playlist?list=PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI";
        await vm.SearchAsync();

        Assert.Equal(BrowseSection.Playlist, vm.CurrentSection);
        Assert.Equal("Road songs", vm.SectionTitle);
        Assert.Equal(2, vm.SearchResults.Count);
        Assert.True(vm.CanSaveOpenPlaylist);
        Assert.Contains(_accessibility.Announcements, a => a.message == "2 videos, by Music");

        await vm.SaveOpenPlaylistAsync();
        Assert.False(vm.CanSaveOpenPlaylist);
        Assert.True(vm.IsOpenPlaylistInLibrary);
        Assert.Equal(2, _library.FindSavedYouTubePlaylist("PLFgquLnL59alCl_2TQvOiD5Vgm1hCaGSI")!.Videos.Count);

        await vm.PlayAllAsync();
        Assert.Equal(new[] { "jNQXAC9IVRw", "dQw4w9WgXcQ" }, vm.PlaybackQueue.Select(q => q.VideoId.Value));
        Assert.True(vm.IsPlayerDialogOpen);
    }

    [Fact]
    public async Task Likes_ShowInPlaylists_AndBackReturns()
    {
        await using var vm = CreateViewModel();
        await vm.ShowSectionByNumberAsync(1);
        vm.ToggleLikeForItem(Video("jNQXAC9IVRw", "Liked one"));

        await vm.ShowSectionByNumberAsync(5);
        Assert.Equal("Playlist: Liked videos, 1 video", vm.SearchResults[0].AccessibleSummary);

        await vm.PlaySearchResultAsync(vm.SearchResults[0]);
        Assert.Equal(BrowseSection.Playlist, vm.CurrentSection);
        Assert.Equal("Liked one", vm.SearchResults.Single().Title);

        // Delete in Liked videos removes the like.
        vm.RemoveSelectedItem(vm.SearchResults[0]);
        Assert.False(_library.IsLiked("jNQXAC9IVRw"));

        await vm.GoBackAsync();
        Assert.Equal(BrowseSection.Playlists, vm.CurrentSection);
    }

    [Fact]
    public async Task OwnPlaylist_AddMoveAndRemove()
    {
        await using var vm = CreateViewModel();
        var playlist = _library.CreatePlaylist("Mine");
        vm.AddToPlaylist(Video("jNQXAC9IVRw", "First"), playlist.Id);
        vm.AddToPlaylist(Video("dQw4w9WgXcQ", "Second"), playlist.Id);
        vm.AddToPlaylist(Video("dQw4w9WgXcQ", "Second"), playlist.Id);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Already in Mine.");

        await vm.ShowSectionByNumberAsync(5);
        await vm.PlaySearchResultAsync(vm.SearchResults.Single(r => r.LibraryPlaylistId == playlist.Id));
        Assert.True(vm.IsOpenPlaylistEditable);

        vm.SelectedSearchResult = vm.SearchResults[1];
        vm.MoveSelectedInPlaylist(-1);
        Assert.Equal("Second", vm.SearchResults[0].Title);
        Assert.Equal("dQw4w9WgXcQ", _library.GetPlaylist(playlist.Id)!.Videos[0].VideoId);

        vm.RemoveSelectedItem(vm.SearchResults[0]);
        Assert.Equal("jNQXAC9IVRw", Assert.Single(_library.GetPlaylist(playlist.Id)!.Videos).VideoId);
    }

    [Fact]
    public async Task ForYou_ShowsRecommendations_AndDeleteMeansNotInterested()
    {
        await using var vm = CreateViewModel();
        _recommendations.Videos.Add(Video("jNQXAC9IVRw", "Pick one"));
        _recommendations.Videos.Add(Video("dQw4w9WgXcQ", "Pick two"));

        await vm.ShowSectionByNumberAsync(2);
        Assert.Equal(BrowseSection.ForYou, vm.CurrentSection);
        Assert.Equal(2, vm.SearchResults.Count);

        vm.RemoveSelectedItem(vm.SearchResults[0]);

        Assert.Single(vm.SearchResults);
        Assert.True(_library.IsHidden(Video("jNQXAC9IVRw", "Pick one")));
        Assert.Single(_recommendations.Videos);
    }

    [Fact]
    public async Task SearchingForPlaylists_ListsPlaylistRows()
    {
        await using var vm = CreateViewModel();
        _playlists.SearchResults.Add(new SearchResult
        {
            Kind = SearchResultKind.Playlist, Id = "PLXIclLvfETS3AgCnZg4N6QqHu_T27XKIq", Title = "Lofi", ChannelTitle = "Mimi", PlaylistVideoCount = 76
        });
        vm.SelectedSearchKind = SearchKind.Playlists;
        vm.SearchQuery = "lofi";

        await vm.SearchAsync();

        Assert.Equal("Playlist: Lofi, 76 videos, by Mimi", vm.SearchResults.Single().AccessibleSummary);
        Assert.Equal("1 playlist", vm.SectionCountText);
    }

    [Fact]
    public async Task ChannelRow_EnterOpensChannelUploads_AndBackRestoresSection()
    {
        await using var vm = CreateViewModel();
        _library.ToggleSubscription("UCBR8-60-B28hp2BmDPdntcQ", "YouTube");
        _discovery.Channels["UCBR8-60-B28hp2BmDPdntcQ"] = new ChannelUploads("UCBR8-60-B28hp2BmDPdntcQ", "YouTube",
            new[] { Video("jNQXAC9IVRw", "Upload one"), Video("dQw4w9WgXcQ", "Upload two") });

        await vm.ShowSectionByNumberAsync(4);
        Assert.Equal("Channel: YouTube", vm.SearchResults.Single().AccessibleSummary);

        await vm.PlaySearchResultAsync(vm.SearchResults[0]);

        Assert.False(vm.IsPlayerDialogOpen);
        Assert.Equal(BrowseSection.Channel, vm.CurrentSection);
        Assert.Equal(2, vm.SearchResults.Count);
        Assert.Contains(_accessibility.Announcements, a => a.message == "2 videos, subscribed");

        await vm.GoBackAsync();
        Assert.Equal(BrowseSection.Channels, vm.CurrentSection);
    }

    [Fact]
    public async Task WhatsNew_MarksUploadsSinceLastVisitAsNew()
    {
        await using var vm = CreateViewModel();
        var lastVisit = DateTimeOffset.UtcNow.AddDays(-1);
        _library.ToggleSubscription("UCBR8-60-B28hp2BmDPdntcQ", "YouTube");
        _library.MarkFeedViewed(lastVisit);
        _discovery.Channels["UCBR8-60-B28hp2BmDPdntcQ"] = new ChannelUploads("UCBR8-60-B28hp2BmDPdntcQ", "YouTube", new[]
        {
            Video("jNQXAC9IVRw", "Fresh", published: lastVisit.AddHours(5)),
            Video("dQw4w9WgXcQ", "Old", published: lastVisit.AddDays(-3))
        });

        await vm.ShowSectionByNumberAsync(3);

        Assert.Equal("New", vm.SearchResults[0].StatusPrefix);
        Assert.Null(vm.SearchResults[1].StatusPrefix);
        Assert.Contains(_accessibility.Announcements, a => a.message == "2 videos, 1 new");
        Assert.True(_library.FeedLastViewedUtc > lastVisit);
    }

    [Fact]
    public async Task WhatsNew_WithoutSubscriptions_ExplainsHowToStart()
    {
        await using var vm = CreateViewModel();

        await vm.ShowSectionByNumberAsync(3);

        Assert.Empty(vm.SearchResults);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("no subscriptions yet") && a.message.Contains("Control+S"));
        Assert.Equal(0, _discovery.FeedRequests);
    }

    [Fact]
    public async Task SubscribeFromSearchResult_UsesItsChannel()
    {
        await using var vm = CreateViewModel();

        vm.ToggleSubscriptionForItem(Video("jNQXAC9IVRw", "Me at the zoo", channelId: "UC4QobU6STFB0P71PMvOGN5A"));

        Assert.True(_library.IsSubscribed("UC4QobU6STFB0P71PMvOGN5A"));
        Assert.Contains(_accessibility.Announcements, a => a.message == "Subscribed to YouTube.");
    }

    // --- Up next, autoplay, comments ---

    private static Comment MakeComment(string id, string text, int replies = 0) => new()
    {
        Id = id,
        Author = "@someone",
        Text = text,
        ReplyCount = replies,
        RepliesToken = replies > 0 ? "replies-" + id : null
    };

    [Fact]
    public async Task PlayingAVideo_LoadsUpNextQuietly()
    {
        _watchPage.Related["jNQXAC9IVRw"] = new() { Video("dQw4w9WgXcQ", "Related one"), Video("9bZkp7q19f0", "Related two") };
        await using var vm = CreateViewModel();

        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));

        Assert.Equal(new[] { "Related one", "Related two" }, vm.UpNext.Select(v => v.Title));
        Assert.Equal("A description", vm.VideoDescription);
        Assert.DoesNotContain(_accessibility.Announcements, a => a.message.Contains("Related", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WhenTheQueueEnds_AutoplayPlaysFirstUnwatchedUpNext()
    {
        _watchPage.Related["jNQXAC9IVRw"] = new() { Video("dQw4w9WgXcQ", "Already seen"), Video("9bZkp7q19f0", "Fresh one") };
        _library.RecordProgress(Video("dQw4w9WgXcQ", "Already seen"), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), flush: true);
        await using var vm = CreateViewModel();
        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));

        _playerEngine.RaiseState(PlaybackState.Ended);

        Assert.Contains(_accessibility.Announcements, a => a.message == "Up next: Fresh one");
        Assert.Contains(_playerEngine.LoadedUrls, u => u.Contains("9bZkp7q19f0"));
    }

    [Fact]
    public async Task WhenTheQueueEnds_WithAutoplayOff_SaysVideoEnded()
    {
        _watchPage.Related["jNQXAC9IVRw"] = new() { Video("9bZkp7q19f0", "Fresh one") };
        await using var vm = CreateViewModel();
        vm.Autoplay = false;
        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));
        int loads = _playerEngine.LoadedUrls.Count;

        _playerEngine.RaiseState(PlaybackState.Ended);

        Assert.Contains(_accessibility.Announcements, a => a.message == "Video ended.");
        Assert.Equal(loads, _playerEngine.LoadedUrls.Count);
    }

    [Fact]
    public async Task Comments_LoadWhenTheTabOpens_PageAtTheEnd_AndShowReplies()
    {
        _watchPage.CommentPages.Add(Enumerable.Range(1, 20).Select(i => MakeComment($"c{i}", $"Comment {i}", replies: i == 1 ? 2 : 0)).ToList());
        _watchPage.CommentPages.Add(new() { MakeComment("c21", "Comment 21") });
        _watchPage.Replies["c1"] = new() { MakeComment("r1", "Reply one"), MakeComment("r2", "Reply two") };
        await using var vm = CreateViewModel();
        await vm.PlaySearchResultAsync(Video("jNQXAC9IVRw", "Me at the zoo"));
        Assert.Equal(0, _watchPage.CommentRequests); // nothing fetched until asked

        vm.SelectedPlayerPanel = PlayerPanel.Comments;
        await Task.Yield();

        Assert.Equal(20, vm.Comments.Count);
        Assert.Contains(_accessibility.Announcements, a => a.message == "20 comments loaded.");

        vm.SelectedComment = vm.Comments[^1];   // reaching the end loads the next page
        await Task.Yield();
        Assert.Equal(21, vm.Comments.Count);

        // A comment with replies starts collapsed, with a placeholder so it can be expanded.
        var first = vm.Comments[0];
        Assert.False(first.IsExpanded);
        Assert.Equal(CommentNodeKind.Loading, Assert.Single(first.Replies).Kind);
        Assert.Empty(vm.Comments[1].Replies);

        // Right arrow in the tree sets IsExpanded: the replies load one level deeper.
        first.IsExpanded = true;
        await Task.Yield();
        Assert.Equal(new[] { "Reply one", "Reply two" }, first.Replies.Select(r => r.Text));
        Assert.All(first.Replies, r => Assert.Equal(2, r.Level));
        Assert.StartsWith("Reply from @someone: Reply one", first.Replies[0].AccessibleName);
        Assert.Contains(_accessibility.Announcements, a => a.message == "2 replies");

        // Enter toggles; the top level is untouched.
        await vm.ToggleRepliesAsync(first);
        Assert.False(first.IsExpanded);
        Assert.Equal("Comment 2", vm.Comments[1].Text);
    }

    [Fact]
    public async Task ShareLinks_UseYoutuBeAndTheCurrentTimeInThePlayer()
    {
        Assert.Equal("https://youtu.be/jNQXAC9IVRw", MainViewModel.ShareLink(Video("jNQXAC9IVRw", "x")));
        Assert.Equal("https://youtu.be/jNQXAC9IVRw?t=65", MainViewModel.ShareLink(Video("jNQXAC9IVRw", "x"), TimeSpan.FromSeconds(65.7)));
        Assert.Equal("https://www.youtube.com/channel/UCBR8-60-B28hp2BmDPdntcQ",
            MainViewModel.ShareLink(new SearchResult { Kind = SearchResultKind.Channel, Id = "UCBR8-60-B28hp2BmDPdntcQ", Title = "YouTube", ChannelTitle = "YouTube" }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Download_AnnouncesStartHalfwayAndDone()
    {
        await using var vm = CreateViewModel();

        await vm.DownloadAsync(Video("jNQXAC9IVRw", "Me at the zoo"), audioOnly: true);

        Assert.Equal(("jNQXAC9IVRw", true), Assert.Single(_downloads.Requests));
        Assert.Contains(_accessibility.Announcements, a => a.message == "Downloading audio, MP3: Me at the zoo");
        Assert.Contains(_accessibility.Announcements, a => a.message == "Downloaded: Me at the zoo. Saved in Downloads, TimePlayer.");
        Assert.False(vm.HasActiveDownloads);
    }

    [Fact]
    public async Task ChannelPage_LoadsOlderUploadsAtTheEnd()
    {
        await using var vm = CreateViewModel();
        _discovery.Channels["UCBR8-60-B28hp2BmDPdntcQ"] = new ChannelUploads("UCBR8-60-B28hp2BmDPdntcQ", "YouTube",
            new[] { Video("jNQXAC9IVRw", "Newest") });
        _discovery.OlderUploads.AddRange(new[] { Video("jNQXAC9IVRw", "Newest"), Video("dQw4w9WgXcQ", "Older") });

        await vm.OpenChannelAsync(Video("jNQXAC9IVRw", "Newest"));
        await Task.Yield();

        // A one-item first page is topped up straight away, without repeating "Newest".
        Assert.Equal(new[] { "Newest", "Older" }, vm.SearchResults.Select(v => v.Title));
        Assert.False(vm.CanLoadMore);
    }

    [Fact]
    public async Task SeekForward_AnnouncesTheNewPosition()
    {
        await using var vm = CreateViewModel();
        await vm.InitializePlayerEngineAsync((nint)1);
        vm.Duration = TimeSpan.FromMinutes(10);
        vm.CurrentTime = TimeSpan.FromSeconds(60);

        await vm.SeekSmallForwardAsync();

        Assert.Equal(TimeSpan.FromSeconds(65), vm.CurrentTime);
        Assert.Equal("1 minute, 5 seconds", _accessibility.Announcements[^1].message);
    }

    [Fact]
    public async Task ResultsList_BecomesVisibleWhenItemsArrive()
    {
        await using var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.IsResultsListVisible);

        vm.ToggleWatchLaterForItem(Video("jNQXAC9IVRw", "Me at the zoo"));
        await vm.ShowSectionByNumberAsync(6);

        Assert.True(vm.IsResultsListVisible);
        Assert.Contains(nameof(MainViewModel.IsResultsListVisible), raised);
    }

    [Fact]
    public void EventsOnTheUiThread_RunInline_UnderARealWpfDispatcher()
    {
        // Regression: comparing SynchronizationContext instances made every handler think it was
        // off the UI thread (WPF uses one context instance per priority), so they re-posted
        // themselves forever and the window froze after the first search.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                var vm = CreateViewModel();
                var frame = new System.Windows.Threading.DispatcherFrame();

                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () =>
                {
                    _playerEngine.RaiseSubtitleText("caption");
                    // Handled inline, not queued again.
                    Assert.Equal("caption", vm.CurrentSubtitleText);
                });
                // Lower priority: only runs if the Send-priority work above did not loop.
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The dispatcher never got to lower-priority work.");
        if (failure is not null) throw failure;
    }
}
