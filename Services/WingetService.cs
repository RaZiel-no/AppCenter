using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// What an exit code says about restarting Windows.
///
/// There is no telling in advance: winget publishes nothing about it, and no
/// manifest field says a package will want the machine restarted. The installer
/// finding a file locked is what decides it, which is why the only honest moment
/// to say so is after the installer has run.
/// </summary>
public enum RestartNeed
{
    /// <summary>Nothing to restart for.</summary>
    None,

    /// <summary>It went in, but Windows has to restart before it is really finished.</summary>
    ToFinish,

    /// <summary>It did not go in, and will not until Windows has been restarted.</summary>
    ToRetry,

    /// <summary>The installer has already asked Windows to restart.</summary>
    Underway,
}

public sealed record WingetResult(int ExitCode, string StdOut, string StdErr)
{
    // winget's own documented return codes.
    private const int RebootRequiredToFinish = unchecked((int)0x8A150109);
    private const int RebootRequiredForInstall = unchecked((int)0x8A15010A);
    private const int RebootInitiated = unchecked((int)0x8A15010B);

    // Windows Installer's, for the packages whose manifests count them as
    // success codes and so hand them through winget untranslated.
    private const int RebootRequiredMsi = 3010;   // ERROR_SUCCESS_REBOOT_REQUIRED
    private const int RebootInitiatedMsi = 1641;  // ERROR_SUCCESS_REBOOT_INITIATED

    public bool Success => ExitCode == 0;

    /// <summary>Whether Windows has to restart, and whether it already is.</summary>
    public RestartNeed Restart => ExitCode switch
    {
        RebootRequiredToFinish or RebootRequiredMsi => RestartNeed.ToFinish,
        RebootRequiredForInstall => RestartNeed.ToRetry,
        RebootInitiated or RebootInitiatedMsi => RestartNeed.Underway,
        _ => RestartNeed.None,
    };

    /// <summary>
    /// Whether the package went in. Wanting a restart afterwards is not a
    /// failure: the files are in place and winget says as much - "Restart your
    /// PC to finish installation" - it just cannot be finished from here.
    /// Counting that as failed puts a red line under a package that installed
    /// correctly, and names it in the tally as one that did not.
    /// </summary>
    public bool Installed => Success || Restart is RestartNeed.ToFinish or RestartNeed.Underway;
}

/// <summary>One parsed row of winget's fixed-width table output.</summary>
public sealed record WingetRow(
    string Name,
    string Id,
    string Version,
    string Available,
    string Source);

/// <summary>
/// What `winget list` said about one package: a row per installed version,
/// or none. Read by a package's own page - from the one read of the machine
/// everything shares, see <c>MachineState.StateOf</c> - to decide between
/// Install, Update and Uninstall, and to say which version is on the machine.
/// </summary>
public sealed record InstallState(IReadOnlyList<WingetRow> Installs)
{
    public static readonly InstallState NotInstalled = new([]);

    public bool IsInstalled => Installs.Count > 0;

    /// <summary>
    /// The installed versions, newest first as far as a string sort can tell,
    /// with blanks and duplicates dropped. Usually one; two or more when a
    /// package is legitimately installed side by side, like an SDK.
    /// </summary>
    public IReadOnlyList<string> InstalledVersions =>
        Installs
            .Select(r => r.Version.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, VersionOrder.Instance)
            .ToList();

    /// <summary>
    /// The update winget offers, or empty. winget only ever fills Available on
    /// one row per id - the newest install - which is the one an upgrade acts on.
    /// </summary>
    public string AvailableVersion =>
        Installs.Select(r => r.Available.Trim()).FirstOrDefault(v => v.Length > 0) ?? string.Empty;

    public bool HasUpdate => AvailableVersion.Length > 0;
}

/// <summary>
/// Orders version strings the way a person reads them - "10.0.2" after
/// "9.0.317" - rather than the way a string sort does. Numeric runs compare as
/// numbers and everything else as text; "1.2" comes before "1.2.1" but after
/// "1.2-rc.1", because a trailing word is a pre-release. Not a full semver
/// parser: winget's own versions are not semver either, and this only has to
/// order what one id's installs look like.
/// </summary>
public sealed class VersionOrder : IComparer<string>
{
    public static readonly VersionOrder Instance = new();

