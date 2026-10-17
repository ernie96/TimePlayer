using TimePlayer.Core.Common;
using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Common;

public class ResultTests
{
    [Fact]
    public void ResultOfT_Success_ReturnsValueAndSetsFlags()
    {
        var result = Result<string>.Success("test payload");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal("test payload", result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ResultOfT_Failure_ReturnsErrorAndSetsFlags()
    {
        var error = AppError.VideoNotFound("dQw4w9WgXcQ");
        var result = Result<string>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Value);
        Assert.NotNull(result.Error);
        Assert.Equal("VIDEO_NOT_FOUND", result.Error.Code);
        Assert.Contains("dQw4w9WgXcQ", result.Error.Message);
    }

    [Fact]
    public void Result_ImplicitConversions_WorkSeamlessly()
    {
        Result<int> successResult = 42;
        Assert.True(successResult.IsSuccess);
        Assert.Equal(42, successResult.Value);

        AppError error = AppError.InvalidUrl("Malformed syntax");
        Result<int> failureResult = error;
        Assert.True(failureResult.IsFailure);
        Assert.Equal("INVALID_YOUTUBE_URL", failureResult.Error?.Code);
    }

    [Fact]
    public void AppError_FactoryMethods_ProduceExpectedCodes()
    {
        var urlErr = AppError.InvalidUrl("Bad url");
        Assert.Equal("INVALID_YOUTUBE_URL", urlErr.Code);
        Assert.True(urlErr.IsRecoverable);

        var notFound = AppError.VideoNotFound("xyz123");
        Assert.Equal("VIDEO_NOT_FOUND", notFound.Code);
        Assert.False(notFound.IsRecoverable);

        var streamErr = AppError.StreamUnavailable("xyz123", "Forbidden");
        Assert.Equal("STREAM_UNAVAILABLE", streamErr.Code);

        var instErr = AppError.AllInstancesFailed("Piped", 5);
        Assert.Equal("ALL_INSTANCES_FAILED", instErr.Code);
        Assert.Contains("5 public Piped instances", instErr.Message);

        var pbErr = AppError.PlaybackFailed("Audio device error");
        Assert.Equal("PLAYBACK_ENGINE_ERROR", pbErr.Code);
    }

    [Fact]
    public void PlaybackError_FactoryMethods_ProduceDetailedUserMessages()
    {
        var timeout = PlaybackError.IpcTimeout("timeplayer-pipe-1");
        Assert.Equal(PlaybackErrorType.IpcTimeout, timeout.ErrorType);
        Assert.Contains("timed out", timeout.UserMessage);
        Assert.Contains("timeplayer-pipe-1", timeout.TechnicalDetails);

        var died = PlaybackError.ProcessDied(139);
        Assert.Equal(PlaybackErrorType.ProcessTerminated, died.ErrorType);
        Assert.Contains("Restarting engine", died.UserMessage);
        Assert.Contains("139", died.TechnicalDetails);

        var unreachable = PlaybackError.StreamUnreachable("https://bad.stream");
        Assert.Equal(PlaybackErrorType.StreamUnreachable, unreachable.ErrorType);
        Assert.Contains("could not be reached", unreachable.UserMessage);
    }
}
