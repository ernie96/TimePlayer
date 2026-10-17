using AccessibleYoutubePlayer.Core.Models;

namespace AccessibleYoutubePlayer.Core.Abstractions;

/// <summary>
/// Retrieves raw public instance listings from official project sources.
/// </summary>
public interface IInstanceDirectoryProvider
{
    ProviderType ProviderType { get; }
    Task<IReadOnlyList<InstanceInfo>> FetchInstancesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Persists and retrieves discovered instance directories on disk to survive network drops.
/// </summary>
public interface IInstanceCache
{
    Task<IReadOnlyList<InstanceInfo>?> LoadCachedInstancesAsync(ProviderType providerType, CancellationToken cancellationToken = default);
    Task SaveCachedInstancesAsync(ProviderType providerType, IReadOnlyList<InstanceInfo> instances, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tests and ranks public instances by live functionality and network latency.
/// </summary>
public interface IInstanceHealthChecker
{
    Task<InstanceInfo> ProbeInstanceAsync(InstanceInfo instance, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstanceInfo>> FilterAndRankInstancesAsync(
        IEnumerable<InstanceInfo> instances,
        int maxConcurrency = 4,
        int maxWorkingToKeep = 5,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Core contract implemented by each backend provider (Piped, Invidious, Local yt-dlp).
/// </summary>
public interface IYouTubeBackendProvider
{
    ProviderType ProviderType { get; }
    Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Unified YouTube service that transparently orchestrates instance discovery, health probing,
/// and automatic failover across Piped -> Invidious -> LocalFallback.
/// </summary>
public interface IYouTubeService
{
    ProviderType CurrentProviderType { get; }
    string? CurrentInstanceName { get; }

    event EventHandler<string>? StatusChanged;
    event EventHandler<ProviderType>? ProviderSwitched;

    Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<VideoMetadata> GetVideoDetailsAsync(string videoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StreamInfo>> GetStreamsAsync(string videoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(string videoId, CancellationToken cancellationToken = default);

    Task RefreshInstancesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches accessible announcements to screen readers via UI Automation or live regions.
/// </summary>
public interface IAccessibilityService
{
    void Announce(string message, AnnouncementPriority priority = AnnouncementPriority.Medium);
}

/// <summary>
/// Video and audio playback engine abstraction (e.g. mpv embedded playback).
/// </summary>
public interface IPlayerEngine : IDisposable
{
    PlaybackState State { get; }
    TimeSpan CurrentPosition { get; }
    TimeSpan Duration { get; }
    double Volume { get; set; }
    double Speed { get; set; }
    bool IsMuted { get; set; }

    event EventHandler<PlaybackState>? StateChanged;
    event EventHandler<TimeSpan>? PositionChanged;
    event EventHandler<TimeSpan>? DurationChanged;
    event EventHandler<string>? ErrorOccurred;

    Task InitializeAsync(IntPtr windowHandle);
    Task OpenAsync(string mediaUrl, TimeSpan? startPosition = null);
    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task SeekAsync(TimeSpan position);
}
