using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Playback.Mpv;

/// <summary>
/// Production-grade implementation of IPlayerEngine wrapping mpv embedded inside an HWND
/// and controlled asynchronously over a strictly local Windows Named Pipe via JSON IPC.
/// </summary>
public sealed class MpvPlaybackEngine : IPlayerEngine
{
    private const int PropIdPause = 1;
    private const int PropIdTimePos = 2;
    private const int PropIdDuration = 3;
    private const int PropIdVolume = 4;
    private const int PropIdMute = 5;
    private const int PropIdSpeed = 6;
    private const int PropIdCoreIdle = 7;
    private const int PropIdEofReached = 8;
    private const int PropIdSub = 9;
    private const int PropIdSubText = 10;

    private readonly IMpvManager _mpvManager;
    private readonly IMpvIpcClient _ipcClient;
    private readonly IConfigurationService _configService;
    private readonly ILogger<MpvPlaybackEngine> _logger;

    internal Func<ProcessStartInfo, IMpvProcess> ProcessFactory { get; set; } = psi => new DefaultMpvProcess(psi);

    private IMpvProcess? _process;
    private nint _windowHandle;
    private PlayerOptions? _currentOptions;
    private bool _isDisposed;
    private bool _isIntentionalStop;

    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan CurrentTime { get; private set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;
    public double Volume { get; private set; } = 80.0;
    public double Speed { get; private set; } = 1.0;
    public bool IsMuted { get; private set; }
    public string? CurrentSubtitleTrack { get; private set; }
    public string? CurrentSubtitleText { get; private set; }

    public bool IsEngineAlive => _process != null && !_process.HasExited && _ipcClient.IsConnected;

    public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;
    public event EventHandler<string>? SubtitleTextChanged;

    public MpvPlaybackEngine(
        IMpvManager mpvManager,
        IMpvIpcClient ipcClient,
        IConfigurationService configService,
        ILogger<MpvPlaybackEngine> logger)
    {
        _mpvManager = mpvManager ?? throw new ArgumentNullException(nameof(mpvManager));
        _ipcClient = ipcClient ?? throw new ArgumentNullException(nameof(ipcClient));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ipcClient.EventReceived += OnMpvEventReceived;
        _ipcClient.PropertyChanged += OnMpvPropertyChanged;
        _ipcClient.Disconnected += OnMpvDisconnected;
    }

    public async Task InitializeAsync(nint windowHandle, PlayerOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("Window handle cannot be zero or null.", nameof(windowHandle));

        if (IsEngineAlive)
            return;

        _windowHandle = windowHandle;
        _currentOptions = options ?? new PlayerOptions();

        await StartMpvProcessAndConnectAsync(cancellationToken);
    }

    private async Task StartMpvProcessAndConnectAsync(CancellationToken cancellationToken)
    {
        var ensureResult = await _mpvManager.EnsureInstalledAsync(cancellationToken);
        if (!ensureResult.IsSuccess)
        {
            throw new InvalidOperationException($"Cannot initialize mpv engine: {ensureResult.Error?.Message}");
        }

        var executablePath = _mpvManager.ExecutablePath;
        var pipeName = _currentOptions?.IpcPipeName ?? $"timeplayer-mpv-{Guid.NewGuid():N}";
        var pipePath = $@"\\.\pipe\{pipeName}";

        _logger.LogInformation("Launching mpv binary {Exe} embedded into HWND {Hwnd} with IPC pipe {Pipe}...",
            executablePath, _windowHandle, pipeName);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Standard embedded player flags
        startInfo.ArgumentList.Add($"--wid={_windowHandle.ToInt64()}");
        startInfo.ArgumentList.Add($"--input-ipc-server={pipePath}");
        startInfo.ArgumentList.Add("--idle=yes");
        startInfo.ArgumentList.Add("--force-window=yes");
        startInfo.ArgumentList.Add("--keep-open=yes");
        startInfo.ArgumentList.Add("--osc=no");
        startInfo.ArgumentList.Add("--osd-level=0");
        startInfo.ArgumentList.Add("--input-default-bindings=no");
        startInfo.ArgumentList.Add("--input-vo-keyboard=no");
        startInfo.ArgumentList.Add("--input-cursor=no");
        startInfo.ArgumentList.Add("--gpu-context=d3d11");

        if (_currentOptions?.HardwareDecoding ?? true)
        {
            startInfo.ArgumentList.Add("--hwdec=auto-safe");
        }
        else
        {
            startInfo.ArgumentList.Add("--hwdec=no");
        }

        if (_currentOptions != null)
        {
            startInfo.ArgumentList.Add($"--volume={_currentOptions.InitialVolume}");
            startInfo.ArgumentList.Add($"--speed={_currentOptions.InitialSpeed}");
            Volume = _currentOptions.InitialVolume;
            Speed = _currentOptions.InitialSpeed;

            if (!string.IsNullOrWhiteSpace(_currentOptions.YtDlpExecutablePath) && File.Exists(_currentOptions.YtDlpExecutablePath))
            {
                startInfo.ArgumentList.Add($"--script-opts=ytdl_hook-ytdl_path={_currentOptions.YtDlpExecutablePath}");
            }
        }

        _isIntentionalStop = false;
        var process = ProcessFactory(startInfo);
        process.Exited += OnProcessExited;
        _process = process;

        // Connect IPC client
        await _ipcClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(10), cancellationToken);

        // Register observed properties
        await _ipcClient.ObservePropertyAsync(PropIdPause, "pause", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdTimePos, "time-pos", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdDuration, "duration", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdVolume, "volume", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdMute, "mute", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdSpeed, "speed", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdCoreIdle, "core-idle", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdEofReached, "eof-reached", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdSub, "sub", cancellationToken);
        await _ipcClient.ObservePropertyAsync(PropIdSubText, "sub-text", cancellationToken);

        SetState(PlaybackState.Stopped);
        _logger.LogInformation("mpv engine initialized and property observers registered.");
    }

