using System.Windows;

namespace AppCenter.Controls;

/// <summary>
/// Attached properties for the sidebar. BadgeText drives the green pill on
/// the Manage entry; it is attached rather than templated so the one NavItem
/// style can serve every sidebar row.
///
/// It is a string rather than an int on purpose: a DataTrigger compares its
/// Value against the bound value without converting, so an int-typed
/// property never matches the literal "0" written in XAML and the badge
/// would show on every row.
/// </summary>
public static class Nav
{
    public static readonly DependencyProperty BadgeTextProperty =
        DependencyProperty.RegisterAttached(
            "BadgeText",
            typeof(string),
            typeof(Nav),
            new PropertyMetadata(string.Empty));

    public static string GetBadgeText(DependencyObject element) =>
        (string)element.GetValue(BadgeTextProperty);

    public static void SetBadgeText(DependencyObject element, string value) =>
        element.SetValue(BadgeTextProperty, value);

    /// <summary>Blank hides the badge; any other text shows it.</summary>
    public static void SetBadgeCount(DependencyObject element, int count) =>
        SetBadgeText(element, count > 0 ? count.ToString() : string.Empty);
}
