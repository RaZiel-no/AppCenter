using System.IO;
using System.Text.Json;
using AppCenter.Models;

namespace AppCenter.Services;

public sealed class CatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string? Icon { get; set; }

    /// <summary>Wide promotional image, used by the Games carousel.</summary>
    public string? Screenshot { get; set; }

    /// <summary>
    /// Screenshots for the detail page, hand-picked from the project's own
    /// site, in the order they are shown. Shown ahead of the Store's.
    /// </summary>
    public List<string>? Screenshots { get; set; }

    /// <summary>
    /// The product's Microsoft Store id, when it is listed there. The detail
    /// page asks the Store for its screenshots, and the icon hunt falls back
    /// to the listing's logo when the homepage yields nothing.
    /// </summary>
    public string? Msstore { get; set; }

    /// <summary>
    /// The same app's Flatpak id, when it is published on Flathub. The
    /// detail page asks Flathub for its screenshots when neither the
    /// catalogue nor the Store has any.
    /// </summary>
    public string? Flatpak { get; set; }

    public string? Badge { get; set; }
}

public sealed class CatalogBanner
{
    public string Title { get; set; } = "Featured apps";
    public string ButtonText { get; set; } = "Discover more";
    public List<string> HighlightIds { get; set; } = [];
}

/// <summary>
/// One tile in the category picker pinned at the foot of Explore.
///
/// Members are named by id rather than repeated as entries, so a package can
/// sit in several categories without its description being copied about. A
/// category may instead name one of the editorial sections, which is how
/// Featured and Games stay in step with the lists the sidebar already shows.
/// </summary>
public sealed class CatalogCategory
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Key of a 24x24 geometry in Themes/Icons.xaml.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Takes its members from this section instead of from Ids.</summary>
    public string? Section { get; set; }

    public List<string> Ids { get; set; } = [];
}

public sealed class CatalogRoot
{
    public CatalogBanner Banner { get; set; } = new();
    public List<CatalogCategory> Categories { get; set; } = [];
    public List<CatalogEntry> Explore { get; set; } = [];
    public List<CatalogEntry> Featured { get; set; } = [];
    public List<CatalogEntry> Productivity { get; set; } = [];
    public List<CatalogEntry> Development { get; set; } = [];
    public List<CatalogEntry> Games { get; set; } = [];
    public List<CatalogEntry> Carousel { get; set; } = [];
}

/// <summary>
/// Loads the curated catalog that backs Explore and the category pages.
/// winget can tell us what exists and what is installed, but it has no
/// notion of editorial grouping - that lives here.
/// </summary>
public static class CatalogService
{
    private static CatalogRoot? _cached;
    private static Task<CatalogRoot>? _loading;

    public static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "catalog.json");

    /// <summary>
    /// Starts reading the file on a thread-pool thread, so that Explore finds
    /// it read rather than reading it.
    /// </summary>
    public static void Preload() => _loading ??= Task.Run(Read);

    public static CatalogRoot Load() => _cached ??= _loading is { } loading ? loading.Result : Read();

    private static CatalogRoot Read()
    {
        try
        {
            if (File.Exists(CatalogPath))
            {
                var json = File.ReadAllText(CatalogPath);
                return JsonSerializer.Deserialize(json, AppJsonContext.Default.CatalogRoot) ?? new CatalogRoot();
            }
        }
        catch (Exception)
        {
            // A malformed catalog should degrade to an empty one, not take
            // the whole app down - search and Manage still work without it.
        }

        return new CatalogRoot();
    }

    public static List<AppPackage> Section(string name)
    {
        var catalog = Load();
        var entries = name.ToLowerInvariant() switch
        {
            "explore" => catalog.Explore,
            "featured" => catalog.Featured,
            "productivity" => catalog.Productivity,
            "development" => catalog.Development,
            "games" => catalog.Games,
            "carousel" => catalog.Carousel,
            _ => [],
        };

        return entries.Select(ToPackage).ToList();
    }

    /// <summary>
    /// The categories worth showing - the ones with something in them. A
    /// category listing nothing yet (Finance has no entries in the catalogue)
    /// stays in the file so it starts working the moment one is added, but a
    /// tile that opens an empty page is a dead end, so it is not offered.
    /// </summary>
    public static List<CatalogCategory> Categories() =>
        Load().Categories.Where(category => Category(category).Count > 0).ToList();

    public static List<AppPackage> Category(CatalogCategory category)
    {
        if (!string.IsNullOrWhiteSpace(category.Section))
            return Section(category.Section);

        var all = AllById();

        // Silently skipping ids the catalogue no longer carries: a card for a
        // package with no entry would have nothing to draw and would fail on
        // install anyway.
        return category.Ids
            .Where(all.ContainsKey)
            .Select(id => ToPackage(all[id]))
            .ToList();
    }

    public static CatalogCategory? CategoryById(string id) =>
        Load().Categories.FirstOrDefault(category =>
            string.Equals(category.Id, id, StringComparison.OrdinalIgnoreCase));

    public static AppPackage ToPackage(CatalogEntry entry) => new()
    {
        Id = entry.Id,
        Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
        Publisher = entry.Publisher,
        Summary = entry.Summary,
        Homepage = entry.Homepage,
        IconUrl = entry.Icon,
        ScreenshotUrl = entry.Screenshot,
        Screenshots = entry.Screenshots,
        StoreId = entry.Msstore,
        FlatpakId = entry.Flatpak,
        Source = "winget",
        Badge = entry.Badge?.ToLowerInvariant() switch
        {
            "verified" => BadgeKind.Verified,
            "star" => BadgeKind.Star,
            _ => BadgeKind.None,
        },
    };

    /// <summary>
    /// Every distinct entry across all sections, used to enrich winget search
    /// hits with a publisher and summary we already know.
    /// </summary>
    public static Dictionary<string, CatalogEntry> AllById()
    {
        var catalog = Load();
        var all = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var list in new[]
                 {
                     catalog.Explore, catalog.Featured, catalog.Productivity,
                     catalog.Development, catalog.Games, catalog.Carousel,
                 })
        {
            foreach (var entry in list)
            {
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    all[entry.Id] = entry;
            }
        }

        return all;
    }
}
