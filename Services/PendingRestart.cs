using Microsoft.Win32;

namespace AppCenter.Services;

/// <summary>
/// Whether Windows is waiting to be restarted to finish something it has
/// already started - an update, or another installer's files.
///
/// Worth knowing because plenty of installers refuse to run until it has: the
/// .NET Framework developer packs check for it and quit with a bare exit code
/// of 1, which winget reports as the installer failing without saying why.
/// Nothing in what winget prints says a restart was the reason; this is the
/// only way to tell.
///
/// Two signals, both of which Windows clears itself on the restart: servicing
/// (<c>Component Based Servicing\RebootPending</c>) and Windows Update
/// (<c>WindowsUpdate\Auto Update\RebootRequired</c>). Not the pending file
/// renames, which on most machines are never empty and would make this true
/// all the time.
/// </summary>
public static class PendingRestart
{
    private const string Servicing = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    private const string WindowsUpdate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";

    /// <summary>Where the answer comes from. Tests put a fixed one in its place.</summary>
    internal static Func<bool> Probe = Read;

    public static bool IsPending => Probe();

    private static bool Read()
    {
        try
        {
            using var servicing = Registry.LocalMachine.OpenSubKey(Servicing);
            using var windowsUpdate = Registry.LocalMachine.OpenSubKey(WindowsUpdate);

            return servicing is not null || windowsUpdate is not null;
        }
        catch (Exception)
        {
            // Unreadable is not the same as pending; say nothing rather than
            // blame a restart that may not be owed.
            return false;
        }
    }
}
