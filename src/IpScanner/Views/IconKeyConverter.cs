using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IpScanner.Views;

/// <summary>Looks up a Geometry resource (Resources/Icons.xaml) by its string key -
/// keeps the view models free of WPF media types while still testable as plain
/// strings.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key ? Application.Current.TryFindResource(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
