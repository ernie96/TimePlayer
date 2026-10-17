using System.Windows;
using AccessibleYoutubePlayer.Wpf.Services;
using AccessibleYoutubePlayer.Wpf.ViewModels;

namespace AccessibleYoutubePlayer.Wpf;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel, WpfAccessibilityService accessibilityService)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Register the live announcement element with the accessibility service
        accessibilityService.RegisterAnnouncementElement(LiveRegionText);
    }
}