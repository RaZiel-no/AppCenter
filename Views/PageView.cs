using System.Windows.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

/// <summary>
/// What a page is allowed to ask of the window that hosts it.
/// </summary>
public interface IShellHost
{
    IconService Icons { get; }

    /// <summary>Push the detail page for a package onto the content area.</summary>
    void ShowDetail(AppPackage package);

    /// <summary>Jump to one of the sidebar destinations by name.</summary>
    void NavigateTo(string destination);

    /// <summary>
    /// Show a category from the picker on Explore. Not a sidebar destination -
    /// it has no nav entry of its own, so the shell remembers it for GoBack.
    /// </summary>
    void ShowCategory(string categoryId);

    /// <summary>
    /// Step back: to the category a detail page was opened from if there was
    /// one, otherwise to whichever sidebar section is selected.
    /// </summary>
    void GoBack();

    /// <summary>Re-read the pending update count and refresh the sidebar badge.</summary>
    void RefreshUpdateBadge();

    /// <summary>Ask the user to confirm a change to this machine.</summary>
    bool ConfirmAction(string title, string message, string confirmLabel);
}

/// <summary>Base for every page hosted in the content area.</summary>
public class PageView : UserControl
{
    public IShellHost Host { get; set; } = null!;

    /// <summary>
    /// What this page is, for remembering how far down it was scrolled. Pages
    /// are rebuilt rather than kept, so the shell files the offset under this
    /// and hands it back when the same page is stepped back to. Null for pages
    /// nothing ever returns to, like a detail page.
    /// </summary>
    public string? ScrollKey { get; set; }

    /// <summary>
    /// The page's scroll area. Every page wraps its content in one, and it is
    /// the UserControl's own content, so this needs no tree walking and works
    /// before the page has been laid out.
    /// </summary>
    public ScrollViewer? Scroller => Content as ScrollViewer;

    /// <summary>Called once after the page is placed in the content area.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
