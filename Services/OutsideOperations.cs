using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AppCenter.Services;

/// <summary>
/// An install, update or uninstall winget is running that App Center did not
/// start, as far as its command line says.
/// </summary>
internal sealed record OutsideCommand(OperationKind Kind, string Id, string? Version)
{
    /// <summary>
    /// The key App Center's own command would have run under, so the rows find
    /// it and nothing is started against the same package on top of it: the id,
    /// or the id and version for an uninstall that names one - which is what
    /// App Center passes for a row that is one of several installed versions.
    /// </summary>
    public string Key => Version is null ? Id : $"{Id}@{Version}";
}

/// <summary>
/// Notices winget commands App Center did not start, and follows them as if it
/// had.
///
/// An install is not App Center's to stop. winget is a child process, nothing
/// kills it when the window goes, and it finishes on its own - which is right,
/// but a window opened afterwards starts with no operations and knows nothing
/// of it. Meanwhile winget runs one install or uninstall at a time across the
/// whole machine, so whatever that window then starts queues behind it, with
/// nothing on screen saying why. The same goes for winget run from a terminal.
///
/// So the running winget processes are looked at, and each one installing,
/// updating or removing a package is taken on as an operation: its rows go
/// busy, their buttons wait, and when it exits its outcome is said and the
/// machine is read again. Nothing more can be had - its output belongs to
/// whoever started it - so the bar pulses throughout.
///
/// None of this needs administrator rights. Reading another process's command
/// line, waiting on it and reading its exit code all go through the limited
/// query access Windows gives any process of the same user, which is what Task
/// Manager uses. A process that will not give even that is left alone.
///
/// App Center's own commands are recognised by already being in flight under
/// the same key, which is why the key is built exactly as theirs are.
/// </summary>
public static class OutsideOperations
{
    /// <summary>Processes already taken on, so each is followed once.</summary>
    private static readonly HashSet<int> Following = [];

    /// <summary>
    /// Takes on any winget command that is not already being followed. Called
    /// on the UI thread whenever the window comes to the front and whenever the
    /// machine has been read: often enough to catch a command started from a
    /// terminal or left behind by a closed window, and cheap enough - one
    /// process list - to do that often.
    /// </summary>
    public static void Scan()
    {
        // Not before the machine has been read: the names come from there,
        // and the window is activated for the first time well before that.
        if (!MachineState.HasLoaded)
            return;

        foreach (var process in Process.GetProcessesByName("winget"))
        {
            using (process)
            {
                if (Following.Contains(process.Id))
                    continue;

                TryFollow(process.Id);
            }
        }
    }

    private static void TryFollow(int pid)
    {
        var handle = Native.OpenProcess(Native.QueryLimited | Native.Synchronize, false, pid);

        if (handle.IsInvalid)
            return;

        if (Native.CommandLine(handle) is not { } commandLine
            || Parse(Native.Split(commandLine).Skip(1).ToList()) is not { } command
            || !OperationService.CanStart(command.Key))
        {
            handle.Dispose();
            return;
        }

        var started = OperationService.Start(
            command.Key, NameOf(command), command.Kind,
            async (_, _) =>
            {
                using (handle)
                {
                    await Native.WaitForExitAsync(handle).ConfigureAwait(false);
                    return new WingetResult(Native.ExitCode(handle), string.Empty, string.Empty);
                }
            },
            startedElsewhere: true);

        if (started is null)
        {
            handle.Dispose();
            return;
        }

        Following.Add(pid);
        OperationService.Finished += Forget;

        void Forget(object? sender, Operation finished)
        {
            if (finished != started)
                return;

            Following.Remove(pid);
            OperationService.Finished -= Forget;
        }
    }

    /// <summary>
    /// What to call the package: what winget listed it as, or the catalogue,
    /// or failing both the id - the last part of it, for the handles winget
    /// makes up for installs it could not match, where the rest is noise.
    /// </summary>
    private static string NameOf(OutsideCommand command)
    {
        var installed = MachineState.Installed
            .Where(p => string.Equals(p.Id, command.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (installed.FirstOrDefault(p => string.Equals(p.Version, command.Version, StringComparison.OrdinalIgnoreCase))
                is { } version)
            return version.NameAndVersion;

        if (installed.FirstOrDefault() is { } package)
            return package.Name;

        if (CatalogService.AllById().TryGetValue(command.Id, out var entry) && entry.Name.Length > 0)
            return entry.Name;

        return command.Id[(command.Id.LastIndexOf('\\') + 1)..];
    }

    /// <summary>The verbs that change the machine, under every name winget accepts for them.</summary>
    private static readonly Dictionary<string, OperationKind> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["install"] = OperationKind.Install,
        ["add"] = OperationKind.Install,
        ["upgrade"] = OperationKind.Update,
        ["update"] = OperationKind.Update,
        ["uninstall"] = OperationKind.Uninstall,
        ["remove"] = OperationKind.Uninstall,
        ["rm"] = OperationKind.Uninstall,
    };

    /// <summary>
    /// The options that take a value, so that value is not read as the query.
    /// Every other option is a switch.
    /// </summary>
    private static readonly HashSet<string> TakesValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--id", "--version", "-v", "--query", "-q", "--name", "--moniker", "--tag", "--command", "--cmd",
        "--source", "-s", "--scope", "--architecture", "-a", "--installer-type", "--locale",
        "--location", "-l", "--log", "-o", "--override", "--custom", "--header", "--rename",
        "--product-code", "--authentication-mode", "--authentication-account", "--manifest", "-m",
        "--pin", "--count", "-n",
    };

