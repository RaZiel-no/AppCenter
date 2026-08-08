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

    /// <summary>Return to whichever sidebar section is currently selected.</summary>
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

    /// <summary>Called once after the page is placed in the content area.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
