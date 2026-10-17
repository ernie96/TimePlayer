namespace TimePlayer.Core.Configuration;

/// <summary>
/// Service contract for application settings management.
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Gets the active configuration snapshot.
    /// </summary>
    AppConfig CurrentConfig { get; }

    /// <summary>
    /// Event raised when configuration is reloaded or changed.
    /// </summary>
    event EventHandler<AppConfig>? ConfigChanged;

    /// <summary>
    /// Validates and updates the configuration.
    /// </summary>
    Task UpdateConfigAsync(AppConfig newConfig, CancellationToken cancellationToken = default);
}
