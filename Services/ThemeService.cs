using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace AppCenter.Services;

/// <summary>
/// One selectable look, plus the frozen swatch brushes the picker draws
/// its preview tiles with. Those are separate instances from the live
/// palette on purpose: they have to keep showing their own theme's
/// colours whichever theme is currently applied.
/// </summary>
public sealed class ThemeOption : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Blurb { get; init; }

    public required Brush ContentBrush { get; init; }
    public required Brush SidebarBrush { get; init; }
    public required Brush CardBrush { get; init; }
    public required Brush CardBorderBrush { get; init; }
    public required Brush AccentBrush { get; init; }
    public required Brush TextPrimaryBrush { get; init; }
    public required Brush TextSecondaryBrush { get; init; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Applies a theme by replacing the brushes in Themes/Palette.xaml with ones
/// mixed from the chosen Themes/Colours file.
///
/// Replacing rather than recolouring is not a stylistic choice: WPF freezes
/// the Freezables in an application-level ResourceDictionary, so the brushes
/// the palette hands out are read-only by the time anything is on screen.
/// Writing a new brush over the key is what raises the resource-changed
/// notification, and that only reaches controls that asked for the brush with
/// DynamicResource - which is why the palette is referenced that way
/// throughout, and why a plain StaticResource here would silently keep the
/// colour it was born with.
/// </summary>
public static class ThemeService
{
    public const string DefaultId = "ubuntu";

    private const string ColourSuffix = "Color";
    private const string BrushSuffix = "Brush";

    private static readonly (string Id, string File, string Name, string Blurb)[] Catalogue =
    [
        ("ubuntu", "Ubuntu", "Ubuntu",
            "The Yaru dark palette this app was built in."),
        ("dark", "Dark", "Dark",
            "Neutral near-black with a blue accent, for when the orange is too loud."),
        ("light", "Light", "Light",
            "Yaru in daylight - white cards, dark text, the orange kept."),
        ("terminal", "Terminal", "Terminal",
            "Phosphor green on black with amber highlights. It is winget under there, after all."),
    ];

    private static readonly Dictionary<string, ResourceDictionary> Loaded = [];

    private static List<ThemeOption>? _options;

    /// <summary>The id of the theme currently applied.</summary>
    public static string CurrentId { get; private set; } = DefaultId;

    /// <summary>Every theme, in picker order. Built once, on first use.</summary>
    public static IReadOnlyList<ThemeOption> Options => _options ??= BuildOptions();

    /// <summary>
    /// Repaints the app in the named theme. An unknown id falls back to the
    /// default rather than leaving the window half-painted.
    /// </summary>
    public static void Apply(string id)
    {
        if (!Catalogue.Any(theme => theme.Id == id))
            id = DefaultId;

        var colours = Colours(id);
        var palette = Palette();

        if (palette is null)
            return;

        foreach (var key in colours.Keys.OfType<string>())
        {
            if (!key.EndsWith(ColourSuffix, StringComparison.Ordinal) || colours[key] is not Color colour)
                continue;

            var brushKey = string.Concat(key.AsSpan(0, key.Length - ColourSuffix.Length), BrushSuffix);

            // The banner stops have no brush of their own; they are mixed into
            // the gradient below instead.
            if (palette.Contains(brushKey))
                palette[brushKey] = Frozen(colour);
        }

        ApplyBannerGradient(colours, palette);

        CurrentId = id;

        // The picker's tiles are built when About first asks for them, with
        // the current theme marked; built already, they are told.
        if (_options is { } options)
            foreach (var option in options)
                option.IsSelected = option.Id == id;
    }

    /// <summary>
    /// The gradient has no single colour to set, so it is remixed from the
    /// three stop colours, keeping the geometry the palette declared.
    /// </summary>
    private static void ApplyBannerGradient(ResourceDictionary colours, ResourceDictionary palette)
    {
        if (palette["BannerGradientBrush"] is not LinearGradientBrush template)
            return;

        var banner = new LinearGradientBrush
        {
            StartPoint = template.StartPoint,
            EndPoint = template.EndPoint,
        };

        for (var i = 0; i < template.GradientStops.Count; i++)
        {
            var stop = template.GradientStops[i];
            var colour = colours[$"BannerStop{i}Color"] is Color themed ? themed : stop.Color;

            banner.GradientStops.Add(new GradientStop(colour, stop.Offset));
        }

        banner.Freeze();
        palette["BannerGradientBrush"] = banner;
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The merged dictionary holding the live brushes - Themes/Palette.xaml.
    /// Found by key rather than by index so re-ordering App.xaml cannot
    /// quietly break theming.
    /// </summary>
    private static ResourceDictionary? Palette()
    {
        var resources = Application.Current?.Resources;

        if (resources is null)
            return null;

        return resources.MergedDictionaries.FirstOrDefault(d => d.Contains("ContentBgBrush")) ?? resources;
    }

    private static List<ThemeOption> BuildOptions() =>
        Catalogue.Select(theme =>
        {
            var colours = Colours(theme.Id);

            return new ThemeOption
            {
                Id = theme.Id,
                Name = theme.Name,
                Blurb = theme.Blurb,
                ContentBrush = Swatch(colours, "ContentBgColor"),
                SidebarBrush = Swatch(colours, "SidebarBgColor"),
                CardBrush = Swatch(colours, "CardBgColor"),
                CardBorderBrush = Swatch(colours, "CardBorderColor"),
                AccentBrush = Swatch(colours, "AccentColor"),
                TextPrimaryBrush = Swatch(colours, "TextPrimaryColor"),
                TextSecondaryBrush = Swatch(colours, "TextSecondaryColor"),
                IsSelected = theme.Id == CurrentId,
            };
        }).ToList();

    private static SolidColorBrush Swatch(ResourceDictionary colours, string key) =>
        Frozen(colours[key] is Color colour ? colour : Colors.Gray);

    private static ResourceDictionary Colours(string id)
    {
        if (Loaded.TryGetValue(id, out var cached))
            return cached;

        var file = Catalogue.First(theme => theme.Id == id).File;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Themes/Colours/{file}.xaml", UriKind.Relative),
        };

        Loaded[id] = dictionary;
        return dictionary;
    }
}
