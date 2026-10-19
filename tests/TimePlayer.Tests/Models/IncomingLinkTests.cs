using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class IncomingLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("\"https://youtu.be/dQw4w9WgXcQ?t=30\"", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=30s")]
    [InlineData("dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("timeplayer://open?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DdQw4w9WgXcQ%26t%3D30", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=30s")]
    [InlineData("timeplayer://open/?url=https%3A%2F%2Fwww.youtube.com%2Fplaylist%3Flist%3DPLabcdefghij", "https://www.youtube.com/playlist?list=PLabcdefghij")]
    [InlineData("TIMEPLAYER://https://youtu.be/dQw4w9WgXcQ/", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("timeplayer://https:/www.youtube.com/shorts/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("timeplayer:dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RDAMVMdQw4w9WgXcQ", "https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijk", "https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijk")]
    [InlineData("timeplayer://open?url=https%3A%2F%2Fmusic.youtube.com%2Fwatch%3Fv%3DdQw4w9WgXcQ", "https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void YouTubeLinks_AreRebuiltCleanly(string argument, string expected) =>
        Assert.Equal(expected, IncomingLink.Normalize(argument));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--exec calc")]
    [InlineData("timeplayer://open?url=https%3A%2F%2Fexample.com%2F")]
    [InlineData("timeplayer://open?other=1")]
    [InlineData("https://www.youtube.com/@somechannel")]
    [InlineData(@"C:WindowsSystem32lc.exe")]
    public void AnythingElse_IsRefused(string? argument) =>
        Assert.Null(IncomingLink.Normalize(argument));

    [Fact]
    public void ProtocolLinks_RoundTrip()
    {
        var link = IncomingLink.ToProtocolLink("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s");
        Assert.StartsWith("timeplayer://open?url=https%3A%2F%2F", link);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s", IncomingLink.Normalize(link));
    }

    [Fact]
    public void TheBookmark_MakesTheSameLinks()
    {
        Assert.Contains("timeplayer://open?url='+encodeURIComponent(location.href)", TimePlayer.Wpf.ViewModels.MainViewModel.BrowserBookmark);
    }

    [Theory]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("music.youtube.com/playlist?list=OLAK5uy_abcdefghijk", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false)]
    [InlineData("https://music.youtube.com.evil.example/watch?v=dQw4w9WgXcQ", false)]
    [InlineData(null, false)]
    public void YouTubeMusicLinks_AreRecognised(string? link, bool music) =>
        Assert.Equal(music, IncomingLink.IsYouTubeMusic(link));

    [Theory]
    [InlineData("taylorswift", false)]
    [InlineData("minecraft12", false)]
    [InlineData("lofi hip hop", false)]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", true)]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefghij", true)]
    [InlineData("https://example.com/page", false)]
    public void TypedText_IsALinkOnlyWhenItLooksLikeOne(string text, bool link) =>
        Assert.Equal(link, YouTubeUrl.LooksLikeLink(text));
}
