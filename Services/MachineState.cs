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

    /// <summary>Every update winget offers.</summary>
    public static IReadOnlyList<AppPackage> Upgrades { get; private set; } = [];

    /// <summary>False until the first refresh has landed.</summary>
    public static bool HasLoaded { get; private set; }

    /// <summary>Raised on the UI thread each time a refresh lands.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Reads the machine again and returns when the result is in. The token
    /// only stops the caller waiting; the read itself finishes regardless, so
    /// a page that goes away mid-read still leaves a fresh answer for the next.
    /// </summary>
    public static Task RefreshAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_current is null)
            {
                _currentStartedAt = Environment.TickCount64;
                _current = RunAsync();
                return _current.WaitAsync(ct);
            }

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

    private static async Task RunAsync()
    {
        try
        {
            // Sequential rather than parallel: two winget processes contend
            // on the same source database.
            var upgrades = await WingetService.ListUpgradesAsync().ConfigureAwait(false);
            var installed = await WingetService.ListInstalledAsync().ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                Upgrades = upgrades;
                Installed = installed;
                HasLoaded = true;
                Changed?.Invoke(null, EventArgs.Empty);
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

        foreach (var upgrade in Upgrades)
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

        var installs = Installed
            .Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            .Select(p => new WingetRow(p.Name, p.Id, p.Version, p.AvailableVersion, p.Source))
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
