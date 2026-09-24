using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

/// <summary>
/// The Manage page: what is installed and what can be updated. What the lists
/// hold and every sentence built from them is <see cref="ManageLists"/>; this
/// puts it on screen, asks before anything changes the machine, and hands the
/// work to OperationService.
/// </summary>
public partial class ManageView : PageView
{
    private readonly ManageLists _lists = new();

    /// <summary>
    /// A stand-in for App Center's own package, for the GitHub release card:
    /// its bar, status and reason are painted by OperationService like any
    /// row's, under the same id a winget update of the app would run under.
    /// </summary>
    private readonly AppPackage _self = new() { Id = AppInfo.PackageId, Name = "App Center" };

    /// <summary>
    /// Covers this page's own winget lookups only. The updates and
    /// uninstalls themselves belong to OperationService and keep running
    /// after the page is gone.
    /// </summary>
    private CancellationTokenSource _cts = new();

    public ManageView()
    {
        InitializeComponent();

        UpdatesList.ItemsSource = _lists.Updates;
        InstalledList.ItemsSource = _lists.Installed;
        SelfUpdateCard.DataContext = _self;
        SelfUpdateIcon.Source = IconService.AppIcon;

        _lists.SuiteOpened += (_, suite) => LoadIcons(suite);

        OperationService.Started += OnOperationChanged;
        OperationService.Progressed += OnOperationProgressed;
        OperationService.Finished += OnOperationFinished;
        AppUpdateService.Changed += OnAppUpdateChanged;

        Unloaded += (_, _) =>
        {
            OperationService.Started -= OnOperationChanged;
            OperationService.Progressed -= OnOperationProgressed;
            OperationService.Finished -= OnOperationFinished;
            AppUpdateService.Changed -= OnAppUpdateChanged;
            _cts.Cancel();
        };
    }

    // ---------------------------------------------------------------
    // App Center's own release
    // ---------------------------------------------------------------

    private void OnAppUpdateChanged(object? sender, EventArgs e) => RefreshSelfUpdate();

    /// <summary>
    /// The card for a newer App Center on GitHub. Only when GitHub has more
    /// than winget is offering for the app: if winget already lists the same
    /// version, its row below is the one to press.
    /// </summary>
    private void RefreshSelfUpdate()
    {
        if (!AppUpdateService.OffersMoreThan(_lists.WingetOffersForSelf) || AppUpdateService.Latest is not { } latest)
        {
            SelfUpdateCard.Visibility = Visibility.Collapsed;
            return;
        }

        var inPlace = AppInfo.IsInstalledCopy && latest.HasInstaller;

        SelfUpdateTitle.Text = $"App Center {latest.Version} is available";
        SelfUpdateDetail.Text = inPlace
            ? $"From GitHub, ahead of winget  ·  {AppInfo.Version} → {latest.Version}  ·  restarts App Center"
            : $"From GitHub, ahead of winget  ·  {AppInfo.Version} → {latest.Version}  ·  download it from the release page";
        SelfUpdateButton.Content = inPlace ? "Update" : "Open release page";
        SelfUpdateButton.IsEnabled = OperationService.CanStart(AppInfo.PackageId);

        OperationService.Paint(_self, OperationKind.Update);
        SelfUpdateCard.Visibility = Visibility.Visible;
    }

