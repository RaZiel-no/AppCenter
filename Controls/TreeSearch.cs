using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AppCenter.Controls;

public static class TreeSearch
{
    /// <summary>
    /// Walks up from an element to the first ancestor of type
    /// <typeparamref name="T"/>, crossing control-template boundaries.
    ///
    /// This is how a click inside an ItemsControl is traced back to the
    /// button that raised it. RoutedEventArgs.Source cannot be used for
    /// that: as a Click bubbles out of an item template WPF re-maps Source
    /// to the ItemsControl itself, so a test like `e.Source is Button` never
    /// matches. OriginalSource is left alone, but for a real mouse click it
    /// points at the innermost hit element inside the button's template -
    /// hence the walk.
    /// </summary>
    public static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;

            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }
}
