using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Playback.Mpv;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Playback;

public class MpvManagerTests
{
    private readonly FakeProcessRunner _processRunner = new();
    private readonly ConfigurationService _configService;

    public MpvManagerTests()
    {
        _configService = new ConfigurationService(Options.Create(new AppConfig()));
    }

    [Fact]
    public void ExecutablePath_WhenCustomPathSpecifiedAndExists_ReturnsCustomPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            _configService.UpdateConfigAsync(new AppConfig
            {
                Playback = new PlaybackConfig { MpvCustomPath = tempFile }
            });

            var httpClient = new HttpClient();
            var manager = new MpvManager(_processRunner, httpClient, _configService, NullLogger<MpvManager>.Instance);

            Assert.Equal(tempFile, manager.ExecutablePath);
            Assert.True(manager.IsInstalled);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExecutablePath_WhenCustomPathNotSpecified_ReturnsManagedPath()
    {
        var httpClient = new HttpClient();
        var manager = new MpvManager(_processRunner, httpClient, _configService, NullLogger<MpvManager>.Instance);

        var path = manager.ExecutablePath;
        Assert.Contains("TimePlayer", path);
        Assert.Contains("bin", path);
        Assert.Contains("mpv", path);
    }
}
