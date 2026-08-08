using System.Collections;
using System.Windows;
using System.Windows.Controls;
using AppCenter.Models;

namespace AppCenter.Controls;

/// <summary>
/// The two-column card grid shared by Explore, the category pages, the
/// Games "Top Rated" section and search results.
/// </summary>
public partial class AppGrid : UserControl
{
    public AppGrid()
    {
        InitializeComponent();
    }

    /// <summary>Raised when a card is clicked, with the package it represents.</summary>
    public event Action<AppPackage>? ItemClick;

    public IEnumerable? ItemsSource
    {
        get => Items.ItemsSource;
        set => Items.ItemsSource = value;
    }

    public string EmptyText
    {
        get => EmptyMessage.Text;
        set => EmptyMessage.Text = value;
    }

    public void ShowEmpty(bool show) =>
        EmptyMessage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        // e.Source is the ItemsControl by the time the Click gets here - WPF
        // re-maps it on the way out of the item template - so trace the
        // clicked card back up from OriginalSource instead.
        var card = TreeSearch.FindAncestor<Button>(e.OriginalSource as DependencyObject);

        if (card?.DataContext is AppPackage package)
            ItemClick?.Invoke(package);
    }
}
