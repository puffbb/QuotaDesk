using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QuotaDesk.Controls;

/// <summary>null → Collapsed，其余 → Visible。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