    public async Task LoadAsync(string mediaUrl, string? audioTrackUrl = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);
        EnsureAlive();

        _logger.LogInformation("Loading media URL in mpv: {Url}", mediaUrl);
        SetState(PlaybackState.Opening);

        var response = await _ipcClient.SendCommandAsync(new object[] { "loadfile", mediaUrl, "replace" }, cancellationToken);
        if (!response.IsSuccess)
        {
            SetState(PlaybackState.Error);
            var errorMsg = $"mpv failed to load media: {response.Error}";
            ErrorOccurred?.Invoke(this, new PlaybackErrorEventArgs(errorMsg));
            throw new InvalidOperationException(errorMsg);
        }

        if (!string.IsNullOrWhiteSpace(audioTrackUrl))
        {
            _logger.LogInformation("Appending separate audio track: {AudioUrl}", audioTrackUrl);
            await _ipcClient.SendCommandAsync(new object[] { "audio-add", audioTrackUrl, "select" }, cancellationToken);
        }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        await _ipcClient.SetPropertyAsync("pause", false, cancellationToken);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        await _ipcClient.SetPropertyAsync("pause", true, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEngineAlive) return;

        _isIntentionalStop = true;
        await _ipcClient.SendCommandAsync(new object[] { "stop" }, cancellationToken);
        SetState(PlaybackState.Stopped);
    }

    public async Task SeekAsync(TimeSpan targetTime, CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        await _ipcClient.SendCommandAsync(new object[] { "seek", targetTime.TotalSeconds, "absolute" }, cancellationToken);
    }

    public async Task SeekRelativeAsync(double deltaSeconds, CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        await _ipcClient.SendCommandAsync(new object[] { "seek", deltaSeconds, "relative" }, cancellationToken);
    }

    public async Task SetVolumeAsync(double volumePercent, CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        var clamped = Math.Clamp(volumePercent, 0.0, 100.0);
        await _ipcClient.SetPropertyAsync("volume", clamped, cancellationToken);
        Volume = clamped;
    }

    public async Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        var clamped = Math.Clamp(speed, 0.25, 4.0);
        await _ipcClient.SetPropertyAsync("speed", clamped, cancellationToken);
        Speed = clamped;
    }

    public async Task SetMuteAsync(bool isMuted, CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        await _ipcClient.SetPropertyAsync("mute", isMuted, cancellationToken);
        IsMuted = isMuted;
    }

    public async Task LoadSubtitleTrackAsync(string subtitleUrl, bool selectImmediately = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitleUrl);
        EnsureAlive();

        var selectFlag = selectImmediately ? "select" : "auto";
        await _ipcClient.SendCommandAsync(new object[] { "sub-add", subtitleUrl, selectFlag }, cancellationToken);
    }

    public async Task SetSubtitleTrackAsync(string subtitleTrackId, CancellationToken cancellationToken = default)
    {
        EnsureAlive();
        await _ipcClient.SetPropertyAsync("sub", subtitleTrackId, cancellationToken);
        CurrentSubtitleTrack = subtitleTrackId;
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restarting mpv engine after failure or termination...");
        await TerminateInternalAsync();
        await StartMpvProcessAndConnectAsync(cancellationToken);
    }

    private void OnMpvEventReceived(object? sender, MpvEvent e)
    {
        _logger.LogDebug("mpv event received: {Event}", e.EventName);

        switch (e.EventName?.ToLowerInvariant())
        {
            case "start-file":
                SetState(PlaybackState.Buffering);
                break;

            case "file-loaded":
                SetState(PlaybackState.Playing);
                break;

            case "end-file":
                if (string.Equals(e.Reason, "error", StringComparison.OrdinalIgnoreCase))
                {
                    SetState(PlaybackState.Error);
                    var msg = $"Playback stopped due to error: {e.Error}";
                    _logger.LogError(msg);
                    ErrorOccurred?.Invoke(this, new PlaybackErrorEventArgs(msg));
                }
                else if (string.Equals(e.Reason, "eof", StringComparison.OrdinalIgnoreCase))
                {
                    SetState(PlaybackState.Ended);
                }
                else
                {
                    SetState(PlaybackState.Stopped);
                }
                break;

            case "playback-restart":
                if (State == PlaybackState.Buffering || State == PlaybackState.Opening)
                {
                    SetState(PlaybackState.Playing);
                }
                break;
        }
    }

    private void OnMpvPropertyChanged(object? sender, MpvPropertyChangeEventArgs e)
    {
        switch (e.PropertyId)
        {
            case PropIdPause:
                if (e.Value.HasValue && e.Value.Value.ValueKind == JsonValueKind.True)
                {
                    if (State == PlaybackState.Playing) SetState(PlaybackState.Paused);
                }
                else if (e.Value.HasValue && e.Value.Value.ValueKind == JsonValueKind.False)
                {
                    if (State == PlaybackState.Paused) SetState(PlaybackState.Playing);
                }
                break;

            case PropIdTimePos:
                if (e.Value.HasValue && e.Value.Value.TryGetDouble(out var seconds))
                {
                    CurrentTime = TimeSpan.FromSeconds(Math.Max(0, seconds));
                    PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(CurrentTime, Duration));
                }
                break;

            case PropIdDuration:
                if (e.Value.HasValue && e.Value.Value.TryGetDouble(out var dur))
                {
                    Duration = TimeSpan.FromSeconds(Math.Max(0, dur));
                    PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(CurrentTime, Duration));
                }
                break;

            case PropIdVolume:
                if (e.Value.HasValue && e.Value.Value.TryGetDouble(out var vol))
                {
                    Volume = vol;
                }
                break;

            case PropIdMute:
                if (e.Value.HasValue)
                {
                    IsMuted = e.Value.Value.ValueKind == JsonValueKind.True;
                }
                break;

            case PropIdSpeed:
                if (e.Value.HasValue && e.Value.Value.TryGetDouble(out var spd))
                {
                    Speed = spd;
                }
                break;

            case PropIdSub:
                if (e.Value.HasValue)
                {
                    CurrentSubtitleTrack = e.Value.Value.ToString();
                }
                break;

            case PropIdSubText:
                if (e.Value.HasValue)
                {
                    var text = e.Value.Value.ValueKind == JsonValueKind.String
                        ? e.Value.Value.GetString()
                        : e.Value.Value.ToString();
                    CurrentSubtitleText = text ?? string.Empty;
                    SubtitleTextChanged?.Invoke(this, CurrentSubtitleText);
                }
                break;
        }
    }

    private void OnMpvDisconnected(object? sender, EventArgs e)
    {
        _logger.LogWarning("mpv named pipe connection dropped.");
        if (!_isDisposed && !_isIntentionalStop && State != PlaybackState.Stopped)
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, new PlaybackErrorEventArgs("mpv connection was lost unexpectedly."));
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("mpv process exited with code {Code}.", exitCode);
        if (!_isDisposed && !_isIntentionalStop)
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, new PlaybackErrorEventArgs($"mpv process terminated unexpectedly (exit code {exitCode})."));
        }
    }

    private void SetState(PlaybackState newState)
    {
        if (State == newState) return;
        var old = State;
        State = newState;
        _logger.LogDebug("PlaybackState changed: {Old} -> {New}", old, newState);
        StateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(old, newState));
    }

    private void EnsureAlive()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(MpvPlaybackEngine));

        if (!IsEngineAlive)
            throw new InvalidOperationException("Playback engine is not running or connected.");
    }

    private async Task TerminateInternalAsync()
    {
        try
        {
            if (_ipcClient.IsConnected)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _ipcClient.SendCommandAsync(new object[] { "quit" }, cts.Token);
                }
                catch { /* ignore */ }
            }

            await _ipcClient.DisposeAsync();

            if (_process != null && !_process.HasExited)
            {
                try
                {
                    _process.Kill();
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception during mpv termination.");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isIntentionalStop = true;

        await TerminateInternalAsync();
        GC.SuppressFinalize(this);
    }
}
