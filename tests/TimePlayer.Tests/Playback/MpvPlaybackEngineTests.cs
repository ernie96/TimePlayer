using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Core.Playback;
using TimePlayer.Core.Playback.Mpv;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Playback;

public class MpvPlaybackEngineTests
{
    private readonly FakeMpvManager _mpvManager = new();
    private readonly FakeMpvIpcClient _ipcClient = new();
    private readonly ConfigurationService _configService;
    private readonly FakeMpvProcess _fakeProcess = new();

    public MpvPlaybackEngineTests()
    {
        _configService = new ConfigurationService(Options.Create(new AppConfig()));
    }

    private MpvPlaybackEngine CreateEngine()
    {
        var engine = new MpvPlaybackEngine(
            _mpvManager,
            _ipcClient,
            _configService,
            NullLogger<MpvPlaybackEngine>.Instance)
        {
            ProcessFactory = _ => _fakeProcess
        };

        return engine;
    }

    [Fact]
    public async Task InitializeAsync_RegistersObserversAndStartsProcess()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345, new PlayerOptions { InitialVolume = 75, InitialSpeed = 1.25 });

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.Equal(75, engine.Volume);
        Assert.Equal(1.25, engine.Speed);
        Assert.True(engine.IsEngineAlive);

        // Check observed properties
        Assert.Contains(_ipcClient.SentCommands, c => c.Count >= 3 && Equals(c[0], "observe_property") && Equals(c[2], "pause"));
        Assert.Contains(_ipcClient.SentCommands, c => c.Count >= 3 && Equals(c[0], "observe_property") && Equals(c[2], "time-pos"));
        Assert.Contains(_ipcClient.SentCommands, c => c.Count >= 3 && Equals(c[0], "observe_property") && Equals(c[2], "duration"));
    }

    [Fact]
    public async Task LoadAsync_SendsLoadFileAndAudioTrack()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.LoadAsync("https://video.stream.url", "https://audio.stream.url");

        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "loadfile") && Equals(c[1], "https://video.stream.url") && Equals(c[2], "replace"));

        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "audio-add") && Equals(c[1], "https://audio.stream.url") && Equals(c[2], "select"));

        Assert.Equal(PlaybackState.Opening, engine.State);
    }

    [Fact]
    public async Task PlayAsync_And_PauseAsync_UpdatePauseProperty()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.PlayAsync();
        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "set_property") && Equals(c[1], "pause") && Equals(c[2], false));

        await engine.PauseAsync();
        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "set_property") && Equals(c[1], "pause") && Equals(c[2], true));
    }

    [Fact]
    public async Task SeekAsync_SendsAbsoluteSeekCommand()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.SeekAsync(TimeSpan.FromSeconds(45));

        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "seek") && Equals(c[1], 45.0) && Equals(c[2], "absolute"));
    }

    [Fact]
    public async Task SeekRelativeAsync_SendsRelativeSeekCommand()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.SeekRelativeAsync(-10.0);

        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "seek") && Equals(c[1], -10.0) && Equals(c[2], "relative"));
    }

    [Fact]
    public async Task SetVolumeAsync_ClampsBetween0And100()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.SetVolumeAsync(150);
        Assert.Equal(100.0, engine.Volume);

        await engine.SetVolumeAsync(-10);
        Assert.Equal(0.0, engine.Volume);
    }

    [Fact]
    public async Task SetSpeedAsync_ClampsBetween025And4()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.SetSpeedAsync(5.0);
        Assert.Equal(4.0, engine.Speed);

        await engine.SetSpeedAsync(0.1);
        Assert.Equal(0.25, engine.Speed);
    }

    [Fact]
    public async Task Subtitles_LoadAndSetTrack_SendsCorrectCommands()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        await engine.LoadSubtitleTrackAsync("https://subtitles.vtt", selectImmediately: true);
        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "sub-add") && Equals(c[1], "https://subtitles.vtt") && Equals(c[2], "select"));

        await engine.SetSubtitleTrackAsync("1");
        Assert.Contains(_ipcClient.SentCommands, c =>
            c.Count >= 3 && Equals(c[0], "set_property") && Equals(c[1], "sub") && Equals(c[2], "1"));
        Assert.Equal("1", engine.CurrentSubtitleTrack);
    }

    [Fact]
    public async Task Events_UpdatePlaybackStateAccurately()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        var stateChanges = new List<PlaybackState>();
        engine.StateChanged += (s, e) => stateChanges.Add(e.NewState);

        _ipcClient.RaiseEvent("start-file");
        Assert.Equal(PlaybackState.Buffering, engine.State);

        _ipcClient.RaiseEvent("file-loaded");
        Assert.Equal(PlaybackState.Playing, engine.State);

        using var jsonDocTrue = JsonDocument.Parse("true");
        _ipcClient.RaisePropertyChange(1, "pause", jsonDocTrue.RootElement);
        Assert.Equal(PlaybackState.Paused, engine.State);

        using var jsonDocFalse = JsonDocument.Parse("false");
        _ipcClient.RaisePropertyChange(1, "pause", jsonDocFalse.RootElement);
        Assert.Equal(PlaybackState.Playing, engine.State);

        _ipcClient.RaiseEvent("end-file", reason: "eof");
        Assert.Equal(PlaybackState.Ended, engine.State);
    }

    [Fact]
    public async Task PropertyChange_TimePosAndDuration_FiresPositionChanged()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        TimeSpan receivedPos = TimeSpan.Zero;
        TimeSpan receivedDur = TimeSpan.Zero;
        engine.PositionChanged += (s, e) =>
        {
            receivedPos = e.Position;
            receivedDur = e.Duration;
        };

        using var jsonDur = JsonDocument.Parse("180.5");
        _ipcClient.RaisePropertyChange(3, "duration", jsonDur.RootElement);
        Assert.Equal(TimeSpan.FromSeconds(180.5), engine.Duration);

        using var jsonPos = JsonDocument.Parse("45.25");
        _ipcClient.RaisePropertyChange(2, "time-pos", jsonPos.RootElement);
        Assert.Equal(TimeSpan.FromSeconds(45.25), engine.CurrentTime);
        Assert.Equal(TimeSpan.FromSeconds(45.25), receivedPos);
        Assert.Equal(TimeSpan.FromSeconds(180.5), receivedDur);
    }

    [Fact]
    public async Task ProcessExit_WhenUnexpected_TransitionsToErrorAndFiresErrorEvent()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        _ipcClient.RaiseEvent("file-loaded");
        Assert.Equal(PlaybackState.Playing, engine.State);

        string? errorMessage = null;
        engine.ErrorOccurred += (s, e) => errorMessage = e.Message;

        // Simulate crash
        _fakeProcess.TriggerExit(-1);

        Assert.Equal(PlaybackState.Error, engine.State);
        Assert.NotNull(errorMessage);
        Assert.Contains("terminated unexpectedly", errorMessage);
    }

    [Fact]
    public async Task RestartAsync_RecoversEngineAfterFailure()
    {
        var engine = CreateEngine();
        await engine.InitializeAsync((nint)12345);

        _fakeProcess.TriggerExit(-1);
        Assert.Equal(PlaybackState.Error, engine.State);

        var newFakeProcess = new FakeMpvProcess();
        engine.ProcessFactory = _ => newFakeProcess;

        await engine.RestartAsync();

        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.True(engine.IsEngineAlive);
    }
}
