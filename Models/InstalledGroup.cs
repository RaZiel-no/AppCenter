using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AppCenter.Models;

/// <summary>
/// One row of the installed list: a package on its own, or a family of
/// installs shown as one row with the members underneath. Built by
/// <c>PackageFamilies.Group</c>; the members are the same AppPackage
/// instances the rest of the page paints, so an operation on one of them
/// shows on its row here without anyone forwarding it.
///
/// A publisher's suite is a group of groups: its <see cref="Children"/> are
/// the families and packages inside it, and its members are all of theirs.
/// </summary>
public sealed class InstalledGroup : INotifyPropertyChanged
{
    public InstalledGroup(
        string key,
        string title,
        IReadOnlyList<AppPackage> members,
        IReadOnlyList<InstalledGroup>? children = null,
        string? idText = null)
    {
        Key = key;
        Title = title;
        Members = members;
        Children = children;
        IdText = idText ?? (key.StartsWith("name:", StringComparison.Ordinal) ? string.Empty : key);

        // The lead's icon arrives after the row is on screen, and the row
        // reads it through here.
        Lead.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppPackage.Icon) or nameof(AppPackage.HasIcon))
            {
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(HasIcon));
            }
        };
    }

    /// <summary>The family this row stands for - see <c>PackageFamilies.FamilyKey</c>.</summary>
    public string Key { get; }

    /// <summary>The family's name, or the package's own when it is alone.</summary>
    public string Title { get; }

    /// <summary>Newest first. One member is the common case.</summary>
    public IReadOnlyList<AppPackage> Members { get; }

    /// <summary>A suite's rows - families and packages of its own - or null for anything else.</summary>
    public IReadOnlyList<InstalledGroup>? Children { get; }

    public bool IsSuite => Children is not null;

    /// <summary>
    /// The rows this one opens to that can open in turn: a suite's own, or
    /// just this one. What anything looking for families walks.
    /// </summary>
    public IEnumerable<InstalledGroup> Families => Children ?? [this];

    /// <summary>
    /// The member that stands for the family where only one can: the newest.
    /// Its icon, its colour and - when it is the only one - its id, version,
    /// state and button are the row's.
    /// </summary>
    public AppPackage Lead => Members[0];

    public bool IsGroup => Members.Count > 1;

    /// <summary>How many installs the row covers, for the group heading.</summary>
    public string Subtitle =>
        IsSuite ? $"{Members.Count} installed"
        : IsGroup ? $"{Members.Count} versions installed"
        : Lead.VersionTransition;

    /// <summary>
    /// The winget id, for rows that have one. The handles winget makes up for
    /// everything else - ARP\Machine\X64\{…} - say nothing to anyone and are
    /// left blank. A family shows what its ids have in common; a suite, which
    /// has none, shows nothing.
    /// </summary>
    public string IdText { get; }

    /// <summary>
    /// True for a family that is entirely the plumbing Manage hides by default;
    /// a family with any app in it stays.
    /// </summary>
    public bool IsSystemPackage => Members.All(m => m.IsSystemPackage);

    private bool _isExpanded;

    /// <summary>Whether a family's members are showing. Meaningless for a single.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    // The tile at the left of the row draws from the lead, through the same
    // bindings every other tile in the app uses.

    public ImageSource? Icon => Lead.Icon;
    public bool HasIcon => Lead.HasIcon;
    public string Initial => Lead.Initial;
    public Brush FallbackBrush => Lead.FallbackBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
