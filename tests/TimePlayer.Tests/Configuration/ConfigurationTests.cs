using Microsoft.Extensions.Options;
using TimePlayer.Core.Configuration;
using Xunit;

namespace TimePlayer.Tests.Configuration;

public class ConfigurationTests
{
    [Fact]
    public void DefaultConfig_HasSensibleValues()
    {
        var config = new AppConfig();

        Assert.Equal(80.0, config.Playback.DefaultVolumePercent);
        Assert.Equal(5.0, config.Playback.SmallSeekSeconds);
        Assert.Equal(10.0, config.Playback.MediumSeekSeconds);
        Assert.Equal(30.0, config.Playback.LargeSeekSeconds);
        Assert.Equal(0.25, config.Playback.MinSpeed);
        Assert.Equal(3.0, config.Playback.MaxSpeed);
        Assert.Equal(0.25, config.Playback.SpeedStep);
        Assert.NotEmpty(config.Backend.PipedSeedInstances);
        Assert.NotEmpty(config.Backend.InvidiousSeedInstances);
        Assert.True(config.Accessibility.AnnouncePlaybackChanges);
        Assert.True(config.Accessibility.HighContrastFocusVisuals);
    }

    [Fact]
    public void ValidateConfig_ThrowsWhenSeekStepIsZeroOrNegative()
    {
        var invalidConfig = new AppConfig
        {
            Playback = new PlaybackConfig { SmallSeekSeconds = 0 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigurationService.ValidateConfig(invalidConfig));
    }

    [Fact]
    public void ValidateConfig_ThrowsWhenVolumeIsOutOfRange()
    {
        var invalidConfig = new AppConfig
        {
            Playback = new PlaybackConfig { DefaultVolumePercent = 150 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigurationService.ValidateConfig(invalidConfig));
    }

    [Fact]
    public void ValidateConfig_ThrowsWhenMaxSpeedLessThanMinSpeed()
    {
        var invalidConfig = new AppConfig
        {
            Playback = new PlaybackConfig { MinSpeed = 2.0, MaxSpeed = 1.0 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ConfigurationService.ValidateConfig(invalidConfig));
    }

    [Fact]
    public async Task UpdateConfigAsync_RaisesConfigChangedEvent()
    {
        var options = Options.Create(new AppConfig());
        var service = new ConfigurationService(options);

        AppConfig? notifiedConfig = null;
        service.ConfigChanged += (sender, c) => notifiedConfig = c;

        var updated = new AppConfig
        {
            Playback = new PlaybackConfig { DefaultVolumePercent = 50.0 }
        };

        await service.UpdateConfigAsync(updated);

        Assert.NotNull(notifiedConfig);
        Assert.Equal(50.0, notifiedConfig.Playback.DefaultVolumePercent);
        Assert.Equal(50.0, service.CurrentConfig.Playback.DefaultVolumePercent);
    }
}
