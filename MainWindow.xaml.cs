using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;
using AppCenter.Views;

namespace AppCenter;

public partial class MainWindow : Window, IShellHost
{
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _badgeCts;

    /// <summary>The sidebar entry to fall back to when the search box is cleared.</summary>
    private string _currentDestination = "explore";

    /// <summary>
    /// The category currently being browsed, if any. The shell keeps no history
    /// stack, and a category is the one place that needs remembering: it has no
    /// sidebar entry, so without this a detail page opened from a category
    /// would send Back to Explore and lose the user's place.
    /// </summary>
    private string? _categoryReturn;

    /// <summary>
    /// How far down each page was when it was left, by <see cref="PageView.ScrollKey"/>.
    /// Pages are rebuilt on every navigation, so this is the only thing that
    /// carries a reading position across one.
    /// </summary>
    private readonly Dictionary<string, double> _scrollOffsets = [];

    public IconService Icons { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += OnSearchDebounceElapsed;

        // Operations outlive the page that started them, so the badge is
        // refreshed from here - whichever page happens to be showing.
        OperationService.Finished += (_, _) => RefreshUpdateBadge();

        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// The default size is generous for a large display; on a smaller or
    /// heavily scaled one it would open larger than the screen, so clamp it
    /// to the work area and re-centre before the window is shown.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var work = SystemParameters.WorkArea;

        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookSearchClearButton();
        await NavigateAsync("explore");
        RefreshUpdateBadge();
    }

    /// <summary>
    /// The clear button lives inside the TextBox template, so it can only be
    /// reached once the template has been applied.
    /// </summary>
    private void HookSearchClearButton()
    {
        SearchBox.ApplyTemplate();

        if (SearchBox.Template.FindName("PART_ClearButton", SearchBox) is Button clear)
            clear.Click += (_, _) =>
            {
                SearchBox.Clear();
                SearchBox.Focus();
            };
    }

    // ---------------------------------------------------------------
    // Navigation
    // ---------------------------------------------------------------

