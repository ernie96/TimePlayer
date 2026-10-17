using TimePlayer.Core.Backends.YtDlp;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class YtDlpErrorClassifierTests
{
    [Fact]
    public void Classify_AgeRestricted_ReturnsAgeRestrictedError()
    {
        var stderr = "ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm your age. This video may be inappropriate for some users.";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("AGE_RESTRICTED", error.Code);
        Assert.False(error.IsRecoverable);
        Assert.Contains("cookies", error.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_PrivateVideo_ReturnsPrivateVideoError()
    {
        var stderr = "ERROR: [youtube] dQw4w9WgXcQ: Private video. Sign in if you've been granted access to this video";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("PRIVATE_VIDEO", error.Code);
        Assert.False(error.IsRecoverable);
    }

    [Fact]
    public void Classify_MembersOnly_ReturnsPrivateVideoError()
    {
        var stderr = "ERROR: [youtube] dQw4w9WgXcQ: Join this channel to get access to members-only content";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("PRIVATE_VIDEO", error.Code);
        Assert.False(error.IsRecoverable);
    }

    [Fact]
    public void Classify_VideoUnavailable_ReturnsVideoNotFoundError()
    {
        var stderr = "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. This video has been removed for violating Terms of Service.";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("VIDEO_NOT_FOUND", error.Code);
        Assert.False(error.IsRecoverable);
    }

    [Fact]
    public void Classify_CookiesRequired_ReturnsCookiesRequiredError()
    {
        var stderr = "ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you're not a bot. Use --cookies-from-browser to provide session cookies.";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("COOKIES_REQUIRED", error.Code);
        Assert.True(error.IsRecoverable);
    }

    [Fact]
    public void Classify_RateLimited_ReturnsRateLimitedError()
    {
        var stderr = "ERROR: [youtube] dQw4w9WgXcQ: HTTP Error 429: Too Many Requests";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("RATE_LIMITED", error.Code);
        Assert.True(error.IsRecoverable);
    }

    [Fact]
    public void Classify_NetworkError_ReturnsNetworkError()
    {
        var stderr = "ERROR: [youtube] Unable to download API page: <urlopen error [Errno 11001] getaddrinfo failed>";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("NETWORK_ERROR", error.Code);
        Assert.True(error.IsRecoverable);
    }

    [Fact]
    public void Classify_ExecutableNotFound_ReturnsDependencyNotFoundError()
    {
        var stderr = "yt-dlp executable not found in system path";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("DEPENDENCY_NOT_FOUND", error.Code);
        Assert.False(error.IsRecoverable);
    }

    [Fact]
    public void Classify_GenericError_ExtractsFirstErrorLine()
    {
        var stderr = "Some prefix info\nERROR: [youtube] Unsupported URL format\nAdditional debug info";
        var error = YtDlpErrorClassifier.Classify(1, stderr, null, "dQw4w9WgXcQ");

        Assert.Equal("YTDLP_EXECUTION_FAILED", error.Code);
        Assert.True(error.IsRecoverable);
        Assert.Contains("Unsupported URL format", error.TechnicalDetails);
    }
}
