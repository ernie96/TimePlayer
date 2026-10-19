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
    public void TryParse_ExtractsIdFromAllStandardFormats(string input, string expectedId)
    {
        Assert.True(YouTubeUrl.TryParse(input, out var url));
        Assert.Equal(expectedId, url.VideoId.Value);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=45s", 45)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=90", 90)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1m30s", 90)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1h2m3s", 3723)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&start=120", 120)]
    public void TryParse_ParsesTimestampVariations(string input, int expectedSeconds)
    {
        Assert.True(YouTubeUrl.TryParse(input, out var url));
        Assert.Equal("dQw4w9WgXcQ", url.VideoId.Value);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), url.StartTime);
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
    }
}
