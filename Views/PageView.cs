using System.Windows;
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

/// <summary>
/// Base for every page hosted in the content area.
///
/// A page is a header over a scrolling body. The header - the title, the
/// primary actions, a filter bar - stays put while the body scrolls under it,
/// so a long list can be filtered from anywhere in it and an app can be
/// installed from anywhere in its description. Pages lay that out themselves
/// as a DockPanel with a <c>PageHeader</c> Border docked top and a ScrollViewer
/// filling the rest; a page with nothing to pin, like Explore, is a bare
/// ScrollViewer.
/// </summary>
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
    /// True once the body has scrolled under the header. The header's style
    /// reads it to draw the hairline that says so: at the top, header and body
    /// read as one page, and the line would only be a stripe across it.
    /// </summary>
    public static readonly DependencyProperty IsScrolledProperty =
        DependencyProperty.Register(nameof(IsScrolled), typeof(bool), typeof(PageView), new PropertyMetadata(false));

    public bool IsScrolled
    {
        get => (bool)GetValue(IsScrolledProperty);
        private set => SetValue(IsScrolledProperty, value);
    }

    /// <summary>
    /// The page's scroll area: the body of the DockPanel, or the whole content
    /// for a page without a header. Found by shape rather than by name, so a
    /// page needs no code of its own to take part in scroll memory.
    /// </summary>
    public ScrollViewer? Scroller => Content switch
    {
        ScrollViewer scroller => scroller,
        Panel panel => panel.Children.OfType<ScrollViewer>().FirstOrDefault(),
        _ => null,
    };

    public PageView()
    {
        // The body's ScrollChanged bubbles up here. Popups - a combo box's
        // list - route through here too, so only the page's own scroller counts.
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnBodyScrolled));
    }

    private void OnBodyScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Scroller))
            IsScrolled = e.VerticalOffset > 0;
    }

    /// <summary>Called once after the page is placed in the content area.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
