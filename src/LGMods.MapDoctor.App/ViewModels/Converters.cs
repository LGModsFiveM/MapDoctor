using System.Globalization;
using System.Windows.Data;

namespace LGMods.MapDoctor.App.ViewModels;

// Segoe icon glyphs: chevron up when open, down when closed.
public sealed class ChevronConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
