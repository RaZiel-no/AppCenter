using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// The packages App Center is standing on.
///
/// This app is framework-dependent: it runs on the shared .NET Desktop Runtime
/// rather than carrying its own. Updating that runtime means an installer
/// replacing files this process has open - and every command here passes
/// --silent, so Windows Installer cannot put up the "these files are in use"
/// prompt it would show a person. What it does instead is hand the job to the
/// Restart Manager, which closes the applications holding those files. App
/// Center is one of them, and WPF answers the resulting WM_QUERYENDSESSION by
/// shutting down: the window goes, with no error and nothing said. App Center's
/// own package does the same thing for the same reason, by way of an installer
/// running over a running copy of itself.
///
/// The update itself survives that. winget is a child process and nothing kills
/// it when its parent goes, so it finishes on its own. What is lost is the rest
/// of a batch, and any account of where the window went.
///
/// So these packages are named on the row, said out loud before the user
/// commits to anything, and left until last when a batch runs - by which point
/// every other package is already done.
/// </summary>
public static class SelfPackages
{
    /// <summary>
    /// App Center's own winget package, which updates by running an installer
    /// over a running copy of itself. The id is deploy.bat's PACKAGE_ID.
    /// </summary>
    private const string Own = "ArnsteinSkara.AppCenter";

    /// <summary>
    /// The ids that close the app.
    ///
    /// The .NET ones are pinned to the band this process is actually running
    /// on: <see cref="Environment.Version"/> is the runtime's own version, so a
    /// build moved to .NET 11 starts naming DesktopRuntime.11 without anyone
    /// having to remember. Every bundle in that band is listed, not only the
    /// desktop one - the SDK, the base runtime and the ASP.NET Core runtime all
    /// carry the same shared-framework MSIs underneath, and any of them
    /// replaces what this process has loaded.
    ///
    /// Being wrong here is not symmetrical. One package too many costs a
    /// sentence on a row and a place at the back of the queue; one too few
    /// closes the app with no warning, which is the whole of what this is for.
    ///
    /// Deliberately not here: Microsoft.AppInstaller. Updating winget breaks
    /// the tool a batch still has packages to get through, which is worth
    /// handling - but it does not close this app, and saying it does on a row
    /// would make every other row's warning worth less.
    /// </summary>
    private static readonly HashSet<string> Ids = Band(Environment.Version.Major);

    private static HashSet<string> Band(int major) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            $"Microsoft.DotNet.DesktopRuntime.{major}",
            $"Microsoft.DotNet.Runtime.{major}",
            $"Microsoft.DotNet.AspNetCore.{major}",
            $"Microsoft.DotNet.SDK.{major}",
            Own,
        };

    /// <summary>Whether acting on this package would close the app.</summary>
    public static bool Includes(string id) => Ids.Contains(id);

    /// <summary>
    /// Under the version line on a row, so it is read before the button is
    /// pressed rather than guessed at after the window has gone.
    /// </summary>
    public const string RowNote = "App Center closes while this installs";

    /// <summary>The same thing at length, for the dialog that asks.</summary>
    public const string Warning =
        "Windows will close App Center to replace files it is using. The update itself " +
        "carries on without it and finishes on its own, so reopen App Center afterwards.";

    /// <summary>
    /// The other direction, which is not an interruption the app comes back
    /// from: nothing is being put in place of what is being taken away.
    /// </summary>
    public const string RemovalWarning =
        "App Center runs on this. Removing it closes the app, and it may not start " +
        "again until this is put back.";

    /// <summary>
    /// The same packages with the ones that close the app moved to the end,
    /// and the order of everything else left alone.
    ///
    /// "Update all" works down the list the user is looking at, so putting them
    /// last here is what puts them last in the batch: everything that can be
    /// updated with the window still open is done first, and the one that takes
    /// the window with it is the last thing that happens. Ordering the list
    /// rather than the batch is deliberate - the two have to agree, or the run
    /// would be seen jumping over a row and coming back to it.
    /// </summary>
    public static List<AppPackage> LastInLine(IEnumerable<AppPackage> packages) =>
        packages.OrderBy(p => Includes(p.Id) ? 1 : 0).ToList();
}
