using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AppCenter.Services;

namespace AppCenter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;

        AskWindowsToReopenUs();

        // Ahead of base.OnStartup, which is what creates MainWindow: the
        // palette has to be right before the first pixel is drawn.
        ThemeService.Apply(SettingsService.Current.Theme);

        base.OnStartup(e);
    }

    /// <summary>
    /// Asks Windows to start App Center again if an installer closes it.
    ///
    /// Updating the runtime this app runs on means replacing files it has open,
    /// and a silent install closes the process holding them rather than asking
    /// - see <see cref="SelfPackages"/>. This is the only way back from that:
    /// the Restart Manager relaunches whatever registered here once it has
    /// finished with the machine.
    ///
    /// The flags rule out every other reason Windows offers to restart a
    /// process. A crash or a hang should leave the app closed, and so should a
    /// reboot; an installer that shut the app down to get at its files is the
    /// one case worth coming back from, and it is what is left when those three
    /// are excluded.
    ///
    /// It is not a promise, which is why nothing in the UI makes one. The
    /// Restart Manager will not relaunch a process whose user does not match
    /// the installer's - "Application SID does not match Conductor SID" in its
    /// event log - and a machine-scope install elevates, so the app may simply
    /// stay closed. Windows also ignores the registration for the first minute
    /// of a process's life. All this does is make the good case possible.
    /// </summary>
    private static void AskWindowsToReopenUs()
    {
        const int NoCrash = 1;
        const int NoHang = 2;
        const int NoReboot = 8;

        try
        {
            // Null command line: the same exe, with no arguments - so a copy
            // started with --update-all comes back as a plain window, rather
            // than starting the batch it was in the middle of over again.
            RegisterApplicationRestart(null, NoCrash | NoHang | NoReboot);
        }
        catch (Exception)
        {
            // Nothing to fall back to and nothing worth interrupting a launch
            // over. The app simply will not let itself back in afterwards.
        }
    }

    /// <summary>
    /// A launch with --update-all exits with how its batch went, however the
    /// window was closed: with the batch's own code after --exit, and with
    /// "interrupted" when closed while it ran, rather than a 0 that says
    /// everything went through. See CommandLine.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        if (CommandLine.Outcome is { } code)
            e.ApplicationExitCode = code;

        base.OnExit(e);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "App Center hit an unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
