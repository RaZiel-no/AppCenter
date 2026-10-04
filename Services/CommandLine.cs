using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;

namespace AppCenter.Services;

/// <summary>"Update all", asked for on the command line rather than with the button.</summary>
/// <param name="WithoutAdmin">Leave out the updates Windows would ask administrator permission for.</param>
/// <param name="Exit">Close App Center once the batch is done.</param>
public sealed record UpdateAllRequest(bool WithoutAdmin, bool Exit);

/// <summary>
/// What App Center does with its command line:
///
///     AppCenter.exe --update-all [--without-admin] [--exit]
///     AppCenter.exe --help
///
/// The window opens on Manage and starts the same batch "Update all" does,
/// without the question - whoever typed the command has answered it. --help
/// lists the options and opens no window, whatever else is on the line.
/// Anything else on the line is ignored, and a launch with neither opens the
/// window as it always has.
///
/// The exit code says how it went, for a script or a scheduled task that waits
/// for it (`start /wait`, `Start-Process -Wait`). See <see cref="Done"/> and the
/// two after it.
///
/// When App Center is already open, the request is handed to that window
/// rather than started in a second one beside it: two batches at once would
/// have two winget processes contending over the same source database. The
/// launch that handed it over waits for the batch and exits with its code, and
/// the open window stays open whatever --exit said - it was not this launch's
/// to close.
/// </summary>
public static class CommandLine
{
    /// <summary>Every update went through, or there was none to do.</summary>
    public const int Done = 0;

    /// <summary>The batch ran, and at least one update did not go through.</summary>
    public const int SomeFailed = 1;

    /// <summary>
    /// Nothing was started: winget could not be run or read, a batch or another
    /// operation was already running, or - with --without-admin - it could not
    /// be told which updates need administrator permission.
    /// </summary>
    public const int CouldNotRun = 2;

    /// <summary>
    /// App Center closed while the batch was running - by hand, or by an
    /// installer replacing files it had open - so some updates may have gone in
    /// and some not. The window shows which, once opened again.
    /// </summary>
    public const int Interrupted = 3;

    /// <summary>What this launch was asked to do, or null for an ordinary launch.</summary>
    public static UpdateAllRequest? Request { get; set; }

    /// <summary>
    /// What this launch exits with if it closes now, or null for an ordinary
    /// launch: <see cref="CouldNotRun"/> until the batch starts,
    /// <see cref="Interrupted"/> while it runs, and its own code once done. The
    /// app takes it on exit however the window was closed (App.OnExit).
    /// </summary>
    public static int? Outcome { get; set; }

    public static UpdateAllRequest? Parse(IReadOnlyList<string> args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        return Has("--update-all") ? new UpdateAllRequest(Has("--without-admin"), Has("--exit")) : null;
    }

    /// <summary>The exit code for a batch that ran to its end.</summary>
    public static int ExitCodeFor(Operation batch) => batch.Failed ? SomeFailed : Done;

    // ---------------------------------------------------------------
    // Help
    // ---------------------------------------------------------------

    /// <summary>The spellings a hand tries first, and the one Windows taught it.</summary>
    private static readonly string[] HelpFlags = ["--help", "-h", "-?", "/?"];

    /// <summary>
    /// Whether the line asks for the options to be listed. Checked ahead of
    /// everything else on it: a line with --help on it does nothing but that.
    /// </summary>
    public static bool AsksForHelp(IReadOnlyList<string> args) =>
        args.Any(a => HelpFlags.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The options and the exit codes, as --help prints them. Plain ASCII,
    /// since a console's code page is anyone's guess, and under eighty
    /// columns, which is a console's width when nothing has been done to it.
    /// </summary>
    public static string Help => $"""
        App Center {AppInfo.Version}
        {AppInfo.Description}

        Usage:
          AppCenter.exe
          AppCenter.exe --update-all [--without-admin] [--exit]
          AppCenter.exe --help

        Without options, App Center opens as usual.

        Options:
          --update-all      Open on Manage and start "Update all" without asking.
                            If App Center is already open, that window does it.
          --without-admin   Leave out the apps installed for every user of the PC,
                            which Windows would ask administrator permission for.
                            A run started this way needs nobody there.
          --exit            Close App Center when the updates are done. Without it
                            the window stays open on the results.
          --help            Print this and exit. Also -h, -? and /?.

        Exit codes, with --update-all:
          0  every update went through, or there was nothing to update
          1  at least one update did not go through
          2  nothing was started: winget could not be run, something else was
             already running, or --without-admin could not tell which updates
             need administrator permission
          3  App Center closed while the updates ran; some may have gone in

        A shell does not wait for a Windows app. To read the exit code, use
        "start /wait AppCenter.exe --update-all --exit" in cmd, or
        Start-Process AppCenter.exe '--update-all','--exit' -Wait -PassThru
        in PowerShell.

        """;

    /// <summary>
    /// Prints <see cref="Help"/> and returns the code to exit with. A Windows
    /// app has no console of its own, so where the text goes depends on how
    /// the launch was made. Output sent to a file or a pipe is written to;
    /// a console the command was typed in is borrowed for the write, and the
    /// text starts on a line of its own because the prompt is already back,
    /// a shell not waiting for a Windows app; and from Run or a shortcut,
    /// where there is neither, a message box shows it.
    /// </summary>
    public static int ShowHelp()
    {
        var text = Help;

        if (HasStandardOutput())
        {
            Console.Out.Write(text);
            Console.Out.Flush();
            return Done;
        }

        if (AttachConsole(AttachParentProcess))
        {
            try
            {
                Console.Out.WriteLine();
                Console.Out.Write(text);
                Console.Out.Flush();
            }
            finally
            {
                FreeConsole();
            }

            return Done;
        }

        MessageBox.Show(text, "App Center", MessageBoxButton.OK, MessageBoxImage.Information);
        return Done;
    }

    /// <summary>
    /// Whether the launch handed this process somewhere to write to. A Windows
    /// app started from a console gets no handles unless its output was
    /// redirected; one started with a redirect gets the file, or the pipe.
    /// Found out before attaching to a console, since attaching would put the
    /// console's handles in their place and send the text to the screen when
    /// a file was asked for.
    /// </summary>
    private static bool HasStandardOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != InvalidHandleValue;
    }

    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = unchecked((uint)-1);
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    // ---------------------------------------------------------------
    // Handing over to the open window
    // ---------------------------------------------------------------

