using System.Windows;
using System.Windows.Input;
using TimePlayer.Wpf.ViewModels;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// A real modal dialog, so screen readers announce it as one and Tab cannot wander into the window behind.
/// Settings bind straight to the view model; closing saves them.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public bool PlayTestPatternRequested { get; private set; }

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => FirstSetting.Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            MessageBox.Show(this,
                "Tab and Shift+Tab move between settings. Space toggles a check box. " +
                "Alt plus the underlined letter jumps to a setting. Escape or Enter closes and saves.",
                "Help: Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PlayTestPattern_Click(object sender, RoutedEventArgs e)
    {
        PlayTestPatternRequested = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _ = _viewModel.SaveSettingsAsync();
    }
}
