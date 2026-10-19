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
    public UpdateConfig Update { get; init; } = new();
    public SponsorBlockConfig SponsorBlock { get; init; } = new();
    public DislikeConfig Dislikes { get; init; } = new();
}

/// <summary>What to do when a SponsorBlock segment of a category starts.</summary>
public enum SegmentAction
{
    /// <summary>Ignore it.</summary>
    Off,
    /// <summary>Say what started and how long it lasts; S skips it.</summary>
    Announce,
    /// <summary>Skip it straight away.</summary>
    Skip
}

/// <summary>
/// SponsorBlock (sponsor.ajay.app): community-submitted segments such as sponsor reads, skipped or
/// announced. Videos are looked up by the first characters of a hash of their id, so the server
/// never learns which video is playing.
/// </summary>
public record SponsorBlockConfig
{
    public bool Enabled { get; init; } = true;
    public string ServerUrl { get; init; } = "https://sponsor.ajay.app";
    /// <summary>Say "Skipped sponsor, 40 seconds" after an automatic skip.</summary>
    public bool AnnounceSkips { get; init; } = true;
    public SegmentAction Sponsor { get; init; } = SegmentAction.Skip;
    public SegmentAction SelfPromotion { get; init; } = SegmentAction.Announce;
    public SegmentAction Interaction { get; init; } = SegmentAction.Announce;
    public SegmentAction Intro { get; init; } = SegmentAction.Off;
    public SegmentAction Outro { get; init; } = SegmentAction.Off;
    public SegmentAction Preview { get; init; } = SegmentAction.Off;
    public SegmentAction Filler { get; init; } = SegmentAction.Off;
    /// <summary>Non-music parts of music videos.</summary>
    public SegmentAction NonMusic { get; init; } = SegmentAction.Off;
    /// <summary>Say when a video has a highlight (H jumps to it).</summary>
    public bool AnnounceHighlight { get; init; } = true;
    /// <summary>Say when the whole video is labeled, e.g. as sponsored or exclusive access.</summary>
    public bool AnnounceWholeVideoLabels { get; init; } = true;
}

/// <summary>Return YouTube Dislike (returnyoutubedislike.com): estimated dislike counts.</summary>
public record DislikeConfig
{
    public bool Enabled { get; init; } = true;
    public string ApiUrl { get; init; } = "https://returnyoutubedislikeapi.com";
}

public record UpdateConfig
{
    /// <summary>Look for a new TimePlayer release when the app starts.</summary>
    public bool CheckAtStartup { get; init; } = true;
    /// <summary>Where official TimePlayer releases are published.</summary>
    public const string OfficialRepository = "ernie96/TimePlayer";

    /// <summary>GitHub repository ("owner/name") whose releases are TimePlayer's updates.</summary>
    public string Repository { get; init; } = OfficialRepository;
    /// <summary>A version the user chose to skip; startup checks stay quiet about it.</summary>
    public string? SkippedVersion { get; init; }
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
