using System;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CxShell.Converters;

public class ConnectionStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isConnected)
        {
            return isConnected
                ? new SolidColorBrush(Color.Parse("#52C41A"))
                : new SolidColorBrush(Colors.Gray);
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
