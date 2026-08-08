using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AppCenter.Models;

namespace AppCenter.Services;

public sealed record WingetResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>One parsed row of winget's fixed-width table output.</summary>
public sealed record WingetRow(
    string Name,
    string Id,
    string Version,
    string Available,
    string Source);

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
            process.Start();
        }
        catch (Exception ex)
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

        return ParseTable(result.StdOut)
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
            })
            .ToList();
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

    public static Task<WingetResult> UninstallAsync(string id, Action<string>? onOutput, CancellationToken ct = default) =>
        RunAsync(["uninstall", "--id", id, "--exact", "--silent", .. CommonArgs], onOutput, ct);

    public static Task<WingetResult> UpgradeAsync(string id, Action<string>? onOutput, CancellationToken ct = default) =>
        RunAsync(
            [
                "upgrade", "--id", id, "--exact", "--silent",
                "--accept-package-agreements", .. CommonArgs,
            ],
            onOutput, ct);

    public static Task<WingetResult> UpgradeAllAsync(Action<string>? onOutput, CancellationToken ct = default) =>
        RunAsync(
            [
                "upgrade", "--all", "--silent", "--include-unknown",
                "--accept-package-agreements", .. CommonArgs,
            ],
            onOutput, ct);

    /// <summary>Whether a specific package id is currently installed.</summary>
    public static async Task<bool> IsInstalledAsync(string id, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["list", "--id", id, "--exact", .. CommonArgs],
            ct: ct).ConfigureAwait(false);

        if (!result.Success)
            return false;

        return ParseTable(result.StdOut)
            .Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
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
