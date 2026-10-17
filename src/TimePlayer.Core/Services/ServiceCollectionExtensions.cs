using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Contracts;

namespace TimePlayer.Core.Services;

/// <summary>
/// Dependency injection registration extensions for TimePlayer.Core.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTimePlayerCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IConfigurationService, ConfigurationService>();

        // Privacy-focused HttpClient: No cookies, no auto-redirects, no credentials
        services.TryAddSingleton(sp => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        }));

        services.TryAddSingleton<FileInstanceCache>();
        services.TryAddSingleton<IPublicInstanceDirectory, PublicInstanceDirectory>();
        services.TryAddSingleton<IInstanceHealthService, InstanceHealthService>();
        services.TryAddSingleton<Backends.Piped.PipedBackendProvider>();
        services.TryAddSingleton<Backends.Invidious.InvidiousBackendProvider>();

        // Process Runner & Local yt-dlp Fallback Backend
        services.TryAddSingleton<Common.Process.IProcessRunner, Common.Process.DefaultProcessRunner>();
        services.TryAddSingleton<Backends.YtDlp.IYtDlpManager, Backends.YtDlp.YtDlpManager>();
        services.TryAddSingleton<Backends.YtDlp.YtDlpBackendProvider>();

        return services;
    }
}
