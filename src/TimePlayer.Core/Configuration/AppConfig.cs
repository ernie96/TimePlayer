using TimePlayer.Core.Models;

namespace TimePlayer.Core.Configuration;

/// <summary>
/// Root application configuration settings.
/// </summary>
public record AppConfig
{
    public PlaybackConfig Playback { get; init; } = new();
    public BackendConfig Backend { get; init; } = new();
    public AccessibilityConfig Accessibility { get; init; } = new();
}

public record PlaybackConfig
{
    public double DefaultVolumePercent { get; init; } = 80.0;
    public double VolumeStepPercent { get; init; } = 5.0;
    public double SmallSeekSeconds { get; init; } = 5.0;
    public double MediumSeekSeconds { get; init; } = 10.0;
    public double LargeSeekSeconds { get; init; } = 30.0;
    public double MinSpeed { get; init; } = 0.25;
    public double MaxSpeed { get; init; } = 3.0;
    public double SpeedStep { get; init; } = 0.25;
    public QualityPreference PreferredQuality { get; init; } = QualityPreference.Auto;
    public bool HardwareDecoding { get; init; } = true;
    public string? MpvCustomPath { get; init; }
}

public record BackendConfig
{
    public BackendProviderType PrimaryProvider { get; init; } = BackendProviderType.Piped;
    public BackendProviderType SecondaryProvider { get; init; } = BackendProviderType.Invidious;
    public BackendProviderType FallbackProvider { get; init; } = BackendProviderType.YtDlpLocal;

    public string PipedInstancesDirectoryUrl { get; init; } =
        "https://raw.githubusercontent.com/wiki/TeamPiped/Piped-Backend/Instances.md";

    public string InvidiousInstancesDirectoryUrl { get; init; } =
        "https://api.invidious.io/instances.json?sort_by=health";

    public IReadOnlyList<string> PipedSeedInstances { get; init; } = new[]
    {
        "https://pipedapi.kavin.rocks",
        "https://api.piped.privacy.com.de",
        "https://piped-api.lunar.icu"
    };

    public IReadOnlyList<string> InvidiousSeedInstances { get; init; } = new[]
    {
        "https://inv.tux.pizza",
        "https://invidious.nerdvpn.de",
        "https://invidious.protokolla.fi"
    };

    public int RequestTimeoutSeconds { get; init; } = 10;
    public int MaxRetriesPerProvider { get; init; } = 2;
    public int InstanceCacheTtlHours { get; init; } = 24;

    public string? YtDlpCustomPath { get; init; }
    public bool YtDlpAutoUpdateOnStartup { get; init; } = true;
    public int YtDlpTimeoutSeconds { get; init; } = 45;
    public string? YtDlpDownloadDirectory { get; init; }
    public string? YtDlpBrowserCookies { get; init; }
    public int YtDlpMaxSearchResults { get; init; } = 20;
}

public record AccessibilityConfig
{
    public bool AnnouncePlaybackChanges { get; init; } = true;
    public bool AnnounceVolumeChanges { get; init; } = true;
    public bool AnnounceSeekChanges { get; init; } = true;
    public bool AnnounceSpeedChanges { get; init; } = true;
    public bool HighContrastFocusVisuals { get; init; } = true;
}
