using System.Windows;
using AppCenter.Models;

namespace AppCenter.Services;

public enum OperationKind
{
    Install,
    Update,
    Uninstall,
    UpdateAll,
}

/// <summary>
/// How far winget has got, in the only terms it gives us. Ordered, because a
/// phase is only ever allowed to move forwards.
/// </summary>
public enum OperationPhase
{
    /// <summary>Launched, and winget has not said anything recognisable yet.</summary>
    Starting,

    /// <summary>"Found &lt;name&gt; [&lt;id&gt;] Version …" - located in the source.</summary>
    Located,

    Downloading,

    /// <summary>Hash checked out; the installer is about to be handed to Windows.</summary>
    Verified,

    /// <summary>The installer itself is running, and says nothing until it is done.</summary>
    Installing,

    Done,
}

/// <summary>
/// One winget command in flight, and every string a page shows for it.
/// The wording lives here rather than in the views so a list row, a detail
/// page and the progress line cannot drift apart.
/// </summary>
public sealed class Operation
{
    /// <summary>"Update all" touches everything, so it runs under its own key.</summary>
    public const string UpdateAllKey = "*update-all*";

    public required string Key { get; init; }
    public required string PackageName { get; init; }
    public required OperationKind Kind { get; init; }

    /// <summary>
    /// How many packages "update all" set out to update, which is the one case
    /// where there is something countable to divide by. 0 when unknown.
    /// </summary>
    public int TotalItems { get; init; }

    /// <summary>The latest line winget printed, trimmed to fit.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>
    /// Where winget has got to, as far as its own output betrays.
    ///
    /// There is no percentage to be had: redirect winget's output and the
    /// progress bar it draws for a terminal disappears entirely - piping an
    /// install yields six plain lines and nothing else. So the bar advances on
    /// the milestones winget does print, and <see cref="IsPulsing"/> covers the
    /// stretches where the next one is genuinely unknowable rather than
    /// inventing movement.
    /// </summary>
    public OperationPhase Phase { get; private set; } = OperationPhase.Starting;

    /// <summary>Packages finished so far - only meaningful for "update all".</summary>
    private int _completed;

    /// <summary>
    /// Why each package was left behind, by package id. Only "update all" fills
    /// this in: a single operation has the whole status line to explain itself,
    /// whereas a batch would otherwise reduce a failure to a name in a list.
    /// It lives here rather than on the row because the rows are rebuilt by the
    /// reload that follows the batch, and this outlives that as LastOutcome.
    /// </summary>
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The other half of that: the ones the batch did get through.</summary>
    private readonly HashSet<string> _updated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The reason this package was skipped, or empty if it was not.</summary>
    public string FailureFor(string id) =>
        _failures.TryGetValue(id, out var reason) ? reason : string.Empty;

    /// <summary>Whether "update all" has already been through this package and updated it.</summary>
    public bool WasUpdated(string id) => _updated.Contains(id);

    /// <summary>
    /// The package "update all" is on right now, empty between packages and for
    /// every other kind of operation. A batch runs under its own key, so the row
    /// it is currently working on has nothing of its own to find - this is what
    /// lets that row go busy, and the list be seen working downwards.
    /// </summary>
    public string CurrentItem { get; private set; } = string.Empty;

    /// <summary>What to call it. Ids are unreadable; the heading needs the name.</summary>
    private string _itemName = string.Empty;

    /// <summary>
    /// Moves the batch onto a package. Everything the phase and the detail were
    /// saying belonged to the package before it, so both go back to the start:
    /// the milestones that follow are this one's, and its row draws its own bar
    /// from them.
    /// </summary>
    internal void BeginItem(string id, string name)
    {
        CurrentItem = id;
        _itemName = name;
        Phase = OperationPhase.Starting;
        Detail = string.Empty;
    }

    /// <summary>
    /// Retires the package the batch has just left, with winget's reason if it
    /// was left behind rather than updated. Either way it counts towards the
    /// tally: a package that failed still took its turn.
    /// </summary>
    internal void EndItem(string id, string reason)
    {
        if (reason.Length > 0)
            _failures[id] = reason;
        else
            _updated.Add(id);

        CurrentItem = string.Empty;
        _completed++;
    }

    public bool IsRunning { get; private set; } = true;

    /// <summary>How it ended. Empty until it does.</summary>
    public string Summary { get; private set; } = string.Empty;

    public bool Failed { get; private set; }

