namespace TimePlayer.Core.Models;

public enum PlaybackErrorType
{
    ConnectionFailed = 1,
    ProcessTerminated = 2,
    FormatNotSupported = 3,
    StreamUnreachable = 4,
    IpcTimeout = 5,
    Unknown = 99
}

/// <summary>
/// Strongly-typed playback error encapsulating user-facing text and diagnostic information.
/// </summary>
public record PlaybackError
{
    public PlaybackErrorType ErrorType { get; init; } = PlaybackErrorType.Unknown;
    public required string UserMessage { get; init; }
    public string? TechnicalDetails { get; init; }
    public Exception? Exception { get; init; }
    public bool IsRecoverable { get; init; } = true;

    public static PlaybackError StreamUnreachable(string url, string? details = null) => new()
    {
        ErrorType = PlaybackErrorType.StreamUnreachable,
        UserMessage = "The media stream could not be reached or timed out.",
        TechnicalDetails = $"Stream URL: {url}. {details}",
        IsRecoverable = true
    };

    public static PlaybackError IpcTimeout(string pipeName) => new()
    {
        ErrorType = PlaybackErrorType.IpcTimeout,
        UserMessage = "Communication with the media player timed out.",
        TechnicalDetails = $"Named pipe: {pipeName}",
        IsRecoverable = true
    };
}
