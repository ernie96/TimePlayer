using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Services;
using TimePlayer.Wpf.Accessibility;
using TimePlayer.Wpf.ViewModels;
using TimePlayer.Wpf.Views;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf;

/// <summary>
/// Application entry point and host lifecycle manager.
/// Configures Dependency Injection, centralized logging, and centralized error handling.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    // Lets the installer and uninstaller (AppMutex in TimePlayer.iss) see that TimePlayer is open
    // and ask the user to close it, instead of leaving a locked TimePlayer.exe behind.
    private Mutex? _runningMutex;

    public App()
    {
        // Centralized unhandled exception handlers
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _runningMutex = new Mutex(initiallyOwned: false, "TimePlayerRunning");


        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        // The language is fixed for the session: windows read their text once, when they open.
        var language = _serviceProvider.GetRequiredService<IConfigurationService>().CurrentConfig.Language;
        TimePlayer.Core.Localization.Loc.Use(language);
        // UI culture only (it steers which caption language is picked first); formatting stays as it is.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = TimePlayer.Core.Localization.Loc.Culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = TimePlayer.Core.Localization.Loc.Culture;

        // Keep the NVDA braille add-on up to date, but only for users who chose it (or already have it).
        // Everyone else is asked once, after the window opens.
        var useBrailleAddon = _serviceProvider.GetRequiredService<IConfigurationService>().CurrentConfig.Accessibility.UseBrailleAddon;
        if (useBrailleAddon == true || (useBrailleAddon is null && NvdaAddonInstaller.IsInstalled))
        {
            NvdaAddonInstaller.EnsureInstalled();
        }
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();

        _logger.LogInformation("TimePlayer {Version} starting up ({Mode}).", AppPaths.Version, AppPaths.IsPortable ? "portable" : "installed");

        // Check for and update yt-dlp asynchronously at startup in background
        var ytDlpManager = _serviceProvider.GetService<IYtDlpManager>();
        if (ytDlpManager != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Initiating startup check and update for yt-dlp...");
                    await ytDlpManager.EnsureInstalledOrUpdatedAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background yt-dlp check and update encountered an error.");
                }
            });
        }

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Build Configuration from appsettings.json
        var basePath = AppContext.BaseDirectory;
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        var configuration = configBuilder.Build();
        services.AddSingleton<IConfiguration>(configuration);

        // Bind AppConfig options
        services.Configure<AppConfig>(configuration);

        // Configure Centralized Logging
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddConfiguration(configuration.GetSection("Logging"));
            loggingBuilder.AddConsole();
            loggingBuilder.AddProvider(new Logging.FileLoggerProvider(AppPaths.LogsFolder));
        });

        // Register Core Services
        services.AddTimePlayerCore();

        // Register WPF-specific Services
        services.AddSingleton<WpfAccessibilityService>();
        services.AddSingleton<IAccessibilityService>(sp => sp.GetRequiredService<WpfAccessibilityService>());

        // Register ViewModels
        services.AddSingleton<MainViewModel>();

        // Register Views
        services.AddSingleton<MainWindow>();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI dispatcher exception: {Message}", e.Exception.Message);

        // The message box is read by the screen reader; an extra announcement would say it twice.
        MessageBox.Show(
            T("An unexpected error occurred:\n\n{0}\n\nThe application will attempt to continue running.", e.Exception.Message),
            T("TimePlayer Error"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Prevent crash
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _logger?.LogCritical(ex, "Fatal domain unhandled exception. IsTerminating: {IsTerminating}", e.IsTerminating);
        }
        else
        {
            _logger?.LogCritical("Fatal domain unhandled exception: {ExceptionObject}", e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogWarning(e.Exception, "Unobserved task exception: {Message}", e.Exception.Message);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("TimePlayer application shutting down with exit code {ExitCode}.", e.ApplicationExitCode);
        // Several singletons (mpv engine, view model) only support async disposal, which a plain
        // Dispose() rejects. Run it off the UI thread so awaits inside cannot deadlock, and don't
        // let a stuck mpv keep the app open.
        if (_serviceProvider is { } provider)
        {
            try
            {
                Task.Run(() => provider.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error while shutting down services.");
            }
        }
        base.OnExit(e);
    }
}
