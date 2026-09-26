using System.Text.RegularExpressions;
using System.Windows;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// A look ahead at the updates winget listed, for the ones it is going to
/// refuse.
///
/// winget's list compares versions and nothing else. Whether the new version
/// can actually go in over the installed copy - the same kind of installer,
/// the same scope, an architecture this machine has - is only checked when the
/// upgrade runs, and the commonest way to fail that check is the installer's
/// kind: an app that came in as an MSI, offered again as an EXE, or the other
/// way round. winget will not update it in place and says to uninstall and
/// install afresh. On a machine App Center did not set up, where nothing was
/// installed by winget, a fair share of the list can be like this, and every
/// one of them fails only after it is pressed.
///
/// Since winget 1.28.190, `list --details` says what kind of installer a
/// package came from, and `show` says what kind the new version comes as. Two
/// commands per package, a second or two each, run one at a time in the
/// background after the list has landed and never while something is
/// installing. The verdict is advice, not a refusal: a listing can carry more
/// than one installer and `show` prints the one winget would prefer, so a row
/// marked here keeps its Update button and gains a Reinstall beside it.
/// </summary>
public static class UpdateProbe
{
    private static readonly object Gate = new();

    /// <summary>What was found for each id at each offered version, for the session.</summary>
    private static readonly Dictionary<string, Verdict> Verdicts = new(StringComparer.OrdinalIgnoreCase);

    private static CancellationTokenSource? _cts;
    private static bool? _supported;

    /// <summary>What the look-ahead found for one package.</summary>
    internal readonly record struct Verdict(string InstalledKind, string OfferedKind, bool Mismatch);

    // The winget calls, as functions so a test can stand in for them.
    internal static Func<CancellationToken, Task<string>> WingetVersion = WingetService.GetVersionAsync;
    internal static Func<string, CancellationToken, Task<string>> Details = WingetService.ListDetailsAsync;
    internal static Func<string, CancellationToken, Task<string>> Show = WingetService.ShowOutputAsync;

    /// <summary>
    /// Whether winget is busy on the machine's behalf - installing something,
    /// or reading the machine again - when the probe should wait its turn.
    /// </summary>
    internal static Func<bool> MachineBusy = () => OperationService.RunningCount > 0 || MachineState.IsReading;

