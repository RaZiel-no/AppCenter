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

    /// <summary>
    /// Hand-picked screenshots from the catalogue, in the order they are
    /// shown. The detail page shows these ahead of anything the Store has.
    /// </summary>
    public IReadOnlyList<string>? Screenshots { get; set; }

    public string Source { get; set; } = string.Empty;

    private string? _storeId;

    /// <summary>
    /// The product's id in the Microsoft Store - <c>9N0DX20HK701</c> - when it
    /// is listed there: from the catalogue's "msstore" field, or the id itself
    /// for a package winget found in the msstore source. Null when there is no
    /// listing to ask about.
    /// </summary>
    public string? StoreId
    {
        get => _storeId ?? (string.Equals(Source, "msstore", StringComparison.OrdinalIgnoreCase) ? Id : null);
        set => _storeId = value;
    }

    /// <summary>
    /// The family name of an installed MSIX package, read out of the id
    /// `winget list` gives one it cannot attribute to a source:
    /// <c>MSIX\Name_Version_Arch_Resource_PublisherId</c> names the family
    /// <c>Name_PublisherId</c>. Null for every other kind of id.
    /// </summary>
    public string? PackageFamilyName => PackageFamilyFromId(Id);

    internal static string? PackageFamilyFromId(string id)
    {
        const string prefix = "MSIX\\";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var full = id[prefix.Length..];
        var first = full.IndexOf('_');
        var last = full.LastIndexOf('_');
        if (first <= 0 || last <= first || last == full.Length - 1)
            return null;

        return string.Concat(full.AsSpan(0, first), "_", full.AsSpan(last + 1));
    }

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

    /// <summary>
    /// True when acting on this package closes App Center: it is the runtime
    /// the app is running on, the winget it drives, or App Center itself.
    /// Decided from the id as the list is read - see <c>SelfPackages</c>.
    /// </summary>
    public bool ClosesApp { get; set; }

    private string _version = string.Empty;
    public string Version
    {
        get => _version;
        set => Set(ref _version, value);
    }

    /// <summary>
    /// True when the installed list held more than one version of this id, so
    /// the id on its own names two installs rather than this one.
    /// </summary>
    public bool IsOneOfSeveralVersions { get; set; }

    /// <summary>
    /// The version winget has to be told about to act on this row instead of on
    /// the id, or null when the id already names one thing on the machine.
    /// </summary>
    public string? IdentifyingVersion => IsOneOfSeveralVersions ? Version : null;

    /// <summary>
    /// What an operation on this row is keyed by. Two versions of one package
    /// are two rows with the same id, and keying by id alone would make them one
    /// row as far as the service is concerned: uninstalling either marks both
    /// busy and hands both the same failure to explain.
    /// </summary>
    public string OperationKey => IsOneOfSeveralVersions ? $"{Id}@{Version}" : Id;

    /// <summary>
    /// What to call this row where the name alone would name two installs -
    /// "7-Zip 26.02". Names that already carry their version are left as they
    /// are: Add/Remove Programs puts it in the name for most of the packages
    /// that end up installed twice, and saying it again reads as a stutter.
    /// </summary>
    public string NameAndVersion =>
        !IsOneOfSeveralVersions
        || Version.Length == 0
        || Name.Contains(Version, StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Name} {Version}";

    /// <summary>
    /// What this install is called inside its family on Manage, when several
    /// are installed: what its name adds to the family's - "2010 x64
    /// Redistributable" - or its version when the name adds nothing. Empty
    /// for a package that is on its own. Set by <c>PackageFamilies.Group</c>.
    /// </summary>
    public string VariantLabel { get; set; } = string.Empty;

    /// <summary>
    /// The id to print on this install's row inside its family: its own,
    /// unless that is the family's - one id installed twice - in which case
    /// the family's row has already said it. Set alongside VariantLabel.
    /// </summary>
    public string VariantId { get; set; } = string.Empty;

    /// <summary>
    /// The line under <see cref="VariantLabel"/>: the version, unless the
    /// label already says it, and the update waiting when there is one.
    /// </summary>
    public string VariantDetail
    {
        get
        {
            if (AvailableVersion.Length > 0)
                return LabelSaysVersion ? $"Update available: {AvailableVersion}" : VersionTransition;

            return LabelSaysVersion ? string.Empty : Version;
        }
    }

    /// <summary>
    /// Whether the label already carries the version: the whole of it, or a
    /// dotted number the version merely goes on from - "14.51.36247" in the
    /// name is the same "14.51.36247.0" winget lists.
    /// </summary>
    private bool LabelSaysVersion =>
        Version.Length > 0
        && (VariantLabel.Contains(Version, StringComparison.OrdinalIgnoreCase)
            || VariantLabel
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.Contains('.') && char.IsDigit(word[0])
                             && Version.StartsWith(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The id when it is one winget knows the package by, otherwise blank.
    /// <c>ARP\Machine\X64\{…}</c> is a handle for an install winget could
    /// not match to any source; it tells a reader nothing and cannot be
    /// searched for, so rows do not print it.
    /// </summary>
    public string DisplayId => Id.Contains('\\') ? string.Empty : Id;

    private string _availableVersion = string.Empty;
    public string AvailableVersion
    {
        get => _availableVersion;
        set
        {
            if (!Set(ref _availableVersion, value))
                return;

            OnPropertyChanged(nameof(VersionTransition));
            OnPropertyChanged(nameof(HasUpdate));
        }
    }

    /// <summary>Whether winget has a newer version than the one installed.</summary>
    public bool HasUpdate => _availableVersion.Length > 0;

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

    // Both of these are painted on by OperationService.Paint rather than set
    // here: an operation outlives the row that started it, so the service is
    // the only thing that can say where the bar should be.

    /// <summary>How far the row's progress bar is along, 0 to 1.</summary>
    private double _progress;
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    /// <summary>
    /// True while the bar should breathe instead of advancing - winget is
    /// working but has not said anything to justify a new position.
    /// </summary>
    private bool _isProgressPulsing;
    public bool IsProgressPulsing
    {
        get => _isProgressPulsing;
        set => Set(ref _isProgressPulsing, value);
    }

    /// <summary>
    /// Why "update all" passed this package over, shown under its row. Empty
    /// for everything that went fine, which is what collapses the line.
    /// </summary>
    private string _error = string.Empty;
    public string Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    /// <summary>
    /// True when that reason is a want of administrator rights, which puts a
    /// "Retry as administrator" under it. Painted on by OperationService.Paint
    /// like the reason itself.
    /// </summary>
    private bool _canRetryAsAdmin;
    public bool CanRetryAsAdmin
    {
        get => _canRetryAsAdmin;
        set => Set(ref _canRetryAsAdmin, value);
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
