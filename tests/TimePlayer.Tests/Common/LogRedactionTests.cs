using TimePlayer.Core.Common;
using Xunit;

namespace TimePlayer.Tests.Common;

public class LogRedactionTests
{
    [Fact]
    public void StreamAddresses_LoseTheirQueryString()
    {
        Assert.Equal("https://rr4---sn-x.googlevideo.com/videoplayback?…",
            LogRedaction.Url("https://rr4---sn-x.googlevideo.com/videoplayback?expire=1&ip=172.93.217.200&sig=abc"));
        Assert.Equal("https://www.youtube.com/watch", LogRedaction.Url("https://www.youtube.com/watch"));
        Assert.Equal(string.Empty, LogRedaction.Url(null));
    }
}
