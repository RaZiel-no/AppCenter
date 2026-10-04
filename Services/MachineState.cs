using System.Windows;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// What winget says is on this machine - every install, and every update it
/// has for one - read once and shared by everything that wants to know.
///
/// Three things want to know, and used to ask separately: the Manage page,
/// the update count on the sidebar, and now every card on every browse page,
/// which marks itself Installed or Update available from this. One `winget
/// upgrade` and one `winget list` answer all of them, and two winget
/// processes contending over the same source database was the one thing
/// each of the callers was already at pains to avoid on its own.
///
/// Refreshed on launch and after every operation, whoever asks. A refresh
/// that is asked for while one is already running is not started a second
/// time. If the one in flight has only just begun, the asker joins it - the
/// window and the Manage page both react to the same finished operation, and
/// that is one read, not two. If it began a while ago its answer may already
/// be stale by the time it lands, so one more run is promised for afterwards
/// and the asker waits on that instead.
/// </summary>
public static class MachineState
{
    private static readonly object Gate = new();

    /// <summary>
    /// How recently a run must have started for a new asker to join it rather
    /// than queue another. Long enough to cover two handlers of one event;
    /// short enough that nothing the user did in between could be missed.
    /// </summary>
    private static readonly TimeSpan JoinWindow = TimeSpan.FromMilliseconds(500);

    private static Task? _current;
    private static Task? _next;
    private static long _currentStartedAt;

    /// <summary>Every install winget listed, one per row it printed.</summary>
    public static IReadOnlyList<AppPackage> Installed { get; private set; } = [];

    /// <summary>
    /// Every update winget offers, of every kind: the ones it is sure of, the
    /// ones whose installed version it cannot read, and the ones a pin holds
    /// back. See <see cref="UpdateGroup"/>.
    /// </summary>
    public static IReadOnlyList<AppPackage> Upgrades { get; private set; } = [];

    /// <summary>
    /// The updates winget is sure of: what the badge counts and a card means
    /// by "Update available". A package whose version winget cannot read may
    /// already be up to date, and a pinned one is being left alone; neither
    /// is a number to put on the sidebar.
    /// </summary>
    public static IReadOnlyList<AppPackage> PendingUpdates { get; private set; } = [];

    /// <summary>False until the first refresh has landed.</summary>
    public static bool HasLoaded { get; private set; }