    private static readonly Regex Part = new(@"\d+|[^\d.\-+ ]+", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        var a = Part.Matches(x ?? string.Empty);
        var b = Part.Matches(y ?? string.Empty);
        var shared = Math.Min(a.Count, b.Count);

        for (var i = 0; i < shared; i++)
        {
            var pa = a[i].Value;
            var pb = b[i].Value;

            var na = long.TryParse(pa, out var va);
            var nb = long.TryParse(pb, out var vb);

            var result = (na, nb) switch
            {
                (true, true) => va.CompareTo(vb),
                // A number outranks a word in the same place: 10.0.100 is
                // newer than 10.0.rc1.
                (true, false) => 1,
                (false, true) => -1,
                _ => string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase),
            };

            if (result != 0)
                return result;
        }

        if (a.Count == b.Count)
            return 0;

        // One is a prefix of the other. What the longer one goes on with
        // decides: a number extends it (1.2 < 1.2.1), a word pre-releases it
        // (1.2-rc.1 < 1.2).
        var (longer, sign) = a.Count > b.Count ? (a, 1) : (b, -1);
        var next = longer[shared].Value;

        return long.TryParse(next, out _) ? sign : -sign;
    }
}

/// <summary>
/// Thin async wrapper over winget.exe.
///
/// winget has no machine-readable output for search/list, so we parse the
/// fixed-width table it prints. The parser locates the dashed separator
/// line, reads the header above it to find where each column starts, and
/// slices data rows at those offsets - which keeps names containing spaces
/// intact and survives the columns being reordered between winget versions.
/// </summary>
public static class WingetService
{
    private static readonly Regex AnsiEscape =
        new("\u001B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    // NOTE: the pattern above is built at runtime so the ESC byte is explicit.

    /// <summary>Progress spinner frames winget emits even with interactivity off.</summary>
    private static readonly Regex SpinnerOnly =
        new(@"^[\s\-\\|/█▒█▒]*$", RegexOptions.Compiled);

    public static bool IsAvailable { get; private set; } = true;

    private static readonly string[] CommonArgs =
    [
        "--accept-source-agreements",
        "--disable-interactivity",
    ];

    // ---------------------------------------------------------------
    // Process plumbing
    // ---------------------------------------------------------------

    private static async Task<WingetResult> RunAsync(
        IEnumerable<string> args,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            var line = Clean(e.Data);
            stdout.AppendLine(line);

            if (onOutputLine is not null && !string.IsNullOrWhiteSpace(line) && !SpinnerOnly.IsMatch(line))
                onOutputLine(line);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(Clean(e.Data));
        };

        try
        {
            // Starting a process is tens of milliseconds of kernel work -
            // too long for the UI thread, which is what usually calls this.
            await Task.Run(process.Start, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsAvailable = false;
            return new WingetResult(-1, string.Empty, $"Could not start winget.exe: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new WingetResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process already died on its own; nothing to clean up.
        }
    }

    private static string Clean(string line) =>
        AnsiEscape.Replace(line, string.Empty).Replace("\b", string.Empty).TrimEnd();

    // ---------------------------------------------------------------
    // Table parsing
    // ---------------------------------------------------------------

    internal static List<WingetRow> ParseTable(string stdout)
    {
        var rows = new List<WingetRow>();
        var lines = stdout.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var separator = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length >= 10 && trimmed.All(c => c == '-'))
            {
                separator = i;
                break;
            }
        }

        if (separator <= 0)
            return rows;

        var columns = ReadColumns(lines[separator - 1]);
        if (columns.Count == 0)
            return rows;

        // Data rows run contiguously until the first blank line; anything
        // after that is a summary note, not part of the table.
        for (var i = separator + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                break;

            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < columns.Count; c++)
            {
                var start = columns[c].Start;
                var end = c + 1 < columns.Count ? columns[c + 1].Start : line.Length;

                if (start >= line.Length)
                {
                    cells[columns[c].Name] = string.Empty;
                    continue;
                }

                end = Math.Min(end, line.Length);
                cells[columns[c].Name] = line[start..end].Trim();
            }

            var id = Get(cells, "Id", columns, 1);
            var name = Get(cells, "Name", columns, 0);

            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                continue;

            rows.Add(new WingetRow(
                name,
                id,
                Get(cells, "Version", columns, 2),
                Get(cells, "Available", columns, -1),
                Get(cells, "Source", columns, -1)));
        }

