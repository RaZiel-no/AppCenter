using System.Text.RegularExpressions;
using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The Store is asked about a package by an exact identity - never by name,
/// which pairs Anki with "MemU Anki Flashcard" - and these pin down where
/// those identities come from and what is taken from the answer. The Store
/// itself is not called here: what it says depends on the machine, the
/// region and the day, and the interop that reaches it is exercised against
/// the real thing by hand.
/// </summary>
public class StoreListingTests
{
    // ---------------------------------------------------------------
    // Identities
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(@"MSIX\Microsoft.WindowsTerminal_1.22.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData(@"MSIX\OpenAI.Codex_26.911.7940.0_x64__2p2nqsd0c76g0", "OpenAI.Codex_2p2nqsd0c76g0")]
    [InlineData(@"MSIX\43210267-5A9C-41A7-B780-F1AD0B7096F3_1.9.0.10_x64__9zz6mkmvx6gxw", "43210267-5A9C-41A7-B780-F1AD0B7096F3_9zz6mkmvx6gxw")]
    [InlineData(@"msix\Some.App_1.0.0.0_neutral_split.scale-100_abcdefghijklm", "Some.App_abcdefghijklm")]
    public void Reads_the_family_name_out_of_an_msix_full_name(string id, string family)
    {
        // Name_Version_Architecture_ResourceId_PublisherId: the family is the
        // first and last of those, and the resource id in the middle may be
        // empty, which is what the double underscore is.
        Assert.Equal(family, new AppPackage { Id = id }.PackageFamilyName);
    }

    [Theory]
    [InlineData("7zip.7zip")]
    [InlineData(@"ARP\Machine\X64\{7EC3ABD3-E528-4B3D-83BD-0A6C0CE30807}")]
    [InlineData(@"MSIX\NoUnderscoresAtAll")]
    [InlineData(@"MSIX\Trailing_")]
    [InlineData("")]
    public void Anything_else_has_no_family_name(string id)
    {
        Assert.Null(new AppPackage { Id = id }.PackageFamilyName);
    }

    [Fact]
    public void A_package_from_the_msstore_source_is_its_own_store_id()
    {
        var package = new AppPackage { Id = "9N0DX20HK701", Source = "msstore" };

        Assert.Equal("9N0DX20HK701", package.StoreId);
    }

    [Fact]
    public void A_winget_package_has_no_store_id_unless_the_catalogue_gave_it_one()
    {
        var bare = new AppPackage { Id = "Microsoft.WindowsTerminal", Source = "winget" };
        var listed = new AppPackage { Id = "Microsoft.WindowsTerminal", Source = "winget", StoreId = "9N0DX20HK701" };

        Assert.Null(bare.StoreId);
        Assert.Equal("9N0DX20HK701", listed.StoreId);
        Assert.False(StoreListings.CanAskAbout(bare));
        Assert.True(StoreListings.CanAskAbout(listed));
    }

    [Fact]
    public void Every_store_id_in_the_catalogue_is_shaped_like_one()
    {
        var all = CatalogService.AllById();

        // A packaged listing's id: twelve characters, beginning with 9, as
        // in 9N0DX20HK701. Those are the only ones the Store API answers
        // for - a Win32 app delivered through the Store has a fourteen-
        // character XP id that the API returns nothing for under any
        // product kind, so one of those here would cost a wasted query on
        // every visit and buy nothing.
        var listed = all.Values.Where(entry => entry.Msstore is not null).ToList();
        Assert.NotEmpty(listed);
        Assert.All(listed, entry => Assert.Matches("^9[0-9A-Z]{11}$", entry.Msstore!));
    }

    // ---------------------------------------------------------------
    // What is taken from a listing
    // ---------------------------------------------------------------

    private static StoreListing Listing(params StoreImage[] images) => new()
    {
        StoreId = "9N0DX20HK701",
        Title = "Windows Terminal",
        Images = images,
    };

    [Fact]
    public void Screenshots_are_the_images_tagged_as_such_largest_first()
    {
        var listing = Listing(
            new StoreImage("Logo", 300, 300, "https://x/logo"),
            new StoreImage("Screenshot", 1186, 693, "https://x/small"),
            new StoreImage("Hero", 2400, 1200, "https://x/hero"),
            new StoreImage("Screenshot", 1920, 1080, "https://x/big"),
            new StoreImage("Screenshot", 1920, 1080, "https://x/big"));

        // A hero is a banner, not a screenshot; and the Store lists the same
        // picture at several sizes, which is one screenshot.
        Assert.Equal(["https://x/big", "https://x/small"], listing.Screenshots);
    }

    [Fact]
    public void The_logo_is_the_largest_image_tagged_as_one()
    {
        var listing = Listing(
            new StoreImage("Logo", 50, 50, "https://x/50"),
            new StoreImage("Logo", 100, 100, "https://x/100"),
            new StoreImage("Tile", 620, 620, "https://x/tile"),
            new StoreImage("Logo", 75, 75, "https://x/75"));

        // A 100px logo beats a 620px tile: the tile is that same icon drawn
        // small in the middle of a safe zone.
        Assert.Equal("https://x/100", listing.Logo);
    }

    [Fact]
    public void A_listing_without_a_logo_settles_for_a_square_tile()
    {
        var listing = Listing(
            new StoreImage("Tile", 310, 150, "https://x/wide"),
            new StoreImage("Tile", 300, 300, "https://x/square"),
            new StoreImage("Tile", 71, 71, "https://x/tiny"),
            new StoreImage("Screenshot", 1920, 1080, "https://x/shot"));

        Assert.Equal("https://x/square", listing.Logo);
    }

    [Fact]
    public void A_listing_with_nothing_square_and_big_enough_has_no_logo()
    {
        var listing = Listing(
            new StoreImage("Tile", 71, 71, "https://x/tiny"),
            new StoreImage("BoxArt", 2160, 2160, "https://x/box"));

        // Box art is square but it is the poster, not the icon.
        Assert.Null(listing.Logo);
    }
}
