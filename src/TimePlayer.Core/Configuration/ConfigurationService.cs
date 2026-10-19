using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace TimePlayer.Core.Configuration;

/// <summary>
/// Default implementation of IConfigurationService using Microsoft.Extensions.Options
/// with persistent local disk storage in LocalApplicationData.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimePlayer");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private AppConfig _currentConfig;

    public AppConfig CurrentConfig => _currentConfig;

    public event EventHandler<AppConfig>? ConfigChanged;

    private readonly string? _customSettingsFilePath;
    private static bool IsTestEnvironment => AppDomain.CurrentDomain.FriendlyName.Contains("test", StringComparison.OrdinalIgnoreCase);

    public ConfigurationService(IOptions<AppConfig> options) : this(options, null)
    {
    }

    public ConfigurationService(IOptions<AppConfig> options, string? customSettingsFilePath)
    {
        _customSettingsFilePath = customSettingsFilePath;
        var baseConfig = options?.Value ?? new AppConfig();
        _currentConfig = LoadPersistedConfig(baseConfig, customSettingsFilePath);
    }

    private static readonly string PortableSettingsFilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static AppConfig LoadPersistedConfig(AppConfig fallback, string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            try
            {
                if (File.Exists(customPath))
                {
                    var json = File.ReadAllText(customPath);
                    var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                    if (loaded != null) return loaded;
                }
            }
            catch { }
            return fallback;
        }

        if (IsTestEnvironment)
        {
            return fallback;
        }

        try
        {
            // 1. Try portable settings file next to executable
            if (File.Exists(PortableSettingsFilePath))
            {
                var json = File.ReadAllText(PortableSettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    return loaded;
                }
            }

            // 2. Try LocalApplicationData settings file (a portable copy never reads the installed copy's)
            if (!AppPaths.IsPortable && File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Silently fall back to base or default configuration
        }
        return fallback;
    }

    public Task UpdateConfigAsync(AppConfig newConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        ValidateConfig(newConfig);

        _currentConfig = newConfig;
        ConfigChanged?.Invoke(this, _currentConfig);

        try
        {
            var json = JsonSerializer.Serialize(_currentConfig, JsonOptions);

            if (!string.IsNullOrEmpty(_customSettingsFilePath))
            {
                var dir = Path.GetDirectoryName(_customSettingsFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(_customSettingsFilePath, json);
                return Task.CompletedTask;
            }

            if (IsTestEnvironment)
            {
                return Task.CompletedTask;
            }

            // 1. Save to LocalApplicationData
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }
            File.WriteAllText(SettingsFilePath, json);

            // 2. Portable copies (portable.txt or settings.json next to the exe) keep their settings there.
            //    An installed copy never gets one created, so it cannot silently turn portable.
            //    appsettings.json is shipped defaults and is never overwritten.
            if (AppPaths.IsPortable)
            {
                try
                {
                    File.WriteAllText(PortableSettingsFilePath, json);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Portable directory might be write-protected (e.g. Program Files)
                }
            }
        }
        catch
        {
            // Non-fatal if directory is not writable
        }

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
