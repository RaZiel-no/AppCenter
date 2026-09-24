using System.Text.RegularExpressions;

namespace AppCenter.Services;

/// <summary>
/// What an exit code means, in words a user can act on.
///
/// winget's own last line is not much help: "0x800401f5 : Application not
/// found" is Windows naming an HRESULT, "Installer failed with exit code:
/// 2147942512" is a number in the wrong base, and "A newer version was
/// found, but the install technology is different" is accurate without
/// saying what to do about it. So the codes that come up in practice are
/// explained here - what happened, and what to do next - and the code itself
/// is kept on the end, because it is what the documentation and every search
/// result are indexed by.
///
/// Three families arrive through the same exit code. winget's own are
/// 0x8A15xxxx, documented in its return-codes page. Windows' HRESULTs -
/// 0x8007xxxx wrapping a Win32 error, 0x80073Cxx from MSIX deployment,
/// 0x8004xxxx from COM - come through when winget hands a failure up
/// untranslated. And an installer's own code is a small positive number,
/// where 1603 and its neighbours are Windows Installer's, while the single
/// digits mean different things to different installers.
///
/// One more wrinkle: when an installer fails, winget usually exits with its
/// generic 0x8A150006 and puts the installer's real code in its last line as
/// an unsigned decimal. That number is the one worth explaining, so it is
/// dug out of the line and read in place of winget's.
/// </summary>
public static partial class WingetErrors
{
    private const uint ShellExecInstallFailed = 0x8A150006;

