using TimePlayer.Core.Common;

namespace TimePlayer.Core.Backends.YtDlp;

/// <summary>
/// Accurately parses and classifies yt-dlp standard error outputs into typed,
/// accessible application errors.
/// </summary>
public static class YtDlpErrorClassifier
{
    public static AppError Classify(int exitCode, string? standardError, string? standardOutput, string videoId = "")
    {
        var text = (standardError ?? string.Empty) + "\n" + (standardOutput ?? string.Empty);

        // 1. Missing executable
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase) || text.Contains("executable", StringComparison.OrdinalIgnoreCase)))
        {
            return AppError.DependencyNotFound("yt-dlp", "The yt-dlp executable could not be found at the configured location.");
        }

        // 2. Age-restricted video
        if (text.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("inappropriate for some users", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.AgeRestricted(
                videoId,
                "YouTube requires age verification for this video. You can provide session cookies in settings to access age-restricted videos.");
        }

        // 3. Private or members-only video
        if (text.Contains("Private video", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("members-only", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Join this channel to get access", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.PrivateVideo(
                videoId,
                "This video is private, members-only, or requires explicit account permissions.");
        }

        // 4. Video unavailable / deleted
        if (text.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("This video has been removed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("This video is no longer available", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Incomplete YouTube ID", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.VideoNotFound(videoId);
        }

        // 5. Cookies required / Bot detection
        if (text.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("--cookies-from-browser", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cookies are required", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Sign in with a web browser", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.CookiesRequired(
                videoId,
                "YouTube triggered bot verification or requested session cookies. Enable browser cookies in preferences.");
        }

        // 6. Rate limits (HTTP 429)
        if (text.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.RateLimited("yt-dlp", "YouTube is temporarily rate-limiting requests from this IP address.");
        }

        // 7. Network connectivity issues
        if (text.Contains("Unable to download API page", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to resolve", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("getaddrinfo failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return AppError.NetworkError("Network error while connecting to YouTube servers through yt-dlp.");
        }

        // 8. General parser breakage or unrecognized error
        var firstErrorLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            ?? (!string.IsNullOrWhiteSpace(standardError) ? standardError.Trim() : $"Exit code {exitCode}");

        return new AppError
        {
            Code = "YTDLP_EXECUTION_FAILED",
            Message = "yt-dlp was unable to process the video.",
            TechnicalDetails = firstErrorLine,
            IsRecoverable = true
        };
    }
}
