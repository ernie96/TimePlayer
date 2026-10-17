namespace TimePlayer.Core.Models;

/// <summary>
/// Supported backend provider types for YouTube metadata and media extraction.
/// </summary>
public enum BackendProviderType
{
    /// <summary>
    /// Piped API public instance (primary).
    /// </summary>
    Piped = 1,

    /// <summary>
    /// Invidious API public instance (secondary).
    /// </summary>
    Invidious = 2,

    /// <summary>
    /// Local yt-dlp executable (fallback).
    /// </summary>
    YtDlpLocal = 3
}

/// <summary>
/// Playback engine operational states.
/// </summary>
public enum PlaybackState
{
    Stopped = 0,
    Opening = 1,
    Buffering = 2,
    Playing = 3,
    Paused = 4,
    Ended = 5,
    Error = 6
}

/// <summary>
/// User stream quality preferences.
/// </summary>
public enum QualityPreference
{
    Auto = 0,
    P1080 = 1080,
    P720 = 720,
    P480 = 480,
    P360 = 360,
    AudioOnly = 1
}
