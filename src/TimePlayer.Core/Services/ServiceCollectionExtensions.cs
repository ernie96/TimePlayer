using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Privacy-focused HttpClient: No cookies, safe auto-redirects for GitHub releases & CDN assets
        services.TryAddSingleton(sp => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false
        }));

        services.TryAddSingleton<FileInstanceCache>();
        services.TryAddSingleton<IPublicInstanceDirectory, PublicInstanceDirectory>();
        services.TryAddSingleton<IInstanceHealthService, InstanceHealthService>();
        services.TryAddSingleton(sp => new Backends.TubeRepair.TubeRepairBackendProvider(
            new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            }),
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Backends.TubeRepair.TubeRepairBackendProvider>>()));
        services.TryAddSingleton<Backends.Invidious.InvidiousBackendProvider>();
        services.TryAddSingleton<Backends.InnerTube.InnerTubeBackendProvider>();

        // Process Runner & Local yt-dlp Fallback Backend
        services.TryAddSingleton<Common.Process.IProcessRunner, Common.Process.DefaultProcessRunner>();
        services.TryAddSingleton<Backends.YtDlp.IYtDlpManager, Backends.YtDlp.YtDlpManager>();
        services.TryAddSingleton<Backends.YtDlp.YtDlpBackendProvider>();

        // mpv Playback Engine & Local Named Pipe IPC
        services.TryAddSingleton<Playback.Mpv.IMpvManager, Playback.Mpv.MpvManager>();
        services.TryAddTransient<Playback.Mpv.IMpvIpcClient, Playback.Mpv.MpvIpcClient>();
        services.TryAddSingleton<Playback.Mpv.MpvPlaybackEngine>();
        services.TryAddSingleton<Playback.IPlayerEngine>(sp => sp.GetRequiredService<Playback.Mpv.MpvPlaybackEngine>());
        services.TryAddSingleton<IPlaybackEngine>(sp => sp.GetRequiredService<Playback.Mpv.MpvPlaybackEngine>());

        // Local library (subscriptions, history, watch later) and discovery feeds
        services.TryAddSingleton<Library.ILibraryService, Library.LibraryService>();
        services.TryAddSingleton<Library.IDiscoveryService, Library.DiscoveryService>();
        services.TryAddSingleton<Library.IWatchPageService, Library.WatchPageService>();
        services.TryAddSingleton<Library.IPlaylistService, Library.PlaylistService>();
        services.TryAddSingleton<Community.ISponsorBlockService, Community.SponsorBlockService>();
        services.TryAddSingleton<Community.IDislikeService, Community.DislikeService>();
        services.TryAddSingleton<Community.IRunCounter, Community.RunCounter>();
        services.TryAddSingleton<Community.IBugReportService, Community.BugReportService>();
        services.TryAddSingleton<Library.IRecommendationService, Library.RecommendationService>();
        services.TryAddSingleton<Library.IDownloadService, Library.DownloadService>();
        services.TryAddSingleton<Updates.IUpdateService, Updates.UpdateService>();

        // High-level Playback Coordinator
        services.TryAddSingleton<Playback.PlaybackCoordinator>();
        services.TryAddSingleton<IPlaybackCoordinator>(sp => sp.GetRequiredService<Playback.PlaybackCoordinator>());

        return services;
    }
}
