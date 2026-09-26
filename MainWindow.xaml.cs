using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;
using AppCenter.Views;

namespace AppCenter;

public partial class MainWindow : Window, IShellHost
{
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>
    /// A stand-in for whatever operation is running, so the sidebar's bar can
    /// be painted by OperationService the way every row's is: its Id is set to
    /// the operation's key, and Paint does the rest.
    /// </summary>
    private readonly AppPackage _activity = new();

    /// <summary>Takes the strip down a few seconds after the last operation ends.</summary>
    private readonly DispatcherTimer _activityLinger;

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

    public IconService Icons { get; } = Warmup.Icons;

    public MainWindow()
    {
        InitializeComponent();

        Logo.Source = Warmup.Logo.Result;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += OnSearchDebounceElapsed;

        _activityLinger = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _activityLinger.Tick += (_, _) => ShowActivity();

        ActivityBar.DataContext = _activity;
        OperationService.Started += (_, _) => ShowActivity();
        OperationService.Progressed += (_, _) => ShowActivity();
        OperationService.Finished += (_, _) => ShowActivity();

        // Operations outlive the page that started them, so the machine is
        // re-read from here - whichever page happens to be showing - and the
        // badge follows whatever the read says.
        OperationService.Finished += (_, _) => RefreshUpdateBadge();
        MachineState.Changed += (_, _) => ShowBadge();
        AppUpdateService.Changed += (_, _) => ShowBadge();

        // winget commands this window did not start - left running by one
        // since closed, or typed into a terminal - are looked for whenever
        // the window comes back to the front and whenever the machine has
        // been read, which is also the first moment the names are known.
        Activated += (_, _) => OutsideOperations.Scan();
        MachineState.Changed += (_, _) => OutsideOperations.Scan();

        StateChanged += OnWindowStateChanged;
        Loaded += OnLoaded;
        ContentRendered += OnFirstFrame;
    }

    /// <summary>
    /// Back where it was closed, if that is still somewhere on screen.
    /// Otherwise the default size, which is generous for a large display; on
    /// a smaller or heavily scaled one it would open larger than the screen,
    /// so clamp it to the work area and centre it. All before the window is
    /// shown, so it appears in place rather than jumping there.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        KeepCornersSquare();

        var saved = SettingsService.Current.Window;

        if (!WindowPlacementService.Restore(this, saved))
        {
            var work = SystemParameters.WorkArea;

            Width = Math.Min(Width, work.Width);
            Height = Math.Min(Height, work.Height);
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }

