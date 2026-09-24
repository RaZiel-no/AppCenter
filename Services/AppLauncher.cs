using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AppCenter.Services;

/// <summary>One app the Start menu can start: what it is called there, and the id it starts by.</summary>
public sealed record StartEntry(string Name, string AppId);

/// <summary>
/// Starts an installed app, for the Open button on its page.
///
/// winget knows how to install a package and nothing about how to run it: no
/// manifest field names the program, and an installer puts its shortcuts
/// wherever it likes. What does know is the Start menu. Its app list -
/// <c>shell:AppsFolder</c> - holds every shortcut an installer made and every
/// Store app, each under an id Explorer can start it by, whatever kind it is:
/// a path for a plain shortcut, an AppUserModelID for one that has one, and
/// <c>PackageFamily!App</c> for a Store app. So the package is found there by
/// name, and started through Explorer the way a click in Start would.
///
/// Matched on the names the package goes by - the one winget lists it under
/// and the catalogue's - against the entry names, leaving out what installers
/// put beside the app: the uninstaller, the manual, the website.
/// </summary>
public static partial class AppLauncher
{
    /// <summary>What an installer puts in Start beside the app that is not the app.</summary>
    [GeneratedRegex(@"\b(uninstall|uninstaller|readme|read me|manual|manuals|documentation|docs|help|website|web site|release notes|license|licence|changelog)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotTheApp();

    /// <summary>An entry that opens a document or a web page rather than starting anything.</summary>
    [GeneratedRegex(@"^(https?:|mailto:)|\.(html?|txt|pdf|chm|url|md|rtf)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpensADocument();

    /// <summary>
    /// Everything the Start menu can start, read on a thread of its own: the
    /// shell's folders are COM objects that want a single-threaded apartment,
    /// and the read should not hold up the page. Empty when the shell will
    /// not say, which only costs the button.
    /// </summary>
    public static Task<IReadOnlyList<StartEntry>> ReadAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<StartEntry>>();

        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Read());
            }
            catch (Exception)
            {
                done.SetResult([]);
            }
        })
        {
            IsBackground = true,
            Name = "Start menu read",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return done.Task;
    }

    private static List<StartEntry> Read()
    {
        var entries = new List<StartEntry>();

        if (Type.GetTypeFromProgID("Shell.Application") is not { } type)
            return entries;

        dynamic shell = Activator.CreateInstance(type)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");

        // By index rather than foreach: the folder's items are an IDispatch
        // collection, and indexing it is the part of that the late binder is
        // surest of.
        dynamic items = folder.Items();
        int count = items.Count;

        for (var i = 0; i < count; i++)
        {
            dynamic item = items.Item(i);
            string name = item.Name;
            string id = item.Path;

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                entries.Add(new StartEntry(name, id));
        }

        return entries;
    }

    /// <summary>
    /// The entry that starts the package going by <paramref name="names"/>, or
    /// null when none of them is in Start.
    ///
    /// A Store app is found by its package family when that is known, which is
    /// exact. Otherwise by name, best first: an entry called just what the
    /// package is called; then one that goes on from it - "Qt Creator 15.0.1
    /// (Community)" for "Qt Creator 15.0.1"; then one the package's name goes
    /// on from - "Python 3.14" for "Python 3.14.5". Between equals, the
    /// shortest name, which is the app rather than something named after it.
    /// </summary>
    public static StartEntry? Find(
        IReadOnlyList<StartEntry> entries, IEnumerable<string> names, string? packageFamily = null)
    {
        if (packageFamily is not null
            && entries.FirstOrDefault(e => e.AppId.StartsWith(packageFamily + "!", StringComparison.OrdinalIgnoreCase))
                is { } storeApp)
            return storeApp;

        var wanted = names
            .Select(n => n.Trim())
            .Where(n => n.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return entries
            .Where(e => !NotTheApp().IsMatch(e.Name) && !OpensADocument().IsMatch(e.AppId))
            .Select(e => (Entry: e, Score: wanted.Max(n => (int?)Score(e.Name, n)) ?? 0))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .Select(x => x.Entry)
            .FirstOrDefault();
    }

    private static int Score(string entry, string name)
    {
        if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
            return 3;

        if (GoesOnFrom(entry, name))
            return 2;

        // The other way round only for an entry long enough to mean something:
        // "Git" is not what "GitHub Desktop" is called.
        if (entry.Length >= 4 && GoesOnFrom(name, entry))
            return 1;

        return 0;
    }

    /// <summary>Whether <paramref name="longer"/> is <paramref name="start"/> and then more words.</summary>
    private static bool GoesOnFrom(string longer, string start) =>
        longer.Length > start.Length
        && longer.StartsWith(start, StringComparison.OrdinalIgnoreCase)
        && longer[start.Length] is ' ' or '(' or '-' or '.';

    /// <summary>Starts the entry the way a click on it in Start would.</summary>
    public static void Start(StartEntry entry) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.AppId}") { UseShellExecute = true });
}
