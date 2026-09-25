using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// Flathub is asked about an app by an exact Flatpak id from the catalogue,
/// and these pin down what is read out of its answer. Flathub itself is not
/// called here: what it says moves as projects update their screenshots.
/// </summary>
public class FlathubListingTests
{
    /// <summary>
    /// The shape of the API's appstream document, cut down to what matters:
    /// dimensions as strings, a ladder of sizes in no particular order, a
    /// "default" flag that is true, false or null and is not read, one
    /// screenshot with no pictures and one whose pictures are not fetchable.
    /// </summary>
    private const string Krita = """
        {
          "id": "org.kde.krita",
          "name": "Krita",
          "summary": "Digital Painting, Creative Freedom",
          "screenshots": [
            {
              "default": false,
              "caption": "The startup window",
              "sizes": [
                { "src": "https://dl.flathub.org/media/org/kde/krita/abc/screenshots/image-2_orig.png", "height": "1043", "scale": "1x", "width": "1920" },
                { "src": "https://dl.flathub.org/media/org/kde/krita/abc/screenshots/image-2_624x338@1.png", "height": "338", "scale": "1x", "width": "624" }
              ]
            },
            {
              "default": true,
              "caption": "  Krita is a full-featured digital painting studio ",
              "sizes": [
                { "src": "https://dl.flathub.org/media/org/kde/krita/abc/screenshots/image-1_224x121@1.png", "height": "121", "scale": "1x", "width": "224" },
                { "src": "https://dl.flathub.org/media/org/kde/krita/abc/screenshots/image-1_orig.png", "height": "1043", "scale": "1x", "width": "1920" },
                { "src": "https://dl.flathub.org/media/org/kde/krita/abc/screenshots/image-1_1248x677@1.png", "height": "677", "scale": "1x", "width": "1248" }
              ]
            },
            {
              "default": null,
              "caption": "No pictures at all",
              "sizes": []
            },
            {
              "default": null,
              "sizes": [
                { "src": "http://example.org/in-the-clear.png", "height": "100", "width": "100" },
                { "src": "screenshots/relative.png", "height": "100", "width": "100" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Reads_the_id_and_name()
    {
        var listing = FlathubListings.Parse(Krita)!;

        Assert.Equal("org.kde.krita", listing.Id);
        Assert.Equal("Krita", listing.Name);
    }

    [Fact]
    public void Takes_the_largest_rendition_of_each_screenshot()
    {
        var listing = FlathubListings.Parse(Krita)!;

        Assert.All(listing.Screenshots, screenshot => Assert.EndsWith("_orig.png", screenshot.Url));
    }

    [Fact]
    public void Keeps_the_screenshots_in_the_order_the_project_lists_them()
    {
        // Not the one flagged "default" first: projects flag several, or
        // none, and the order they wrote is the order they meant.
        var listing = FlathubListings.Parse(Krita)!;

        Assert.Contains("image-2_", listing.Screenshots[0].Url);
        Assert.Contains("image-1_", listing.Screenshots[1].Url);
    }

    [Fact]
    public void Carries_the_caption_trimmed()
    {
        var listing = FlathubListings.Parse(Krita)!;

        Assert.Equal("The startup window", listing.Screenshots[0].Caption);
        Assert.Equal("Krita is a full-featured digital painting studio", listing.Screenshots[1].Caption);
    }

    [Fact]
    public void Skips_a_screenshot_with_nothing_it_would_fetch()
    {
        // The third has no sizes; the fourth has only an http:// picture and
        // a relative path, neither of which is fetched.
        var listing = FlathubListings.Parse(Krita)!;

        Assert.Equal(2, listing.Screenshots.Count);
    }

    [Fact]
    public void A_listing_without_screenshots_is_empty_rather_than_null()
    {
        var listing = FlathubListings.Parse("""{ "id": "org.example.Cli", "name": "A command-line tool" }""")!;

        Assert.Empty(listing.Screenshots);
    }

    [Fact]
    public void A_blank_caption_is_no_caption()
    {
        var listing = FlathubListings.Parse("""
            { "id": "x", "name": "x", "screenshots": [ { "caption": "   ", "sizes": [
              { "src": "https://dl.flathub.org/a.png", "width": "10", "height": "10" } ] } ] }
            """)!;

        Assert.Null(listing.Screenshots[0].Caption);
    }

    [Fact]
    public void A_dimension_that_is_not_a_number_counts_as_nothing()
    {
        // The unsized one is still fetchable, so it is the fallback when
        // nothing sized is listed - but a sized one wins over it.
        var listing = FlathubListings.Parse("""
            { "id": "x", "name": "x", "screenshots": [ { "sizes": [
              { "src": "https://dl.flathub.org/unsized.png", "width": "", "height": null },
              { "src": "https://dl.flathub.org/sized.png", "width": "10", "height": "10" } ] } ] }
            """)!;

        Assert.Equal("https://dl.flathub.org/sized.png", listing.Screenshots[0].Url);
    }

    [Fact]
    public async Task A_package_without_a_flatpak_id_is_not_asked_about()
    {
        var package = new AppPackage { Id = "7zip.7zip", Source = "winget" };

        Assert.False(FlathubListings.CanAskAbout(package));
        Assert.Null(await FlathubListings.ForPackageAsync(package));
    }

    [Fact]
    public void A_winget_package_has_no_flatpak_id_unless_the_catalogue_gave_it_one()
    {
        var bare = CatalogService.ToPackage(new CatalogEntry { Id = "KDE.Krita" });
        var listed = CatalogService.ToPackage(new CatalogEntry { Id = "KDE.Krita", Flatpak = "org.kde.krita" });

        Assert.Null(bare.FlatpakId);
        Assert.Equal("org.kde.krita", listed.FlatpakId);
    }
}