    /// <summary>"Installing Firefox…" - the page-level heading.</summary>
    public string Heading => Kind switch
    {
        OperationKind.Install => $"Installing {PackageName}…",
        OperationKind.Update => $"Updating {PackageName}…",
        OperationKind.Uninstall => $"Uninstalling {PackageName}…",
        _ => BatchHeading,
    };

    /// <summary>
    /// "Updating 7-Zip (3 of 22)…". A batch names the package it is on rather
    /// than only the batch: these installers run silently and can restart the
    /// shell or a service out from under the machine, and which package was in
    /// flight when that happened is the whole question afterwards. The general
    /// form is only for before the first package and between two of them.
    /// </summary>
    private string BatchHeading =>
        CurrentItem.Length > 0
            ? $"Updating {_itemName} ({_completed + 1} of {TotalItems})…"
            : "Updating all packages…";

    /// <summary>"Installing…" - the short label a list row has room for.</summary>
    public string RowLabel => Kind switch
    {
        OperationKind.Install => "Installing…",
        OperationKind.Uninstall => "Uninstalling…",
        _ => "Updating…",
    };

    public string Status => Detail.Length == 0 ? Heading : $"{Heading}  {Detail}";

    /// <summary>
    /// Where to draw the page-level bar, 0 to 1. The steps are deliberately
    /// uneven: they reflect where winget's milestones fall, not equal thirds of
    /// anything. "Update all" is the exception - one finished package is a real
    /// fraction of a known total, so it counts instead of guessing.
    /// </summary>
    public double Percent => Kind == OperationKind.UpdateAll
        ? (TotalItems > 0 ? Math.Min(1.0, (double)_completed / TotalItems) : 0.5)
        : RowPercent;

    /// <summary>
    /// Where to draw one package's own bar. Always the milestones, "update all"
    /// included: the batch keeps the phase of whichever package it is on, so the
    /// row it is working through fills like any single update would.
    /// </summary>
    public double RowPercent => Phase switch
    {
        OperationPhase.Starting => 0.04,
        OperationPhase.Located => 0.15,
        OperationPhase.Downloading => 0.40,
        OperationPhase.Verified => 0.62,
        OperationPhase.Installing => 0.80,
        _ => 1.0,
    };

    /// <summary>
    /// True while the bar would be lying if it claimed to be moving: before
    /// winget has said anything, and through the installer's own run, which
    /// prints nothing at all between "Starting package install" and its result.
    /// "Update all" pulses throughout, since between two finished packages it
    /// knows no more than that.
    /// </summary>
    public bool IsPulsing =>
        IsRunning && (Kind == OperationKind.UpdateAll || RowPulsing);

    /// <summary>The same question for one package's own bar, batch or not.</summary>
    public bool RowPulsing =>
        IsRunning && Phase is OperationPhase.Starting or OperationPhase.Installing;

    internal void Report(string line)
    {
        Detail = Shorten(line);
        Advance(line);
    }

    private void Advance(string line)
    {
        if (Milestone(line) is not { } next)
            return;

        // Forwards only within one package - a bar that slides backwards reads
        // as a bug. "Update all" runs the whole sequence again for every
        // package, and starts each one over in BeginItem rather than here.
        if (next > Phase)
            Phase = next;
    }

    /// <summary>
    /// winget's own milestones, matched on the English it prints. A localised
    /// winget matches none of them and stays at <see cref="OperationPhase.Starting"/>,
    /// which pulses - no progress claimed rather than the wrong one.
    /// </summary>
    private static OperationPhase? Milestone(string line)
    {
        if (Says(line, "Successfully installed")
            || Says(line, "Successfully uninstalled")
            || Says(line, "Successfully upgraded"))
            return OperationPhase.Done;

        // "Starting package install…" and "…uninstall…" both land here.
        if (Says(line, "Starting package"))
            return OperationPhase.Installing;

        if (Says(line, "verified installer hash"))
            return OperationPhase.Verified;

        if (Says(line, "Downloading"))
            return OperationPhase.Downloading;

        if (Says(line, "Found "))
            return OperationPhase.Located;

        return null;
    }

