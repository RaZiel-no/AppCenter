using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace AppCenter.Services;

/// <summary>One published release of App Center, as GitHub describes it.</summary>
public sealed record AppRelease(
    string Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    DateTimeOffset? PublishedAt,
    string SetupName,
    string SetupUrl,
    long SetupSize,
    string? HashUrl)
{
    /// <summary>Whether the release carries the installer this app can run over itself.</summary>
    public bool HasInstaller => SetupUrl.Length > 0;

    public bool IsNewerThan(string version) => VersionOrder.Instance.Compare(Version, version) > 0;
}

/// <summary>
/// Updates App Center from its own GitHub releases, ahead of winget.
///
/// A release is on GitHub the minute it is built; the winget package follows
/// once its pull request on microsoft/winget-pkgs is reviewed and merged,
/// which takes weeks. This asks GitHub for the latest release, and when it is
/// newer than the running build, downloads the same installer winget would
/// eventually fetch - checked against the SHA-256 published beside it - runs
/// it silently, and steps aside so it can replace the files this process has
/// open. The installer starts the new version when it is done.
///
/// The download and the hand-over run as an ordinary operation under App
/// Center's own package id, so the sidebar strip, the Manage row and the
/// About page all show it the way they show any update, and a winget update
/// of the same package cannot run at the same time.
/// </summary>
public static class AppUpdateService
{
    private const string ApiUrl =
        $"https://api.github.com/repos/{AppInfo.RepositoryOwner}/{AppInfo.RepositoryName}/releases/latest";

    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();
    private static readonly Regex HexDigest = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);

    private static Task? _check;

    /// <summary>The latest release GitHub lists, once a check has answered.</summary>
    public static AppRelease? Latest { get; private set; }

    /// <summary>Why the last check gave no answer, or null when it did.</summary>
    public static string? LastError { get; private set; }

    public static bool IsChecking { get; private set; }
    public static bool HasChecked { get; private set; }
    public static DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>Raised on the UI thread whenever any of the above moves.</summary>
    public static event EventHandler? Changed;

    /// <summary>Whether GitHub has a newer version than the one running.</summary>
    public static bool IsAvailable => Latest is { } latest && latest.IsNewerThan(AppInfo.Version);

    /// <summary>
    /// Whether this route has something winget does not: a newer version than
    /// winget is offering, or any newer version when winget offers none. When
    /// winget already has the same release, its row on Manage is the one to
    /// press, and this stays out of the way.
    /// </summary>
    public static bool OffersMoreThan(string? wingetAvailable) =>
        IsAvailable
        && (string.IsNullOrWhiteSpace(wingetAvailable)
            || VersionOrder.Instance.Compare(Latest!.Version, wingetAvailable) > 0);

    /// <summary>Where downloaded installers go, and are cleared from.</summary>
    private static string DownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppCenter",
        "updates");

    // ---------------------------------------------------------------
    // Checking
    // ---------------------------------------------------------------

    /// <summary>
    /// Asks GitHub for the latest release. One request however many ask at
    /// once; the token only stops the caller waiting.
    /// </summary>
    public static Task CheckAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            _check ??= RunCheckAsync();
            return _check.WaitAsync(ct);
        }
    }

    private static async Task RunCheckAsync()
    {
        await OnUiAsync(() =>
        {
            IsChecking = true;
            Changed?.Invoke(null, EventArgs.Empty);
        }).ConfigureAwait(false);

        AppRelease? release = null;
        string? error = null;

        try
        {
            // Bounded here rather than on the client: the client's timeout would
            // cap the installer download too, and a slow line deserves better.
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var json = await Http.GetStringAsync(ApiUrl, patience.Token).ConfigureAwait(false);
            release = Parse(json);

            if (release is null)
                error = "GitHub lists no release.";
        }
        catch (Exception ex)
        {
            error = Explain(ex);
        }

        // An installer left behind by the last update, if there was one, is
        // done with: the app running now is the one it installed.
        ClearDownloads();

        await OnUiAsync(() =>
        {
            if (error is null)
            {
                Latest = release;
                CheckedAt = DateTimeOffset.Now;
            }

            LastError = error;
            IsChecking = false;
            HasChecked = true;

            lock (Gate)
                _check = null;

            Changed?.Invoke(null, EventArgs.Empty);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads GitHub's "latest release" answer. Null for a draft, a pre-release
    /// or anything that is not a release at all. A release without the setup
    /// exe among its assets still comes back - its page can be opened, it
    /// just cannot be installed from here.
    /// </summary>
    internal static AppRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagProperty))
            return null;

        if (Flag(root, "draft") || Flag(root, "prerelease"))
            return null;

        var tag = tagProperty.GetString() ?? string.Empty;
        var version = tag.TrimStart('v', 'V').Trim();

        if (version.Length == 0)
            return null;

        var setupName = string.Empty;
        var setupUrl = string.Empty;
        long setupSize = 0;
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");

                if (name.Length == 0 || url.Length == 0)
                    continue;

                if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    hashes[name[..^".sha256".Length]] = url;
                }
                else if (name.StartsWith("AppCenter-", StringComparison.OrdinalIgnoreCase)
                         && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setupName = name;
                    setupUrl = url;
                    setupSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0;
                }
            }
        }

        DateTimeOffset? published = null;
        if (root.TryGetProperty("published_at", out var publishedAt)
            && publishedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedAt.GetString(), out var parsed))
            published = parsed;

        return new AppRelease(
            version,
            tag,
            Text(root, "name"),
            Text(root, "body"),
            Text(root, "html_url"),
            published,
            setupName,
            setupUrl,
            setupSize,
            setupName.Length > 0 && hashes.TryGetValue(setupName, out var hashUrl) ? hashUrl : null);
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// The digest out of a published .sha256 file, whatever else is in it:
    /// "&lt;hex&gt;  AppCenter-1.0.4-Setup.exe" is the usual shape, a bare hex
    /// string is fine too. Null when there is no 64-digit hex run to be found.
    /// </summary>
    internal static string? ParseHash(string text) =>
        HexDigest.Match(text) is { Success: true } match ? match.Value.ToUpperInvariant() : null;

    // ---------------------------------------------------------------
    // Updating
    // ---------------------------------------------------------------

    /// <summary>
    /// Downloads the latest release's installer and hands the machine over to
    /// it, as an operation on App Center's own package. Null when there is
    /// nothing newer, no installer to run, this is a portable copy, or the
    /// package is already being worked on.
    /// </summary>
    public static Operation? StartUpdate()
    {
        if (!IsAvailable || Latest is not { HasInstaller: true } release || !AppInfo.IsInstalledCopy)
            return null;

        return OperationService.Start(
            AppInfo.PackageId, "App Center", OperationKind.Update,
            (report, token) => DownloadAndRunAsync(release, report, token));
    }

    /// <summary>
    /// The update itself. It speaks in winget's milestones - "Found", "Downloading",
    /// "verified installer hash", "Starting package install" - so the bar every
    /// row draws from those lines works for this without knowing the difference.
    /// Anything wrong is thrown: the operation then ends failed with the reason,
    /// and nothing has been run.
    /// </summary>
    private static async Task<WingetResult> DownloadAndRunAsync(
        AppRelease release,
        Action<string> report,
        CancellationToken ct)
    {
        report($"Found App Center [{AppInfo.PackageId}] Version {release.Version}");

        Directory.CreateDirectory(DownloadDir);
        ClearDownloads();

        var path = Path.Combine(DownloadDir, release.SetupName);

        report($"Downloading {release.SetupUrl}");
        var actualHash = await DownloadAsync(release, path, report, ct).ConfigureAwait(false);

        // The published digest, when the release carries one. Without it the
        // download still came over HTTPS from the same place winget's manifest
        // points at; with it, a corrupted or swapped file is caught here.
        if (release.HashUrl is { } hashUrl)
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(TimeSpan.FromSeconds(15));

            var expected = ParseHash(await Http.GetStringAsync(hashUrl, patience.Token).ConfigureAwait(false));

            if (expected is null)
                throw new InvalidOperationException("The hash published with the release could not be read. Nothing was run.");

            if (!string.Equals(expected, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                throw new InvalidOperationException("The download does not match the SHA-256 published with the release. Nothing was run.");
            }

            report("Successfully verified installer hash");
        }
        else
        {
            report("No published hash to check the download against");
        }

        if (!LooksLikeAnExecutable(path))
        {
            TryDelete(path);
            throw new InvalidOperationException("The download is not a Windows installer. Nothing was run.");
        }

        report("Starting package install...");

        // Silent, per-user, no prompts. RELAUNCH is our own switch: the installer
        // starts App Center again when it is done - see installer\AppCenter.iss.
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            ArgumentList =
            {
                "/VERYSILENT", "/NORESTART", "/SP-", "/SUPPRESSMSGBOXES",
                "/CLOSEAPPLICATIONS", "/RELAUNCH=1",
            },
        });

        // The installer has to replace the files this process holds open, so
        // this process goes. A beat first, so the line above is seen.
        await Task.Delay(400, ct).ConfigureAwait(false);
        await OnUiAsync(() => Application.Current?.Shutdown()).ConfigureAwait(false);

        return new WingetResult(0, "Starting package install...", string.Empty);
    }

    /// <summary>Streams the installer to disk, hashing as it goes, and returns the SHA-256.</summary>
    private static async Task<string> DownloadAsync(AppRelease release, string path, Action<string> report, CancellationToken ct)
    {
        using var response = await Http
            .GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expectedSize = release.SetupSize > 0 ? release.SetupSize : response.Content.Headers.ContentLength ?? 0;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        long lastReported = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = File.Create(path))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                total += read;

                // Every quarter megabyte, not every buffer: the line is repainted
                // on the UI thread each time it is reported.
                if (total - lastReported >= 256 * 1024)
                {
                    lastReported = total;
                    report($"Downloading {release.SetupName} ({Megabytes(total)} of {Megabytes(expectedSize)} MB)");
                }
            }
        }

        if (expectedSize > 0 && total != expectedSize)
        {
            TryDelete(path);
            throw new InvalidOperationException(
                $"The download stopped at {Megabytes(total)} of {Megabytes(expectedSize)} MB. Nothing was run.");
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0");

    /// <summary>Every Windows executable opens with "MZ". Cheap, and a download page saved as .exe fails it.</summary>
    private static bool LooksLikeAnExecutable(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return file.Length > 2 && file.ReadByte() == 'M' && file.ReadByte() == 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void ClearDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadDir))
                return;

            foreach (var file in Directory.EnumerateFiles(DownloadDir))
                TryDelete(file);
        }
        catch (Exception)
        {
            // Left for next time.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left for next time.
        }
    }

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private static string Explain(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"GitHub answered {(int)status} {status}.",
        HttpRequestException => "Could not reach GitHub.",
        TaskCanceledException => "GitHub did not answer in time.",
        JsonException => "GitHub's answer could not be read.",
        _ => ex.Message,
    };

    private static HttpClient CreateClient()
    {
        // The default 100-second timeout stays: the one long request here is
        // the installer download, and the short ones bound themselves.
        var client = new HttpClient();

        // GitHub's API refuses requests with no User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AppCenter/{AppInfo.Version} (+{AppInfo.RepositoryUrl})");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>Forgets everything. For tests, which share the static.</summary>
    internal static void Reset()
    {
        lock (Gate)
            _check = null;

        Latest = null;
        LastError = null;
        IsChecking = false;
        HasChecked = false;
        CheckedAt = null;
        Changed = null;
    }
}
