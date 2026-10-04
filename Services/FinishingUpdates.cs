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
/// Kept for this session only. A restart of App Center is not a restart of the
/// app, but it is the one thing that clears a mark that turned out to be
/// wrong - an installer whose own version never matches what its source says,
/// for one.
/// </summary>
public static class FinishingUpdates
{
    private sealed record Entry(string Version, bool Windows);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

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
            Entries[id] = new Entry(version, windows);
        }
    }

    /// <summary>
    /// Marks a fresh read of the updates, and forgets every entry the read no
    /// longer bears out.
    /// </summary>
    public static void Apply(IReadOnlyList<AppPackage> upgrades)
    {
        lock (Gate)
        {
            var stillListed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in upgrades)
            {
                var finishing = Entries.TryGetValue(package.Id, out var entry)
                    && string.Equals(package.AvailableVersion, entry.Version, StringComparison.OrdinalIgnoreCase);

                package.IsFinishing = finishing;
                package.FinishesWithWindows = finishing && entry!.Windows;

                if (finishing)
                    stillListed.Add(package.Id);
            }

            foreach (var id in Entries.Keys.Where(id => !stillListed.Contains(id)).ToList())
                Entries.Remove(id);
        }
    }

    /// <summary>Forgets everything. For tests, which share the static.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}
