using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AppCenter.Models;

public enum BadgeKind
{
    None,
    Verified,
    Star,
}

/// <summary>
/// One installable thing. The same type backs curated catalog entries,
/// winget search hits, and rows in the Manage list, so most fields are
/// optional depending on where the instance came from.
/// </summary>
public sealed class AppPackage : INotifyPropertyChanged
{
    public string Id { get; set; } = string.Empty;
    public string? IconUrl { get; set; }

    /// <summary>
    /// A wide promotional image for the Games carousel. Always an explicit URL
    /// from the catalogue - unlike icons there is nothing to guess at, since no
    /// convention says where a project keeps its screenshots.
    /// </summary>
    public string? ScreenshotUrl { get; set; }

    public string Source { get; set; } = string.Empty;

    // Search fills these in after the cards are already on screen, so they
    // have to raise change notifications rather than be plain properties.

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(Initial));
        }
    }

    private string _publisher = string.Empty;
    public string Publisher
    {
        get => _publisher;
        set => Set(ref _publisher, value);
    }

    private string _summary = string.Empty;
    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value);
    }

    private string _homepage = string.Empty;
    public string Homepage
    {
        get => _homepage;
        set => Set(ref _homepage, value);
    }

    private BadgeKind _badge;
    public BadgeKind Badge
    {
        get => _badge;
        set => Set(ref _badge, value);
    }

    /// <summary>True for the Windows/driver noise that Manage hides by default.</summary>
    public bool IsSystemPackage { get; set; }

    private string _version = string.Empty;
    public string Version
    {
        get => _version;
        set => Set(ref _version, value);
    }

    private string _availableVersion = string.Empty;
    public string AvailableVersion
    {
        get => _availableVersion;
        set
        {
            if (Set(ref _availableVersion, value))
                OnPropertyChanged(nameof(VersionTransition));
        }
    }

    /// <summary>Renders as "1.2.3 → 1.2.4" under the name in the updates list.</summary>
    public string VersionTransition =>
        string.IsNullOrWhiteSpace(AvailableVersion)
            ? Version
            : $"{Version} → {AvailableVersion}";

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    private ImageSource? _screenshot;
    public ImageSource? Screenshot
    {
        get => _screenshot;
        set
        {
            if (Set(ref _screenshot, value))
                OnPropertyChanged(nameof(HasScreenshot));
        }
    }

    /// <summary>Drives the carousel's scrim: a photo needs more of one than a flat gradient.</summary>
    public bool HasScreenshot => _screenshot is not null;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        set => Set(ref _isInstalled, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    /// <summary>Shown in the icon tile when no real icon could be fetched.</summary>
    public string Initial =>
        string.IsNullOrWhiteSpace(Name) ? "?" : Name.TrimStart()[..1].ToUpperInvariant();

    private Brush? _fallbackBrush;

    /// <summary>
    /// A stable colour per package id, so the placeholder tile for a given
    /// app looks the same on every launch.
    /// </summary>
    public Brush FallbackBrush => _fallbackBrush ??= BuildFallbackBrush();

    private Brush BuildFallbackBrush()
    {
        var seed = string.IsNullOrEmpty(Id) ? Name : Id;
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in seed)
            {
                hash ^= c;
                hash *= 16777619;
            }

            var hue = hash % 360u;
            var top = ColorFromHsl(hue, 0.55, 0.52);
            var bottom = ColorFromHsl((hue + 24) % 360, 0.58, 0.40);
            var brush = new LinearGradientBrush(top, bottom, 90);
            brush.Freeze();
            return brush;
        }
    }

    private static Color ColorFromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
