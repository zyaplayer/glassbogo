using System.Globalization;
using System.Windows.Data;

namespace GlassBogo.App;

internal sealed class TaskColorConverter(AppContext ctx) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var color = ctx.Db.Current.TaskCategories.FirstOrDefault(c => c.Id == value as string)?.Color ?? "#8995A5";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