    // Only between unelevated copies, both ways. An open window taking orders
    // from any process of the user's would let one without administrator
    // rights have an elevated App Center run installers; and an elevated
    // launch handing over to an unelevated window would quietly lose the
    // rights it was started with. An elevated launch runs in its own window.
    internal static Func<bool> Elevated = () => Environment.IsPrivilegedProcess;

    /// <summary>One pipe per user and session: the window on this desktop, not someone else's.</summary>
    internal static Func<string> PipeName = () =>
        $"ArnsteinSkara.AppCenter.{WindowsIdentity.GetCurrent().User?.Value}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Hands the request to an App Center already open, waits for its batch,
    /// and returns the exit code. Null when there is no such window to hand
    /// it to, and this launch should run it itself.
    /// </summary>
    public static int? HandOff(UpdateAllRequest request)
    {
        if (Elevated())
            return null;

        var name = PipeName();

        // Looked for before connecting: Connect spins out its whole timeout
        // when the pipe does not exist, and most launches have no window open.
        // Reading the pipe directory takes a few milliseconds.
        if (!PipeExists(name))
            return null;

        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);

            // A window that has the pipe takes the connection at once; the
            // timeout is for one that closed between the look and this.
            pipe.Connect(2000);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            writer.WriteLine(request.WithoutAdmin ? "--update-all --without-admin" : "--update-all");

            // No answer at all: the window closed before it could give one.
            return int.TryParse(reader.ReadLine(), out var code) ? code : Interrupted;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            // Connected, and the window went away before it answered - closed,
            // or closed by an installer. Whether the batch finished is not
            // known here.
            return Interrupted;
        }
    }

    private static bool PipeExists(string name)
    {
        try
        {
            var path = @"\\.\pipe\" + name;
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Then Connect finds out the slow way.
            return true;
        }
    }

    /// <summary>The pipe taken at launch, before the window exists to serve it.</summary>
    private static NamedPipeServerStream? _reserved;

    /// <summary>
    /// Takes the pipe as early as a launch can, so that a second launch a
    /// moment later finds this one rather than running a batch of its own
    /// beside it. The connection waits until <see cref="Listen"/> serves it.
    /// Does nothing when another copy has the pipe - that one is the window.
    /// </summary>
    public static void Reserve()
    {
        if (Elevated())
            return;

        try
        {
            _reserved = Create(firstInstance: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another copy has it.
        }
    }

    /// <summary>
    /// Takes requests handed over by later launches, for as long as the app
    /// runs. <paramref name="run"/> is handed each one and returns its exit
    /// code; it is called off the UI thread. Does nothing when another copy is
    /// already listening - that one is the window requests go to.
    /// </summary>
    public static void Listen(Func<UpdateAllRequest, Task<int>> run)
    {
        if (Elevated())
            return;

        var first = _reserved;
        _reserved = null;

        if (first is null)
        {
            try
            {
                first = Create(firstInstance: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        _ = ServeAsync(first, run);
    }

    private static NamedPipeServerStream Create(bool firstInstance) => new(
        PipeName(),
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private static async Task ServeAsync(NamedPipeServerStream pipe, Func<UpdateAllRequest, Task<int>> run)
    {
        NamedPipeServerStream? waiting = pipe;

        try
        {
            while (true)
            {
                await waiting!.WaitForConnectionAsync().ConfigureAwait(false);

                // This caller is answered whatever happens next - which may
                // take as long as a batch - and its instance is its own from
                // here on, not this loop's to dispose.
                var connected = waiting;
                waiting = null;

                try
                {
                    // A fresh instance for the next caller, which has to find
                    // someone to tell it a batch is already running.
                    waiting = Create(firstInstance: false);
                }
                finally
                {
                    _ = AnswerAsync(connected, run);
                }
            }
        }
        catch (Exception)
        {
            // The app is closing, or no further instance could be made. Later
            // launches run in a window of their own.
            if (waiting is not null)
                await waiting.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AnswerAsync(NamedPipeServerStream pipe, Func<UpdateAllRequest, Task<int>> run)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                var request = line is null ? null : Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

                var code = request is null ? CouldNotRun : await run(request).ConfigureAwait(false);

                await writer.WriteLineAsync(code.ToString()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The launch that asked stopped waiting. The batch goes on.
            }
        }
    }
}