        // Over whichever place it got: a window that was maximised over a
        // monitor since unplugged is better maximised over this one than
        // opened at a size chosen for that one.
        if (saved?.Maximized == true)
            WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        SettingsService.Current.Window = WindowPlacementService.Capture(this);
        SettingsService.Save();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookSearchClearButton();
        await NavigateAsync("explore");
    }

    /// <summary>
    /// The first frame is on screen. What starts here is everything that
    /// reaches outside the app - winget for the machine, GitHub for a newer
    /// App Center - so that two winget processes are not spawned in the same
    /// milliseconds the window is being laid out.
    /// </summary>
    private void OnFirstFrame(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstFrame;

        Warmup.Release();
        RefreshUpdateBadge();

        // App Center's own newer release, from GitHub - one request, unless
        // switched off in About.
        if (SettingsService.Current.CheckForUpdates)
            _ = AppUpdateService.CheckAsync();
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

        await GoToSectionAsync(button);
    }

    /// <summary>
    /// The section is still checked while an app's page, a category or a
    /// search is showing - it is where the trip started - so clicking it again
    /// raises no Checked. It should still go there: the entry under the pointer
    /// says Explore, and Explore is what it should show.
    /// </summary>
    private async void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } button)
            return;

        if (PageHost.Content is DetailView or SearchView
            || (PageHost.Content is CategoryView && _categoryReturn is not null))
            await GoToSectionAsync(button);
    }

    private async Task GoToSectionAsync(RadioButton button)
    {
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

        // Moving to a section means leaving whatever search was showing. The
        // clear starts the search debounce, which would navigate again to
        // the same place a moment later; this is that navigation.
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            _searchDebounce.Stop();
        }

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
            e.Handled = true;
            _searchDebounce.Stop();
            await RunSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
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

    /// <summary>
    /// Re-reads the machine. The badge, the Manage page and every card that
    /// says Installed all follow from the one read, through MachineState.
    /// </summary>
    public async void RefreshUpdateBadge()
    {
        try
        {
            await MachineState.RefreshAsync();
        }
        catch (Exception)
        {
            Nav.SetBadgeCount(NavManage, 0);
        }
    }

    /// <summary>
    /// What the Manage badge counts: winget's updates, plus App Center's own
    /// newer release on GitHub when winget is not already offering it.
    /// </summary>
    private void ShowBadge()
    {
        // The updates winget is sure of. A package whose version it cannot
        // read may be up to date already, and a pinned one is being left
        // alone; a number on the sidebar has to be one the user can clear.
        var count = MachineState.PendingUpdates.Count;

        var wingetOffers = MachineState.Upgrades
            .FirstOrDefault(p => string.Equals(p.Id, AppInfo.PackageId, StringComparison.OrdinalIgnoreCase))
            ?.AvailableVersion;

        if (AppUpdateService.OffersMoreThan(wingetOffers))
            count++;

        Nav.SetBadgeCount(NavManage, count);
    }

    // ---------------------------------------------------------------
    // Activity strip
    // ---------------------------------------------------------------

    /// <summary>
    /// Paints the strip from what the service is doing. Running: the newest
    /// operation's heading, how many more there are, and its bar. Just
    /// finished: how it went, held for a few seconds. Otherwise nothing.
    /// </summary>
    private void ShowActivity()
    {
        _activityLinger.Stop();

        if (OperationService.Current is { } current)
        {
            _activity.Id = current.Key;
            OperationService.Paint(_activity);

            var others = OperationService.RunningCount - 1;

            ActivityText.Text = current.Heading;
            ActivityDetail.Text = others > 0 ? $"and {others} more" : string.Empty;
            ActivityDetail.ToolTip = null;
            Activity.Visibility = Visibility.Visible;
            return;
        }

        _activity.Id = string.Empty;
        OperationService.Paint(_activity);

        // Nothing running. Say how the last one went, briefly - unless this is
        // the timer taking it down, in which case the last one has been said.
        if (OperationService.LastOutcome is { } outcome && Activity.Visibility == Visibility.Visible
            && ActivityText.Text != Outcome(outcome))
        {
            ActivityText.Text = Outcome(outcome);
            ActivityDetail.Text = outcome.Failed ? outcome.Summary : string.Empty;
            ActivityDetail.ToolTip = outcome.Summary;
            _activityLinger.Start();
            return;
        }

        Activity.Visibility = Visibility.Collapsed;
    }

    /// <summary>"Firefox installed", "7-Zip could not be updated".</summary>
    private static string Outcome(Operation outcome)
    {
        var name = outcome.Kind == OperationKind.UpdateAll ? "Updates" : outcome.PackageName;

        var verb = (outcome.Kind, outcome.Failed) switch
        {
            (OperationKind.Install, false) => "installed",
            (OperationKind.Install, true) => "could not be installed",
            (OperationKind.Uninstall, false) => "removed",
            (OperationKind.Uninstall, true) => "could not be removed",
            (OperationKind.UpdateAll, false) => "finished",
            (OperationKind.UpdateAll, true) => "finished with failures",
            (OperationKind.Reinstall, false) => "reinstalled",
            (OperationKind.Reinstall, true) => "could not be reinstalled",
            (OperationKind.Skip, false) => "will be skipped",
            (OperationKind.Skip, true) => "could not be skipped",
            (OperationKind.Resume, false) => "updates resumed",
            (OperationKind.Resume, true) => "updates could not be resumed",
            (_, false) => "updated",
            (_, true) => "could not be updated",
        };

        return $"{name} {verb}";
    }

    // ---------------------------------------------------------------
    // Keyboard and mouse
    // ---------------------------------------------------------------

    /// <summary>
    /// Ctrl+F and Ctrl+K go to the search box. Escape, Backspace, Alt+Left and
    /// the mouse's back button step back from a page that has somewhere to
    /// step back to - never from a text box, whose own keys they are.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key is Key.F or Key.K)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        var inText = Keyboard.FocusedElement is TextBoxBase;
        var altLeft = e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        // With a screenshot up, "back" means putting it away - the page
        // under it is still the page, and it must not change behind the
        // picture. The lightbox takes Escape and the arrows itself.
        if (Lightbox.IsOpen)
        {
            if (altLeft || (!inText && e.Key is Key.Escape or Key.Back))
            {
                Lightbox.Close();
                e.Handled = true;
            }

            return;
        }

        if (altLeft || (!inText && e.Key is Key.Escape or Key.Back))
            e.Handled = TryGoBack();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        if (e.ChangedButton != MouseButton.XButton1)
            return;

        if (Lightbox.IsOpen)
        {
            Lightbox.Close();
            e.Handled = true;
            return;
        }

        e.Handled = TryGoBack();
    }

    /// <summary>
    /// Steps back if the page showing came from somewhere: an app's page, a
    /// category opened from Explore, or a search. A sidebar section is where
    /// the trip started, and there is nothing behind it.
    /// </summary>
    private bool TryGoBack()
    {
        switch (PageHost.Content)
        {
            case DetailView:
            case CategoryView when _categoryReturn is not null:
                GoBack();
                return true;

            case SearchView:
                // Clearing the box is what leaves a search; the debounce
                // takes it from there.
                SearchBox.Clear();
                return true;

            default:
                return false;
        }
    }

    // ---------------------------------------------------------------
    // Confirmation
    // ---------------------------------------------------------------

    public bool ConfirmAction(string title, string message, string confirmLabel) =>
        ConfirmDialog.Show(this, title, message, confirmLabel);

    // ---------------------------------------------------------------
    // Lightbox
    // ---------------------------------------------------------------

    public void ShowScreenshots(IReadOnlyList<Screenshot> screenshots, int index)
    {
        Lightbox.Icons = Icons;
        Lightbox.Show(screenshots, index);
    }

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

    /// <summary>
    /// Windows 11 rounds the corners of any window it draws the frame for,
    /// which the glass pixel in the chrome asks it to. The window was drawn
    /// square before that and stays square.
    /// </summary>
    private void KeepCornersSquare()
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_DONOTROUND = 1;

        var preference = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(
            new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
