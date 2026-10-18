using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimePlayer.Wpf.Views;

/// <summary>A one-line text question (playlist names). Enter accepts, Escape cancels.</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;

    private TextPromptWindow(string title, string prompt, string initial)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
        Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF5));

        _input = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(4) };
        AutomationProperties.SetName(_input, prompt.TrimEnd(':'));
        var label = new Label { Content = prompt, Target = _input, Foreground = Foreground, Padding = new Thickness(0) };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 4, 8, 4) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(label);
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <returns>The text entered, or null when cancelled.</returns>
    public static string? Ask(Window owner, string title, string prompt, string initial)
    {
        var window = new TextPromptWindow(title, prompt, initial) { Owner = owner };
        return window.ShowDialog() == true ? window._input.Text : null;
    }
}
