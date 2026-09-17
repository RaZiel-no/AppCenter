using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
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

    /// <summary>Stop trying the network after this many consecutive transport failures.</summary>
    private const int OfflineThreshold = 6;

    /// <summary>
    /// How long to leave the network alone once <see cref="OfflineThreshold"/>
    /// transport failures have piled up. Fixed rather than escalating: the point
    /// is to stop hammering a network that is not answering, not to punish it.
    /// Short enough that moving to another page recovers on its own.
    /// </summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bumped when the resolution rules change, so that everyone's cache of
    /// wrong answers is discarded rather than outliving the fix. "Rules" covers
    /// what gets written as well as which icon is chosen - v3 is v2 brought down
    /// to <see cref="IconDecodeWidth"/>, and leaving the full-size v2 files in
    /// place would have meant the fix reached nobody who had already run the app.
    /// </summary>
    private const string CacheSuffix = ".v3.png";

    /// <summary>
    /// Marks a package we have already hunted for and found nothing for, holding
    /// the source it was hunted from. Shares the version token with
    /// <see cref="CacheSuffix"/> so that both are swept by the same bump: a rule
    /// change can turn a miss into a hit.
    /// </summary>
    private const string MissSuffix = ".v3.none";

    /// <summary>
    /// How long such a marker is trusted before the hunt runs again. Long enough
    /// that the nine requests behind that conclusion are not repeated on every
    /// launch, short enough that a site which gains a favicon is picked up
    /// without anyone clearing a cache by hand.
    /// </summary>
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// Icons are drawn at 72px at the largest - the detail-page header; cards
    /// are 48px and Manage rows 32px - so this is that, doubled for a 200%
    /// display. Beyond it a bitmap is paying for detail nobody can see: a
    /// 256x256 favicon costs 256KB of memory, and the same picture at this width
    /// costs 81KB.
    /// </summary>
    private const int IconDecodeWidth = 144;

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

    /// <summary>
    /// App Center's own icon, for App Center's own row. Its homepage is on
    /// GitHub, which the hunt skips on purpose, so it would otherwise be drawn
    /// as a letter tile - by the very exe that carries the real thing. Read from
    /// the assembly's copy, the one Window.Icon already needs, and put through
    /// the same decoder as everything else so it lands at the same size.
    /// </summary>
    private static readonly Lazy<BitmapSource?> OwnIcon = new(LoadOwnIcon);

    private static BitmapSource? LoadOwnIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("AppCenter.ico", UriKind.Relative));
            if (resource is null)
                return null;

            using var stream = resource.Stream;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Decode(buffer.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly string _cacheDir;
    private readonly string _screenshotDir;
    private readonly ConcurrentDictionary<string, Task<IconResult>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Task<IconResult>> _inFlightScreenshots = new();

    /// <summary>
    /// Icon hosts: a couple of hundred app homepages, small responses, and the
    /// one whose slowness is felt because a whole page of cards is waiting.
    /// </summary>
    private readonly Channel _icons = new(4);

    /// <summary>
    /// Screenshot hosts: nine URLs, megabytes each. Fewer permits because the
    /// bandwidth per request is so much larger, and separate ones so that a slow
    /// press image cannot sit on the allowance the cards are queued behind.
    /// </summary>
    private readonly Channel _screenshots = new(2);

    /// <summary>
    /// One class of host, with its own permits and its own read on whether the
    /// network is answering.
    ///
    /// Icons and screenshots used to share both, which went wrong in two
    /// directions: six failures against a single dead screenshot host armed the
    /// backoff for icons as well, and one package's icon hunt could hold a
    /// quarter of the permits for the length of nine timeouts. They are
    /// unrelated hosts answering unrelated questions, so they get unrelated
    /// signals.
    /// </summary>
    private sealed class Channel
    {
        private readonly SemaphoreSlim _gate;

        private int _consecutiveFailures;

        /// <summary>
        /// When the network may be tried again, on the monotonic tick clock; 0
        /// means now. A backoff, deliberately not a latch - this used to be a
        /// one-way `_assumeOffline` flag that nothing ever cleared, so a single
        /// blip left every remaining card on a letter tile until the app was
        /// restarted.
        /// </summary>
        private long _retryAfterTicks;

        public Channel(int permits) => _gate = new SemaphoreSlim(permits);

        /// <summary>True while the network is being left alone to recover.</summary>
        public bool IsQuiet => Environment.TickCount64 < Interlocked.Read(ref _retryAfterTicks);

        /// <summary>
        /// Claims a permit for one HTTP exchange. Held for the exchange and
        /// nothing else - not across a fan-out, and not across decoding - so
        /// that what the permits bound is time on the wire.
        /// </summary>
        public Task WaitAsync(CancellationToken ct) => _gate.WaitAsync(ct);

        public void Release() => _gate.Release();

        /// <summary>
        /// Sorts a transport exception into "the network is down" and "this host
        /// is". A name that does not resolve, or a certificate that will not
        /// negotiate, is a dead homepage - and the catalogue carries a couple of
        /// hundred of those, plus whatever `winget show` reports for a search
        /// hit. Counting them was the same false positive as counting a 404: six
        /// dead domains on one page would have silenced icons on a perfectly
        /// healthy network.
        /// </summary>
        public void NoteFailure(HttpRequestException ex)
        {
            if (ex.HttpRequestError is HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError)
                return;

            NoteFailure();
        }

        /// <summary>
        /// A request that never reached a server. Enough of these in a row and
        /// the network is left alone for <see cref="QuietPeriod"/>.
        /// </summary>
        public void NoteFailure()
        {
            var count = Interlocked.Increment(ref _consecutiveFailures);

            if (count < OfflineThreshold)
                return;

            // Claimed against the exact count that was seen, and clearing it in
            // the same stroke: the next window then starts with a full allowance
            // instead of re-arming on its first failure. If a success landed in
            // between and zeroed the counter, this fails and the success wins -
            // otherwise a backoff could be armed moments after proof the network
            // is alive.
            if (Interlocked.CompareExchange(ref _consecutiveFailures, 0, count) != count)
                return;

            Interlocked.Exchange(
                ref _retryAfterTicks,
                Environment.TickCount64 + (long)QuietPeriod.TotalMilliseconds);
        }

        /// <summary>
        /// Evidence that the network is there. Called for any completed HTTP
        /// exchange, including a 404: a server that says "no" is still a server
        /// that answered, and treating that as failure is what let six sites
        /// without a favicon look like an offline machine.
        /// </summary>
        public void NoteSuccess()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _retryAfterTicks, 0);
        }
    }

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

            // Both directories, because both are written with CacheSuffix.
            // Sweeping only the icons meant bumping the constant purged stale
            // icons and left stale screenshots on disk forever, which is exactly
            // what the constant exists to prevent.
            PruneStaleCache(_cacheDir);
            PruneStaleCache(_screenshotDir);
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
    ///
    /// Everything in the directory is considered, not just the images: a miss
    /// marker records a conclusion reached under the old rules too, and is just
    /// as wrong to keep.
    /// </summary>
    private static void PruneStaleCache(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (file.EndsWith(CacheSuffix, StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(MissSuffix, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// What one load concluded. <see cref="Tried"/> is the whole point: it
    /// separates "this app has no icon" from "we never looked". Inferring that
    /// afterwards from the backoff flag was racy - another thread's success can
    /// clear the flag between the null being produced and anyone reading it, and
    /// the untried null would then be kept for the rest of the session, which is
    /// the very bug the backoff was meant to end.
    /// </summary>
    private readonly record struct IconResult(BitmapSource? Image, bool Tried)
    {
        /// <summary>Nothing was asked, so nothing was learned. Do not remember this.</summary>
        public static IconResult NotTried => default;

        /// <summary>A real conclusion, including "asked, and there is none".</summary>
        public static IconResult Answer(BitmapSource? image) => new(image, true);
    }

    public Task<BitmapSource?> GetIconAsync(AppPackage package, CancellationToken ct = default)
    {
        var key = CacheKey(package.Id.Length > 0 ? package.Id : package.Name);
        if (key.Length == 0)
            return Task.FromResult<BitmapSource?>(null);

        return Remember(_inFlight, key, k => LoadAsync(package, k, ct));
    }

    /// <summary>
    /// Shares one load per package and keeps the answer - but only when there was
    /// one. A load that never looked is dropped, so the next page to ask tries
    /// again. That covers both the backoff and the commoner case of a package
    /// whose homepage is not known yet: the detail page asks once before
    /// `winget show` answers and again afterwards, and without this the second
    /// ask would be served the first ask's memoised nothing.
    /// </summary>
    private Task<BitmapSource?> Remember(
        ConcurrentDictionary<string, Task<IconResult>> inFlight,
        string key,
        Func<string, Task<IconResult>> load)
    {
        var task = inFlight.GetOrAdd(key, load);

        return Settle();

        async Task<BitmapSource?> Settle()
        {
            try
            {
                var result = await task.ConfigureAwait(false);

                if (!result.Tried)
                    Forget();

                return result.Image;
            }
            catch
            {
                // Cancelled or faulted: not an answer either. Evicting here also
                // stops a cancelled load from poisoning the key for the session.
                Forget();
                throw;
            }
        }

        // Matched on key and value, so this only ever removes our own entry.
        // Note the value is not a unique token: the async machinery hands back
        // one shared cached Task for every synchronous default result, so several
        // keys can legitimately hold the same instance - harmless, because that
        // instance always means NotTried, which is always safe to drop. Safe to
        // call after GetOrAdd returned, which is what puts the entry there.
        void Forget() => inFlight.TryRemove(new KeyValuePair<string, Task<IconResult>>(key, task));
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

        return Remember(_inFlightScreenshots, key, k => LoadScreenshotAsync(package, k, ct));
    }

    private async Task<IconResult> LoadScreenshotAsync(AppPackage package, string key, CancellationToken ct)
    {
        var path = Path.Combine(_screenshotDir, key + CacheSuffix);

        var cached = TryLoadFile(path);
        if (cached is not null)
            return IconResult.Answer(cached);

        if (_screenshots.IsQuiet)
            return IconResult.NotTried;

        byte[] bytes;

        await _screenshots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await Http
                .GetAsync(package.ScreenshotUrl!, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            _screenshots.NoteSuccess();

            if (!response.IsSuccessStatusCode)
                return IconResult.Answer(null);

            if (response.Content.Headers.ContentLength > MaxScreenshotBytes)
                return IconResult.Answer(null);

            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out rather than answered: nothing was learned about the
            // image, so this must not be remembered as "there is none".
            _screenshots.NoteFailure();
            return IconResult.NotTried;
        }
        catch (HttpRequestException ex)
        {
            _screenshots.NoteFailure(ex);
            return IconResult.NotTried;
        }
        finally
        {
            _screenshots.Release();
        }

        // Outside the permit: decoding a press image is slow and costs nothing
        // on the wire, so holding one of two permits through it would idle half
        // the allowance.
        if (bytes.Length is < 1024 or > MaxScreenshotBytes)
            return IconResult.Answer(null);

        var decoded = DecodeScaled(bytes, ScreenshotDecodeWidth);
        if (decoded is null)
            return IconResult.Answer(null);

        SaveFile(path, decoded);
        return IconResult.Answer(decoded);
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

    private async Task<IconResult> LoadAsync(AppPackage package, string key, CancellationToken ct)
    {
        // Ahead of the disk cache as well as the network: the cache holds what
        // some site said, and this build knows better what it looks like.
        if (string.Equals(package.Id, AppInfo.PackageId, StringComparison.OrdinalIgnoreCase))
            return IconResult.Answer(OwnIcon.Value);

        var cached = TryLoadFromDisk(key);
        if (cached is not null)
            return IconResult.Answer(cached);

        // Only successes used to reach the disk, so a package with no findable
        // icon paid the whole hunt again on every launch - one homepage fetch,
        // four declared candidates and four root guesses. "There is none" is an
        // answer too, and this is where it is remembered.
        var source = HuntSource(package);
        if (HasRecentMiss(key, source))
            return IconResult.Answer(null);

        if (_icons.IsQuiet)
            return IconResult.NotTried;

        // No permit is taken here. It is claimed and released around each
        // request instead: holding one across the fan-out let a single package
        // occupy a quarter of the allowance for up to nine timeouts, which
        // stalled every other icon on the page behind four slow homepages.
        var candidates = await CandidateUrlsAsync(package, ct).ConfigureAwait(false);

        // Nowhere to look yet - no homepage, a code host we skip on purpose,
        // or a scheme we cannot fetch. Not an answer: a homepage may arrive
        // later, and re-deciding this costs no requests.
        if (candidates.Count == 0)
            return IconResult.NotTried;

        var looked = true;

        foreach (var url in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // The backoff can arm while this fan-out is still running. Stop
            // adding to a network that has just stopped answering, rather
            // than only throttling whatever starts next.
            if (_icons.IsQuiet)
                return IconResult.NotTried;

            // No reset needed on the way out: TryFetchAsync has already
            // noted the successful exchange that produced this image.
            var attempt = await TryFetchAsync(url, key, ct).ConfigureAwait(false);

            if (attempt.Image is not null)
                return attempt;

            looked &= attempt.Tried;
        }

        // Every candidate answered and none of them held an icon: that is a
        // real answer and worth keeping. If any of them never answered, it
        // is not.
        if (!looked)
            return IconResult.NotTried;

        MarkMiss(key, source);
        return IconResult.Answer(null);
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

        await _icons.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await Http
                .GetAsync(homepage, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            _icons.NoteSuccess();

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
            _icons.NoteFailure();
            return Array.Empty<string>();
        }
        catch (HttpRequestException ex)
        {
            // The likeliest dead host in the app: a catalogue homepage that no
            // longer resolves. NoteFailure sorts that from a dead network.
            _icons.NoteFailure(ex);
            return Array.Empty<string>();
        }
        finally
        {
            // Released before the parsing below, which is all local work.
            _icons.Release();
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

    private async Task<IconResult> TryFetchAsync(string url, string key, CancellationToken ct)
    {
        byte[] bytes;

        await _icons.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            _icons.NoteSuccess();

            if (!response.IsSuccessStatusCode)
                return IconResult.Answer(null);

            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout, not our cancellation token.
            _icons.NoteFailure();
            return IconResult.NotTried;
        }
        catch (HttpRequestException ex)
        {
            _icons.NoteFailure(ex);
            return IconResult.NotTried;
        }
        finally
        {
            _icons.Release();
        }

        if (bytes.Length is < 64 or > 4 * 1024 * 1024)
            return IconResult.Answer(null);

        var decoded = Decode(bytes);
        if (decoded is null)
            return IconResult.Answer(null);

        SaveToDisk(key, decoded);
        return IconResult.Answer(decoded);
    }

    /// <summary>
    /// Decodes ico/png/jpg, picks the largest frame an .ico contains, and brings
    /// that frame down to the size it will actually be drawn at.
    ///
    /// The frame is chosen before the scaling rather than by handing an .ico to
    /// <c>DecodePixelWidth</c> and hoping: WPF would then pick whichever frame
    /// sits nearest the requested width, and happily scale a 16x16 one up to
    /// meet it. Largest first, and only ever downwards, keeps a small icon small
    /// instead of inflating it into a blurry one.
    /// </summary>
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

            var source = Downsample(frame, IconDecodeWidth);
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

    /// <summary>
    /// Fits a bitmap inside a square of <paramref name="target"/>, keeping its
    /// aspect ratio. Anything already that size or smaller is handed straight
    /// back - upscaling would spend memory to add nothing.
    ///
    /// The scaled pixels are copied into a bitmap of their own rather than
    /// returning the <see cref="TransformedBitmap"/>: that only wraps its source,
    /// so the full-size frame would stay alive behind it and the saving would be
    /// imaginary. Copying is what lets the original be collected.
    /// </summary>
    private static BitmapSource Downsample(BitmapSource source, int target)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= target)
            return source;

        var scale = (double)target / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));

        var stride = (scaled.PixelWidth * scaled.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * scaled.PixelHeight];
        scaled.CopyPixels(pixels, stride, 0);

        // 96 DPI throughout: every icon is drawn at an explicit Width and Height,
        // so a source's own DPI metadata only muddies what PixelWidth means.
        return BitmapSource.Create(
            scaled.PixelWidth,
            scaled.PixelHeight,
            96,
            96,
            scaled.Format,
            scaled.Palette,
            pixels,
            stride);
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

    private void SaveToDisk(string key, BitmapSource image)
    {
        SaveFile(Path.Combine(_cacheDir, key + CacheSuffix), image);

        // An icon turned up after all - most likely a site that has since
        // published one. The marker saying otherwise is now a lie, and would
        // outlive the image if the cache were ever cleared by hand.
        ClearMiss(key);
    }

    /// <summary>
    /// Where the hunt looked, and therefore what a miss is a statement about.
    /// Kept with the marker so that a package whose homepage changes under it -
    /// `winget show` reporting one the catalogue did not carry - is hunted afresh
    /// instead of being served a conclusion drawn about a different site. That
    /// retry is deliberate and was hard won; a blanket marker would undo it.
    /// </summary>
    private static string HuntSource(AppPackage package) =>
        !string.IsNullOrWhiteSpace(package.IconUrl) ? package.IconUrl! : package.Homepage;

    /// <summary>
    /// Whether the hunt has already been run against this same source, recently,
    /// and came back with nothing.
    /// </summary>
    private bool HasRecentMiss(string key, string source)
    {
        try
        {
            var marker = new FileInfo(MissPath(key));
            if (!marker.Exists)
                return false;

            if (DateTime.UtcNow - marker.LastWriteTimeUtc >= MissLifetime)
            {
                // Expired. Deleted here rather than on the way out, so that a
                // hunt interrupted halfway cannot leave one reading as current.
                marker.Delete();
                return false;
            }

            return string.Equals(
                File.ReadAllText(marker.FullName),
                source,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unreadable marker is no marker. The cost is the hunt we would
            // have run anyway before any of this existed.
            return false;
        }
    }

    /// <summary>
    /// Records "asked, and there is none". The write time is half the content -
    /// it is what lets the answer expire rather than becoming permanent the way
    /// a bare flag would - and the source is the other half.
    /// </summary>
    private void MarkMiss(string key, string source)
    {
        try
        {
            File.WriteAllText(MissPath(key), source);
        }
        catch (Exception)
        {
            // As with a failed image write: we simply look again next launch.
        }
    }

    private void ClearMiss(string key)
    {
        try
        {
            File.Delete(MissPath(key));
        }
        catch (Exception)
        {
            // Leaving it costs one stale marker, which expires on its own.
        }
    }

    private string MissPath(string key) => Path.Combine(_cacheDir, key + MissSuffix);

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
