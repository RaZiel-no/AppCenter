using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AppCenter.Controls;

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
