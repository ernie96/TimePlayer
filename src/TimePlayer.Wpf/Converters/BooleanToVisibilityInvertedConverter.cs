using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimePlayer.Wpf.Converters;

/// <summary>
/// Converts a boolean to Visibility, inverting true to Collapsed and false to Visible.
/// </summary>
public sealed class BooleanToVisibilityInvertedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }

        // Counts: hide when non-zero, so an "empty list" prompt disappears once items arrive.
        if (value is int count)
        {
            return count != 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility != Visibility.Visible;
        }

        return false;
    }
}