        return rows;
    }

    private readonly record struct Column(string Name, int Start);

    private static List<Column> ReadColumns(string header)
    {
        var columns = new List<Column>();
        for (var i = 0; i < header.Length; i++)
        {
            if (char.IsWhiteSpace(header[i]))
                continue;

            if (i > 0 && !char.IsWhiteSpace(header[i - 1]))
                continue;

            var end = i;
            while (end < header.Length && !char.IsWhiteSpace(header[end]))
                end++;

            columns.Add(new Column(header[i..end], i));
        }

        return columns;
    }

    /// <summary>
    /// Look a cell up by its English header name, falling back to a column
    /// index so a localised winget still yields usable data.
    /// </summary>
    private static string Get(
        Dictionary<string, string> cells,
        string name,
        List<Column> columns,
        int fallbackIndex)
    {
        if (cells.TryGetValue(name, out var value))
            return value;

        if (fallbackIndex >= 0 && fallbackIndex < columns.Count)
            return cells.TryGetValue(columns[fallbackIndex].Name, out var byIndex) ? byIndex : string.Empty;

        return string.Empty;
    }

    // ---------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------

    public static async Task<List<AppPackage>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var result = await RunAsync(
            ["search", "--query", query, "--source", "winget", .. CommonArgs],
            ct: ct).ConfigureAwait(false);

        return ParseTable(result.StdOut)
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => new AppPackage
            {
                Id = r.Id,
                Name = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name,
                Version = r.Version,
                Source = string.IsNullOrWhiteSpace(r.Source) ? "winget" : r.Source,
            })
            .ToList();
    }

    public static async Task<List<AppPackage>> ListInstalledAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["list", .. CommonArgs], ct: ct).ConfigureAwait(false);

        var packages = ParseTable(result.StdOut)
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new AppPackage
            {
                Id = string.IsNullOrWhiteSpace(r.Id) ? r.Name : r.Id,
                Name = r.Name,
                Version = r.Version,
                AvailableVersion = r.Available,
                Source = r.Source,
                IsInstalled = true,
                IsSystemPackage = LooksLikeSystemPackage(r.Name, r.Id),
                ClosesApp = SelfPackages.Includes(r.Id),
            })
            .ToList();

        MarkSeveralVersions(packages);

        return packages;
    }

    /// <summary>
    /// Marks the rows whose id does not say which install is meant.
    ///
    /// `winget list` prints one row per installed version, so a machine with
    /// 7-Zip 22.01 and 26.02 on it gets two rows of 7zip.7zip - and
    /// `uninstall --id 7zip.7zip` refuses both with 0x8A150016 rather than
    /// choosing between them. The version is what tells the rows apart, so from
    /// here on each of them carries it: winget gets told which one, and the two
    /// rows stop sharing one operation between them.
    ///
    /// Rows that share a version as well as an id are left alone. The version
    /// cannot separate those either, and passing it would only move winget's
    /// refusal rather than answer it.
    /// </summary>
    internal static void MarkSeveralVersions(IReadOnlyList<AppPackage> packages)
    {
        foreach (var sameId in packages.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (sameId.Count() < 2)
                continue;

            foreach (var sameVersion in sameId.GroupBy(p => p.Version, StringComparer.OrdinalIgnoreCase))
            {
                if (sameVersion.Count() > 1 || string.IsNullOrWhiteSpace(sameVersion.Key))
                    continue;

                sameVersion.Single().IsOneOfSeveralVersions = true;
            }
        }
    }

    public static async Task<List<AppPackage>> ListUpgradesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["upgrade", "--include-unknown", .. CommonArgs],
            ct: ct).ConfigureAwait(false);

        return ParseTable(result.StdOut)
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Available))
            .Select(r => new AppPackage
            {
                Id = string.IsNullOrWhiteSpace(r.Id) ? r.Name : r.Id,
                Name = r.Name,
                Version = r.Version,
                AvailableVersion = r.Available,
                Source = r.Source,
                IsInstalled = true,
                ClosesApp = SelfPackages.Includes(r.Id),
            })
            .ToList();
    }

    /// <summary>Fills in publisher, description and homepage for a single package.</summary>
    public static async Task<Dictionary<string, string>> ShowAsync(string id, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = await RunAsync(
            ["show", "--id", id, "--exact", .. CommonArgs],
            ct: ct).ConfigureAwait(false);

        if (!result.Success)
            return fields;

        string? lastKey = null;
        foreach (var raw in result.StdOut.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Indented lines continue the previous field (long descriptions wrap).
            if (char.IsWhiteSpace(raw[0]))
            {
                if (lastKey is not null && fields.ContainsKey(lastKey) && !raw.Contains(':'))
                    fields[lastKey] = $"{fields[lastKey]} {raw.Trim()}".Trim();

                continue;
            }

            var colon = raw.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();

            if (key.Length == 0)
                continue;

            fields[key] = value;
            lastKey = key;
        }

        return fields;
    }

    public static Task<WingetResult> InstallAsync(string id, Action<string>? onOutput, CancellationToken ct = default) =>
        RunAsync(
            [
                "install", "--id", id, "--exact", "--silent",
                "--accept-package-agreements", .. CommonArgs,
            ],
            onOutput, ct);

    /// <summary>
    /// Removes one package. <paramref name="version"/> says which installed
    /// version to take, for the ids that have more than one: winget will not
    /// choose between them on its own, and refuses the whole command with
    /// 0x8A150016 instead. Null for everything else, where the id is answer
    /// enough and naming a version would only be one more thing to get wrong.
    /// </summary>
    public static Task<WingetResult> UninstallAsync(
        string id,
        string? version,
        Action<string>? onOutput,
        CancellationToken ct = default) =>
        RunAsync(UninstallArgs(id, version), onOutput, ct);

    internal static string[] UninstallArgs(string id, string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? ["uninstall", "--id", id, "--exact", "--silent", .. CommonArgs]
            : ["uninstall", "--id", id, "--exact", "--silent", "--version", version, .. CommonArgs];

    /// <summary>
    /// Upgrades one package. <paramref name="includeUnknown"/> is what "update
    /// all" passes: without it winget refuses any package whose installed
    /// version it cannot read, which is how a batch loses a package to
    /// 0x8A15002B rather than to anything actually going wrong.
    /// </summary>
    public static Task<WingetResult> UpgradeAsync(
        string id,
        Action<string>? onOutput,
        CancellationToken ct = default,
        bool includeUnknown = false)
    {
        string[] args = includeUnknown
            ?
            [
                "upgrade", "--id", id, "--exact", "--silent", "--include-unknown",
                "--accept-package-agreements", .. CommonArgs,
            ]
            :
            [
                "upgrade", "--id", id, "--exact", "--silent",
                "--accept-package-agreements", .. CommonArgs,
            ];

        return RunAsync(args, onOutput, ct);
    }

    /// <summary>
    /// Updates every package in the list, one winget process each, and carries
    /// on past the ones that fail.
    ///
    /// This used to be a single `winget upgrade --all`, which handed the whole
    /// batch to winget and left the app no say in what happened after the first
    /// failure. Driving the list here makes "keep going" ours to guarantee, and
    /// it means the failures can be named at the end instead of arriving as one
    /// exit code for the lot.
    ///
    /// Sequential on purpose: two winget processes contend over the same source
    /// database, exactly as the reload path already avoids.
    /// </summary>
    /// <param name="onStart">
    /// Handed each package's id and name as its turn comes round, which is what
    /// lets the list show the batch working down it, and the status line name
    /// the package a silent installer is currently doing things to.
    /// </param>
    /// <param name="onDone">
    /// Handed the package id, winget's own reason each time one is left behind -
    /// or an empty reason when it went through - and whether Windows has to
    /// restart before it counts. So the row for it can say why, or say what is
    /// still owed, or go, rather than the batch reducing it to a name in a tally.
    /// </param>
    public static Task<WingetResult> UpgradeEachAsync(
        IReadOnlyList<(string Id, string Name)> packages,
        Action<string>? onOutput,
        Action<string, string>? onStart = null,
        Action<string, string, RestartNeed>? onDone = null,
        CancellationToken ct = default) =>
        UpgradeEachAsync(
            packages, onOutput, onStart, onDone,
            (id, output, token) => UpgradeAsync(id, output, token, includeUnknown: true),
            ct);

    /// <summary>
    /// The batch itself, over whatever "upgrade one package" happens to mean.
    /// The overload above passes winget. A test passes a stand-in, which is the
    /// only way to hold the order, the carrying on past a failure and the
    /// closing tally to account without installing software to check.
    /// </summary>
    internal static async Task<WingetResult> UpgradeEachAsync(
        IReadOnlyList<(string Id, string Name)> packages,
        Action<string>? onOutput,
        Action<string, string>? onStart,
        Action<string, string, RestartNeed>? onDone,
        Func<string, Action<string>?, CancellationToken, Task<WingetResult>> upgrade,
        CancellationToken ct,
        ShellWatch? shell = null)
    {
        var failed = new List<string>();
        var restarting = new List<string>();
        var shellClosedBy = new List<string>();
        var firstFailureCode = 0;
        var log = new StringBuilder();

        // Watched per package rather than around the whole run: which one closed
        // the shell is the part a batch would otherwise lose.
        shell ??= new ShellWatch();

        for (var i = 0; i < packages.Count; i++)
        {
            // Cancellation is the one thing that does stop the batch: it means
            // the app is going away, not that a package misbehaved.
            ct.ThrowIfCancellationRequested();

            var (id, name) = packages[i];

            // The batch names the package it is on and counts it off itself, so
            // there is nothing to report here: winget's own next line goes on
            // the end of that heading rather than replacing it.
            onStart?.Invoke(id, name);

            shell.Before();
            var result = await upgrade(id, onOutput, ct).ConfigureAwait(false);

            if (await shell.AfterAsync(ct).ConfigureAwait(false))
            {
                shellClosedBy.Add(name);

                // Said as it happens as well as in the tally: the shell has just
                // come back on a machine where the taskbar vanished, and this
                // window is the only thing that can account for it.
                onOutput?.Invoke(ShellWatch.Note([name]));
            }

            log.Append(result.StdOut);

            if (result.Installed)
            {
                if (result.Restart is not RestartNeed.None)
                    restarting.Add(name);

                onDone?.Invoke(id, string.Empty, result.Restart);
                continue;
            }

            failed.Add(name);

            if (firstFailureCode == 0)
                firstFailureCode = result.ExitCode;

            onDone?.Invoke(id, Reason(result), result.Restart);
        }

        var updated = packages.Count - failed.Count;

        // The last line reported becomes the operation's summary, so this is
        // where the batch says what actually happened.
        var tally = failed.Count == 0
            ? $"Updated {updated} package{(updated == 1 ? string.Empty : "s")}."
            : $"{updated} of {packages.Count} updated. {failed.Count} failed: {string.Join(", ", failed)}.";

        // Named rather than counted. A restart is something the user has to go
        // and do, and which packages are waiting on it is the thing that makes
        // it worth doing now rather than at some point.
        if (restarting.Count > 0)
            tally += $" Restart Windows to finish: {string.Join(", ", restarting)}.";

        // Kept in the closing line as well, because that is the one the page
        // goes on showing after the run - by which point the shell is back and
        // there would otherwise be nothing left saying it ever went.
        if (shellClosedBy.Count > 0)
            tally += $" {ShellWatch.Note(shellClosedBy)}";

        onOutput?.Invoke(tally);

        return new WingetResult(firstFailureCode, log.ToString(), string.Empty);
    }

    /// <summary>
    /// What went wrong with one package, in winget's own words plus its code.
    /// The explanation is the last thing winget says - its preamble ("Found …",
    /// the licence notices) comes first - and the code is kept because it is
    /// what the documentation and every search result are indexed by.
    /// </summary>
    private static string Reason(WingetResult result)
    {
        var said = LastLine(result.StdOut);

        if (said.Length == 0)
            said = LastLine(result.StdErr);

        // winget's own failures are the 0x8A15xxxx family and read as hex; an
        // installer's own code arrives as a small positive number.
        var code = result.ExitCode < 0
            ? $"0x{result.ExitCode:X8}"
            : result.ExitCode.ToString();

        return said.Length > 0 ? $"{said} ({code})" : $"winget exited with {code}.";
    }

    private static string LastLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();

            if (line.Length > 0 && !SpinnerOnly.IsMatch(line))
                return line;
        }

        return string.Empty;
    }

    public static async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunAsync(["--version"], ct: ct).ConfigureAwait(false);
            IsAvailable = result.Success;
            return result.StdOut.Trim();
        }
        catch
        {
            IsAvailable = false;
            return string.Empty;
        }
    }

    /// <summary>
    /// Heuristic for the runtime/redistributable clutter that dominates
    /// `winget list` but that nobody thinks of as an installed app.
    /// </summary>
    private static bool LooksLikeSystemPackage(string name, string id)
    {
        ReadOnlySpan<string> markers =
        [
            "Microsoft Visual C++",
            "Windows Software Development Kit",
            "Windows Driver",
            "Update for",
            "Security Update",
            "Redistributable",
            "Runtime -",
            "Hotfix",
            ".NET Host",
            ".NET Runtime",
            "Driver Package",
        ];

        foreach (var marker in markers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("ARP\\Machine\\X64\\{", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Microsoft.VCRedist", StringComparison.OrdinalIgnoreCase);
    }
}