    private static bool Says(string line, string marker) =>
        line.Contains(marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps winget's own last words. Detail is deliberately not cleared:
    /// it holds the final meaningful line winget printed, which is where the
    /// useful part lives - "Successfully installed. Restart the application
    /// to complete the upgrade." or "No applicable upgrade found." winget
    /// says almost nothing on stderr, so quoting only stderr, as this used
    /// to, produced a bare exit code and no explanation at all.
    /// </summary>
    internal void Complete(WingetResult? result, string? error)
    {
        IsRunning = false;

        if (error is not null)
        {
            Failed = true;
            Summary = error;
            return;
        }

        var said = Detail.Length > 0 ? Detail : Shorten(result?.StdErr ?? string.Empty);

        if (result is { Success: true })
        {
            Summary = said.Length > 0 ? said : "Done.";
            return;
        }

        Failed = true;

        // "Update all" finishes with a tally of its own: it worked through every
        // package and can name the ones that failed, which is worth more than
        // the exit code of whichever failed first.
        if (Kind == OperationKind.UpdateAll && said.Length > 0)
        {
            Summary = said;
            return;
        }

        var code = result?.ExitCode ?? -1;

        // winget's own failures are the 0x8A15xxxx family, which is how they
        // are documented and searched for; an installer's own code comes
        // through as a small positive number and reads better in decimal.
        var shown = code < 0 ? $"0x{code:X8}" : code.ToString();

        Summary = $"winget exited with {shown}. {said}".TrimEnd();
    }

    /// <summary>
    /// Flattens a line and caps it. The cap was 110 while the status line was a
    /// single trimmed row; it wraps now, so winget's longer sentences - "…use
    /// --include-unknown" and friends - fit whole rather than ending in an
    /// ellipsis the user cannot expand anywhere. Still bounded: a pathological
    /// line should not push the page around.
    /// </summary>
    private static string Shorten(string text)
    {
        var trimmed = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return trimmed.Length <= 240 ? trimmed : trimmed[..240] + "…";
    }
}

/// <summary>
/// Owns every install, update and uninstall for as long as it runs.
///
/// The work used to belong to whichever page started it, which meant
/// navigating away cancelled it - the page's CancellationTokenSource fired on
/// Unloaded and took winget down with it. Holding the operations here instead
/// means a page can be built, thrown away and built again while winget keeps
/// working, and each page asks what is in flight when it loads rather than
/// remembering anything itself.
///
/// Everything is touched on the UI thread: Start is called from a click, and
/// the winget callbacks - which arrive on its output-reader thread - are
/// marshalled before they reach the list or the events.
/// </summary>
public static class OperationService
{
    private static readonly List<Operation> InFlight = [];

    public static event EventHandler<Operation>? Started;
    public static event EventHandler<Operation>? Progressed;
    public static event EventHandler<Operation>? Finished;

    /// <summary>The most recently started operation still running, if any.</summary>
    public static Operation? Current => InFlight.Count > 0 ? InFlight[^1] : null;

    /// <summary>
    /// The last operation to finish, however it went, kept so a page opened
    /// or reloaded afterwards can still say what happened. Successes matter
    /// as much as failures here: "restart the application to complete the
    /// upgrade" is the whole explanation for a package that is still listed
    /// as upgradable. Cleared when anything new starts.
    /// </summary>
    public static Operation? LastOutcome { get; private set; }

    public static Operation? For(string key) =>
        InFlight.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts whatever is happening to a package onto the row that shows it -
    /// busy, label, and how far along its bar should be.
    ///
    /// Rows are rebuilt from scratch by every reload and by every page that is
    /// navigated back to, so none of this can be carried on the row: it has to
    /// be re-derived from here, because the service is the only thing that
    /// remembers an operation. Call it when a page loads and whenever the
    /// service raises anything.
    /// </summary>
    /// <param name="shows">
    /// The action this row's button offers, so it only explains failures of that
    /// kind. Without it a package that is both upgradable and installed would
    /// carry an update failure into the installed list as well, where the
    /// button says Uninstall and the message makes no sense. Null shows any.
    /// </param>
    public static void Paint(AppPackage package, OperationKind? shows = null)
    {
        var operation = For(package.Id) ?? BatchOn(package.Id);

        package.IsBusy = operation is not null;
        package.Status = operation?.RowLabel ?? string.Empty;
        package.Progress = operation?.RowPercent ?? 0;
        package.IsProgressPulsing = operation?.RowPulsing ?? false;
        package.Error = FailureFor(package.Id, shows);
    }

    /// <summary>
    /// "Update all", if it is on this package right now. The batch runs under
    /// its own key, so the row for the package it has reached would otherwise
    /// find nothing and sit there looking untouched - which is what made a batch
    /// impossible to follow, and made the order it worked in a guess.
    /// </summary>
    private static Operation? BatchOn(string id) =>
        For(Operation.UpdateAllKey) is { } batch
        && string.Equals(batch.CurrentItem, id, StringComparison.OrdinalIgnoreCase)
            ? batch
            : null;

