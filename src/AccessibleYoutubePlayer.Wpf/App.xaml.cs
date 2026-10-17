using System.Net.Http;
using System.Windows;
using AccessibleYoutubePlayer.Core.Abstractions;
using AccessibleYoutubePlayer.Core.Services;
using AccessibleYoutubePlayer.Core.Services.Discovery;
using AccessibleYoutubePlayer.Core.Services.Health;
using AccessibleYoutubePlayer.Core.Services.Providers;
using AccessibleYoutubePlayer.Wpf.Services;
using AccessibleYoutubePlayer.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AccessibleYoutubePlayer.Wpf;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // Logging
        services.AddLogging();

        // Network
        services.AddSingleton<HttpClient>();

        // Discovery & Caching
        services.AddSingleton<PipedDirectoryProvider>();
        services.AddSingleton<InvidiousDirectoryProvider>();
        services.AddSingleton<IInstanceCache, FileInstanceCache>();
        services.AddSingleton<IInstanceHealthChecker, InstanceHealthChecker>();

        // Providers
        services.AddSingleton<PipedBackendProvider>(sp => new PipedBackendProvider(
            sp.GetRequiredService<PipedDirectoryProvider>(),
            sp.GetRequiredService<IInstanceCache>(),
            sp.GetRequiredService<IInstanceHealthChecker>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetService<ILogger<PipedBackendProvider>>()
        ));

        services.AddSingleton<InvidiousBackendProvider>(sp => new InvidiousBackendProvider(
            sp.GetRequiredService<InvidiousDirectoryProvider>(),
            sp.GetRequiredService<IInstanceCache>(),
            sp.GetRequiredService<IInstanceHealthChecker>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetService<ILogger<InvidiousBackendProvider>>()
        ));

        services.AddSingleton<LocalYtDlpBackendProvider>();

        // Unified Failover Service
        services.AddSingleton<IYouTubeService>(sp => new FailoverYouTubeService(
            sp.GetRequiredService<PipedBackendProvider>(),
            sp.GetRequiredService<InvidiousBackendProvider>(),
            sp.GetRequiredService<LocalYtDlpBackendProvider>(),
            sp.GetService<ILogger<FailoverYouTubeService>>()
        ));

        // Accessibility Service
        var a11y = new WpfAccessibilityService();
        services.AddSingleton<WpfAccessibilityService>(a11y);
        services.AddSingleton<IAccessibilityService>(a11y);

        // UI & ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
