using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// Fetches an icon per package and caches it on disk.
///
/// winget carries no icon data, so icons come from each app's own homepage:
/// the page is read for whatever it declares in
/// <c>&lt;link rel="apple-touch-icon"&gt;</c> or <c>&lt;link rel="icon"&gt;</c>,
/// falling back to the well-known paths at the site root. Nothing is sent to a
/// third-party icon service - only the app's own site, and whatever host it
/// names its icon on, is contacted, and the result is cached under
/// %LOCALAPPDATA%\AppCenter\icons. Packages that yield nothing fall back to a
/// generated letter tile drawn by the UI.
/// </summary>
public sealed class IconService
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Give up on the network after this many consecutive transport failures.</summary>
    private const int OfflineThreshold = 6;

    /// <summary>
    /// Bumped when the resolution rules change, so that everyone's cache of
    /// wrong answers is discarded rather than outliving the fix.
    /// </summary>
    private const string CacheSuffix = ".v2.png";

    private const int MaxHtmlBytes = 1024 * 1024;
    private const int MaxDeclaredIcons = 4;

    /// <summary>
    /// Carousel screenshots are full-size press images - several megabytes is
    /// normal - and are only ever drawn about 730px wide, so they are decoded
    /// down on the way in rather than held at source resolution.
    /// </summary>
    private const int MaxScreenshotBytes = 12 * 1024 * 1024;
    private const int ScreenshotDecodeWidth = 960;

    /// <summary>
    /// Code hosts, where every project shares one page furniture and therefore
    /// one icon: a github.com/{user}/{repo} homepage yields the Octocat, which
    /// then sits on two dozen unrelated cards claiming they are all GitHub. A
    /// letter tile carries more information than that, so these are skipped
    /// outright and the app falls back to one.
    ///
    /// Matched exactly, not by suffix - desktop.github.com is GitHub's own
    /// product and the Octocat is the correct icon for it.
    /// </summary>
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "www.github.com", "gist.github.com",
        "gitlab.com", "www.gitlab.com",
        "bitbucket.org", "www.bitbucket.org",
        "sourceforge.net", "www.sourceforge.net",
        "codeberg.org", "www.codeberg.org",
        "gitea.com", "git.sr.ht", "launchpad.net",
        "savannah.gnu.org", "savannah.nongnu.org",
    };

    private static readonly Regex LinkTagPattern =
        new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SizePattern =
        new(@"(\d+)\s*[xX]\s*\d+", RegexOptions.Compiled);

    private readonly string _cacheDir;
    private readonly string _screenshotDir;
    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlightScreenshots = new();

    private int _consecutiveFailures;
    private volatile bool _assumeOffline;

    public IconService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppCenter");

        _cacheDir = Path.Combine(root, "icons");
        _screenshotDir = Path.Combine(root, "screenshots");

        try
        {
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(_screenshotDir);
            PruneStaleCache();
        }
        catch (Exception)
        {
            // Without a cache directory we simply refetch each run.
        }
    }

    /// <summary>
    /// Drops entries written by an older set of resolution rules. Without this
    /// a fix to the rules never reaches anyone who has already run the app -
    /// the wrong icon is on disk and is returned before any of it is consulted.
    /// </summary>
    private void PruneStaleCache()
    {
        foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.png"))
        {
            if (file.EndsWith(CacheSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // Locked or read-only; it is only stale cache, so leave it.
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AppCenter/1.0 (+winget app browser)");
        return client;
    }

    /// <summary>
    /// Fire-and-forget icon population for a whole page of packages. Each
    /// result is pushed back onto the UI thread as it arrives, so the grid
    /// paints immediately with letter tiles and fills in as icons land.
    /// </summary>
    public void BeginLoad(IEnumerable<AppPackage> packages, Dispatcher dispatcher)
    {
        foreach (var package in packages)
        {
            if (package.HasIcon)
                continue;

            var target = package;
            _ = Task.Run(async () =>
            {
                try
                {
                    var image = await GetIconAsync(target).ConfigureAwait(false);
                    if (image is not null)
                        await dispatcher.BeginInvoke(() => target.Icon = image);
                }
                catch (Exception)
                {
                    // An icon is decoration; never let a failure surface.
                }
            });
        }
    }

    public Task<BitmapSource?> GetIconAsync(AppPackage package, CancellationToken ct = default)
    {
        var key = CacheKey(package.Id.Length > 0 ? package.Id : package.Name);
        if (key.Length == 0)
            return Task.FromResult<BitmapSource?>(null);

        return _inFlight.GetOrAdd(key, _ => LoadAsync(package, key, ct));
    }

    /// <summary>
    /// Same fire-and-forget shape as <see cref="BeginLoad"/>, for the wide
    /// images behind the Games carousel. Only entries carrying an explicit
    /// screenshot URL are touched; everything else keeps its gradient.
    /// </summary>
    public void BeginLoadScreenshots(IEnumerable<AppPackage> packages, Dispatcher dispatcher)
    {
        foreach (var package in packages)
        {
            if (package.HasScreenshot || string.IsNullOrWhiteSpace(package.ScreenshotUrl))
                continue;

            var target = package;
            _ = Task.Run(async () =>
            {
                try
                {
                    var image = await GetScreenshotAsync(target).ConfigureAwait(false);
                    if (image is not null)
                        await dispatcher.BeginInvoke(() => target.Screenshot = image);
                }
                catch (Exception)
                {
                    // A slide without its screenshot still renders its gradient.
                }
            });
        }
    }

    public Task<BitmapSource?> GetScreenshotAsync(AppPackage package, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(package.ScreenshotUrl))
            return Task.FromResult<BitmapSource?>(null);

        var key = CacheKey(package.Id.Length > 0 ? package.Id : package.Name);
        if (key.Length == 0)
            return Task.FromResult<BitmapSource?>(null);

        return _inFlightScreenshots.GetOrAdd(key, _ => LoadScreenshotAsync(package, key, ct));
    }

    private async Task<BitmapSource?> LoadScreenshotAsync(AppPackage package, string key, CancellationToken ct)
    {
        var path = Path.Combine(_screenshotDir, key + CacheSuffix);

        var cached = TryLoadFile(path);
        if (cached is not null)
            return cached;

        if (_assumeOffline)
            return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] bytes;
            try
            {
                using var response = await Http
                    .GetAsync(package.ScreenshotUrl!, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return null;

                if (response.Content.Headers.ContentLength > MaxScreenshotBytes)
                    return null;

                bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                NoteFailure();
                return null;
            }
            catch (HttpRequestException)
            {
                NoteFailure();
                return null;
            }

            if (bytes.Length is < 1024 or > MaxScreenshotBytes)
                return null;

            var decoded = DecodeScaled(bytes, ScreenshotDecodeWidth);
            if (decoded is null)
                return null;

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            SaveFile(path, decoded);
            return decoded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Decodes straight to the width it will be drawn at. A 1920x1080 press
    /// shot held at source size costs 8MB of bitmap for a 250px-tall slide.
    /// </summary>
    private static BitmapSource? DecodeScaled(byte[] bytes, int width)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = width;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image.PixelWidth < 64 ? null : image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<BitmapSource?> LoadAsync(AppPackage package, string key, CancellationToken ct)
    {
        var cached = TryLoadFromDisk(key);
        if (cached is not null)
            return cached;

        if (_assumeOffline)
            return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var url in await CandidateUrlsAsync(package, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                var image = await TryFetchAsync(url, key, ct).ConfigureAwait(false);
                if (image is not null)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return image;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _gate.Release();
        }

        return null;
    }

    /// <summary>
    /// Ordered list of places an icon might live. A catalogue entry with an
    /// explicit "icon" settles it. Otherwise the homepage is asked what it
    /// declares, and only then are the conventional root paths guessed at -
    /// those are a last resort, because the root of a site is the company, not
    /// the product, and mozilla.org's icon is the Mozilla flag rather than
    /// Firefox's.
    /// </summary>
    private async Task<IReadOnlyList<string>> CandidateUrlsAsync(AppPackage package, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(package.IconUrl))
            return new[] { package.IconUrl! };

        if (string.IsNullOrWhiteSpace(package.Homepage))
            return Array.Empty<string>();

        if (!Uri.TryCreate(package.Homepage, UriKind.Absolute, out var home))
            return Array.Empty<string>();

        if (home.Scheme != Uri.UriSchemeHttp && home.Scheme != Uri.UriSchemeHttps)
            return Array.Empty<string>();

        if (SharedHosts.Contains(home.Host))
            return Array.Empty<string>();

        var candidates = new List<string>(await DeclaredIconsAsync(home, ct).ConfigureAwait(false));

        var root = $"{home.Scheme}://{home.Host}";
        candidates.Add($"{root}/apple-touch-icon.png");
        candidates.Add($"{root}/apple-touch-icon-precomposed.png");
        candidates.Add($"{root}/favicon.ico");
        candidates.Add($"{root}/favicon.png");

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reads the homepage and returns the icons it declares, best first:
    /// apple-touch-icon ahead of favicon, larger ahead of smaller. Resolved
    /// against the URL the request actually ended on, so redirects and relative
    /// hrefs both land in the right place.
    /// </summary>
    private async Task<IEnumerable<string>> DeclaredIconsAsync(Uri homepage, CancellationToken ct)
    {
        string html;
        Uri baseUri;

        try
        {
            using var response = await Http
                .GetAsync(homepage, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not ("text/html" or "application/xhtml+xml"))
                return Array.Empty<string>();

            if (response.Content.Headers.ContentLength > MaxHtmlBytes)
                return Array.Empty<string>();

            baseUri = response.RequestMessage?.RequestUri ?? homepage;
            html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            NoteFailure();
            return Array.Empty<string>();
        }
        catch (HttpRequestException)
        {
            NoteFailure();
            return Array.Empty<string>();
        }

        var declared = new List<(int Rank, int Size, string Url)>();

        foreach (Match tag in LinkTagPattern.Matches(html))
        {
            var rel = Attribute(tag.Value, "rel");
            if (rel is null || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                continue;

            // A mask-icon is a monochrome silhouette for Safari's pinned tabs,
            // and is always an SVG. Neither of those is any use here.
            if (rel.Contains("mask-icon", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = Attribute(tag.Value, "href");
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var icon))
                continue;

            if (icon.Scheme != Uri.UriSchemeHttp && icon.Scheme != Uri.UriSchemeHttps)
                continue;

            // WPF has no SVG decoder, so fetching one only wastes a request.
            if (icon.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                continue;

            var rank = rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            declared.Add((rank, LargestSize(Attribute(tag.Value, "sizes")), icon.AbsoluteUri));
        }

        return declared
            .OrderBy(icon => icon.Rank)
            .ThenByDescending(icon => icon.Size)
            .Select(icon => icon.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxDeclaredIcons)
            .ToList();
    }

    /// <summary>Pulls one attribute out of a tag, quoted however the page felt like.</summary>
    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(
            tag,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+))",
            RegexOptions.IgnoreCase);

        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
    }

    /// <summary>Widest size out of a sizes="16x16 32x32" list; 0 for "any" or absent.</summary>
    private static int LargestSize(string? sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes))
            return 0;

        var largest = 0;
        foreach (Match match in SizePattern.Matches(sizes))
        {
            if (int.TryParse(match.Groups[1].Value, out var width))
                largest = Math.Max(largest, width);
        }

        return largest;
    }

    private async Task<BitmapSource?> TryFetchAsync(string url, string key, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout, not our cancellation token.
            NoteFailure();
            return null;
        }
        catch (HttpRequestException)
        {
            NoteFailure();
            return null;
        }

        if (bytes.Length is < 64 or > 4 * 1024 * 1024)
            return null;

        var decoded = Decode(bytes);
        if (decoded is null)
            return null;

        SaveToDisk(key, decoded);
        return decoded;
    }

    private void NoteFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= OfflineThreshold)
            _assumeOffline = true;
    }

    /// <summary>Decodes ico/png/jpg and picks the largest frame an .ico contains.</summary>
    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
                return null;

            var frame = decoder.Frames
                .OrderByDescending(f => f.PixelWidth * f.PixelHeight)
                .First();

            if (frame.PixelWidth < 8 || frame.PixelHeight < 8)
                return null;

            BitmapSource source = frame;
            if (source.CanFreeze)
                source.Freeze();

            return source;
        }
        catch (Exception)
        {
            // Not an image format WPF understands (SVG, HTML error page, ...).
            return null;
        }
    }

    private BitmapSource? TryLoadFromDisk(string key) =>
        TryLoadFile(Path.Combine(_cacheDir, key + CacheSuffix));

    private static BitmapSource? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveToDisk(string key, BitmapSource image) =>
        SaveFile(Path.Combine(_cacheDir, key + CacheSuffix), image);

    private static void SaveFile(string path, BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var file = File.Create(path);
            encoder.Save(file);
        }
        catch (Exception)
        {
            // A failed cache write just means we refetch next launch.
        }
    }

    private static string CacheKey(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;

        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return new string(chars.ToArray());
    }
}