    /// <summary>
    /// Why this package was left where it is, if anything. A batch keeps its
    /// failures by package; a single operation is its own. Both are read back
    /// from the service rather than remembered by the row, which is what lets a
    /// reason survive the reload that rebuilt it.
    /// </summary>
    private static string FailureFor(string id, OperationKind? shows)
    {
        // A batch only ever fails at updating, so its reasons belong on the rows
        // that offer one.
        if (shows is null or OperationKind.Update
            && Batch?.FailureFor(id) is { Length: > 0 } why)
            return why;

        // A single failure says its piece in the status line as well, but that
        // line only describes the last thing that happened. Once the user is
        // reading a list, the row it happened to is where they will look.
        return LastOutcome is { Failed: true, Kind: not OperationKind.UpdateAll } last
               && (shows is null || last.Kind == shows)
               && string.Equals(last.Key, id, StringComparison.OrdinalIgnoreCase)
            ? last.Summary
            : string.Empty;
    }

    /// <summary>
    /// The "update all" currently running, or the last one to finish. Anything
    /// else starting clears LastOutcome, which is what retires its failures.
    /// </summary>
    private static Operation? Batch =>
        For(Operation.UpdateAllKey)
        ?? (LastOutcome is { Kind: OperationKind.UpdateAll } last ? last : null);

    /// <summary>Moves "update all" onto the next package down the list.</summary>
    public static void NoteBatchStart(string id, string name) =>
        MoveBatch(batch => batch.BeginItem(id, name));

    /// <summary>
    /// Marks the package "update all" has just finished with, and why it was
    /// skipped if it was. An empty reason means it went through.
    /// </summary>
    public static void NoteBatchDone(string id, string reason) =>
        MoveBatch(batch => batch.EndItem(id, reason));

    /// <summary>
    /// Both of the above. Called from the batch as it works, on whatever thread
    /// winget's output arrived on, so it hops to the UI thread like every other
    /// mutation here.
    /// </summary>
    private static void MoveBatch(Action<Operation> step) =>
        OnUi(() =>
        {
            if (For(Operation.UpdateAllKey) is not { } batch)
                return;

            step(batch);

            // Raised so the list follows the batch package by package - the row
            // it has reached, and the ones it is done with - rather than
            // standing still until the whole run is over.
            Progressed?.Invoke(null, batch);
        });

    /// <summary>
    /// A package can only have one command running against it, and "update
    /// all" is exclusive in both directions - it is already touching every
    /// package, so nothing else may be moving underneath it. Anything else
    /// may overlap: starting an install and going off to install something
    /// else is normal use, not an error.
    /// </summary>
    public static bool CanStart(string key) =>
        For(key) is null
        && For(Operation.UpdateAllKey) is null
        && (key != Operation.UpdateAllKey || InFlight.Count == 0);

    /// <summary>
    /// Starts a command and returns it, or null if <see cref="CanStart"/>
    /// would have refused. The command is handed a progress callback and a
    /// token; the token is never cancelled, so an install that outlives the
    /// window finishes on its own rather than being killed half-written.
    /// </summary>
    public static Operation? Start(
        string key,
        string packageName,
        OperationKind kind,
        Func<Action<string>, CancellationToken, Task<WingetResult>> command,
        int totalItems = 0)
    {
        if (!CanStart(key))
            return null;

        var operation = new Operation
        {
            Key = key,
            PackageName = packageName,
            Kind = kind,
            TotalItems = totalItems,
        };

        InFlight.Add(operation);
        LastOutcome = null;

        Started?.Invoke(null, operation);
        _ = RunAsync(operation, command);

        return operation;
    }

    private static async Task RunAsync(
        Operation operation,
        Func<Action<string>, CancellationToken, Task<WingetResult>> command)
    {
        try
        {
            var result = await command(
                line => OnUi(() =>
                {
                    operation.Report(line);
                    Progressed?.Invoke(null, operation);
                }),
                CancellationToken.None);

            OnUi(() => Finish(operation, result, null));
        }
        catch (OperationCanceledException)
        {
            OnUi(() => Finish(operation, null, "Cancelled."));
        }
        catch (Exception ex)
        {
            OnUi(() => Finish(operation, null, $"Failed: {ex.Message}"));
        }
    }

    private static void Finish(Operation operation, WingetResult? result, string? error)
    {
        operation.Complete(result, error);
        InFlight.Remove(operation);
        LastOutcome = operation;

        Finished?.Invoke(null, operation);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
