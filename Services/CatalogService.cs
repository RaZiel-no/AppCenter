using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public string? Badge { get; set; }
}

public sealed class CatalogBanner
{
    public string Title { get; set; } = "Featured apps";
    public string ButtonText { get; set; } = "Discover more";
    public List<string> HighlightIds { get; set; } = [];
}

public sealed class CatalogRoot
{
    public CatalogBanner Banner { get; set; } = new();
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
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static CatalogRoot? _cached;

    public static string CatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "catalog.json");

    public static CatalogRoot Load()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(CatalogPath))
            {
                var json = File.ReadAllText(CatalogPath);
                _cached = JsonSerializer.Deserialize<CatalogRoot>(json, Options) ?? new CatalogRoot();
            }
            else
            {
                _cached = new CatalogRoot();
            }
        }
        catch (Exception)
        {
            // A malformed catalog should degrade to an empty one, not take
            // the whole app down - search and Manage still work without it.
            _cached = new CatalogRoot();
        }

        return _cached;
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

    public static AppPackage ToPackage(CatalogEntry entry) => new()
    {
        Id = entry.Id,
        Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
        Publisher = entry.Publisher,
        Summary = entry.Summary,
        Homepage = entry.Homepage,
        IconUrl = entry.Icon,
        ScreenshotUrl = entry.Screenshot,
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
