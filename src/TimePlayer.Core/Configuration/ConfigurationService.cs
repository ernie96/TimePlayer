using Microsoft.Extensions.Options;

namespace TimePlayer.Core.Configuration;

/// <summary>
/// Default implementation of IConfigurationService using Microsoft.Extensions.Options.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private AppConfig _currentConfig;

    public AppConfig CurrentConfig => _currentConfig;

    public event EventHandler<AppConfig>? ConfigChanged;

    public ConfigurationService(IOptions<AppConfig> options)
    {
        _currentConfig = options?.Value ?? new AppConfig();
    }

    public Task UpdateConfigAsync(AppConfig newConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        ValidateConfig(newConfig);

        _currentConfig = newConfig;
        ConfigChanged?.Invoke(this, _currentConfig);
        return Task.CompletedTask;
    }

    public static void ValidateConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Playback.SmallSeekSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(config.Playback.SmallSeekSeconds), "Small seek seconds must be greater than zero.");
        if (config.Playback.MediumSeekSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(config.Playback.MediumSeekSeconds), "Medium seek seconds must be greater than zero.");
        if (config.Playback.LargeSeekSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(config.Playback.LargeSeekSeconds), "Large seek seconds must be greater than zero.");
        if (config.Playback.MinSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(config.Playback.MinSpeed), "Min speed must be positive.");
        if (config.Playback.MaxSpeed < config.Playback.MinSpeed)
            throw new ArgumentOutOfRangeException(nameof(config.Playback.MaxSpeed), "Max speed cannot be less than Min speed.");
        if (config.Playback.DefaultVolumePercent < 0 || config.Playback.DefaultVolumePercent > 100)
            throw new ArgumentOutOfRangeException(nameof(config.Playback.DefaultVolumePercent), "Volume percent must be between 0 and 100.");
    }
}
