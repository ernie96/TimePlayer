namespace AccessibleYoutubePlayer.Core.Models;

/// <summary>
/// Identifies the backend provider or service type.
/// </summary>
public enum ProviderType
{
    Piped,
    Invidious,
    LocalFallback
}

/// <summary>
/// Playback state for the media player.
/// </summary>
public enum PlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Buffering,
    Ended,
    Error
}

/// <summary>
/// Urgency priority for screen reader announcements.
/// </summary>
public enum AnnouncementPriority
{
    Low,
    Medium,
    High
}
