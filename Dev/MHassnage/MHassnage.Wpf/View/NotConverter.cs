using System.Globalization;
using System.Windows.Data;

namespace MHassnage.Wpf.View;

/// <summary>Inverse un booléen (IsBusy → IsEnabled).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
