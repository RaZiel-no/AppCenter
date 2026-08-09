using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppCenter.Services;

/// <summary>
/// Puts the Windows shell back when an installer takes it away.
///
/// A shell extension - 7-Zip, TortoiseGit, Total Commander, anything that adds
/// to the context menu - cannot have its DLLs replaced while explorer.exe has
/// them loaded. Every command here passes --silent, so Windows Installer cannot
/// ask a person to close anything and hands the job to the Restart Manager,
/// which closes the shell instead. Starting it again afterwards is the
/// installer's own business, and not all of them do it.
///
/// What that leaves is a machine with no taskbar and no desktop, nothing on
/// screen saying which package did it, and no obvious way back. App Center's own
/// window survives - it does not depend on the shell - so it is in a position to
/// both say what happened and undo it.
///
/// The shell is therefore looked at either side of every package. One that was
/// there before and is gone after is started again, and the package it happened
/// to is named. One that was already gone before is left alone: the user may
/// have closed it themselves, and an update is not the moment to overrule that.
/// </summary>
public sealed class ShellWatch
{
    /// <summary>
    /// How long the shell is given to come back on its own. The Restart Manager
    /// restarts what it closed, and the installers that do it themselves are not
    /// far behind - starting a second shell on top of one already on its way
    /// back is how a File Explorer window appears out of nowhere.
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(4);

    private readonly Func<bool> _isUp;
    private readonly Action _start;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    private bool _wasUp;

    public ShellWatch()
        : this(NativeShell.IsUp, NativeShell.Start, Task.Delay)
    {
    }

    /// <summary>
    /// The seam the tests use. Asking Windows for its shell and starting one are
    /// both things a test cannot do to the machine it is running on.
    /// </summary>
    internal ShellWatch(
        Func<bool> isUp,
        Action start,
        Func<TimeSpan, CancellationToken, Task> wait)
    {
        _isUp = isUp;
        _start = start;
        _wait = wait;
    }

    /// <summary>Notes whether there was a shell before the installer ran.</summary>
    public void Before() => _wasUp = _isUp();

    /// <summary>
    /// Starts the shell again if the installer took it and did not give it back.
    /// True only when it actually had to, which is the only case worth a word.
    /// </summary>
    public async Task<bool> AfterAsync(CancellationToken ct = default)
    {
        if (!_wasUp || _isUp())
            return false;

        await _wait(Grace, ct).ConfigureAwait(false);

        // It came back on its own after all, which is the ordinary case and
        // wants no comment.
        if (_isUp())
            return false;

        try
        {
            _start();
        }
        catch (Exception)
        {
            // There is nothing else to try. Saying nothing is better than
            // claiming to have fixed something that is still broken.
            return false;
        }

        return true;
    }

    /// <summary>
    /// What to say about it afterwards. Written so it reads the same for one
    /// package as for the several a long batch can get through.
    /// </summary>
    public static string Note(IEnumerable<string> packages) =>
        $"Started Windows Explorer again after {string.Join(", ", packages)} closed it.";

    private static class NativeShell
    {
        /// <summary>
        /// Whether Windows has a shell, asked of Windows rather than worked out
        /// by looking for a process called explorer.exe: a File Explorer window
        /// opened in its own process carries that name too, and would answer for
        /// a shell that is not there.
        /// </summary>
        public static bool IsUp() => GetShellWindow() != IntPtr.Zero;

        /// <summary>
        /// Started the way Task Manager's "Run new task" starts it. Not through
        /// ShellExecute, which is the shell's own machinery and is exactly what
        /// is missing at this point; and App Center runs as the user rather than
        /// elevated, so the shell that comes back is the user's own.
        /// </summary>
        public static void Start() =>
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false })?.Dispose();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
    }
}
