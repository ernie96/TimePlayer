using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.Invidious;
using TimePlayer.Core.Backends.Piped;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
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

    private readonly PipedBackendProvider _piped;
    private readonly InvidiousBackendProvider _invidious;
    private readonly YtDlpBackendProvider _ytdlp;
    private readonly PlaybackCoordinator _coordinator;

    public MainViewModelTests()
    {
        _configService = new ConfigurationService(Options.Create(new AppConfig()));
        var httpHandler = MockHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(httpHandler);

        _piped = new PipedBackendProvider(httpClient, _healthService, _configService, NullLogger<PipedBackendProvider>.Instance);
        _invidious = new InvidiousBackendProvider(httpClient, _healthService, _configService, NullLogger<InvidiousBackendProvider>.Instance);
        _ytdlp = new YtDlpBackendProvider(_ytDlpManager, _processRunner, _configService, NullLogger<YtDlpBackendProvider>.Instance);

        _coordinator = new PlaybackCoordinator(
            _piped,
            _invidious,
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
        vm.ClosePlayerDialog();

        Assert.False(vm.IsPlayerDialogOpen);
        Assert.True(resultsFocusRequested);
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Closed video player"));
    }

    [Fact]
    public async Task SubtitleTextChanged_UpdatesCurrentSubtitleTextAndAnnouncesToScreenReader()
    {
        await using var vm = CreateViewModel();

        _playerEngine.RaiseSubtitleText("Welcome to the video screen reader caption test");

        Assert.Equal("Welcome to the video screen reader caption test", vm.CurrentSubtitleText);
        Assert.Contains(_accessibility.Announcements, a => a.message == "Welcome to the video screen reader caption test");
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
        Assert.Contains(_accessibility.Announcements, a => a.message.Contains("Added 'Song 1' to playback queue"));

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
        Assert.Contains("Piped Backend Status", vm.DiagnosticsReport);
        Assert.Contains("Invidious Backend Status", vm.DiagnosticsReport);
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
}
