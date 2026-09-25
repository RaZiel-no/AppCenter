using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AppCenter.Services;

/// <summary>
/// The other half of the Store: the Win32 apps it carries alongside the
/// packaged ones. Their listings have <c>XP</c> ids rather than <c>9</c>
/// ids, and <c>GetStoreProductsAsync</c> answers nothing for them under any
/// product kind - the documented API knows only what can be installed as a
/// package. They are reachable through the Store's own web catalogue, the
/// endpoint the Store client and winget's msstore source read from. It is
/// not documented, so it may change without notice; when it does, these
/// apps go back to having no screenshots, which is where they started.
///
/// The same request for everyone, in the US market and in English: the
/// pictures are the same in every market that carries the app, and a market
/// that does not is the one case where the answer would differ - a case the
/// Store client on this machine already decides for the install button.
/// </summary>
public static partial class StoreListings
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Whether the Store client can be asked about this id. Packaged
    /// listings have ids beginning with <c>9</c> (<c>9N0DX20HK701</c>); the
    /// Win32 apps the Store also carries have <c>XP</c> ids
    /// (<c>XP9KHM4BK9FZ7Q</c>), which only the web catalogue answers for.
    /// </summary>
    internal static bool IsPackagedId(string storeId) => storeId.StartsWith('9');

    private static async Task<StoreListing?> LookupOnWebAsync(string storeId)
    {
        try
        {
            var url = "https://storeedgefd.dsx.mp.microsoft.com/v9.0/products/"
                + Uri.EscapeDataString(storeId)
                + "?market=US&locale=en-us&deviceFamily=Windows.Desktop";

            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            // A 404 is the catalogue saying there is no such product; anything
            // else that is not a 200 is the catalogue not answering. Either
            // way there is no listing to show.
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseWeb(json);
        }
        catch (Exception)
        {
            // Same rule as the Store client: a listing is decoration, and
            // whatever went wrong the answer is "nothing".
            return null;
        }
    }

    /// <summary>
    /// Reads a listing out of the web catalogue's document, into the same
    /// shape the Store client's answer takes, so that <see cref="StoreListing.Screenshots"/>
    /// and <see cref="StoreListing.Logo"/> read both alike. The catalogue
    /// tags its images in lower case where the API capitalises; the tags are
    /// brought up to the API's spelling.
    /// </summary>
    internal static StoreListing? ParseWeb(string json)
    {
        var document = JsonSerializer.Deserialize(json, AppJsonContext.Default.StoreEdgeDocument);
        var payload = document?.Payload;
        if (payload is null || string.IsNullOrWhiteSpace(payload.ProductId))
            return null;

        var images = new List<StoreImage>();

        foreach (var image in payload.Images ?? [])
        {
            if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var uri))
                continue;

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                continue;

            // As for the Store client's answers: the CDN answers https:// just
            // the same, and there is no reason to fetch a picture in clear.
            var url = image.Url!;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = string.Concat("https://", url.AsSpan(7));

            images.Add(new StoreImage(Purpose(image.ImageType), image.Width ?? 0, image.Height ?? 0, url));
        }

        return new StoreListing
        {
            StoreId = payload.ProductId,
            Title = payload.Title ?? string.Empty,
            Images = images,
        };
    }

    private static string Purpose(string? imageType) => imageType?.ToLowerInvariant() switch
    {
        "screenshot" => "Screenshot",
        "logo" => "Logo",
        "tile" => "Tile",
        "poster" => "Poster",
        "boxart" => "BoxArt",
        "hero" => "Hero",
        null or "" => string.Empty,
        var other => string.Concat(char.ToUpperInvariant(other[0]).ToString(), other.AsSpan(1)),
    };

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
// The shape of the web catalogue's document - the few fields read out of it
// ---------------------------------------------------------------

/// <summary>A product document from the Store's web catalogue, as far as it is read.</summary>
public sealed class StoreEdgeDocument
{
    public StoreEdgePayload? Payload { get; set; }
}

public sealed class StoreEdgePayload
{
    public string? ProductId { get; set; }
    public string? Title { get; set; }
    public List<StoreEdgeImage>? Images { get; set; }
}

public sealed class StoreEdgeImage
{
    /// <summary>"logo", "screenshot", "tile" and so on, in lower case.</summary>
    public string? ImageType { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Url { get; set; }
}
