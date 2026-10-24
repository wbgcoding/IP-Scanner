using System.Globalization;
using System.Windows.Data;
using IpScanner.Core.Scanner;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

/// <summary>Turns a <see cref="PortResult"/> into its state or reason text; the converter
/// parameter picks which ("state" or "reason").</summary>
public sealed class PortTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PortResult r) return string.Empty;
        return parameter as string == "reason" ? PortDisplay.ReasonText(r.Reason) : PortDisplay.StateText(r.State);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
