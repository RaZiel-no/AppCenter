using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class ManageView : PageView
{
    private readonly ObservableCollection<AppPackage> _updates = [];
    private readonly ObservableCollection<AppPackage> _installed = [];

    private List<AppPackage> _allInstalled = [];

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

        OperationService.Started += OnOperationChanged;
        OperationService.Progressed += OnOperationProgressed;
        OperationService.Finished += OnOperationFinished;

        Unloaded += (_, _) =>
        {
            OperationService.Started -= OnOperationChanged;
            OperationService.Progressed -= OnOperationProgressed;
            OperationService.Finished -= OnOperationFinished;
            _cts.Cancel();
        };
    }

    public override Task LoadAsync() => ReloadAsync();

    // ---------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------

    private async Task ReloadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetProgress("Checking winget for updates…");
        CheckButton.IsEnabled = false;

        try
        {
            // Sequential rather than parallel: two winget processes can
            // contend on the same source database.
            var upgrades = await WingetService.ListUpgradesAsync(token);
            token.ThrowIfCancellationRequested();

            var installed = await WingetService.ListInstalledAsync(token);
            token.ThrowIfCancellationRequested();

            Enrich(upgrades);
            Enrich(installed);

            // Whatever closes the app goes to the bottom of the list, which is
            // also the bottom of the batch: "update all" works down the rows in
            // the order they are shown. See SelfPackages.
            _updates.Clear();
            foreach (var package in SelfPackages.LastInLine(upgrades))
                _updates.Add(package);

            _allInstalled = installed;

            RefreshUpdatesSection();

            if (!WingetService.IsAvailable)
                UpdatesEmptyText.Text = "winget could not be started. Install App Installer from the Microsoft Store.";

            ApplyFilter();
            Host.Icons.BeginLoad(upgrades, Dispatcher);

            // The rows were just rebuilt from scratch, so anything winget is
            // still working on has to be marked busy again.
            ApplyOperations();
            RefreshButtons();
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            // Page was navigated away from mid-load.
        }
        catch (Exception ex)
        {
            SetProgress($"Could not read package list: {ex.Message}");
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
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
        }
    }

    // ---------------------------------------------------------------
    // Filtering
    // ---------------------------------------------------------------

    private void ApplyFilter()
    {
        var needle = FilterBox.Text.Trim();
        var showSystem = SystemToggle.IsChecked == true;

        IEnumerable<AppPackage> query = _allInstalled;

        if (!showSystem)
            query = query.Where(p => !p.IsSystemPackage);

        if (needle.Length > 0)
        {
            query = query.Where(p =>
                p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                p.Id.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        query = InstalledSort.SelectedIndex == 1
            ? query.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            : query.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);

        var results = query.ToList();

        _installed.Clear();
        foreach (var package in results)
            _installed.Add(package);

        var hidden = _allInstalled.Count - results.Count;
        InstalledStatus.Text = hidden > 0
            ? $"Showing {results.Count} of {_allInstalled.Count} packages ({hidden} hidden by the current filter)."
            : $"Showing {results.Count} packages.";

        // Only the visible rows are worth fetching icons for.
        Host.Icons.BeginLoad(results.Take(60), Dispatcher);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
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

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
        Host.RefreshUpdateBadge();
    }

    private void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        if (_updates.Count == 0 || !OperationService.CanStart(Operation.UpdateAllKey))
            return;

        var names = string.Join(", ", _updates.Take(5).Select(p => p.Name));
        if (_updates.Count > 5)
            names += $", and {_updates.Count - 5} more";

        // The list is already ordered so these come last, but the question still
        // has to name them: the batch ends where they are, and being told that
        // afterwards is exactly the position this is here to avoid.
        var closes = _updates.Where(p => p.ClosesApp).Select(p => p.Name).ToList();

        var confirmed = Host.ConfirmAction(
            $"Update {_updates.Count} package{(_updates.Count == 1 ? string.Empty : "s")}?",
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
        var batch = _updates.Select(p => (p.Id, p.Name)).ToList();

        OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll,
            (progress, token) => WingetService.UpgradeEachAsync(
                batch, progress,
                OperationService.NoteBatchStart, OperationService.NoteBatchDone,
                token));
    }

    /// <summary>
    /// Opens the clicked row's page. Back returns here: the shell keeps the
    /// sidebar destination while a detail page is showing, so there is nothing
    /// to remember on this side.
    /// </summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        // A click on Update or Uninstall is theirs, not the row's. Button marks
        // its own mouse events handled, so one should never arrive here - the
        // check is what makes that a stated assumption rather than a hope.
        if (TreeSearch.FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;

        if ((e.OriginalSource as FrameworkElement)?.DataContext is AppPackage package)
            Host.ShowDetail(package);
    }

    private void OnRowButtonClick(object sender, RoutedEventArgs e)
    {
        // Not e.Source: WPF re-maps it to the ItemsControl on the way out of
        // the item template, so the Button is only reachable by walking up
        // from OriginalSource. See TreeSearch.FindAncestor.
        var button = TreeSearch.FindAncestor<Button>(e.OriginalSource as DependencyObject);

        if (button?.DataContext is not AppPackage package || !OperationService.CanStart(package.OperationKey))
            return;

        var action = button.Tag as string;

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
    /// </summary>
    private void ApplyOperations()
    {
        foreach (var (package, shows) in Rows())
            OperationService.Paint(package, shows);
    }

    /// <summary>
    /// Every row the page holds, each with the action its button offers - which
    /// is what decides the failures it is entitled to explain.
    /// </summary>
    private IEnumerable<(AppPackage Package, OperationKind Shows)> Rows() =>
        _updates.Select(p => (p, OperationKind.Update))
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
        var dropped = false;

        // Backwards, so removing a row does not move the one after it out from
        // under the loop. Most calls find nothing: this runs on every line
        // winget prints, not only on the ones that end a package.
        for (var i = _updates.Count - 1; i >= 0; i--)
        {
            if (!batch.WasUpdated(_updates[i].Id))
                continue;

            _updates.RemoveAt(i);
            dropped = true;
        }

        if (dropped)
            RefreshUpdatesSection();
    }

    /// <summary>The heading and the empty card, from whatever the list holds now.</summary>
    private void RefreshUpdatesSection()
    {
        var any = _updates.Count > 0;

        UpdatesHeading.Text = $"Updates available ({_updates.Count})";
        UpdatesPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdatesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshButtons()
    {
        UpdateAllButton.IsEnabled =
            _updates.Count > 0 && OperationService.CanStart(Operation.UpdateAllKey);
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
        _updates.Concat(_installed).Any(p =>
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

        foreach (var package in _updates)
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
