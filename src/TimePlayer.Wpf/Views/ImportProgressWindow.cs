using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.InnerTube;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// Shown while TimePlayer reads your lists from YouTube after you sign in: which of the five steps it is on,
/// how many it has found so far, and Cancel. Each new step is said aloud; the running counts are only shown,
/// so F1 or reading the window gives them without a stream of speech.
/// </summary>
public sealed class ImportProgressWindow : Window, IProgress<AccountImportProgress>
{
    private readonly IAccessibilityService _accessibility;
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;
    private bool _closed;

    public CancellationToken Token => _cts.Token;

    public ImportProgressWindow(IAccessibilityService accessibility)
    {
        _accessibility = accessibility;
        Title = T("Importing from YouTube");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));

        _status = new TextBlock { Text = T("Starting..."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        _bar = new ProgressBar { Minimum = 0, Maximum = InnerTubeBackendProvider.AccountImportSteps, Height = 18, Margin = new Thickness(0, 0, 0, 14) };
        AutomationProperties.SetName(_bar, T("Import progress"));
        _cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 90, Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Right };
        _cancel.Click += (_, _) => CancelImport();

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_status);
        panel.Children.Add(_bar);
        panel.Children.Add(_cancel);
        Content = panel;

        Loaded += (_, _) => _cancel.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F1)
            {
                _accessibility.Announce(_status.Text + " " + T("Escape or Cancel stops the import; nothing is imported then."), AnnouncementPriority.High);
                e.Handled = true;
            }
        };
        // Closing the window is the same as Cancel, until the import is done.
        Closing += (_, _) =>
        {
            if (!_finished)
                _cts.Cancel();
        };
        Closed += (_, _) => _closed = true;
    }

    public void Report(AccountImportProgress value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(value));
            return;
        }
        if (_closed || _cts.IsCancellationRequested)
            return;
        _status.Text = value.Text;
        _bar.Value = value.NewStep ? value.Step - 1 : Math.Max(_bar.Value, value.Step - 1);
        AutomationProperties.SetHelpText(_bar, T("Step {0} of {1}", value.Step, value.Steps));
        if (value.NewStep)
            _accessibility.Announce(T("Step {0} of {1}: {2}", value.Step, value.Steps, value.Text), AnnouncementPriority.Medium);
    }

    /// <summary>Closes the window once the import has finished (or was canceled).</summary>
    public void Finish()
    {
        _finished = true;
        if (!_closed)
            Close();
    }

    private void CancelImport()
    {
        if (_cts.IsCancellationRequested)
            return;
        _cts.Cancel();
        _cancel.IsEnabled = false;
        _status.Text = T("Stopping...");
    }
}
