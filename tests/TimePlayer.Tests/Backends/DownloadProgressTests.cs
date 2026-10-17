using TimePlayer.Core.Backends.YtDlp;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class DownloadProgressTests
{
    [Fact]
    public void AccessibleSummary_WhenComplete_AnnouncesDownloadComplete()
    {
        var progress = new DownloadProgress
        {
            Percent = 100,
            StatusMessage = "Finished"
        };

        Assert.Equal("Download complete.", progress.AccessibleSummary);
    }

    [Fact]
    public void AccessibleSummary_WhenInProgress_IncludesPercentSpeedAndEta()
    {
        var progress = new DownloadProgress
        {
            Percent = 45.6,
            Speed = "3.2MiB/s",
            Eta = "00:15"
        };

        var summary = progress.AccessibleSummary;
        Assert.Contains("Downloading, 45 percent", summary);
        Assert.Contains("at 3.2MiB/s", summary);
        Assert.Contains("ETA 00:15", summary);
    }
}
