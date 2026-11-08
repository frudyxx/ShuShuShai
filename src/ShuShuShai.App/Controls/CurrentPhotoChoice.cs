using System.Globalization;
using System.Windows.Data;
using ShuShuShai.Core;

namespace ShuShuShai.App.Controls;

public sealed class CurrentPhotoChoice : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] switch
        {
            int stars => Equals(stars, values[1]),
            Decision decision => Equals(decision, values[2]),
            _ => false
        };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
