using System.Collections.ObjectModel;
using System.ComponentModel;
using AppCenter.Services;

namespace AppCenter.Models;

/// <summary>A question to put to the user before something changes the machine.</summary>
public sealed record Question(string Title, string Message, string Confirm);

/// <summary>
/// What the Manage page holds and says: the updates and the installs winget
/// listed, what the filter leaves of them, and every sentence the page builds
/// from that. The page itself only puts this on screen and hands clicks to the
/// services - which is what lets all of it be tested without a window.
/// </summary>
public sealed class ManageLists
{
    /// <summary>Every update winget offers, in the order the batch would take them.</summary>
    private List<AppPackage> _allUpdates = [];

    /// <summary>Every install winget listed, one per row it printed.</summary>
    private List<AppPackage> _allInstalled = [];

    /// <summary>
    /// The families the user has opened, by key. The rows are rebuilt on every
    /// filter change and every reload, so which ones were open has to be kept
    /// apart from them.
    /// </summary>
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the filter box said at the last <see cref="Filter"/>, trimmed.</summary>
    private string _needle = string.Empty;

    private bool _showSystem;

    public IReadOnlyList<AppPackage> AllUpdates => _allUpdates;

    public IReadOnlyList<AppPackage> AllInstalled => _allInstalled;

    // What the two lists show: the above, filtered and sorted. The rows are
    // the same AppPackage instances, so an operation painted on a package
    // shows wherever the package is on screen.
    public ObservableCollection<AppPackage> Updates { get; } = [];

    public ObservableCollection<InstalledGroup> Installed { get; } = [];

    /// <summary>
    /// A suite the user has just opened. Its rows each have an icon of their
    /// own, which are only worth fetching once they can be seen.
    /// </summary>
    public event EventHandler<InstalledGroup>? SuiteOpened;

    // ---------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------

    /// <summary>
    /// Takes a fresh read of the machine. winget list/upgrade give only name,
    /// id and version; anything the catalogue already knows about a package -
    /// publisher, homepage (and therefore its icon) - is merged in here.
    /// </summary>
    public void Load(
        IEnumerable<AppPackage> upgrades,
        IEnumerable<AppPackage> installed,
        IReadOnlyDictionary<string, CatalogEntry> catalog)
    {
        var updates = upgrades.ToList();
        var installs = installed.ToList();

        Enrich(updates, catalog);
        Enrich(installs, catalog);

        // Whatever closes the app goes to the bottom of the list, which is
        // also the bottom of the batch: "update all" works down the rows in
        // the order they are shown. See SelfPackages.
        _allUpdates = SelfPackages.LastInLine(updates);
        _allInstalled = installs;
    }

    private static void Enrich(List<AppPackage> packages, IReadOnlyDictionary<string, CatalogEntry> catalog)
    {
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

    /// <summary>What winget offers for App Center's own package, if anything.</summary>
    public string? WingetOffersForSelf =>
        _allUpdates
            .FirstOrDefault(p => string.Equals(p.Id, AppInfo.PackageId, StringComparison.OrdinalIgnoreCase))
            ?.AvailableVersion;

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
    public void Filter(string needle, bool showSystem, bool descending)
    {
        _needle = needle.Trim();
        _showSystem = showSystem;

        bool Matches(AppPackage p) =>
            _needle.Length == 0
            || p.Name.Contains(_needle, StringComparison.OrdinalIgnoreCase)
            || p.Id.Contains(_needle, StringComparison.OrdinalIgnoreCase);

        // Updates: filtered, sorted, and still with whatever closes the app
        // at the end - the order shown is the order "update all" runs in.
        var updates = _allUpdates.Where(Matches);
        updates = descending
            ? updates.OrderByDescending(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            : updates.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);

        Updates.Clear();
        foreach (var package in SelfPackages.LastInLine(updates))
            Updates.Add(package);

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

        Installed.Clear();
        foreach (var group in groups)
        {
            // A suite's own families open and close as well, and are
            // remembered the same way: by key, apart from the rows.
            foreach (var row in RowsOf(group))
            {
                row.IsExpanded = _expanded.Contains(row.Key);
                row.PropertyChanged += OnGroupChanged;
            }

            Installed.Add(group);
        }
    }

    /// <summary>A top-level row and, for a suite, the families inside it - everything that opens.</summary>
    private static IEnumerable<InstalledGroup> RowsOf(InstalledGroup group) =>
        group.IsSuite ? group.Families.Prepend(group) : [group];

    /// <summary>Keeps a family's open state across the rebuilds that follow.</summary>
    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not InstalledGroup group || e.PropertyName != nameof(InstalledGroup.IsExpanded))
            return;

