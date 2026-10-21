using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MHassnage.Wpf.View;

/// <summary>Inverse un booléen (IsBusy → IsEnabled).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Ratio 0..1 → largeur en étoiles : « fill » = Ratio*, « rest » = (1-Ratio)*.</summary>
public sealed class RatioStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double ratio = Math.Clamp(value is double d ? d : 0, 0.001, 1);
        return new GridLength("rest".Equals(parameter) ? 1 - ratio + 0.001 : ratio, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
