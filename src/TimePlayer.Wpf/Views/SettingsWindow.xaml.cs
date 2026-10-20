using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimePlayer.Wpf.ViewModels;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// A real modal dialog, so screen readers announce it as one and Tab cannot wander into the window behind.
/// Settings bind straight to the view model; closing saves them.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public bool PlayTestPatternRequested { get; private set; }
    public bool AppUpdateCheckRequested { get; private set; }
    public bool BugReportRequested { get; private set; }
    public bool FeatureRequestRequested { get; private set; }

    private void SuggestFeature_Click(object sender, RoutedEventArgs e)
    {
        FeatureRequestRequested = true;
        Close();
    }

    private async void ImportSignIn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("Import a sign-in file"),
            Filter = T("Sign-in files (*.txt)|*.txt|All files|*.*"),
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (dialog.ShowDialog(this) == true)
            await _viewModel.ImportSignInFileAsync(dialog.FileName);
    }

    private void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        BugReportRequested = true;
        Close();
    }

    private void CheckAppUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateCheckRequested = true;
        Close();
    }

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
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            var page = (Pages.SelectedItem as TabItem)?.Header as string ?? Title;
            Accessibility.WpfAccessibilityService.SpeakIn(HelpLiveRegion, Views.QuickHelp.Settings(page));
            e.Handled = true;
        }
        else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MessageBox.Show(this,
                T("Settings has eight pages: Playback, Sound, Speech and braille, Library, SponsorBlock and dislikes, Links and browser, YouTube account, and Updates and help. ") +
                T("Control+Tab and Control+Shift+Tab move between pages; on the page names, Left and Right do too.\n") +
                T("Tab and Shift+Tab move between settings. Space toggles a check box; Up and Down change a choice. ") +
                T("Alt plus the underlined letter jumps to a setting on the current page.\n") +
                T("Escape closes and saves."),
                T("Help: Settings"), MessageBoxButton.OK, MessageBoxImage.Information);
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
