using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class ManageView : PageView
{
    /// <summary>Every update winget offers, in the order the batch would take them.</summary>
    private List<AppPackage> _allUpdates = [];

    /// <summary>Every install winget listed, one per row it printed.</summary>
    private List<AppPackage> _allInstalled = [];

    // What the two lists show: the above, filtered and sorted. The rows are
    // the same AppPackage instances, so an operation painted on a package
    // shows wherever the package is on screen.
    private readonly ObservableCollection<AppPackage> _updates = [];
    private readonly ObservableCollection<InstalledGroup> _installed = [];

    /// <summary>
    /// The families the user has opened, by key. The rows are rebuilt on every
    /// filter change and every reload, so which ones were open has to be kept
    /// apart from them.
    /// </summary>
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

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

        UpdatesList.ItemsSource = _updates;
        InstalledList.ItemsSource = _installed;
        SelfUpdateCard.DataContext = _self;
        SelfUpdateIcon.Source = IconService.AppIcon;

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
        var wingetOffers = _allUpdates
            .FirstOrDefault(p => string.Equals(p.Id, AppInfo.PackageId, StringComparison.OrdinalIgnoreCase))
            ?.AvailableVersion;

        if (!AppUpdateService.OffersMoreThan(wingetOffers) || AppUpdateService.Latest is not { } latest)
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
        var upgrades = MachineState.Upgrades.ToList();
        var installed = MachineState.Installed.ToList();

        Enrich(upgrades);
        Enrich(installed);

        // Whatever closes the app goes to the bottom of the list, which is
        // also the bottom of the batch: "update all" works down the rows in
        // the order they are shown. See SelfPackages.
        _allUpdates = SelfPackages.LastInLine(upgrades);
        _allInstalled = installed;

        if (!WingetService.IsAvailable)
            UpdatesEmptyText.Text = "winget could not be started. Install App Installer from the Microsoft Store.";

        ApplyFilter();
        RefreshSelfUpdate();
        Host.Icons.BeginLoad(upgrades, Dispatcher);

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

    /// <summary>
    /// winget list/upgrade give only name, id and version. Anything we
    /// already know about a package from the catalog - publisher, homepage
    /// (and therefore its icon) - is merged in here.
    /// </summary>
    private static void Enrich(List<AppPackage> packages)
    {
        var catalog = CatalogService.AllById();

        foreach (var package in packages)
        {
            if (!catalog.TryGetValue(package.Id, out var entry))
                continue;

            package.Publisher = entry.Publisher;
            package.Summary = entry.Summary;
            package.Homepage = entry.Homepage;
            package.IconUrl = entry.Icon;
            package.Screenshots = entry.Screenshots;
            package.StoreId = entry.Msstore;
        }
    }

    // ---------------------------------------------------------------
    // Filtering
    // ---------------------------------------------------------------

    /// <summary>
    /// Rebuilds both lists from what winget said, through the filter box, the
    /// system toggle and the sort. The filter reaches the updates as well as
    /// the installs - typing a name is how a package is found, and it is found
    /// wherever it is. The toggle is for the installs only: a system package
    /// with an update waiting is exactly the kind worth seeing.
    /// </summary>
    private void ApplyFilter()
    {
        var needle = FilterBox.Text.Trim();
        var showSystem = SystemToggle.IsChecked == true;
        var descending = InstalledSort.SelectedIndex == 1;

        bool Matches(AppPackage p) =>
            needle.Length == 0
            || p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || p.Id.Contains(needle, StringComparison.OrdinalIgnoreCase);

        // Updates: filtered, sorted, and still with whatever closes the app
        // at the end - the order shown is the order "update all" runs in.
        var updates = _allUpdates.Where(Matches);
        updates = descending
            ? updates.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            : updates.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);

        _updates.Clear();
        foreach (var package in SelfPackages.LastInLine(updates))
            _updates.Add(package);

        RefreshUpdatesSection(needle);

        // Installs: filtered, then folded into families, then sorted by what
        // the row will say. Folding after filtering means a family shrinks to
        // the members that match rather than vanishing or showing the rest.
        IEnumerable<AppPackage> query = _allInstalled;

        if (!showSystem)
            query = query.Where(p => !p.IsSystemPackage);

        var groups = PackageFamilies.Group(query.Where(Matches));

        groups = (descending
            ? groups.OrderByDescending(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            : groups.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)).ToList();

        _installed.Clear();
        foreach (var group in groups)
        {
            // A suite's own families open and close as well, and are
            // remembered the same way: by key, apart from the rows.
            foreach (var row in group.IsSuite ? group.Families.Prepend(group) : [group])
            {
                row.IsExpanded = _expanded.Contains(row.Key);
                row.PropertyChanged += OnGroupChanged;
            }

            _installed.Add(group);
        }

        var shown = groups.Sum(g => g.Members.Count);
        var hidden = _allInstalled.Count - shown;
        var families = groups.SelectMany(g => g.Families).Count(g => g.IsGroup);

        // An empty list is a card that says why, not a hairline.
        InstalledPanel.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledEmpty.Visibility = groups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        InstalledEmptyText.Text = _allInstalled.Count == 0
            ? "winget lists nothing as installed."
            : needle.Length > 0
                ? $"No installed apps match “{needle}”." + (showSystem ? string.Empty : " System packages are hidden.")
                : "Every installed package is a system package. Turn on “System packages” to see them.";

        InstalledStatus.Text =
            (hidden > 0
                ? $"Showing {shown} of {_allInstalled.Count} packages ({hidden} hidden by the current filter)"
                : $"Showing {shown} packages")
            + (families > 0
                ? $", with {families} installed in several versions."
                : ".");

        // Only the visible rows are worth fetching icons for. A family's other
        // members share its icon; a suite's rows each have one of their own,
        // fetched when the suite is opened.
        Host.Icons.BeginLoad(groups.Take(60).Select(g => g.Lead), Dispatcher);

        foreach (var suite in groups.Where(g => g.IsSuite && g.IsExpanded))
            LoadIcons(suite);
    }

    private void LoadIcons(InstalledGroup suite) =>
        Host.Icons.BeginLoad(suite.Families.Select(f => f.Lead), Dispatcher);

    /// <summary>Keeps a family's open state across the rebuilds that follow.</summary>
    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not InstalledGroup group || e.PropertyName != nameof(InstalledGroup.IsExpanded))
            return;

        if (group.IsExpanded)
            _expanded.Add(group.Key);
        else
            _expanded.Remove(group.Key);

        if (group.IsSuite && group.IsExpanded)
            LoadIcons(group);
    }

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
        // Everything winget offers, not only what the filter is showing: the
        // button says "all", and the question that follows names them.
        var all = _allUpdates;

        if (all.Count == 0 || !OperationService.CanStart(Operation.UpdateAllKey))
            return;

        var names = string.Join(", ", all.Take(5).Select(p => p.Name));
        if (all.Count > 5)
            names += $", and {all.Count - 5} more";

        // The list is already ordered so these come last, but the question still
        // has to name them: the batch ends where they are, and being told that
        // afterwards is exactly the position this is here to avoid.
        var closes = all.Where(p => p.ClosesApp).Select(p => p.Name).ToList();

        var confirmed = Host.ConfirmAction(
            $"Update {all.Count} package{(all.Count == 1 ? string.Empty : "s")}?",
            $"winget will download and install updates for: {names}.\n\n" +
            "Windows may prompt for administrator permission for some of them." +
            (closes.Count == 0
                ? string.Empty
                : $"\n\n{string.Join(", ", closes)} {(closes.Count == 1 ? "is" : "are")} " +
                  $"left until last. {SelfPackages.Warning}"),
            "Update all");

        if (!confirmed)
            return;

        // Snapshotted before the operation starts: the list is rebuilt by the
        // reload that follows every finished update, and the batch has to keep
        // working through the packages the user actually confirmed.
        var batch = all.Select(p => (p.Id, p.Name)).ToList();

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
            var confirmed = Host.ConfirmAction(
                $"Update {package.Name}?",
                $"winget will install {package.AvailableVersion} over the installed {package.Version}.\n\n" +
                "Windows may prompt for administrator permission." +
                (package.ClosesApp ? $"\n\n{SelfPackages.Warning}" : string.Empty),
                "Update");

            if (!confirmed)
                return;

            OperationService.Start(
                package.OperationKey, package.Name, OperationKind.Update,
                (progress, token) => WingetService.UpgradeAsync(package.Id, progress, token));
        }
        else if (action == "uninstall")
        {
            // A row that is one of several installed versions says which version
            // it is - in the question, and in the heading it runs under after -
            // because the id it shows and the id its neighbour shows are one and
            // the same, and only one of them is going.
            var what = package.NameAndVersion;

            var confirmed = Host.ConfirmAction(
                $"Uninstall {what}?",
                $"This removes {what} from this computer. " +
                (package.IsOneOfSeveralVersions
                    ? "Other versions of it stay installed. "
                    : string.Empty) +
                "Windows may prompt for administrator permission." +
                // Removing the runtime the app is running on closes it for the
                // same reason updating it does, except that this time nothing
                // is being put back. The row sits behind the system-package
                // toggle rather than out of reach, so the question says so.
                (package.ClosesApp ? $"\n\n{SelfPackages.RemovalWarning}" : string.Empty),
                "Uninstall");

            if (!confirmed)
                return;

            OperationService.Start(
                package.OperationKey, what, OperationKind.Uninstall,
                (progress, token) => WingetService.UninstallAsync(
                    package.Id, package.IdentifyingVersion, progress, token));
        }
    }

    // ---------------------------------------------------------------
    // Operation state
    // ---------------------------------------------------------------

    /// <summary>
    /// Marks every row winget is currently working on. The rows are rebuilt
    /// on each reload, so their busy state has to be re-derived rather than
    /// carried - the service is the only thing that remembers.
    ///
    /// A family with a member that is busy, or has a reason to show, opens so
    /// the member can be seen: a bar or a red line inside a closed family is a
    /// bar or a red line nobody is looking at.
    /// </summary>
    private void ApplyOperations()
    {
        foreach (var (package, shows) in Rows())
            OperationService.Paint(package, shows);

        OperationService.Paint(_self, OperationKind.Update);
        SelfUpdateButton.IsEnabled = OperationService.CanStart(AppInfo.PackageId);

        // A suite opens as far as the family the member is in.
        foreach (var group in _installed)
        {
            foreach (var row in group.IsSuite ? group.Families.Prepend(group) : [group])
            {
                if (row.IsGroup && !row.IsExpanded
                    && row.Members.Any(m => m.IsBusy || m.Error.Length > 0))
                    row.IsExpanded = true;
            }
        }
    }

    /// <summary>
    /// Every row the page holds, each with the action its button offers - which
    /// is what decides the failures it is entitled to explain.
    /// </summary>
    private IEnumerable<(AppPackage Package, OperationKind Shows)> Rows() =>
        _allUpdates.Select(p => (p, OperationKind.Update))
            .Concat(_allInstalled.Select(p => (p, OperationKind.Uninstall)));

    /// <summary>
    /// Repaints one row, for the progress lines that arrive while an operation
    /// runs - a new milestone moves that row's bar and nothing else. Not for
    /// "update all", which is keyed to no package and moves from row to row.
    /// </summary>
    private void PaintRow(string key)
    {
        foreach (var (package, shows) in Rows())
        {
            if (string.Equals(package.OperationKey, key, StringComparison.OrdinalIgnoreCase))
                OperationService.Paint(package, shows);
        }
    }

    /// <summary>
    /// Takes the rows "update all" is done with off the list as it goes, so what
    /// is left is what it still has to do and the top of the list is always the
    /// package being worked on. The ones it could not update stay where they
    /// are, carrying winget's reason: they are still upgradable, and removing
    /// them would take the explanation with them.
    /// </summary>
    private void DropUpdated(Operation batch)
    {
        var dropped = _allUpdates.RemoveAll(p => batch.WasUpdated(p.Id)) > 0;

        // Backwards, so removing a row does not move the one after it out from
        // under the loop. Most calls find nothing: this runs on every line
        // winget prints, not only on the ones that end a package.
        for (var i = _updates.Count - 1; i >= 0; i--)
        {
            if (batch.WasUpdated(_updates[i].Id))
                _updates.RemoveAt(i);
        }

        if (dropped)
            RefreshUpdatesSection(FilterBox.Text.Trim());
    }

    /// <summary>
    /// The heading, the button and the empty card, from whatever the lists
    /// hold now. The count is of every update, not of the ones the filter is
    /// showing - that is what "update all" would do.
    /// </summary>
    private void RefreshUpdatesSection(string needle)
    {
        var total = _allUpdates.Count;
        var shown = _updates.Count;

        UpdatesHeading.Text = $"Updates available ({total})";
        UpdateAllLabel.Text = total > 0 ? $"Update all ({total})" : "Update all";

        UpdatesPanel.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesEmpty.Visibility = shown > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (shown == 0 && WingetService.IsAvailable)
            UpdatesEmptyText.Text = total == 0
                ? "Everything is up to date."
                : $"None of the {total} updates match “{needle}”.";
    }

    private void RefreshButtons()
    {
        UpdateAllButton.IsEnabled =
            _allUpdates.Count > 0 && OperationService.CanStart(Operation.UpdateAllKey);
    }

    /// <summary>
    /// The progress line says nothing while winget works: the row it is working
    /// on is busy and drawing its own bar, and echoing every line winget printed
    /// up here only re-flowed the page around a download URL. What is left is
    /// the last operation's closing words, which have to survive the reload that
    /// follows: they are often the only explanation for what the list looks like
    /// afterwards, and by then there is no operation left to ask.
    /// </summary>
    private void RefreshStatus() =>
        SetProgress(OperationService.Current is null
            ? Unexplained(OperationService.LastOutcome)
            : null);

    /// <summary>
    /// What the finished operation still needs to say up here. Nothing, when
    /// every package it has to complain about is on screen already saying it in
    /// red: a failure belongs with the item it happened to, and printing it
    /// twice - once in grey up here, once in red down there - reads as a glitch
    /// rather than as emphasis.
    ///
    /// A batch is no different. Its closing tally names the packages it could
    /// not update, and those are exactly the rows it left in the list carrying
    /// winget's reason; naming them again above the list says nothing new.
    ///
    /// What is left up here is what no row can say: how a run that went fine
    /// ended, and a failure whose row is not there to be read - hidden by the
    /// filter, or taken away by the reload.
    /// </summary>
    private string? Unexplained(Operation? outcome)
    {
        if (outcome is null || !outcome.Failed)
            return outcome?.Summary;

        var onARow = outcome.Kind == OperationKind.UpdateAll
            ? outcome.FailedItems.Count > 0 && outcome.FailedItems.All(SaidByARow)
            : SaidByARow(outcome.Key);

        return onARow ? null : outcome.Summary;
    }

    /// <summary>
    /// Whether a row the user can actually see is already explaining this
    /// package. The rows are asked rather than the service, because that is the
    /// question: the same reason is painted onto a row the filter is hiding,
    /// where it explains nothing to anybody.
    /// </summary>
    private bool SaidByARow(string key) =>
        _updates.Concat(_installed.SelectMany(g => g.Members)).Any(p =>
            p.Error.Length > 0
            && string.Equals(p.OperationKey, key, StringComparison.OrdinalIgnoreCase));

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
            DropUpdated(operation);
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

        NoteUnfinishedUpdate(operation);
    }

    /// <summary>
    /// An update winget called a success can leave the row exactly where it
    /// was - Teams and anything else that swaps itself out on next launch
    /// keeps reporting the old version until it restarts. Silently redrawing
    /// the same row reads as "the button did nothing", so the row says why.
    ///
    /// A batch needs the same sentence more than a single update does: it took
    /// the row off the list on its way past, and the reload has just put it
    /// back. Without a word on it, that reads as a package it skipped.
    ///
    /// Which restart it is matters. Reopening one app is a moment; restarting
    /// Windows is a decision, and being told the wrong one is worse than being
    /// told nothing - so the row only says Windows when winget said Windows.
    /// </summary>
    private void NoteUnfinishedUpdate(Operation operation)
    {
        bool WentThrough(AppPackage package) => operation.Kind switch
        {
            OperationKind.Update => !operation.Failed
                && string.Equals(package.OperationKey, operation.Key, StringComparison.OrdinalIgnoreCase),
            OperationKind.UpdateAll => operation.WasUpdated(package.Id),
            _ => false,
        };

        foreach (var package in _allUpdates)
        {
            if (package.IsBusy || !WentThrough(package))
                continue;

            package.Status = operation.NeedsRestart(package.OperationKey)
                ? "Restart Windows to finish"
                : "Restart the app to finish";
        }
    }

    private void SetProgress(string? text)
    {
        ProgressText.Text = text ?? string.Empty;
        ProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
