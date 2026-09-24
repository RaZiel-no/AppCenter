using System.Diagnostics;
using System.Text.RegularExpressions;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// The games Steam installed, which `winget list` shows beside everything else
/// and which winget cannot take off the machine by itself.
///
/// Steam registers each game with Windows under "Steam App &lt;id&gt;", and
/// the uninstall command it registers is not an uninstaller: it is
/// <c>steam.exe steam://uninstall/&lt;id&gt;</c>, which asks the Steam client
/// to do it. winget runs that command and waits for the process to exit. If
/// Steam was already running, that process passes the request on and exits at
/// once, so winget reports success before anything has been removed. If Steam
/// was not running, that process becomes the Steam client, and winget waits
/// until the user quits Steam. Meanwhile it keeps holding the one-at-a-time
/// lock winget takes for any install or uninstall on the machine, so every
/// other install queues up behind a game uninstall that is going nowhere.
///
/// So a game is handed to Steam directly, the way its own uninstall entry
/// would, and winget is left out of it. Steam asks the user to confirm, and
/// removes the game itself.
/// </summary>
public static partial class SteamGames
{
    /// <summary>
    /// The id winget gives a Steam game: the registry key Steam made, under
    /// whichever hive and architecture it went in, e.g.
    /// <c>ARP\Machine\X64\Steam App 730</c>.
    /// </summary>
    [GeneratedRegex(@"^ARP\\[^\\]+\\[^\\]+\\Steam App (\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamAppId();

    /// <summary>Steam's own number for the game, or null if this is not a Steam game.</summary>
    public static string? AppIdOf(string wingetId) =>
        SteamAppId().Match(wingetId) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>What to ask before handing a game over to Steam.</summary>
    public static Question UninstallQuestion(string name) =>
        new(
            $"Uninstall {name}?",
            $"{name} is a Steam game, and Steam removes its own games. Steam will open " +
            "and ask you to confirm there.\n\n" +
            "Once Steam has removed it, check for updates on the Manage page to take it off the list.",
            "Open Steam");

    /// <summary>
    /// Asks Steam to uninstall the game, the same way its uninstall entry does.
    /// Returns as soon as the request is handed over; Steam carries on from there.
    /// </summary>
    public static void Uninstall(string appId) =>
        Process.Start(new ProcessStartInfo($"steam://uninstall/{appId}") { UseShellExecute = true })?.Dispose();
}
