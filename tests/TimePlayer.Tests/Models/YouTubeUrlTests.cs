using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class YouTubeUrlTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/v/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&feature=share&si=abc123", "dQw4w9WgXcQ")]
    public void TryExtractVideoId_ExtractsIdFromAllStandardFormats(string input, string expectedId)
    {
        var success = YouTubeUrl.TryExtractVideoId(input, out var videoId, out _);

        Assert.True(success);
        Assert.Equal(expectedId, videoId.Value);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=45s", 45)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=90", 90)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1m30s", 90)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1h2m3s", 3723)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&start=120", 120)]
    public void TryExtractVideoId_ParsesTimestampVariations(string input, int expectedSeconds)
    {
        var success = YouTubeUrl.TryExtractVideoId(input, out var videoId, out var startTime);

        Assert.True(success);
        Assert.Equal("dQw4w9WgXcQ", videoId.Value);
        Assert.NotNull(startTime);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), startTime.Value);
    }

    [Fact]
    public void Normalize_StripsTrackingParametersAndPreservesIdAndTimestamp()
    {
        var input = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&feature=emb_rel_pause&t=45s&si=xyz987";
        var normalized = YouTubeUrl.Normalize(input);

        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=45s", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://example.com/watch?v=123")]
    [InlineData("dQw4w9WgXc")]       // 10 chars, too short
    [InlineData("dQw4w9WgXcQ1")]     // 12 chars, too long
    [InlineData("dQw4w9WgXc@")]      // Invalid character @
    public void TryParse_RejectsInvalidInputs(string? input)
    {
        var success = YouTubeUrl.TryParse(input, out var parsed);

        Assert.False(success);
        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_ThrowsFormatExceptionOnInvalidInput()
    {
        Assert.Throws<FormatException>(() => YouTubeUrl.Parse("invalid-string"));
    }

    [Fact]
    public void YouTubeVideoId_GeneratesCorrectUrls()
    {
        var id = new YouTubeVideoId("dQw4w9WgXcQ");

        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", id.ToWatchUrl());
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", id.ToShortUrl());
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ", id.ToEmbedUrl());
    }
}
