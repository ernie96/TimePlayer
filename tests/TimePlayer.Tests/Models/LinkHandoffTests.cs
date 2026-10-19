using TimePlayer.Wpf;
using Xunit;

namespace TimePlayer.Tests.Models;

public class LinkHandoffTests
{
    [Fact]
    public async Task ALink_ReachesTheOpenCopy_EvenSeveralInARow()
    {
        // Its own pipe, so a TimePlayer that's open on this computer doesn't get these.
        var pipe = $"TimePlayer-Links-Test-{Guid.NewGuid():N}";
        var received = new List<string>();
        var got = new SemaphoreSlim(0);
        using var stop = new CancellationTokenSource();
        var listening = SingleInstance.ListenForLinksAsync(l => { lock (received) received.Add(l); got.Release(); }, _ => { }, stop.Token, pipe);

        var link = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=30s";
        Assert.True(SingleInstance.SendLinkToOtherCopy(link, pipe));
        Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SingleInstance.SendLinkToOtherCopy("https://youtu.be/aqz-KE-bpKQ", pipe));
        Assert.True(await got.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { link, "https://youtu.be/aqz-KE-bpKQ" }, received);

        stop.Cancel();
        await listening.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void NoOpenCopy_IsReportedSoTheLinkIsntLost()
    {
        Assert.False(SingleInstance.SendLinkToOtherCopy("https://youtu.be/aqz-KE-bpKQ", $"TimePlayer-Links-Test-{Guid.NewGuid():N}"));
    }
}