    [GeneratedRegex(@"exit code:?\s*(?:0x(?<hex>[0-9a-f]{1,8})\b|(?<dec>-?\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerExitCode();

    /// <summary>
    /// The code as it should be written: winget's and Windows' failures are
    /// negative in an int and read as hex, which is how they are documented;
    /// an installer's own code is a small positive number and reads better in
    /// decimal.
    /// </summary>
    public static string Show(int code) => code < 0 ? $"0x{code:X8}" : code.ToString();

    /// <summary>
    /// A failed operation's summary: the explanation with the code on the
    /// end when the code is a known one, else winget's own last words behind
    /// the code, so an unfamiliar failure still says whatever winget said.
    /// </summary>
    public static string Summarise(int code, string said, bool uninstalling = false, string? output = null)
    {
        if (Explain(code, said, uninstalling, output) is { } explained)
            return explained;

        var shown = Show(code);

        return said.Length > 0 ? $"winget exited with {shown}. {said}" : $"winget exited with {shown}.";
    }

    /// <summary>
    /// The explanation, code included, for a failure winget reported with
    /// <paramref name="code"/> and <paramref name="said"/> as its last line;
    /// null when neither the code nor the installer's code is one this knows.
    ///
    /// The installer's code is looked for in the last line first, then in the
    /// whole of <paramref name="output"/>: winget follows "Installer failed with
    /// exit code: 1" with a line saying where the installer's log is, so the
    /// last line alone usually misses it.
    ///
    /// When it was the installer that failed and Windows is waiting to be
    /// restarted, that is said as well - see <see cref="PendingRestart"/>.
    /// </summary>
    public static string? Explain(int code, string said, bool uninstalling = false, string? output = null)
    {
        if (unchecked((uint)code) == ShellExecInstallFailed
            && (InstallerCode(said) ?? (output is null ? null : InstallerCode(output))) is { } inner)
        {
            // The installer's code, not winget's, is the one that says what
            // happened, and it is the one the publisher's own pages go by.
            if (Explain(inner, uninstalling) is { } fromInstaller)
                return WithRestart($"{fromInstaller} (installer returned {Show(inner)})");

            return WithRestart($"The installer failed with a code of its own and said nothing more. (installer returned {Show(inner)})");
        }

        if (Explain(code, uninstalling) is not { } explained)
            return null;

        // winget's own "the installer failed", or an installer's code handed
        // through as it was.
        var installerFailed = unchecked((uint)code) == ShellExecInstallFailed || code > 0;

        return installerFailed ? WithRestart($"{explained} ({Show(code)})") : $"{explained} ({Show(code)})";
    }

    /// <summary>
    /// Whether the failure was for want of administrator rights, so the same
    /// command run with them stands a chance. Read the same way as
    /// <see cref="Explain(int, string, bool, string?)"/>: the installer's own
    /// code when winget only says that the installer failed. Every code here is
    /// one whose explanation already says to try as administrator.
    /// </summary>
    public static bool WantsAdmin(int code, string said, string? output = null)
    {
        if (unchecked((uint)code) == ShellExecInstallFailed
            && (InstallerCode(said) ?? (output is null ? null : InstallerCode(output))) is { } inner)
            code = inner;

        return unchecked((uint)code) is 0x8A150019 or 0x80073D28 or 0x80070005 or 0x800702E4 or 740 or 5;
    }

    /// <summary>
    /// An installer's failure, with the pending restart on the end when there
    /// is one: many installers refuse to run until Windows has restarted, and
    /// say nothing about it but their exit code.
    /// </summary>
    private static string WithRestart(string explained) =>
        PendingRestart.IsPending
            ? $"{explained} Windows is also waiting to be restarted, and many installers will not run until it has: restart, then try again."
            : explained;

    /// <summary>
    /// The installer's own exit code from winget's "Installer failed with exit
    /// code: N" line, or null when the line is not that. winget mostly prints
    /// it as an unsigned decimal, so an HRESULT arrives as a ten-digit number
    /// and is folded back into the int it is documented as; an MSIX
    /// package's failure comes as hex instead ("exit code: 0x80073d28 : …").
    /// </summary>
    internal static int? InstallerCode(string said)
    {
        var match = InstallerExitCode().Match(said);

        if (!match.Success)
            return null;

        if (match.Groups["hex"].Success)
            return unchecked((int)Convert.ToUInt32(match.Groups["hex"].Value, 16));

        if (long.TryParse(match.Groups["dec"].Value, out var value) && value >= int.MinValue && value <= uint.MaxValue)
            return unchecked((int)value);

        return null;
    }

    /// <summary>
    /// The plain-words explanation of a code, or null for one this does not
    /// know. Uninstalling changes the meaning of a few - a missing program is
    /// the uninstaller rather than the installer - so the caller says which.
    /// </summary>
    public static string? Explain(int code, bool uninstalling = false) => unchecked((uint)code) switch
    {
        // winget: general.
        0x8A150001 => "winget hit an internal error. Try again; if it keeps happening, updating App Installer from the Microsoft Store usually clears it.",
        0x8A150003 => "winget could not carry the command through. Try again; running the same command in a terminal shows the full output if it keeps failing.",
        0x8A150005 or 0x8A15006A => "winget was stopped before it finished.",
        0x8A150006 => "The installer could not be started, or ran and failed without saying why. Declining the permission prompt does this, and so does an antivirus blocking the file. Try again and allow it when asked.",
        0x8A150007 => "This package's listing is in a newer format than the installed winget understands. Update App Installer from the Microsoft Store and try again.",
        0x8A150008 => "The installer could not be downloaded. Check the connection and try again; if it keeps failing, the download link in the package listing may be dead.",
        0x8A150010 => "None of this package's installers fit this machine, usually because of the Windows version, the processor, or whether it installs for one user or the whole machine.",
        0x8A150011 => "The downloaded installer is not the file the package listing describes. Usually the publisher replaced the file and the listing has not caught up yet; try again in a day or two.",
        0x8A150014 => "winget could not find this package in any of its sources. It may have been removed, or the source may need refreshing.",
        0x8A150016 => "More than one package matched, so winget did not pick one.",
        0x8A150019 => "This needs administrator rights. Run App Center as administrator and try again.",
        0x8A15001B or 0x8A15001C => "A policy on this machine blocks the Microsoft Store, so Store packages cannot be installed from here.",
        0x8A15001E => "The Microsoft Store could not install this package. Try installing it from the Store app itself.",
        0x8A15002B => "winget has no update it can apply to the installed copy. Either it could not tell which version is installed, or the installed one is already the newest its source has.",
        0x8A15002D => "The installer failed a security check, which normally means SmartScreen or an antivirus flagged it.",
        0x8A15002E or 0x8A150086 => "The download was cut short and the file is not what the listing says it should be. Check the connection and try again.",
        0x8A15002F => "Windows has no record of how to uninstall this package, so winget cannot do it. Try Windows Settings › Apps › Installed apps.",
        0x8A150030 => "The package's own uninstaller ran but reported a failure. Try Windows Settings › Apps › Installed apps, or run the uninstaller by hand.",
        0x8A15003A => "A Group Policy on this machine blocks winget from doing this.",
        0x8A150041 => "The package needs its licence terms accepted before it will install.",
        0x8A150045 or 0x8A15004B => "winget could not reach its package source. Check the connection and try again.",
        0x8A150049 => "The Windows Installer package failed to install.",
        0x8A15004F => "The version on offer is not newer than the one installed.",
        0x8A150050 => "winget cannot tell which version is installed, so it will not update it blind.",
        0x8A150052 => "The portable package could not be put in place. Check that nothing is holding its folder open and try again.",
        0x8A150054 => "A portable copy of this package from another source is already installed. Uninstall that one first.",
        0x8A150056 => "This installer refuses to run as administrator. Run App Center without administrator rights and try again.",
        0x8A150057 => "The portable package could not be removed. Check that it is not running and try again.",
        0x8A15005C => "The downloaded archive could not be unpacked. Try again.",
        0x8A15005F => "This package's installer has to be told which folder to install into, and App Center does not pick one for it. Update it from within the app itself, or run winget in a terminal with --location set to the folder it is installed in.",
        0x8A150060 => "The downloaded archive failed a malware scan and was not installed.",
        0x8A150061 => "A version of this package is already installed.",
        0x8A150068 => "This package is pinned in winget, which stops it being updated. Remove the pin with `winget pin remove` to update it.",
        0x8A150069 => "The installed copy is a Microsoft Store placeholder, not the full app. Install it from the Store first.",
        0x8A15006B or 0x8A150110 => "Something this package depends on could not be installed alongside it.",
        0x8A15006D => "A service winget needs is busy or unavailable. Try again in a minute.",
        0x8A150075 or 0x8A150076 or 0x8A150077 or 0x8A150078 => "The package source needs you to sign in, and that did not succeed.",
        0x8A15007D => "This package was installed for the current user only, and cannot be changed while running as administrator. Run App Center normally and try again.",
        0x8A15007F or 0x8A150080 or 0x8A150081 or 0x8A150082 or 0x8A150083 or 0x8A150084 or 0x8A150085 => "The Microsoft Store could not supply this package. Try installing it from the Store app itself.",
        0x8A15008E => "The new version comes as a different kind of installer from the one on this machine, so winget cannot update it in place. Uninstall it, then install the new version.",

        // winget: what the installer told it.
        0x8A150101 or 0x8A150111 or 0x80073D02 => "The app is running. Close it and try again.",
        0x8A150102 => "Another installation is already running. Wait for it to finish and try again.",
        0x8A150103 => "A file the installer needs to replace is in use. Close the app and try again.",
        0x8A150104 => "Something this package depends on is not installed.",
        0x8A150105 or 0x80070070 => "The disk is full. Free some space and try again.",
        0x8A150106 => "There is not enough memory to install. Close other programs and try again.",
        0x8A150107 => "The installer needs an internet connection. Connect and try again.",
        0x8A150108 => "The installer hit an error of its own and gave no detail. The publisher's support pages are the place to look.",
        0x8A15010A => "It did not go in, and will not until Windows has restarted. Restart, then try again.",
        0x8A15010C => "The installation was cancelled.",
        0x8A15010D => "Another version of this app is already installed, and the installer will not run alongside it. Uninstall that one first.",
        0x8A15010E => "A newer version than this one is already installed.",
        0x8A15010F => "Policies on this machine block this installation.",
        0x8A150113 => "This package does not support this version of Windows or this processor.",
        0x8A150114 => "The installer cannot update the existing copy in place. Uninstall it, then install the new version.",
        0x8A150115 => "The installer failed with an error of its own.",

        // Windows: COM and Win32, wrapped as HRESULTs.
        0x800401F5 => uninstalling
            ? "Windows could not find the uninstaller this package registered: it was probably deleted or moved by hand, so Windows still lists the app but cannot remove it. Uninstall it from Windows Settings › Apps › Installed apps, or install the same version again to put the uninstaller back, then uninstall."
            : "Windows could not find the program it was told to run. Try again; if it keeps happening, the package listing points at something that is not there.",
        0x80070002 or 0x80070003 => uninstalling
            ? "A file the uninstaller needed is missing, so it could not finish. Uninstall from Windows Settings › Apps › Installed apps instead."
            : "A file the installer needed is missing. Try again; if it keeps happening, the download may be incomplete.",
        0x80070005 => "Windows refused access. Run App Center as administrator, or check that nothing else has the files open.",
        0x80070020 => "A file it needed is open in another program. Close the app and try again.",
        0x800704C7 => "It was cancelled, usually by declining the permission prompt.",
        0x80070422 => "A Windows service this needs is disabled. The Windows Installer and Microsoft Store Install services are the usual ones.",
        0x800702E4 => Explain(740),
        0x80070490 =>"Windows could not find what it was asked to change, usually a registry entry or file that was removed by hand.",
        0x80070643 => Explain(1603),
        0x80070652 => Explain(1618),
        0x80072EE2 or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x80072F8F => "The download failed with a network error. Check the connection and try again.",
        0x80073CF3 => "A package this one depends on could not be found, so Windows would not install it.",
        0x80073CFB => "This package is already installed.",
        0x80073CFF or 0x80073D01 => "A policy on this machine blocks installing this kind of package.",
        0x80073D28 => "Windows will only install this package with administrator rights. Run App Center as administrator and try again.",
        0xC000013A => "It was interrupted before it finished.",

        // The installer's own code. Windows Installer's are unambiguous; the
        // single digits are read one way by Inno Setup and NSIS and another
        // by Windows, so those say so.
        1 => "The installer stopped with code 1, which most installers use for a general failure or a cancelled setup.",
        2 => "The installer stopped with code 2. Most installers use that for a setup that was cancelled or aborted; Windows uses it for a missing file. Running the installer by hand shows what it objects to.",
        5 => "The installer stopped with code 5. Windows uses that for access denied, and some installers for a cancelled setup. Try again as administrator.",
        740 => "The installer needs administrator rights. Run App Center as administrator and try again.",
        1223 => "It was cancelled, usually by declining the permission prompt.",
        1601 => "The Windows Installer service is not running. Restart Windows and try again.",
        1602 => "The installation was cancelled.",
        1603 => "The installer hit a fatal error. The usual causes are the app still running, leftovers of an older copy, or missing permissions. Close the app and try again; failing that, uninstall the old copy and install afresh.",
        1618 => "Another installation is already running, and Windows Installer runs one at a time. Wait for it to finish and try again.",
        1619 => "The installer package could not be opened, so the download may be corrupt. Try again.",
        1625 => "A policy on this machine blocks this installation.",
        1633 => "This installer does not support this version of Windows or this processor.",
        1638 => "Another version of this app is already installed, and the installer will not run alongside it. Uninstall that one first.",
        1639 => "The installer was given arguments it does not accept. The package listing is at fault rather than this machine.",

        _ => null,
    };
}
