using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// Updates that went through and that winget lists all the same, because they
/// only take once something restarts. An MSIX app that is running - Windows
/// Terminal, Teams - has its update staged until it closes; an installer that
/// told winget Windows must restart has left the old version registered until
/// then. Either way the next read lists the same update again, and a row
/// offering Update for it invites running the same installer for nothing.
///
/// So each update that goes through is written down with the version it went
/// to, and every read of the machine marks the rows still offering exactly
/// that version as finishing rather than pending. An entry goes once winget
/// offers that package a different version - the restart happened and the
/// source has moved on - or once it is too old to believe (below). A package
/// that simply stops being listed keeps its entry: a read comes back short
/// when one source fails, and remembering an update that has taken costs
/// nothing, since an entry only ever matches the exact version it names.
///
/// A mark can be wrong: an installer that exits 0 and changes nothing has
/// winget say "Successfully installed" and list the same update again. The row
/// offers "Update again" for that, which forgets the mark first.
///
/// Kept in the settings file, so that closing App Center does not put an
/// Update button back on an app that is still waiting. Which means a mark that
/// turned out to be wrong - an installer whose own version never matches what
/// its source says, for one - needs a way out that does not depend on App
/// Center being restarted. Two: Windows having restarted since, by when every
/// app has been restarted too, so anything still listed did not take; and age,
/// for the machine that only ever sleeps or shuts down with Fast Startup.
/// Either way the row offers Update again.
/// </summary>
public static class FinishingUpdates
{
    /// <summary>How long a mark is believed without Windows restarting.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private static readonly object Gate = new();

    // Where the entries live and how they are kept, and the clocks, as
    // functions so a test can give it a dictionary and a time of its own.
    internal static Func<Dictionary<string, FinishingUpdate>> Store = () => SettingsService.Current.FinishingUpdates;
    internal static Action Persist = SettingsService.Save;
    internal static Func<DateTime> Now = () => DateTime.UtcNow;
    internal static Func<DateTime> BootedAt = () => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// Writes down that <paramref name="id"/> was just updated to
    /// <paramref name="version"/>, and whether winget said Windows has to
    /// restart before it counts.
    /// </summary>
    public static void Record(string id, string version, bool windows) => Record([(id, version, windows)]);

    /// <summary>The same for several at once, with one write of the settings.</summary>
    public static void Record(IReadOnlyCollection<(string Id, string Version, bool Windows)> updates)
    {
        var any = false;

        lock (Gate)
        {
            foreach (var (id, version, windows) in updates)
            {
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                    continue;

                Store()[Key(id)] = new FinishingUpdate(version, windows, Now());
                any = true;
            }
        }

        if (any)
            Persist();
    }

    /// <summary>Forgets one package's entry: the user is updating it again.</summary>
    public static void Forget(string id)
    {
        bool had;

        lock (Gate)
        {
            had = Store().Remove(Key(id));
        }

        if (had)
            Persist();
    }

    /// <summary>
    /// Marks a fresh read of the updates, and forgets the entries it no longer
    /// bears out.
    /// </summary>
    public static void Apply(IReadOnlyList<AppPackage> upgrades)
    {
        bool forgot;

        lock (Gate)
        {
            var store = Store();
            var bootedAt = BootedAt();
            var now = Now();

            // Forgotten: entries too old to believe, and entries for a package
            // winget now offers a different version of. Not entries for the
            // packages this read did not list - see the summary above.
            var gone = store
                .Where(e => e.Value.When <= bootedAt || now - e.Value.When >= Lifetime)
                .Select(e => e.Key)
                .ToHashSet();

            foreach (var package in upgrades)
            {
                var key = Key(package.Id);
                var finishing = store.TryGetValue(key, out var entry)
                    && !gone.Contains(key)
                    && string.Equals(package.AvailableVersion, entry.Version, StringComparison.OrdinalIgnoreCase);

                package.IsFinishing = finishing;
                package.FinishesWithWindows = finishing && entry!.Windows;

                if (entry is not null && !finishing)
                    gone.Add(key);
            }

            foreach (var key in gone)
                store.Remove(key);

            forgot = gone.Count > 0;
        }

        // Every read comes through here; the file is only written when it changes.
        if (forgot)
            Persist();
    }

    private static string Key(string id) => id.Trim().ToLowerInvariant();

    /// <summary>
    /// Gives the entries a dictionary of their own and no file to write, so a
    /// test never reads or touches the settings on the machine it runs on.
    /// </summary>
    internal static void UseScratch()
    {
        var scratch = new Dictionary<string, FinishingUpdate>();

        lock (Gate)
        {
            Store = () => scratch;
            Persist = () => { };
            Now = () => DateTime.UtcNow;
            BootedAt = () => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
    }

    /// <summary>Forgets everything, clocks included. For tests, which share the static.</summary>
    internal static void Clear() => UseScratch();
}
