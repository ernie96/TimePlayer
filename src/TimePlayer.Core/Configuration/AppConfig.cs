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
    public LibraryConfig Library { get; init; } = new();
}

public record LibraryConfig
{
    /// <summary>Continue videos from where they were left off.</summary>
    public bool ResumePlayback { get; init; } = true;
    /// <summary>Keep a local watch history (needed for resume).</summary>
    public bool RecordHistory { get; init; } = true;
    public bool HideShortsInFeed { get; init; }
    /// <summary>Two-letter region code for Featured, e.g. "US", "ES", "MX".</summary>
    public string FeaturedRegion { get; init; } = "US";
    /// <summary>Refresh What's New in the background at startup and announce new uploads.</summary>
    public bool CheckFeedOnStartup { get; init; } = true;
    /// <summary>When the queue runs out, play the first unwatched related video.</summary>
    public bool Autoplay { get; init; } = true;
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
    public BackendProviderType PrimaryProvider { get; init; } = BackendProviderType.TubeRepair;
    public BackendProviderType SecondaryProvider { get; init; } = BackendProviderType.Invidious;
    public BackendProviderType TertiaryProvider { get; init; } = BackendProviderType.InnerTube;
    public BackendProviderType FallbackProvider { get; init; } = BackendProviderType.YtDlpLocal;

    /// <summary>
    /// Exact TubeRepair legacy YouTube-compatible API server.
    /// Configured in one place as specified.
    /// </summary>
    public string TubeRepairBaseUrl { get; init; } = "https://tuberepair.uptimetrackers.com/";
    public int TubeRepairTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Anonymous YouTube InnerTube API base URL.
    /// </summary>
    public string InnerTubeBaseUrl { get; init; } = "https://www.youtube.com/youtubei/v1/";
    public int InnerTubeTimeoutSeconds { get; init; } = 10;

    public string InvidiousInstancesDirectoryUrl { get; init; } =
        "https://api.invidious.io/instances.json?sort_by=health";

    public string PipedInstancesDirectoryUrl { get; init; } =
        "https://raw.githubusercontent.com/TeamPiped/documentation/main/content/docs/public-instances/index.md";

    public IReadOnlyList<string> PipedSeedInstances { get; init; } = new[]
    {
        "https://api.piped.private.coffee",
        "https://pipedapi.ducks.party",
        "https://pipedapi.leptons.xyz"
    };

    public IReadOnlyList<string> InvidiousSeedInstances { get; init; } = new[]
    {
        "https://invidious.f5.si",
        "https://inv.nadeko.net",
        "https://invidious.nerdvpn.de"
    };

    public int RequestTimeoutSeconds { get; init; } = 15;
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
    public int BrailleDisplayCellCount { get; init; } = 40;
    public bool EnableBrailleProgress { get; init; } = true;
    /// <summary>Read captions aloud through the screen reader as they appear.</summary>
    public bool SpeakCaptions { get; init; }
    /// <summary>
    /// Whether the user wants TimePlayer's NVDA braille add-on: null until they have been asked
    /// (only asked when NVDA is installed), then never asked again.
    /// </summary>
    public bool? UseBrailleAddon { get; init; }
}
