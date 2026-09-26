using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// What App Center installed over the packages whose version winget cannot
/// read, so their rows can say so.
///
/// Some installers never write a version to Add/Remove Programs. winget lists
/// such a package as "Unknown", sorts unknown below every real version, and so
/// offers it as an update whenever its source has any version at all - and
/// goes on offering it after the update has gone in, because nothing it can
/// read has changed. Without a memory of its own, the app would show the same
/// row with the same button for ever, and "Update all" would run the same
/// installer on every visit.
///
/// So each time such an update goes through, the version it installed is
/// written down beside the id, and the next read of the machine paints it back
/// onto the row: "2.0 was installed on 26 September". Kept in the settings
/// file; it is the user's own history, and small.
/// </summary>
public static class UpdateMemory
{
    private static readonly object Gate = new();

    // Where the memory lives and how it is kept, as functions so a test can
    // give it a dictionary of its own rather than the user's settings file.
    internal static Func<Dictionary<string, RecordedInstall>> Store = () => SettingsService.Current.UnknownVersionInstalls;
    internal static Action Persist = SettingsService.Save;

    /// <summary>What the clock says, for the date on the row. A test sets it.</summary>
    internal static Func<DateTime> Now = () => DateTime.Now;

    /// <summary>Writes down that <paramref name="version"/> of <paramref name="id"/> went in just now.</summary>
    public static void Record(string id, string version)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            return;

        lock (Gate)
        {
            Store()[Key(id)] = new RecordedInstall(version, Now());
        }

        Persist();
    }

    /// <summary>What was last installed over this package, or null.</summary>
    public static RecordedInstall? Recorded(string id)
    {
        lock (Gate)
        {
            return Store().TryGetValue(Key(id), out var recorded) ? recorded : null;
        }
    }

    /// <summary>
    /// Paints the memory onto a fresh read of the updates. Only the rows whose
    /// version winget cannot read carry one; for every other row the memory
    /// is beside the point, since winget can see for itself what went in.
    /// </summary>
    public static void Apply(IEnumerable<AppPackage> upgrades)
    {
        foreach (var package in upgrades)
            package.RecordedInstall = package.HasUnknownVersion ? Recorded(package.Id) : null;
    }

    private static string Key(string id) => id.Trim().ToLowerInvariant();

    /// <summary>
    /// Gives the memory a dictionary of its own and no file to write, so a
    /// test never reads or touches the settings on the machine it runs on.
    /// </summary>
    internal static void UseScratch()
    {
        var scratch = new Dictionary<string, RecordedInstall>();

        lock (Gate)
        {
            Store = () => scratch;
            Persist = () => { };
            Now = () => DateTime.Now;
        }
    }
}
