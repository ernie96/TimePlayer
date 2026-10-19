namespace TimePlayer.Core.Playback;

/// <summary>
/// Operational options for initializing the media player engine.
/// </summary>
public record PlayerOptions
{
    public string IpcPipeName { get; init; } = $"timeplayer-mpv-{Guid.NewGuid():N}";
    public bool HardwareDecoding { get; init; } = true;
    public double InitialVolume { get; init; } = 80.0;
    public double InitialSpeed { get; init; } = 1.0;
    public string? YtDlpExecutablePath { get; init; }
}
