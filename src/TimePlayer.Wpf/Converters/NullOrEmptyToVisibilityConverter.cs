using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimePlayer.Wpf.Converters;

/// <summary>
/// Converts non-empty string to Visibility.Visible, empty/null to Visibility.Collapsed.
/// </summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
