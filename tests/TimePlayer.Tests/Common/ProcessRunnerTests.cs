using Microsoft.Extensions.Logging.Abstractions;
using TimePlayer.Core.Common.Process;
using Xunit;

namespace TimePlayer.Tests.Common;

public class ProcessRunnerTests
{
    private readonly DefaultProcessRunner _runner = new(NullLogger<DefaultProcessRunner>.Instance);

    [Fact]
    public async Task RunAsync_EchoOutput_CapturesStdoutSuccessfully()
    {
        // cmd.exe /c echo Hello World
        var result = await _runner.RunAsync("cmd.exe", new[] { "/c", "echo", "Hello World" });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.IsSuccess);
        Assert.Contains("Hello World", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_CapturesLineProgress()
    {
        var lines = new List<string>();
        var progress = new Progress<string>(line => lines.Add(line));

        var result = await _runner.RunAsync(
            "cmd.exe",
            new[] { "/c", "echo Line 1 & echo Line 2" },
            standardOutputLineProgress: progress);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Line 1", result.StandardOutput);
        Assert.Contains("Line 2", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_WhenTimesOut_ThrowsTimeoutException()
    {
        var options = new ProcessRunOptions
        {
            Timeout = TimeSpan.FromMilliseconds(200)
        };

        // ping 127.0.0.1 -n 5 will take 4-5 seconds
        await Assert.ThrowsAsync<TimeoutException>(() =>
            _runner.RunAsync("ping.exe", new[] { "127.0.0.1", "-n", "5" }, options));
    }
}
