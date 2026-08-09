using System.Windows;
using System.Windows.Controls;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class ExploreView : PageView
{
    private readonly List<AppPackage> _packages;
    private readonly List<AppPackage> _highlights;

    public ExploreView()
    {
        InitializeComponent();

        var catalog = CatalogService.Load();

        _packages = CatalogService.Section("explore");

        // The three logos on the banner are drawn from the same catalog, so
        // they reuse whatever icon the grid has already fetched.
        var byId = CatalogService.AllById();
        _highlights = catalog.Banner.HighlightIds
            .Where(byId.ContainsKey)
            .Select(id => CatalogService.ToPackage(byId[id]))
            .ToList();

        BannerTitle.Text = catalog.Banner.Title;
        DiscoverButton.Content = catalog.Banner.ButtonText;

        Cards.ItemsSource = _packages;
        HighlightIcons.ItemsSource = _highlights;
        Cards.ItemClick += package => Host.ShowDetail(package);

        Categories.ItemsSource = CatalogService.Categories();
    }

    public override Task LoadAsync()
    {
        Host.Icons.BeginLoad(_packages, Dispatcher);
        Host.Icons.BeginLoad(_highlights, Dispatcher);
        return Task.CompletedTask;
    }

    private void OnDiscoverClick(object sender, RoutedEventArgs e) =>
        Host.NavigateTo("featured");

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        var tile = TreeSearch.FindAncestor<Button>(e.OriginalSource as DependencyObject);

        if (tile?.DataContext is CatalogCategory category)
            Host.ShowCategory(category.Id);
    }

    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        // Same trick as the card grid: Source has been re-mapped to the
        // ItemsControl by now, so walk up from OriginalSource instead.
        var tile = TreeSearch.FindAncestor<Button>(e.OriginalSource as DependencyObject);

        if (tile?.DataContext is AppPackage package)
            Host.ShowDetail(package);
    }
}
