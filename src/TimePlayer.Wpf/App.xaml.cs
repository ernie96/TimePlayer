using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Services;
using TimePlayer.Wpf.Accessibility;
using TimePlayer.Wpf.ViewModels;
using TimePlayer.Wpf.Views;

namespace TimePlayer.Wpf;

/// <summary>
/// Application entry point and host lifecycle manager.
/// Configures Dependency Injection, centralized logging, and centralized error handling.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

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

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();

        _logger.LogInformation("TimePlayer application starting up.");

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

        var accessibility = _serviceProvider?.GetService<IAccessibilityService>();
        accessibility?.Announce("An error occurred. The application recovered.", AnnouncementPriority.High);

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will attempt to continue running.",
            "TimePlayer Error",
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
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
