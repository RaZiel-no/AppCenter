using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AppCenter.Controls;

/// <summary>
/// Resolves an icon key from catalog.json into the geometry it names, so a new
/// category can arrive as data without a code change. An unknown key draws the
/// generic grid glyph rather than nothing at all - a tile with no icon looks
/// broken, whereas a placeholder just looks unfinished.
/// </summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && key.Length > 0
            && Application.Current?.TryFindResource(key) is Geometry named)
            return named;

        return Application.Current?.TryFindResource("IconGrid") as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when its bound string is empty.
///
/// A DataTrigger with Value="" is unreliable here - the trigger compares the
/// bound value against the literal without conversion, so the empty case
/// silently fails to match and the badge shows on every sidebar row.
/// </summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