    private void OnSelfUpdate(object sender, RoutedEventArgs e)
    {
        if (AppUpdateService.Latest is not { } latest)
            return;

        if (!AppInfo.IsInstalledCopy || !latest.HasInstaller)
        {
            try
            {
                Process.Start(new ProcessStartInfo(latest.PageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetProgress($"Could not open {latest.PageUrl}: {ex.Message}");
            }

            return;
        }

        if (!OperationService.CanStart(AppInfo.PackageId))
            return;

        var confirmed = Host.ConfirmAction(
            $"Update App Center to {latest.Version}?",
            $"The installer for {latest.Version} is downloaded from GitHub and run silently - the same one " +
            "winget will offer once its pull request is merged. App Center closes while it runs and opens " +
            "again on the new version.\n\n" +
            "No administrator permission is needed: App Center installs per user.",
            "Update");

        if (!confirmed)
            return;

        AppUpdateService.StartUpdate();
    }

    public override Task LoadAsync() => ReloadAsync();

    // ---------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------

    /// <summary>
    /// Fills the page from the last read of the machine straight away, if
    /// there has been one, then reads again and fills it from that. The first
    /// visit of a launch has nothing to open on, so it shows the shape of the
    /// lists until winget answers; every visit after opens on the lists as
    /// they were and lets the refresh land behind them.
    /// </summary>
    private async Task ReloadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetProgress("Checking winget for updates…");
        CheckButton.IsEnabled = false;

        if (MachineState.HasLoaded)
            ShowMachine();
        else
            ShowLoading(true);

        try
        {
            // One read for the whole app - the badge and the cards on the
            // browse pages follow from the same one. See MachineState.
            await MachineState.RefreshAsync(token);
            token.ThrowIfCancellationRequested();

            ShowLoading(false);
            ShowMachine();
        }
        catch (OperationCanceledException)
        {
            // Page was navigated away from mid-load.
        }
        catch (Exception ex)
        {
            ShowLoading(false);
            SetProgress($"Could not read package list: {ex.Message}");
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    /// <summary>Rebuilds both lists from what MachineState currently holds.</summary>
    private void ShowMachine()
    {
        _lists.Load(MachineState.Upgrades, MachineState.Installed, CatalogService.AllById());

        ApplyFilter();
        RefreshSelfUpdate();
        Host.Icons.BeginLoad(_lists.AllUpdates, Dispatcher);

        // The rows were just rebuilt from scratch, so anything winget is
        // still working on has to be marked busy again.
        ApplyOperations();
        RefreshButtons();
        RefreshStatus();
    }

    private void ShowLoading(bool loading)
    {
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        Lists.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;

        if (loading)
            UpdateAllButton.IsEnabled = false;
    }

    // ---------------------------------------------------------------
    // Filtering
    // ---------------------------------------------------------------

    /// <summary>Rebuilds both lists through the filter box, the system toggle and the sort.</summary>
    private void ApplyFilter()
    {
        _lists.Filter(FilterBox.Text, SystemToggle.IsChecked == true, InstalledSort.SelectedIndex == 1);

        RefreshUpdatesSection();

        var any = _lists.Installed.Count > 0;
        InstalledPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        InstalledEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        InstalledEmptyText.Text = _lists.InstalledEmptyText;
        InstalledStatus.Text = _lists.InstalledStatus;

        // Only the visible rows are worth fetching icons for. A family's other
        // members share its icon; a suite's rows each have one of their own,
        // fetched when the suite is opened.
        Host.Icons.BeginLoad(_lists.Installed.Take(60).Select(g => g.Lead), Dispatcher);

        foreach (var suite in _lists.Installed.Where(g => g.IsSuite && g.IsExpanded))
            LoadIcons(suite);
    }

    private void LoadIcons(InstalledGroup suite) =>
        Host.Icons.BeginLoad(suite.Families.Select(f => f.Lead), Dispatcher);

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        FilterBox.Clear();
        e.Handled = true;
    }

    private void OnFilterToggled(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    // ---------------------------------------------------------------
    // Actions that change the machine
    // ---------------------------------------------------------------

    /// <summary>The badge follows the same read, so nothing else to ask for.</summary>
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        if (_lists.AllUpdates.Count == 0 || !OperationService.CanStart(Operation.UpdateAllKey))
            return;

        if (!Confirm(_lists.UpdateAllQuestion()))
            return;

        var batch = _lists.UpdateAllBatch();

        OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll,
            (progress, token) => WingetService.UpgradeEachAsync(
                batch, progress,
                OperationService.NoteBatchStart, OperationService.NoteBatchDone,
                token));
    }

    /// <summary>
    /// A press on a row, or on one of the buttons inside a row - the rows are
    /// buttons too, tagged "row", so a press anywhere on one arrives here the
    /// same way. An update row and a package on its own open the package's
    /// page; a family's row opens the family out. Back returns here: the shell
    /// keeps the sidebar destination while a detail page is showing, so there
    /// is nothing to remember on this side.
    /// </summary>
    private void OnRowButtonClick(object sender, RoutedEventArgs e)
    {
        // Not e.Source: WPF re-maps it to the ItemsControl on the way out of
        // the item template, so the Button is only reachable by walking up
        // from OriginalSource. See TreeSearch.FindAncestor. The walk finds
        // the innermost button, so Update inside a row is Update, not the row.
        var button = TreeSearch.FindAncestor<Button>(e.OriginalSource as DependencyObject);
        var action = button?.Tag as string;

        if (action == "row")
        {
            switch (button!.DataContext)
            {
                case InstalledGroup { IsGroup: true } family:
                    family.IsExpanded = !family.IsExpanded;
                    break;

                case InstalledGroup single:
                    Host.ShowDetail(single.Lead);
                    break;

                case AppPackage opened:
                    Host.ShowDetail(opened);
                    break;
            }

            return;
        }

        if (button?.DataContext is not AppPackage package || !OperationService.CanStart(package.OperationKey))
            return;

        if (action == "update")
        {
            if (!Confirm(ManageLists.UpdateQuestion(package)))
                return;

            OperationService.Start(
                package.OperationKey, package.Name, OperationKind.Update,
                (progress, token) => WingetService.UpgradeAsync(package.Id, progress, token));
        }
        else if (action == "update-admin")
        {
            // The update was already asked about and agreed to, and Windows'
            // own prompt for the rights is the confirmation this one needs -
            // unless the update takes App Center with it, which that prompt
            // does not say.
            if (package.ClosesApp && !Confirm(ManageLists.UpdateQuestion(package)))
                return;

            OperationService.Start(
                package.OperationKey, package.Name, OperationKind.Update,
                (progress, token) => WingetService.UpgradeAsAdminAsync(package.Id, progress, token),
                asAdmin: true);
        }
        else if (action == "uninstall")
        {
            if (!Confirm(ManageLists.UninstallQuestion(package)))
                return;

            OperationService.Start(
                package.OperationKey, package.NameAndVersion, OperationKind.Uninstall,
                (progress, token) => WingetService.UninstallAsync(
                    package.Id, package.IdentifyingVersion, progress, token));
        }
    }

    private bool Confirm(Question question) =>
        Host.ConfirmAction(question.Title, question.Message, question.Confirm);

    // ---------------------------------------------------------------
    // Operation state
    // ---------------------------------------------------------------

    /// <summary>
    /// Marks every row winget is currently working on. The rows are rebuilt
    /// on each reload, so their busy state has to be re-derived rather than
    /// carried - the service is the only thing that remembers.
    /// </summary>
    private void ApplyOperations()
    {
        foreach (var (package, shows) in _lists.Rows())
            OperationService.Paint(package, shows);

        OperationService.Paint(_self, OperationKind.Update);
        SelfUpdateButton.IsEnabled = OperationService.CanStart(AppInfo.PackageId);

        _lists.OpenFamiliesWithNews();
    }

    /// <summary>
    /// Repaints one row, for the progress lines that arrive while an operation
    /// runs - a new milestone moves that row's bar and nothing else. Not for
    /// "update all", which is keyed to no package and moves from row to row.
    /// </summary>
    private void PaintRow(string key)
    {
        foreach (var (package, shows) in _lists.RowsFor(key))
            OperationService.Paint(package, shows);
    }

    /// <summary>The heading, the button and the empty card, from whatever the lists hold now.</summary>
    private void RefreshUpdatesSection()
    {
        var shown = _lists.Updates.Count > 0;

        UpdatesHeading.Text = _lists.UpdatesHeading;
        UpdateAllLabel.Text = _lists.UpdateAllLabel;

        UpdatesPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        UpdatesEmpty.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        UpdatesEmptyText.Text = _lists.UpdatesEmptyText(WingetService.IsAvailable);
    }

    private void RefreshButtons()
    {
        UpdateAllButton.IsEnabled =
            _lists.AllUpdates.Count > 0 && OperationService.CanStart(Operation.UpdateAllKey);
    }

    /// <summary>
    /// The progress line says nothing while winget works: the row it is working
    /// on is busy and drawing its own bar, and echoing every line winget printed
    /// up here only re-flowed the page around a download URL. What is left is
    /// the last operation's closing words, which have to survive the reload that
    /// follows: they are often the only explanation for what the list looks like
    /// afterwards, and by then there is no operation left to ask. See
    /// <see cref="ManageLists.Unexplained"/>.
    /// </summary>
    private void RefreshStatus() =>
        SetProgress(OperationService.Current is null
            ? _lists.Unexplained(OperationService.LastOutcome)
            : null);

    private void OnOperationChanged(object? sender, Operation operation)
    {
        ApplyOperations();
        RefreshButtons();
        RefreshStatus();
    }

    private void OnOperationProgressed(object? sender, Operation operation)
    {
        // Nothing for the page line to do: it is quiet for as long as anything
        // is running, and the rows are what a milestone moves.
        //
        // A batch has no single row to repaint: it works down the list, taking
        // off the ones it has updated and leaving a reason on any it could not.
        if (operation.Kind == OperationKind.UpdateAll)
        {
            if (_lists.DropUpdated(operation))
                RefreshUpdatesSection();

            ApplyOperations();
        }
        else
        {
            PaintRow(operation.Key);
        }
    }

    private async void OnOperationFinished(object? sender, Operation operation)
    {
        ApplyOperations();
        RefreshButtons();

        // Through RefreshStatus rather than straight from the operation: the
        // rows have just been painted with whatever went wrong, and a summary
        // they already carry has nothing to add up here - not even for the
        // moment the reload takes, and not if the reload is cancelled before it
        // can have its own say.
        RefreshStatus();

        // Versions and the installed list have both moved on; the reload ends
        // by re-marking whatever is still running.
        await ReloadAsync();

        _lists.NoteUnfinishedUpdate(operation);
    }

    private void SetProgress(string? text)
    {
        ProgressText.Text = text ?? string.Empty;
        ProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
