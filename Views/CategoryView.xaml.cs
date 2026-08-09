using System.Windows;
using System.Windows.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

/// <summary>
/// The Featured / Productivity / Development pages. They differ only in
/// which catalog section they read and the heading they show.
/// </summary>
public partial class CategoryView : PageView
{
    private readonly List<AppPackage> _packages;

    /// <summary>A sidebar section: Featured, Productivity, Development.</summary>
    public CategoryView(string section, string title)
        : this(title, CatalogService.Section(section), false)
    {
    }

    /// <summary>
    /// A category picked from Explore. It gets a Back button the sidebar pages
    /// have no use for - the sidebar is its own way back, but nothing in the
    /// chrome says where a category came from.
    /// </summary>
    public CategoryView(CatalogCategory category)
        : this(category.Name, CatalogService.Category(category), true)
    {
    }

    private CategoryView(string title, List<AppPackage> packages, bool showBack)
    {
        InitializeComponent();

        Title.Text = title;
        _packages = packages;

        BackButton.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;

        Cards.ItemsSource = _packages;
        Cards.ItemClick += package => Host.ShowDetail(package);

        if (_packages.Count == 0)
        {
            Cards.EmptyText = "No apps are listed in this category yet.";
            Cards.ShowEmpty(true);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Host.GoBack();

    public override Task LoadAsync()
    {
        Host.Icons.BeginLoad(_packages, Dispatcher);
        return Task.CompletedTask;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        // Index 0 is "Relevance", which means catalog order - the order the
        // list already arrived in, so there is nothing to re-sort.
        IEnumerable<AppPackage> sorted = SortBox.SelectedIndex switch
        {
            1 => _packages.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => _packages.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            3 => _packages.OrderBy(p => p.Publisher, StringComparer.CurrentCultureIgnoreCase)
                          .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => _packages,
        };

        Cards.ItemsSource = sorted.ToList();
    }
}
