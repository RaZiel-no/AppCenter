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
    /// Stands in for "whatever winget is doing right now" so the page-level bar
    /// can use the same style, and the same painting, as the rows. Never shown
    /// as a package - only its progress fields are ever read.
    /// </summary>
    private readonly AppPackage _overall = new();

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
        OverallBar.DataContext = _overall;

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

            _updates.Clear();
            foreach (var package in upgrades)
                _updates.Add(package);

            _allInstalled = installed;

            UpdatesHeading.Text = $"Updates available ({upgrades.Count})";
            UpdatesPanel.Visibility = upgrades.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatesEmpty.Visibility = upgrades.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

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

        var confirmed = Host.ConfirmAction(
            $"Update {_updates.Count} package{(_updates.Count == 1 ? string.Empty : "s")}?",
            $"winget will download and install updates for: {names}.\n\n" +
            "Windows may prompt for administrator permission for some of them.",
            "Update all");

        if (!confirmed)
            return;

        // Snapshotted before the operation starts: the list is rebuilt by the
        // reload that follows every finished update, and the batch has to keep
        // working through the packages the user actually confirmed.
        var batch = _updates.Select(p => (p.Id, p.Name)).ToList();

        // The count is also what makes "update all" measurable: each package
        // that comes back updated is a real fraction of a known total.
        OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll,
            (progress, token) => WingetService.UpgradeEachAsync(
                batch, progress, OperationService.NoteBatchFailure, token),
            batch.Count);
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

        if (button?.DataContext is not AppPackage package || !OperationService.CanStart(package.Id))
            return;

        var action = button.Tag as string;

        if (action == "update")
        {
            var confirmed = Host.ConfirmAction(
                $"Update {package.Name}?",
                $"winget will install {package.AvailableVersion} over the installed {package.Version}.\n\n" +
                "Windows may prompt for administrator permission.",
                "Update");

            if (!confirmed)
                return;

            OperationService.Start(
                package.Id, package.Name, OperationKind.Update,
                (progress, token) => WingetService.UpgradeAsync(package.Id, progress, token));
        }
        else if (action == "uninstall")
        {
            var confirmed = Host.ConfirmAction(
                $"Uninstall {package.Name}?",
                $"This removes {package.Name} from this computer. " +
                "Windows may prompt for administrator permission.",
                "Uninstall");

            if (!confirmed)
                return;

            OperationService.Start(
                package.Id, package.Name, OperationKind.Uninstall,
                (progress, token) => WingetService.UninstallAsync(package.Id, progress, token));
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
    /// runs - a new milestone moves that row's bar and nothing else. "Update
    /// all" matches no row, and shows on the page-level bar instead.
    /// </summary>
    private void PaintRow(string key)
    {
        foreach (var (package, shows) in Rows())
        {
            if (string.Equals(package.Id, key, StringComparison.OrdinalIgnoreCase))
                OperationService.Paint(package, shows);
        }
    }

    private void RefreshButtons()
    {
        UpdateAllButton.IsEnabled =
            _updates.Count > 0 && OperationService.CanStart(Operation.UpdateAllKey);
    }

    /// <summary>
    /// The progress line follows the newest running operation, and otherwise
    /// keeps reporting the last one that finished. It has to survive the
    /// reload that follows an operation: winget's closing line is often the
    /// only explanation for what the list looks like afterwards.
    /// </summary>
    private void RefreshStatus()
    {
        var running = OperationService.Current;

        SetProgress(running is not null
            ? running.Status
            : Unexplained(OperationService.LastOutcome));

        RefreshBar();
    }

    /// <summary>
    /// What the finished operation still needs to say up here. Nothing, when it
    /// failed and the row it failed on is on screen carrying the same words:
    /// printing them twice, once in grey and once in red, reads as a glitch
    /// rather than as emphasis.
    /// </summary>
    private string? Unexplained(Operation? outcome)
    {
        if (outcome is null || !outcome.Failed)
            return outcome?.Summary;

        bool Listed(IEnumerable<AppPackage> rows) =>
            rows.Any(p => string.Equals(p.Id, outcome.Key, StringComparison.OrdinalIgnoreCase));

        var onARow = outcome.Kind switch
        {
            OperationKind.Update => Listed(_updates),
            OperationKind.Uninstall => Listed(_installed),
            // A batch's summary is its tally, which no row says.
            _ => false,
        };

        return onARow ? null : outcome.Summary;
    }

    /// <summary>
    /// Points the page-level bar at whatever is running, and hides it when
    /// nothing is. Like the rows, it is re-derived rather than remembered, so
    /// coming back to this page mid-install redraws the bar where it was.
    /// </summary>
    private void RefreshBar()
    {
        var running = OperationService.Current;

        _overall.IsBusy = running is not null;
        _overall.Progress = running?.Percent ?? 0;
        _overall.IsProgressPulsing = running?.IsPulsing ?? false;
    }

    private void OnOperationChanged(object? sender, Operation operation)
    {
        ApplyOperations();
        RefreshButtons();
        RefreshStatus();
    }

    private void OnOperationProgressed(object? sender, Operation operation)
    {
        RefreshStatus();

        // A batch has no single row to repaint: it moves through the list and
        // leaves a reason on any row it could not update.
        if (operation.Kind == OperationKind.UpdateAll)
            ApplyOperations();
        else
            PaintRow(operation.Key);
    }

    private async void OnOperationFinished(object? sender, Operation operation)
    {
        ApplyOperations();
        RefreshButtons();
        SetProgress(operation.Summary);
        RefreshBar();

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
    /// </summary>
    private void NoteUnfinishedUpdate(Operation operation)
    {
        if (operation.Failed || operation.Kind != OperationKind.Update)
            return;

        var stillListed = _updates.FirstOrDefault(p =>
            string.Equals(p.Id, operation.Key, StringComparison.OrdinalIgnoreCase));

        if (stillListed is not null && !stillListed.IsBusy)
            stillListed.Status = "Restart the app to finish";
    }

    private void SetProgress(string? text)
    {
        ProgressText.Text = text ?? string.Empty;
        ProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
