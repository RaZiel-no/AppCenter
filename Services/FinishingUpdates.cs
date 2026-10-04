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
/// that version as finishing rather than pending. An entry lasts as long as
/// winget goes on listing that update: once the restart has happened the
/// version moves on, the row goes, and so does the entry.
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
    public static void Record(string id, string version, bool windows)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            return;

        lock (Gate)
        {
            Store()[Key(id)] = new FinishingUpdate(version, windows, Now());
        }

        Persist();
    }

    /// <summary>
    /// Marks a fresh read of the updates, and forgets every entry the read no
    /// longer bears out.
    /// </summary>
    public static void Apply(IReadOnlyList<AppPackage> upgrades)
    {
        bool forgot;

        lock (Gate)
        {
            var store = Store();
            var bootedAt = BootedAt();
            var now = Now();
            var stillListed = new HashSet<string>();

            foreach (var package in upgrades)
            {
                var key = Key(package.Id);
                var finishing = store.TryGetValue(key, out var entry)
                    && entry.When > bootedAt
                    && now - entry.When < Lifetime
                    && string.Equals(package.AvailableVersion, entry.Version, StringComparison.OrdinalIgnoreCase);

                package.IsFinishing = finishing;
                package.FinishesWithWindows = finishing && entry!.Windows;

                if (finishing)
                    stillListed.Add(key);
            }

            var gone = store.Keys.Where(key => !stillListed.Contains(key)).ToList();
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
