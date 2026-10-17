using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TimePlayer.Core.Accessibility;
using TimePlayer.Wpf.Accessibility;
using TimePlayer.Wpf.ViewModels;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// Accessible MainWindow code-behind managing focus restoration, keyboard shortcuts,
/// embedded mpv surface creation, and screen reader live-region registration.
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
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        MpvHost.HandleCreated += MpvHost_HandleCreated;

        // Subscribe to focus restoration requests from ViewModel
        _viewModel.RequestFocusSearch += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });

        _viewModel.RequestFocusResults += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ResultsListBox.Items.Count > 0)
            {
                ResultsListBox.Focus();
                if (ResultsListBox.SelectedItem == null)
                {
                    ResultsListBox.SelectedIndex = 0;
                }
            }
            else
            {
                SearchBox.Focus();
            }
        });

        _viewModel.RequestFocusPlayer += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            BackToSearchButton.Focus();
        });
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

        // Set initial focus to search input for instant screen reader workflow
        SearchBox.Focus();
        _accessibility.Announce("Welcome to TimePlayer. Focus is in the search box. Enter search keywords or paste a link and press Enter.", AnnouncementPriority.Medium);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        bool isTextBox = focused is TextBox;
        bool isSlider = focused is Slider;
        bool isList = focused is ListBox or ListBoxItem;

        // 1. Escape: Close settings dialog if open, or close player dialog and return to search results
        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsSettingsDialogOpen)
            {
                _viewModel.CloseSettings();
                e.Handled = true;
                return;
            }

            if (_viewModel.IsPlayerDialogOpen)
            {
                _viewModel.ClosePlayerDialog();
                e.Handled = true;
                return;
            }
        }

        // 2. Ctrl+E or Ctrl+F: Focus search box
        if ((e.Key == Key.E || e.Key == Key.F) && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // 3. F2: Toggle Settings & Diagnostics
        if (e.Key == Key.F2)
        {
            if (_viewModel.IsSettingsDialogOpen)
            {
                _viewModel.CloseSettings();
            }
            else
            {
                _viewModel.OpenSettings();
            }
            e.Handled = true;
            return;
        }

        // 4. Space / K: Toggle Play/Pause when not editing text
        if (!isTextBox && (e.Key == Key.Space || e.Key == Key.K))
        {
            _ = _viewModel.TogglePlayPauseAsync();
            e.Handled = true;
            return;
        }

        // 5. M: Toggle Mute when not editing text
        if (!isTextBox && e.Key == Key.M)
        {
            _ = _viewModel.ToggleMuteAsync();
            e.Handled = true;
            return;
        }

        // 6. Left / Right: Seek backward / forward 5s when not in a text box or slider
        if (!isTextBox && !isSlider)
        {
            if (e.Key == Key.Left)
            {
                _ = _viewModel.SeekSmallBackwardAsync();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right)
            {
                _ = _viewModel.SeekSmallForwardAsync();
                e.Handled = true;
                return;
            }
        }

        // 7. Up / Down: Volume up / down when not editing text or navigating lists / sliders
        if (!isTextBox && !isSlider && !isList)
        {
            if (e.Key == Key.Up)
            {
                _ = _viewModel.VolumeUpAsync();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                _ = _viewModel.VolumeDownAsync();
                e.Handled = true;
                return;
            }
        }
    }
}
