using System.IO;
using System.Text.RegularExpressions;
using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// winget can say what exists and what is installed, but it has no notion of
/// editorial grouping - that lives in catalog.json, which is written by hand.
/// These run against the real file that ships with the app, because every way
/// it can be wrong fails quietly: a dangling id is a card that never appears, a
/// mistyped icon key is a placeholder glyph, and neither says a word.
/// </summary>
public class CatalogTests
{
    [Fact]
    public void Reads_the_catalogue_that_ships_with_the_app()
    {
        Assert.True(File.Exists(CatalogService.CatalogPath), CatalogService.CatalogPath);
        Assert.NotEmpty(CatalogService.AllById());
    }

    [Theory]
    [InlineData("explore")]
    [InlineData("featured")]
    [InlineData("productivity")]
    [InlineData("development")]
    [InlineData("games")]
    [InlineData("carousel")]
    public void Every_section_the_app_asks_for_has_something_in_it(string section)
    {
        // The sidebar offers all of these; one that came back empty would be a
        // page with nothing on it.
        Assert.NotEmpty(CatalogService.Section(section));
    }

    [Fact]
    public void An_unknown_section_is_empty_rather_than_an_error()
    {
        Assert.Empty(CatalogService.Section("no-such-section"));
    }

    [Fact]
    public void Sections_are_named_however_they_are_cased()
    {
        Assert.Equal(
            CatalogService.Section("games").Count,
            CatalogService.Section("Games").Count);
    }

    [Fact]
    public void Every_entry_carries_an_id()
    {
        var all = CatalogService.AllById();

        // The id is what winget is asked to install; an entry without one is a
        // card that cannot do anything.
        Assert.All(all.Values, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Id)));
    }

    [Fact]
    public void The_same_package_in_two_sections_is_one_entry()
    {
        var all = CatalogService.AllById();

        // A package is deliberately allowed in several sections - the point of
        // keying by id is that its description is not copied about.
        Assert.Equal(
            all.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            all.Count);
    }

    [Fact]
    public void Every_category_resolves_to_the_packages_it_names()
    {
        var all = CatalogService.AllById();

        foreach (var category in CatalogService.Categories())
        {
            var dangling = category.Ids.Where(id => !all.ContainsKey(id)).ToList();

            // Unknown ids are skipped silently at runtime, which is right for a
            // card that could not be drawn but wrong as a way to find out.
            Assert.True(dangling.Count == 0, $"{category.Id}: {string.Join(", ", dangling)}");
        }
    }

    [Fact]
    public void Only_categories_with_something_in_them_are_offered()
    {
        var offered = CatalogService.Categories();

        // Finance, Health and Fitness and News and Weather have nothing in the
        // catalogue yet. They stay in the file so they start working the moment
        // one is added, but a tile that opens an empty page is a dead end.
        Assert.All(offered, category => Assert.NotEmpty(CatalogService.Category(category)));
        Assert.DoesNotContain(offered, category => category.Id == "finance");
    }

    [Fact]
    public void A_category_can_take_its_members_from_a_section()
    {
        var games = CatalogService.CategoryById("games");

        Assert.NotNull(games);
        Assert.Equal("games", games!.Section);

        // Which is how the picker and the sidebar cannot drift apart.
        Assert.Equal(
            CatalogService.Section("games").Select(p => p.Id),
            CatalogService.Category(games).Select(p => p.Id));
    }

    [Fact]
    public void A_category_is_found_however_its_id_is_cased()
    {
        Assert.NotNull(CatalogService.CategoryById("DEVELOPMENT"));
        Assert.Null(CatalogService.CategoryById("no-such-category"));
    }

    [Fact]
    public void Every_category_names_an_icon_that_exists()
    {
        var geometries = Geometries();

        foreach (var category in CatalogService.Categories())
        {
            // An unknown key silently draws the generic grid glyph, so a typo
            // here looks like a design choice rather than a mistake.
            Assert.True(
                geometries.Contains(category.Icon),
                $"{category.Id} names the icon {category.Icon}, which is not in Icons.xaml");
        }
    }

    [Fact]
    public void The_fallback_icon_the_converter_reaches_for_exists()
    {
        Assert.Contains("IconGrid", Geometries());
    }

    private static HashSet<string> Geometries()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Icons.xaml"));

        return Regex.Matches(xaml, @"x:Key=""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    // -----------------------------------------------------------------
    // Turning an entry into a package
    // -----------------------------------------------------------------

    [Fact]
    public void Falls_back_to_the_id_when_an_entry_has_no_name()
    {
        var package = CatalogService.ToPackage(new CatalogEntry { Id = "Git.Git" });

        Assert.Equal("Git.Git", package.Name);
    }

    [Theory]
    [InlineData("verified", BadgeKind.Verified)]
    [InlineData("Verified", BadgeKind.Verified)]
    [InlineData("star", BadgeKind.Star)]
    [InlineData("", BadgeKind.None)]
    [InlineData(null, BadgeKind.None)]
    [InlineData("something-else", BadgeKind.None)]
    public void Reads_the_badge_an_entry_asks_for(string? badge, BadgeKind expected)
    {
        var package = CatalogService.ToPackage(new CatalogEntry { Id = "Git.Git", Badge = badge });

        Assert.Equal(expected, package.Badge);
    }

    [Fact]
    public void Carries_everything_the_app_would_otherwise_have_to_ask_winget_for()
    {
        var package = CatalogService.ToPackage(new CatalogEntry
        {
            Id = "Git.Git",
            Name = "Git",
            Publisher = "The Git Project",
            Summary = "Distributed version control.",
            Homepage = "https://git-scm.com",
            Icon = "https://git-scm.com/icon.png",
            Screenshot = "https://git-scm.com/wide.png",
        });

        Assert.Equal("Git.Git", package.Id);
        Assert.Equal("Git", package.Name);
        Assert.Equal("The Git Project", package.Publisher);
        Assert.Equal("Distributed version control.", package.Summary);
        Assert.Equal("https://git-scm.com", package.Homepage);
        Assert.Equal("https://git-scm.com/icon.png", package.IconUrl);
        Assert.Equal("https://git-scm.com/wide.png", package.ScreenshotUrl);
        Assert.Equal("winget", package.Source);
    }
}