    /// <summary>Raised on the UI thread each time a refresh lands.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Reads the machine again because something may have changed - an
    /// operation has just finished - and returns when the result is in. A run
    /// that has only just started is joined; one that has been going for a
    /// while may have looked before the change, so one more run is promised
    /// after it. The token only stops the caller waiting; the read itself
    /// finishes regardless, so a page that goes away mid-read still leaves a
    /// fresh answer for the next.
    /// </summary>
    public static Task RefreshAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_current is null)
                return Start().WaitAsync(ct);

            if (Environment.TickCount64 - _currentStartedAt < JoinWindow.TotalMilliseconds)
                return _current.WaitAsync(ct);

            // Already reading, and has been for a while: the run in flight may
            // have started before whatever this caller knows has changed, so
            // promise one more run after it - one, however many callers arrive
            // while it is going.
            _next ??= _current.ContinueWith(_ => RunAsync(), TaskScheduler.Default).Unwrap();
            return _next.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Reads the machine as it is, for a page opening on it: the read already
    /// under way, however long it has been going, or a new one when there is
    /// none. Nothing this caller knows of has changed, so the run in flight -
    /// or the one already promised after it - is the answer. Going through
    /// <see cref="RefreshAsync"/> instead promised a second read after the
    /// launch one, and the first visit to Manage waited for both.
    /// </summary>
    public static Task ReadAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            // A run already promised after the one in flight is there because
            // someone knows of a change; a page opening now wants that one.
            return (_next ?? _current ?? Start()).WaitAsync(ct);
        }
    }

    /// <summary>
    /// Which of the updates are installed for every user of the PC - see
    /// <see cref="WingetService.ListMachineWideUpdatesAsync"/> - or null when
    /// winget could not say. For "update all", from the button and from the
    /// command line alike. Read once no read of the machine is under way, since
    /// two winget processes contend over the source database; never faults.
    /// </summary>
    public static async Task<HashSet<string>?> ReadMachineWideAsync()
    {
        await WhenIdleAsync().ConfigureAwait(false);

        try
        {
            return await WingetService.ListMachineWideUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Done when no read is under way - straight away when none is. Starts
    /// nothing, and never faults: whether the read worked is its own callers'
    /// business.
    /// </summary>
    private static Task WhenIdleAsync()
    {
        lock (Gate)
        {
            var run = _next ?? _current;
            return run is null ? Task.CompletedTask : run.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Begins a run, under the lock.</summary>
    private static Task Start()
    {
        _currentStartedAt = Environment.TickCount64;
        _current = RunAsync();
        return _current;
    }

    private static async Task RunAsync()
    {
        try
        {
            // Sequential rather than parallel: two winget processes contend
            // on the same source database. The pins come first because the
            // update list is read in their light - see ReadUpgrades.
            var pins = await WingetService.ListPinsAsync().ConfigureAwait(false);
            var upgrades = await WingetService.ListUpgradesAsync(pins).ConfigureAwait(false);
            var installed = await WingetService.ListInstalledAsync().ConfigureAwait(false);

            UpdateMemory.Apply(upgrades);

            await OnUiAsync(() =>
            {
                // On the UI thread, where everything else that touches the
                // settings runs: this one can drop entries and save them.
                FinishingUpdates.Apply(upgrades);

                Upgrades = upgrades;
                PendingUpdates = upgrades.Where(p => p.Group == UpdateGroup.Pending && !p.IsFinishing).ToList();
                Installed = installed;
                HasLoaded = true;
                Changed?.Invoke(null, EventArgs.Empty);

                // After everyone has the list: which of the updates the Start
                // menu can start, for the rows winget turns out not to be able
                // to update.
                _ = AppLauncher.PaintAsync(upgrades, CatalogService.AllById());
            }).ConfigureAwait(false);
        }
        finally
        {
            lock (Gate)
            {
                _current = _next;
                _next = null;
                _currentStartedAt = Environment.TickCount64;
            }
        }
    }

    /// <summary>
    /// Paints what the machine has onto packages that came from somewhere
    /// else - a catalogue card, a search hit - so a card can say Installed, or
    /// that an update is waiting, without a winget call of its own. Silent for
    /// anything winget lists under a made-up id, which is most of what was
    /// installed by hand; those it cannot match to a catalogue id anyway.
    /// </summary>
    public static void Apply(IEnumerable<AppPackage> packages)
    {
        if (!HasLoaded)
            return;

        var installed = new HashSet<string>(Installed.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var upgrades = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var upgrade in PendingUpdates)
            upgrades.TryAdd(upgrade.Id, upgrade.AvailableVersion);

        foreach (var package in packages)
        {
            package.IsInstalled = installed.Contains(package.Id);
            package.AvailableVersion = upgrades.TryGetValue(package.Id, out var available) ? available : string.Empty;
        }
    }

    /// <summary>
    /// What the last read said about one package - the rows `winget list
    /// --id` would print for it - or null before the first read has landed.
    /// A package's page takes its state from here rather than asking winget
    /// again: the page then agrees with the card it was opened from, and
    /// opening it starts no winget process of its own for the question.
    /// </summary>
    public static InstallState? StateOf(string id)
    {
        if (!HasLoaded)
            return null;

        // winget's installed list carries the version on offer as well, and
        // knows nothing of an update that went in and waits on a restart: the
        // page would offer Update for it where the Manage row does not.
        var finishing = Upgrades.Any(p => p.IsFinishing && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        var installs = Installed
            .Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            .Select(p => new WingetRow(p.Name, p.Id, p.Version, finishing ? string.Empty : p.AvailableVersion, p.Source))
            .ToList();

        return new InstallState(installs);
    }

    /// <summary>Forgets everything. For tests, which share the static.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _current = null;
            _next = null;
        }

        Installed = [];
        Upgrades = [];
        PendingUpdates = [];
        HasLoaded = false;
        Changed = null;
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }
}
