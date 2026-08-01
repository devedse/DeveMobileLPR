using System.Globalization;

namespace DeveMobileLPR.App.UI;

public sealed class MillisecondsValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double milliseconds
            ? $"{milliseconds:0.0} ms"
            : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
