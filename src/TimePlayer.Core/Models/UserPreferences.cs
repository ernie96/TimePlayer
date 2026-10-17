namespace TimePlayer.Core.Models;

/// <summary>
/// User preferences and playback defaults.
/// </summary>
public record UserPreferences
{
    public QualityPreference PreferredQuality { get; init; } = QualityPreference.Auto;
    public bool AudioOnly { get; init; }
    public bool HardwareDecoding { get; init; } = true;
    public double DefaultVolume { get; init; } = 80.0;
    public double VolumeStep { get; init; } = 5.0;
    public double SmallSeekSeconds { get; init; } = 5.0;
    public double MediumSeekSeconds { get; init; } = 10.0;
    public double LargeSeekSeconds { get; init; } = 30.0;
    public bool SubtitlesEnabled { get; init; }
    public string PreferredSubtitleLanguage { get; init; } = "en";
    public bool AutoPlayNextInQueue { get; init; } = true;
    public bool AnnouncePlaybackChanges { get; init; } = true;
    public bool AnnounceVolumeChanges { get; init; } = true;
    public bool AnnounceSeekChanges { get; init; } = true;
    public bool AnnounceSpeedChanges { get; init; } = true;
    public bool HighContrastFocusVisuals { get; init; } = true;
}
