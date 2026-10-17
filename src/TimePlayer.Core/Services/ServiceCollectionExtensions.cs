using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Configuration;

namespace TimePlayer.Core.Services;

/// <summary>
/// Dependency injection registration extensions for TimePlayer.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTimePlayerCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IConfigurationService, ConfigurationService>();
        return services;
    }
}