    /// <summary>
    /// Reads a winget command line, without the program itself. Null for
    /// anything that is not an install, update or uninstall of one package
    /// that can be told from the line alone - listings, searches, "upgrade
    /// --all", a local manifest, a query by name. Those either change nothing
    /// or change something App Center could only guess at, and a guess would
    /// put the wrong row to sleep.
    /// </summary>
    internal static OutsideCommand? Parse(IReadOnlyList<string> args)
    {
        OperationKind? kind = null;
        string? id = null, version = null, query = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? value = null;

            // "--id=Git.Git" as well as "--id Git.Git".
            if (arg.StartsWith('-') && arg.IndexOf('=') is > 0 and var eq)
            {
                value = arg[(eq + 1)..];
                arg = arg[..eq];
            }
            else if (TakesValue.Contains(arg) && i + 1 < args.Count)
            {
                value = args[++i];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--id":
                    id = value;
                    break;

                case "--version" or "-v":
                    version = value;
                    break;

                case "--query" or "-q":
                    query = value;
                    break;

                case "--manifest" or "-m" or "--all" or "-r" or "--recurse":
                    return null;

                case var _ when arg.StartsWith('-'):
                    break;

                // The first word that is not an option is the verb, and the one
                // after it the query.
                default:
                    if (kind is null)
                    {
                        if (!Verbs.TryGetValue(arg, out var verb))
                            return null;

                        kind = verb;
                    }
                    else
                    {
                        query ??= arg;
                    }

                    break;
            }
        }

        if (kind is not { } found)
            return null;

        // A query stands in for an id only when it looks like one: "Git.Git",
        // not "git". winget matches a name loosely, which App Center could
        // only guess at.
        id ??= query is not null && query.Contains('.') && !query.Contains(' ') ? query : null;

        if (string.IsNullOrWhiteSpace(id))
            return null;

        // App Center only names a version to tell apart the installs of one
        // package, which is only ever a question for an uninstall.
        return new OutsideCommand(found, id, found == OperationKind.Uninstall ? version : null);
    }

    private static class Native
    {
        public const int QueryLimited = 0x1000;
        public const int Synchronize = 0x100000;

        private const int ProcessCommandLineInformation = 60;
        private const int InfoLengthMismatch = unchecked((int)0xC0000004);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returned);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>
        /// The process's command line, or null if it will not say. The class
        /// asked for needs only limited query access, unlike reading it out of
        /// the process's memory, which needs a good deal more.
        /// </summary>
        public static string? CommandLine(SafeProcessHandle process)
        {
            var size = 1024;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(size);

                try
                {
                    var status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out var needed);

                    if (status == InfoLengthMismatch)
                    {
                        size = needed;
                        continue;
                    }

                    if (status != 0)
                        return null;

                    // A UNICODE_STRING, whose characters follow it in the same buffer.
                    var length = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);

                    return Marshal.PtrToStringUni(text, length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }

        /// <summary>A command line split the way the program it started will have split it.</summary>
        public static List<string> Split(string commandLine)
        {
            var argv = CommandLineToArgvW(commandLine, out var count);

            if (argv == IntPtr.Zero)
                return [];

            try
            {
                return Enumerable.Range(0, count)
                    .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty)
                    .ToList();
            }
            finally
            {
                LocalFree(argv);
            }
        }

        public static int ExitCode(SafeProcessHandle process) =>
            GetExitCodeProcess(process, out var code) ? code : -1;

        /// <summary>
        /// Waits for the process without holding a thread for however long an
        /// installer takes: the wait is registered with the thread pool.
        /// </summary>
        public static Task WaitForExitAsync(SafeProcessHandle process)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = new ProcessWait(process);

            RegisteredWaitHandle? registration = null;
            registration = ThreadPool.RegisterWaitForSingleObject(
                wait,
                (_, _) =>
                {
                    registration?.Unregister(null);
                    wait.Dispose();
                    done.TrySetResult();
                },
                null, Timeout.Infinite, executeOnlyOnce: true);

            return done.Task;
        }

        /// <summary>The process handle as something the thread pool can wait on. It does not own it.</summary>
        private sealed class ProcessWait : WaitHandle
        {
            public ProcessWait(SafeProcessHandle process) =>
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
