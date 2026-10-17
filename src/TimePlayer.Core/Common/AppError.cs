namespace TimePlayer.Core.Common;

/// <summary>
/// Standard application error representing domain, network, instance, or playback failures.
/// Separates friendly user-facing messages from technical diagnostic details.
/// </summary>
public record AppError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? TechnicalDetails { get; init; }
    public Exception? Exception { get; init; }
    public bool IsRecoverable { get; init; } = true;

    public static AppError InvalidUrl(string details) => new()
    {
        Code = "INVALID_YOUTUBE_URL",
        Message = "The provided text is not a valid YouTube URL or video ID.",
        TechnicalDetails = details,
        IsRecoverable = true
    };

    public static AppError VideoNotFound(string videoId) => new()
    {
        Code = "VIDEO_NOT_FOUND",
        Message = $"Video '{videoId}' could not be found or has been removed.",
        TechnicalDetails = $"Video ID: {videoId}",
        IsRecoverable = false
    };

    public static AppError StreamUnavailable(string videoId, string? reason = null) => new()
    {
        Code = "STREAM_UNAVAILABLE",
        Message = "Unable to extract a playable media stream for this video.",
        TechnicalDetails = reason ?? $"Video ID: {videoId}",
        IsRecoverable = true
    };

    public static AppError AllInstancesFailed(string providerName, int count) => new()
    {
        Code = "ALL_INSTANCES_FAILED",
        Message = $"All {count} public {providerName} instances failed to respond. Failing over to next provider.",
        TechnicalDetails = $"Provider: {providerName}, Attempted: {count}",
        IsRecoverable = true
    };

    public static AppError AgeRestricted(string videoId, string? details = null) => new()
    {
        Code = "AGE_RESTRICTED",
        Message = "This video is age-restricted and requires sign-in or session cookies to access.",
        TechnicalDetails = details ?? $"Video ID: {videoId}",
        IsRecoverable = false
    };

    public static AppError PrivateVideo(string videoId, string? details = null) => new()
    {
        Code = "PRIVATE_VIDEO",
        Message = "This video is private, members-only, or requires special permissions to view.",
        TechnicalDetails = details ?? $"Video ID: {videoId}",
        IsRecoverable = false
    };

    public static AppError CookiesRequired(string videoId, string? details = null) => new()
    {
        Code = "COOKIES_REQUIRED",
        Message = "YouTube requires browser cookies or authentication to access this content.",
        TechnicalDetails = details ?? $"Video ID: {videoId}",
        IsRecoverable = true
    };

    public static AppError RateLimited(string providerName, string? details = null) => new()
    {
        Code = "RATE_LIMITED",
        Message = $"The service ({providerName}) is currently rate-limiting requests. Please try again shortly.",
        TechnicalDetails = details,
        IsRecoverable = true
    };

    public static AppError NetworkError(string message, Exception? ex = null) => new()
    {
        Code = "NETWORK_ERROR",
        Message = message,
        TechnicalDetails = ex?.Message,
        Exception = ex,
        IsRecoverable = true
    };

    public static AppError DependencyNotFound(string dependencyName, string details) => new()
    {
        Code = "DEPENDENCY_NOT_FOUND",
        Message = $"The required application dependency '{dependencyName}' was not found: {details}",
        TechnicalDetails = details,
        IsRecoverable = false
    };

    public static AppError PlaybackFailed(string message, Exception? ex = null) => new()
    {
        Code = "PLAYBACK_ENGINE_ERROR",
        Message = message,
        TechnicalDetails = ex?.Message,
        Exception = ex,
        IsRecoverable = true
    };
}
