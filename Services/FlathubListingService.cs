using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// One screenshot from a Flathub listing: the largest rendition Flathub keeps
/// of it, and the caption the project wrote for it, when there is one.
/// </summary>
public sealed record FlathubScreenshot(string Url, string? Caption);

/// <summary>What Flathub says about one app.</summary>
public sealed class FlathubListing
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The screenshots, in the order the project lists them.</summary>
    public required IReadOnlyList<FlathubScreenshot> Screenshots { get; init; }
}

/// <summary>
/// Asks Flathub what it knows about an app, through its public, versioned
/// API: one JSON document per Flatpak id, carrying the same AppStream
/// metadata the project ships in its own metainfo file. The pictures it
/// names are on Flathub's own CDN, in several sizes.
///
/// Only ever asked by an exact Flatpak id from the catalogue - never found by
/// name, and never guessed. The ids look like reversed domains, which tempts
/// deriving them from a homepage, but a derived id lands on a launcher for
/// the app, another product from the same publisher or a port nobody
/// maintains often enough that every one is checked by hand.
///
/// Remembered for the session per id, as the Store listings are. An answer
/// is remembered whether it held screenshots or not - Flathub saying "no such
/// app" is an answer. A request that never got one, because the network was
/// not there, is forgotten, so the next visit to the page asks again.
/// </summary>
public static class FlathubListings
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly ConcurrentDictionary<string, Task<Lookup>> Memo = new(StringComparer.Ordinal);

    /// <summary>
    /// The listing for a package that carries a Flatpak id. Null when it has
    /// none, or Flathub has nothing under it, or Flathub could not be reached.
    /// The shared lookup runs without the caller's token - one page navigating
    /// away must not cancel it for the next - and each caller waits on it
    /// with its own.
    /// </summary>
    public static async Task<FlathubListing?> ForPackageAsync(AppPackage package, CancellationToken ct = default)
    {
        var id = package.FlatpakId?.Trim();
        if (string.IsNullOrEmpty(id))
            return null;

        var task = Memo.GetOrAdd(id, LookupAsync);
        var result = await task.WaitAsync(ct).ConfigureAwait(false);

        // Nothing was learned: drop the memo so the next visit tries again.
        // Matched on key and value, so this only ever removes our own entry.
        if (!result.Answered)
            Memo.TryRemove(new KeyValuePair<string, Task<Lookup>>(id, task));

        return result.Listing;
    }

    /// <summary>True when <see cref="ForPackageAsync"/> has something to ask about.</summary>
    public static bool CanAskAbout(AppPackage package) => !string.IsNullOrWhiteSpace(package.FlatpakId);

    /// <summary>
    /// Whether Flathub answered at all. A listing, or its absence, is an
    /// answer; a timeout or an unreachable host is not.
    /// </summary>
    private readonly record struct Lookup(FlathubListing? Listing, bool Answered)
    {
        public static readonly Lookup NotAnswered = new(null, false);
    }

    private static async Task<Lookup> LookupAsync(string id)
    {
        try
        {
            var url = "https://flathub.org/api/v2/appstream/" + Uri.EscapeDataString(id);

            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new Lookup(null, Answered: true);

            if (!response.IsSuccessStatusCode)
                return Lookup.NotAnswered;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new Lookup(Parse(json), Answered: true);
        }
        catch (Exception)
        {
            // A listing is decoration. Whatever went wrong - no network, a
            // timeout, a document that would not parse - the answer for now
            // is "nothing", and the question is left open for next time.
            return Lookup.NotAnswered;
        }
    }

    /// <summary>
    /// Reads a listing out of the API's appstream document. Each screenshot
    /// comes in a ladder of sizes; the largest is taken, since the strip
    /// decodes it down and the lightbox wants it whole. A screenshot with no
    /// usable picture is skipped. The order is the project's own: AppStream
    /// lets a project flag one screenshot as the default, but projects flag
    /// several or none, and the listed order is what they meant either way.
    /// </summary>
    internal static FlathubListing? Parse(string json)
    {
        var app = JsonSerializer.Deserialize(json, AppJsonContext.Default.FlathubAppstream);
        if (app is null)
            return null;

        var screenshots = new List<FlathubScreenshot>();

        foreach (var screenshot in app.Screenshots ?? [])
        {
            var largest = (screenshot.Sizes ?? [])
                .Select(size => (size.Src, Area: Pixels(size.Width) * Pixels(size.Height)))
                .Where(size => IsUsable(size.Src))
                .OrderByDescending(size => size.Area)
                .Select(size => size.Src)
                .FirstOrDefault();

            if (largest is null)
                continue;

            var caption = string.IsNullOrWhiteSpace(screenshot.Caption) ? null : screenshot.Caption.Trim();
            screenshots.Add(new FlathubScreenshot(largest, caption));
        }

        return new FlathubListing
        {
            Id = app.Id ?? string.Empty,
            Name = app.Name ?? string.Empty,
            Screenshots = screenshots,
        };
    }

    /// <summary>The API writes dimensions as strings; a missing or odd one counts as nothing.</summary>
    private static long Pixels(string? dimension) =>
        int.TryParse(dimension, out var value) && value > 0 ? value : 0;

    /// <summary>
    /// Only an absolute https URL is fetched. The document is external data,
    /// and a relative or malformed value would throw out of the fetch, not
    /// fail it.
    /// </summary>
    private static bool IsUsable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AppCenter/1.0 (+winget app browser)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

// ---------------------------------------------------------------
// The shape of the API's document - the few fields read out of it
// ---------------------------------------------------------------

/// <summary>An appstream document from Flathub's API, as far as it is read.</summary>
public sealed class FlathubAppstream
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<FlathubAppstreamScreenshot>? Screenshots { get; set; }
}

public sealed class FlathubAppstreamScreenshot
{
    public string? Caption { get; set; }
    public List<FlathubAppstreamSize>? Sizes { get; set; }
}

/// <summary>One rendition of a screenshot. Width and height come as strings.</summary>
public sealed class FlathubAppstreamSize
{
    public string? Src { get; set; }
    public string? Width { get; set; }
    public string? Height { get; set; }
}