    private async void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton button)
            return;

        var destination = button.Name switch
        {
            nameof(NavExplore) => "explore",
            nameof(NavFeatured) => "featured",
            nameof(NavProductivity) => "productivity",
            nameof(NavDevelopment) => "development",
            nameof(NavGames) => "games",
            nameof(NavManage) => "manage",
            nameof(NavAbout) => "about",
            _ => "explore",
        };

        // Moving to a section means leaving whatever search was showing.
        if (SearchBox.Text.Length > 0)
            SearchBox.Clear();

        await NavigateAsync(destination);
    }

    public void NavigateTo(string destination)
    {
        var button = destination switch
        {
            "explore" => NavExplore,
            "featured" => NavFeatured,
            "productivity" => NavProductivity,
            "development" => NavDevelopment,
            "games" => NavGames,
            "manage" => NavManage,
            "about" => NavAbout,
            _ => NavExplore,
        };

        button.IsChecked = true;
    }

    private async Task NavigateAsync(string destination, bool resume = false)
    {
        _currentDestination = destination;

        // Reaching a section by any route ends the trip a category started.
        _categoryReturn = null;

        PageView page = destination switch
        {
            "featured" => new CategoryView("featured", "Featured"),
            "productivity" => new CategoryView("productivity", "Productivity"),
            "development" => new CategoryView("development", "Development"),
            "games" => new GamesView(),
            "manage" => new ManageView(),
            "about" => new AboutView(),
            _ => new ExploreView(),
        };

        page.ScrollKey = destination;

        await ShowPageAsync(page, resume);
    }

    /// <summary>
    /// Puts a page in the content area and loads it. <paramref name="resume"/>
    /// is for stepping back: it puts the page back where the user left it,
    /// whereas arriving somewhere fresh - from the sidebar, or by opening an app
    /// - should start at the top.
    /// </summary>
    private async Task ShowPageAsync(PageView page, bool resume = false)
    {
        RememberScroll();

        page.Host = this;
        PageHost.Content = page;

        try
        {
            await page.LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was replaced while it was still loading.
        }

        if (resume)
            RestoreScroll(page);
    }

    /// <summary>Files the outgoing page's position under whatever it is.</summary>
    private void RememberScroll()
    {
        if (PageHost.Content is PageView { ScrollKey: { } key } page && page.Scroller is { } scroller)
            _scrollOffsets[key] = scroller.VerticalOffset;
    }

    private void RestoreScroll(PageView page)
    {
        if (page.ScrollKey is not { } key
            || !_scrollOffsets.TryGetValue(key, out var offset)
            || offset <= 0
            || page.Scroller is not { } scroller)
            return;

        // Measured first: a list the page filled in LoadAsync has no scrollable
        // height yet, and scrolling past the extent would just clamp to the top.
        scroller.UpdateLayout();
        scroller.ScrollToVerticalOffset(offset);
    }

    public async void ShowDetail(AppPackage package) =>
        await ShowPageAsync(new DetailView(package));

    public async void ShowCategory(string categoryId) => await ShowCategoryAsync(categoryId);

    private async Task ShowCategoryAsync(string categoryId, bool resume = false)
    {
        if (CatalogService.CategoryById(categoryId) is not { } category)
            return;

        _categoryReturn = categoryId;

        await ShowPageAsync(
            new CategoryView(category) { ScrollKey = $"category:{categoryId}" },
            resume);
    }

    public async void GoBack()
    {
        // Every branch here is a step back, so each one resumes where the page
        // was left rather than starting it again from the top.
        if (SearchBox.Text.Trim().Length >= 2)
        {
            var query = SearchBox.Text.Trim();
            await ShowPageAsync(new SearchView(query) { ScrollKey = $"search:{query}" }, resume: true);
            return;
        }

        // A detail page opened from a category goes back to that category. The
        // category itself falls through to the sidebar section that offered it,
        // which is also what clears the trip.
        if (PageHost.Content is DetailView && _categoryReturn is { } category)
        {
            await ShowCategoryAsync(category, resume: true);
            return;
        }

        await NavigateAsync(_currentDestination, resume: true);
    }

    // ---------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _searchDebounce.Stop();
            await RunSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
        }
    }

    private async void OnSearchDebounceElapsed(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();

        if (query.Length == 0)
        {
            // Back to whichever section the sidebar has selected - a return
            // rather than an arrival, so it resumes where that page was left.
            await NavigateAsync(_currentDestination, resume: true);
            return;
        }

        if (query.Length < 2)
            return;

        // A new set of results always starts at the top; the key is only there
        // so returning from an app's page can come back to the same spot.
        await ShowPageAsync(new SearchView(query) { ScrollKey = $"search:{query}" });
    }

    // ---------------------------------------------------------------
    // Update badge
    // ---------------------------------------------------------------

    public async void RefreshUpdateBadge()
    {
        _badgeCts?.Cancel();
        _badgeCts = new CancellationTokenSource();
        var token = _badgeCts.Token;

        try
        {
            var upgrades = await WingetService.ListUpgradesAsync(token);
            if (!token.IsCancellationRequested)
                Nav.SetBadgeCount(NavManage, upgrades.Count);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception)
        {
            Nav.SetBadgeCount(NavManage, 0);
        }
    }

    // ---------------------------------------------------------------
    // Confirmation
    // ---------------------------------------------------------------

    public bool ConfirmAction(string title, string message, string confirmLabel) =>
        ConfirmDialog.Show(this, title, message, confirmLabel);

    // ---------------------------------------------------------------
    // Window chrome
    // ---------------------------------------------------------------

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximise();
            return;
        }

        // Dragging a maximised window restores it under the cursor, the way
        // a normal Windows title bar behaves.
        if (WindowState == WindowState.Maximized)
        {
            var restoreWidth = RestoreBounds.Width;
            var local = e.GetPosition(this);
            var ratio = ActualWidth > 0 ? local.X / ActualWidth : 0.5;
            var onScreen = PointToScreen(local);

            WindowState = WindowState.Normal;
            Left = onScreen.X - restoreWidth * ratio;
            Top = onScreen.Y - local.Y;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already released; nothing to drag.
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        MaximiseButton.Tag = WindowState == WindowState.Maximized
            ? FindResource("IconRestore")
            : FindResource("IconMaximize");

        MaximiseButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
    }

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnMinimiseClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e) => ToggleMaximise();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
