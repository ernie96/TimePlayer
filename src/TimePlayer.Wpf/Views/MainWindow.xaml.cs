using System.Windows;
using TimePlayer.Core.Accessibility;
using TimePlayer.Wpf.Accessibility;
using TimePlayer.Wpf.ViewModels;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IAccessibilityService _accessibility;

    public MainWindow(MainViewModel viewModel, IAccessibilityService accessibility)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        DataContext = _viewModel;

        if (_accessibility is WpfAccessibilityService wpfAccessibility)
        {
            wpfAccessibility.RegisterLiveRegion(LiveRegionStatusText);
        }

        Loaded += MainWindow_Loaded;
        MpvHost.HandleCreated += MpvHost_HandleCreated;
    }

    private void MpvHost_HandleCreated(object? sender, EventArgs e)
    {
        if (MpvHost.WindowHandle != IntPtr.Zero)
        {
            _ = _viewModel.InitializePlayerEngineAsync(MpvHost.WindowHandle);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (MpvHost.WindowHandle != IntPtr.Zero && !_viewModel.IsEngineInitialized)
        {
            _ = _viewModel.InitializePlayerEngineAsync(MpvHost.WindowHandle);
        }

        // Focus the search box on startup for smooth keyboard/screen reader workflow
        SearchBox.Focus();
        _accessibility.Announce("Welcome to TimePlayer. Focus is in the search box. Type keywords or paste a link and press Enter.", AnnouncementPriority.Medium);
    }
}
