using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class UrlValidationTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void Validate_ValidVideoUrls_ReturnsSuccessWithVideoId(string input, string expectedId)
    {
        var result = YouTubeUrl.Validate(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedId, result.Value.VideoId.Value);
    }

    [Fact]
    public void Validate_WithTimestamp_ParsesStartTimeCorrectly()
    {
        var input = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s";
        var result = YouTubeUrl.Validate(input);

        Assert.True(result.IsSuccess);
        Assert.Equal("dQw4w9WgXcQ", result.Value.VideoId.Value);
        Assert.Equal(TimeSpan.FromSeconds(90), result.Value.StartTime);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL1234567890abcdef")]
    [InlineData("https://www.youtube.com/playlist?list=WL")]
    public void Validate_PlaylistUrlWithoutVideo_ReturnsFriendlyUnsupportedError(string input)
    {
        var result = YouTubeUrl.Validate(input);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNSUPPORTED_URL", result.Error?.Code);
        Assert.Contains("Playlists are not supported", result.Error?.Message);
    }

    [Fact]
    public void Validate_WatchUrlInsidePlaylist_AcceptsAsIndividualVideo()
    {
        var input = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1234567890abcdef";
        var result = YouTubeUrl.Validate(input);

        Assert.True(result.IsSuccess);
        Assert.Equal("dQw4w9WgXcQ", result.Value.VideoId.Value);
    }

    [Theory]
    [InlineData("https://www.youtube.com/@mkbhd")]
    [InlineData("https://www.youtube.com/channel/UCBJycsmduvYEL83R_U4JriQ")]
    [InlineData("https://www.youtube.com/c/Veritasium")]
    [InlineData("https://www.youtube.com/user/TEDtalksDirector")]
    public void Validate_ChannelUrl_ReturnsFriendlyUnsupportedError(string input)
    {
        var result = YouTubeUrl.Validate(input);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNSUPPORTED_URL", result.Error?.Code);
        Assert.Contains("Channel pages cannot be played directly", result.Error?.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_NullOrEmpty_ReturnsInvalidUrlError(string? input)
    {
        var result = YouTubeUrl.Validate(input);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_YOUTUBE_URL", result.Error?.Code);
        Assert.Contains("Input cannot be empty", result.Error?.TechnicalDetails);
    }

    [Theory]
    [InlineData("https://example.com/not-youtube")]
    [InlineData("https://vimeo.com/12345678")]
    public void Validate_NonYouTubeHttpUrl_ReturnsUnsupportedUrlError(string input)
    {
        var result = YouTubeUrl.Validate(input);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNSUPPORTED_URL", result.Error?.Code);
        Assert.Contains("does not appear to be a supported YouTube video URL", result.Error?.Message);
    }

    [Theory]
    [InlineData("random search query")]
    [InlineData("not a link!")]
    public void Validate_GeneralText_ReturnsInvalidUrlError(string input)
    {
        var result = YouTubeUrl.Validate(input);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_YOUTUBE_URL", result.Error?.Code);
        Assert.Contains("The provided text is not a valid YouTube URL", result.Error?.Message);
        Assert.Contains("is not a valid 11-character YouTube video ID", result.Error?.TechnicalDetails);
    }
}
