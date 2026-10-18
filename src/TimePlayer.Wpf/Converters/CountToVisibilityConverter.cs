using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimePlayer.Wpf.Converters;

/// <summary>Visible when a count is above zero (a list with items), collapsed otherwise.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