    /// <summary>
    /// The kinds of installer winget names, as `list --details` and `show`
    /// print them. Anything else on a line is not a kind.
    /// </summary>
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "inno", "nullsoft", "burn", "msi", "wix", "msix", "appx", "msstore", "portable", "zip",
    };

    /// <summary>
    /// Starts looking ahead at these updates, dropping whatever look-ahead was
    /// running for the list before. Called on the UI thread with each read of
    /// the machine. Rows already looked at this session are painted at once;
    /// the rest are looked at in the background, one winget process at a time.
    /// </summary>
    public static void Begin(IReadOnlyList<AppPackage> upgrades)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var todo = new List<AppPackage>();

        foreach (var package in upgrades)
        {
            // Only the ordinary updates: a pinned one is not going anywhere,
            // and a package with an unreadable version is reinstalled outright.
            // A Store listing offers its own kind of installer, which winget
            // handles on its own terms.
            if (package.Group != UpdateGroup.Pending || IsStore(package))
                continue;

            Verdict? known;
            lock (Gate)
            {
                known = Verdicts.TryGetValue(KeyOf(package), out var verdict) ? verdict : null;
            }

            if (known is { } found)
                Paint(package, found);
            else
                todo.Add(package);
        }

        if (todo.Count > 0)
            _ = RunAsync(todo, ct);
    }

    private static async Task RunAsync(List<AppPackage> todo, CancellationToken ct)
    {
        try
        {
            if (!await IsSupportedAsync(ct).ConfigureAwait(false))
                return;

            foreach (var package in todo)
            {
                ct.ThrowIfCancellationRequested();

                // A run this one replaced may have got to it in the meantime.
                Verdict? already;
                lock (Gate)
                {
                    already = Verdicts.TryGetValue(KeyOf(package), out var found) ? found : null;
                }

                if (already is { } known)
                {
                    await OnUiAsync(() => Paint(package, known)).ConfigureAwait(false);
                    continue;
                }

                await WhileIdleAsync(ct).ConfigureAwait(false);
                var installed = InstalledKind(await Details(package.Id, ct).ConfigureAwait(false));

                Verdict verdict;
                if (installed is null)
                {
                    // winget would not say, or this winget cannot: nothing to
                    // advise, and nothing to ask again this session.
                    verdict = new Verdict(string.Empty, string.Empty, false);
                }
                else
                {
                    await WhileIdleAsync(ct).ConfigureAwait(false);
                    var offered = OfferedKind(await Show(package.Id, ct).ConfigureAwait(false));

                    verdict = new Verdict(
                        installed,
                        offered ?? string.Empty,
                        offered is not null && !Compatible(installed, offered));
                }

                lock (Gate)
                {
                    Verdicts[KeyOf(package)] = verdict;
                }

                await OnUiAsync(() => Paint(package, verdict)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer list replaced this one, or the app is going away.
        }
        catch (Exception)
        {
            // The look-ahead is advice. A row it never reached is a row with
            // an Update button, which is what it had before.
        }
    }

    /// <summary>Waits while winget is installing something; two of them contend.</summary>
    private static async Task WhileIdleAsync(CancellationToken ct)
    {
        while (MachineBusy())
            await Task.Delay(1000, ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsSupportedAsync(CancellationToken ct)
    {
        if (_supported is { } known)
            return known;

        var version = await WingetVersion(ct).ConfigureAwait(false);
        _supported = VersionAtLeast(version, 1, 28, 190);

        return _supported.Value;
    }

    /// <summary>
    /// Whether "v1.29.380", as `winget --version` prints it, is at or past the
    /// given version. An unreadable answer is taken as too old: the probe is
    /// then quiet rather than wrong.
    /// </summary>
    internal static bool VersionAtLeast(string printed, int major, int minor, int build)
    {
        var match = Regex.Match(printed ?? string.Empty, @"(\d+)\.(\d+)\.(\d+)");
        if (!match.Success)
            return false;

        var have = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));

        return have.CompareTo((major, minor, build)) >= 0;
    }

    /// <summary>
    /// The kind of installer the installed copy came from, out of `list
    /// --details`: the value of its "Installer Category" line. Found by the
    /// value rather than the label, which a localised winget prints in its
    /// own language; the kinds themselves are not translated.
    /// </summary>
    internal static string? InstalledKind(string details) => KindsIn(details).FirstOrDefault();

    /// <summary>
    /// The kind of installer the new version comes as, out of `show`: its
    /// "Installer Type" line. An archive's own kind says nothing about what
    /// is inside it, so when winget also names the nested installer that is
    /// the answer, and an archive with no nested kind named is no answer.
    /// </summary>
    internal static string? OfferedKind(string show)
    {
        var kinds = KindsIn(show).ToList();

        return kinds.FirstOrDefault(k => !string.Equals(k, "zip", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> KindsIn(string output)
    {
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0)
                continue;

            var value = raw[(colon + 1)..].Trim();

            if (Kinds.Contains(value))
                yield return value.ToLowerInvariant();
        }
    }

    /// <summary>
    /// winget's own rule for updating in place: the two kinds have to belong
    /// to the same family. Anything it does not class is let through - the
    /// probe advises, and only where it is sure.
    /// </summary>
    internal static bool Compatible(string installed, string offered)
    {
        var a = Family(installed);
        var b = Family(offered);

        return a is null || b is null || a == b;
    }

    private static string? Family(string kind) => kind.ToLowerInvariant() switch
    {
        "exe" or "inno" or "nullsoft" or "burn" => "exe",
        "msi" or "wix" => "msi",
        "msix" or "appx" or "msstore" => "msix",
        "portable" => "portable",
        _ => null,
    };

    private static bool IsStore(AppPackage package) =>
        string.Equals(package.Source, "msstore", StringComparison.OrdinalIgnoreCase);

    private static string KeyOf(AppPackage package) => $"{package.Id}|{package.AvailableVersion}";

    private static void Paint(AppPackage package, Verdict verdict)
    {
        package.InstalledKind = verdict.InstalledKind;
        package.OfferedKind = verdict.OfferedKind;
        package.InstallerMismatch = verdict.Mismatch;
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

    /// <summary>Forgets everything. For tests, which share the static.</summary>
    internal static void Reset()
    {
        _cts?.Cancel();
        _cts = null;
        _supported = null;

        lock (Gate)
        {
            Verdicts.Clear();
        }

        WingetVersion = WingetService.GetVersionAsync;
        Details = WingetService.ListDetailsAsync;
        Show = WingetService.ShowOutputAsync;
        MachineBusy = () => OperationService.RunningCount > 0 || MachineState.IsReading;
    }
}
