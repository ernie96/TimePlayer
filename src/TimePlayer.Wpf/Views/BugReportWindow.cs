using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Community;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// Report a problem (Control+Shift+B): what went wrong, an optional way to answer, and optionally the log.
/// Suggest a feature (Control+Shift+F): the same form for an idea, without the log.
/// Both go privately to TimePlayer's developer.
/// </summary>
public sealed class BugReportWindow : Window
{
    private readonly IBugReportService _reports;
    private readonly IAccessibilityService _accessibility;
    private readonly TextBox _description;
    private readonly TextBox _contact;
    private readonly CheckBox _includeLog;
    private readonly Button _send;
    private readonly TextBlock _status;
    private readonly bool _feature;
    private CancellationTokenSource? _sending;

    private BugReportWindow(IBugReportService reports, IAccessibilityService accessibility, bool feature)
    {
        _reports = reports;
        _accessibility = accessibility;
        _feature = feature;
        Title = feature ? T("Suggest a feature") : T("Report a problem");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));

        _description = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, Padding = new Thickness(4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = BugReportService.MaxDescriptionLength,
            Margin = new Thickness(0, 4, 0, 12)
        };
        AutomationProperties.SetName(_description, feature
            ? T("What would you like TimePlayer to do? How would it help you?")
            : T("What went wrong? What were you doing when it happened?"));
        _contact = new TextBox { Padding = new Thickness(4), MaxLength = BugReportService.MaxContactLength, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(_contact, T("Your email or name, if you would like an answer (optional)"));
        _includeLog = new CheckBox { Content = T("_Include TimePlayer's log (recommended: it shows what went wrong)"), IsChecked = true, Foreground = Foreground, Margin = new Thickness(0, 0, 0, 6) };

        var viewLog = new Button { Content = T("_View the log"), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        viewLog.Click += (_, _) => ViewLog();

        var note = new TextBlock
        {
            Text = feature
                ? T("Suggestions go privately to TimePlayer's developer. Enter adds a new line; Tab moves on to Send.")
                : T("The log lists what TimePlayer did, such as the videos played and any errors. Your Windows user name is taken out. Reports go privately to TimePlayer's developer. Enter adds a new line; Tab moves on to Send."),
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)), Margin = new Thickness(0, 0, 0, 12)
        };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _send = new Button { Content = T("_Send"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
        _send.Click += async (_, _) => await SendAsync();
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, MinWidth = 90, Padding = new Thickness(8, 4, 8, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_send);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new Label
        {
            Content = feature ? T("_What would you like TimePlayer to do? How would it help you?") : T("_What went wrong? What were you doing when it happened?"),
            Target = _description, Foreground = Foreground, Padding = new Thickness(0)
        });
        panel.Children.Add(_description);
        panel.Children.Add(new Label { Content = T("Your _email or name, if you would like an answer (optional):"), Target = _contact, Foreground = Foreground, Padding = new Thickness(0) });
        panel.Children.Add(_contact);
        if (!feature)
        {
            panel.Children.Add(_includeLog);
            panel.Children.Add(viewLog);
        }
        panel.Children.Add(note);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _description.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F1)
            {
                Accessibility.WpfAccessibilityService.SpeakIn(_status, QuickHelp.ReportForm);
                e.Handled = true;
            }
        };
        Closing += (_, _) => _sending?.Cancel();
    }

    public static void Show(Window owner, IBugReportService reports, IAccessibilityService accessibility, bool feature = false) =>
        new BugReportWindow(reports, accessibility, feature) { Owner = owner }.ShowDialog();

    private void ViewLog()
    {
        try
        {
            var file = Path.Combine(Path.GetTempPath(), "TimePlayer-log-for-report.txt");
            var log = _reports.ReadRecentLog();
            File.WriteAllText(file, log.Length == 0 ? T("There is no log yet.") : log);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, T("Could not open the log: {0}", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(_description.Text))
        {
            MessageBox.Show(this, _feature ? T("Please describe your idea.") : T("Please describe the problem."), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            _description.Focus();
            return;
        }

        _send.IsEnabled = false;
        _status.Text = _feature ? T("Sending your suggestion...") : T("Sending the report...");
        _accessibility.Announce(_status.Text, AnnouncementPriority.High);
        _sending = new CancellationTokenSource();
        try
        {
            var report = new BugReport
            {
                Kind = _feature ? BugReport.Feature : BugReport.Problem,
                Description = _description.Text,
                Contact = _contact.Text,
                Log = !_feature && _includeLog.IsChecked == true ? await Task.Run(_reports.ReadRecentLog) : null
            };
            var result = await _reports.SendAsync(report, _sending.Token);
            if (result.IsSuccess)
            {
                MessageBox.Show(this, _feature
                    ? T("Thank you. Your suggestion was sent as number {0}.", result.Value)
                    : T("Thank you. Your report was sent as number {0}.", result.Value), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }
            // Don't lose what they typed.
            try { Clipboard.SetText(_description.Text); } catch (System.Runtime.InteropServices.ExternalException) { }
            _status.Text = string.Empty;
            MessageBox.Show(this, (result.Error?.Message ?? (_feature ? T("Your suggestion could not be sent.") : T("The report could not be sent."))) + "\n\n" + T("What you wrote is copied to the clipboard, so it isn't lost."),
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            // Closed while sending.
        }
        finally
        {
            _send.IsEnabled = true;
            _sending?.Dispose();
            _sending = null;
        }
    }
}