        if (group.IsExpanded)
            _expanded.Add(group.Key);
        else
            _expanded.Remove(group.Key);

        if (group.IsSuite && group.IsExpanded)
            SuiteOpened?.Invoke(this, group);
    }

    // ---------------------------------------------------------------
    // What the page says
    // ---------------------------------------------------------------

    // The counts are of every update, not of the ones the filter is showing -
    // that is what "update all" would do.
    public string UpdatesHeading => $"Updates available ({_allUpdates.Count})";

    public string UpdateAllLabel => _allUpdates.Count > 0 ? $"Update all ({_allUpdates.Count})" : "Update all";

    /// <summary>What the updates card says when it has no rows to show.</summary>
    public string UpdatesEmptyText(bool wingetAvailable) =>
        !wingetAvailable ? "winget could not be started. Install App Installer from the Microsoft Store."
        : _allUpdates.Count == 0 ? "Everything is up to date."
        : $"None of the {_allUpdates.Count} updates match “{_needle}”.";

    /// <summary>An empty list is a card that says why, not a hairline.</summary>
    public string InstalledEmptyText =>
        _allInstalled.Count == 0
            ? "winget lists nothing as installed."
            : _needle.Length > 0
                ? $"No installed apps match “{_needle}”." + (_showSystem ? string.Empty : " System packages are hidden.")
                : "Every installed package is a system package. Turn on “System packages” to see them.";

    public string InstalledStatus
    {
        get
        {
            var shown = Installed.Sum(g => g.Members.Count);
            var hidden = _allInstalled.Count - shown;
            var families = Installed.SelectMany(g => g.Families).Count(g => g.IsGroup);

            return (hidden > 0
                       ? $"Showing {shown} of {_allInstalled.Count} packages ({hidden} hidden by the current filter)"
                       : $"Showing {shown} packages")
                   + (families > 0
                       ? $", with {families} installed in several versions."
                       : ".");
        }
    }

    // ---------------------------------------------------------------
    // Questions
    // ---------------------------------------------------------------

    /// <summary>
    /// "Update all": everything winget offers, not only what the filter is
    /// showing - the button says "all", and the question names them.
    /// </summary>
    public Question UpdateAllQuestion()
    {
        var all = _allUpdates;

        var names = string.Join(", ", all.Take(5).Select(p => p.Name));
        if (all.Count > 5)
            names += $", and {all.Count - 5} more";

        // The list is already ordered so these come last, but the question still
        // has to name them: the batch ends where they are, and being told that
        // afterwards is exactly the position this is here to avoid.
        var closes = all.Where(p => p.ClosesApp).Select(p => p.Name).ToList();

        return new Question(
            $"Update {all.Count} package{(all.Count == 1 ? string.Empty : "s")}?",
            $"winget will download and install updates for: {names}.\n\n" +
            "Windows may prompt for administrator permission for some of them." +
            (closes.Count == 0
                ? string.Empty
                : $"\n\n{string.Join(", ", closes)} {(closes.Count == 1 ? "is" : "are")} " +
                  $"left until last. {SelfPackages.Warning}"),
            "Update all");
    }

    /// <summary>
    /// What "update all" works through, snapshotted before it starts: the list
    /// is rebuilt by the reload that follows every finished update, and the
    /// batch has to keep working through the packages the user confirmed.
    /// </summary>
    public List<(string Id, string Name)> UpdateAllBatch() =>
        _allUpdates.Select(p => (p.Id, p.Name)).ToList();

    public static Question UpdateQuestion(AppPackage package) => new(
        $"Update {package.Name}?",
        $"winget will install {package.AvailableVersion} over the installed {package.Version}.\n\n" +
        "Windows may prompt for administrator permission." +
        (package.ClosesApp ? $"\n\n{SelfPackages.Warning}" : string.Empty),
        "Update");

    /// <summary>
    /// A row that is one of several installed versions says which version it
    /// is - in the question, and in the heading it runs under after - because
    /// the id it shows and the id its neighbour shows are one and the same,
    /// and only one of them is going.
    /// </summary>
    public static Question UninstallQuestion(AppPackage package)
    {
        var what = package.NameAndVersion;

        return new Question(
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
    }

    // ---------------------------------------------------------------
    // Operation state
    // ---------------------------------------------------------------

    /// <summary>
    /// Every row the page holds, each with the action its button offers - which
    /// is what decides the failures it is entitled to explain.
    /// </summary>
    public IEnumerable<(AppPackage Package, OperationKind Shows)> Rows() =>
        _allUpdates.Select(p => (p, OperationKind.Update))
            .Concat(_allInstalled.Select(p => (p, OperationKind.Uninstall)));

    /// <summary>The rows an operation under this key is working on.</summary>
    public IEnumerable<(AppPackage Package, OperationKind Shows)> RowsFor(string key) =>
        Rows().Where(r => string.Equals(r.Package.OperationKey, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens every family with a member that is busy, or has a reason to show:
    /// a bar or a red line inside a closed family is a bar or a red line nobody
    /// is looking at. A suite opens as far as the family the member is in.
    /// </summary>
    public void OpenFamiliesWithNews()
    {
        foreach (var row in Installed.SelectMany(RowsOf))
        {
            if (row.IsGroup && !row.IsExpanded
                && row.Members.Any(m => m.IsBusy || m.Error.Length > 0))
                row.IsExpanded = true;
        }
    }

    /// <summary>
    /// Takes the rows "update all" is done with off the list as it goes, so what
    /// is left is what it still has to do and the top of the list is always the
    /// package being worked on. The ones it could not update stay where they
    /// are, carrying winget's reason: they are still upgradable, and removing
    /// them would take the explanation with them. True when any went.
    /// </summary>
    public bool DropUpdated(Operation batch)
    {
        var dropped = _allUpdates.RemoveAll(p => batch.WasUpdated(p.Id)) > 0;

        // Backwards, so removing a row does not move the one after it out from
        // under the loop. Most calls find nothing: this runs on every line
        // winget prints, not only on the ones that end a package.
        for (var i = Updates.Count - 1; i >= 0; i--)
        {
            if (batch.WasUpdated(Updates[i].Id))
                Updates.RemoveAt(i);
        }

        return dropped;
    }

    /// <summary>
    /// What the finished operation still needs to say above the lists. Nothing,
    /// when every package it has to complain about is on screen already saying
    /// it in red: a failure belongs with the item it happened to, and printing
    /// it twice - once in grey up there, once in red down here - reads as a
    /// glitch rather than as emphasis.
    ///
    /// A batch is no different. Its closing tally names the packages it could
    /// not update, and those are exactly the rows it left in the list carrying
    /// winget's reason; naming them again above the list says nothing new.
    ///
    /// What is left is what no row can say: how a run that went fine ended, and
    /// a failure whose row is not there to be read - hidden by the filter, or
    /// taken away by the reload.
    /// </summary>
    public string? Unexplained(Operation? outcome)
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
        Updates.Concat(Installed.SelectMany(g => g.Members)).Any(p =>
            p.Error.Length > 0
            && string.Equals(p.OperationKey, key, StringComparison.OrdinalIgnoreCase));

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
    public void NoteUnfinishedUpdate(Operation operation)
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
}
