using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using AppCenter.Controls;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class AboutView : PageView
{
    private const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.txt";

    /// <summary>
    /// The author's name as it is actually written. Everywhere outside this
    /// page - the installer's publisher, the Add/Remove entry, the winget
    /// manifest - uses the plain spelling from &lt;Company&gt; in the csproj,
    /// because those want something conservative. This page is ordinary WPF
    /// text, so it can show the real thing.
    /// </summary>
    private const string AuthorName = "Arnstein \"RaZiel\" Skåra";
    // The GitHub profile rather than an email address: reachable all the same,
    // but nothing for address harvesters once the repo and binary are public.
    private const string AuthorLink = "https://github.com/RaZiel-no";

    private sealed record Fact(string Label, string Value);

    /// <summary>
    /// A stand-in for App Center's own package, so the update card's bar and
    /// status are painted by OperationService like any row's.
    /// </summary>
    private readonly AppPackage _self = new() { Id = AppInfo.PackageId, Name = "App Center" };

    public AboutView()
    {
        InitializeComponent();

        VersionText.Text = $"Version {AppInfo.Version} · a winget front-end";

        ShowAuthor();

        ThemeList.ItemsSource = ThemeService.Options;
        ShowBlurb(ThemeService.CurrentId);

        UpdateBar.DataContext = _self;
        CheckOnLaunch.IsChecked = SettingsService.Current.CheckForUpdates;

        AppUpdateService.Changed += OnUpdateChanged;
        OperationService.Started += OnOperationChanged;
        OperationService.Progressed += OnOperationChanged;
        OperationService.Finished += OnOperationChanged;

        Unloaded += (_, _) =>
        {
            AppUpdateService.Changed -= OnUpdateChanged;
            OperationService.Started -= OnOperationChanged;
            OperationService.Progressed -= OnOperationChanged;
            OperationService.Finished -= OnOperationChanged;
        };

        ShowUpdateState();
    }

    // ---------------------------------------------------------------
    // Updates
    // ---------------------------------------------------------------

    private void OnUpdateChanged(object? sender, EventArgs e) => ShowUpdateState();

    private void OnOperationChanged(object? sender, Operation operation) => ShowUpdateState();

    /// <summary>
    /// Paints the update card from what the service knows: checking, could not
    /// check, newer version waiting, or up to date - and, over the top of any of
    /// those, the download in progress or how the last one ended.
    /// </summary>
    private void ShowUpdateState()
    {
        var latest = AppUpdateService.Latest;
        var running = OperationService.For(AppInfo.PackageId);
        var canStart = OperationService.CanStart(AppInfo.PackageId);

        OperationService.Paint(_self);

        UpdateButton.Visibility = Visibility.Collapsed;
        ReleasePageButton.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = canStart;
        CheckUpdatesButton.IsEnabled = !AppUpdateService.IsChecking && canStart;

        if (AppUpdateService.IsChecking)
        {
            Dot("TextMutedBrush");
            UpdateHeadline.Text = "Checking GitHub for a newer version…";
            UpdateDetail.Text = $"This is version {AppInfo.Version}.";
        }
        else if (AppUpdateService.LastError is { } error)
        {
            Dot("ErrorBrush");
            UpdateHeadline.Text = "Could not check for a newer version";
            UpdateDetail.Text = $"{error} This is version {AppInfo.Version}; the releases page has the rest.";
            ShowReleasePage("Releases on GitHub");
        }
        else if (latest is null)
        {
            Dot("TextMutedBrush");
            UpdateHeadline.Text = $"Version {AppInfo.Version}";
            UpdateDetail.Text = "GitHub has not been asked for a newer version yet.";
        }
        else if (AppUpdateService.IsAvailable)
        {
            Dot("AccentBrush");
            UpdateHeadline.Text = $"Version {latest.Version} is available";
            UpdateDetail.Text = Offer(latest);
            ShowReleasePage("What's new");

            if (AppInfo.IsInstalledCopy && latest.HasInstaller)
            {
                UpdateButton.Content = $"Update to {latest.Version}";
                UpdateButton.Visibility = Visibility.Visible;
            }
        }
        else
        {
            Dot("GreenBrush");
            UpdateHeadline.Text = "App Center is up to date";
            UpdateDetail.Text =
                $"Version {AppInfo.Version} is the latest release on GitHub"
                + (AppUpdateService.CheckedAt is { } at ? $", checked at {at:HH:mm}." : ".");
            ShowReleasePage("Release notes");
        }

        // The operation's line over the top, while it runs and once it is done.
        var line = running?.Status
            ?? (OperationService.LastOutcome is { } outcome
                && string.Equals(outcome.Key, AppInfo.PackageId, StringComparison.OrdinalIgnoreCase)
                ? outcome.Summary
                : null);

        UpdateProgress.Text = line ?? string.Empty;
        UpdateProgress.Visibility = string.IsNullOrEmpty(line) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What the newer release means for this copy, in a sentence or two.</summary>
    private static string Offer(AppRelease latest)
    {
        var released = latest.PublishedAt is { } at
            ? $"Released on GitHub on {at.LocalDateTime:d MMMM yyyy}. "
            : "Released on GitHub. ";

        if (!latest.HasInstaller)
            return released + "The release has no installer attached, so it has to be fetched from its page.";

        if (!AppInfo.IsInstalledCopy)
            return released + "This is a portable copy: download the new one from the release page and unpack it over this folder.";

        return released +
               "Updating from here runs the same installer winget will offer once its pull request is merged - " +
               "App Center closes while it runs and opens again on the new version.";
    }

    private void ShowReleasePage(string label)
    {
        ReleasePageLabel.Text = label;
        ReleasePageButton.Visibility = Visibility.Visible;
    }

    private void Dot(string brushKey) =>
        UpdateDot.Fill = (System.Windows.Media.Brush)FindResource(brushKey);

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (AppUpdateService.Latest is not { } latest || !OperationService.CanStart(AppInfo.PackageId))
            return;

        var confirmed = Host.ConfirmAction(
            $"Update App Center to {latest.Version}?",
            $"The installer for {latest.Version} is downloaded from GitHub and run silently - the same one " +
            "winget will offer once its pull request is merged. App Center closes while it runs and opens " +
            "again on the new version.\n\n" +
            "No administrator permission is needed: App Center installs per user.",
            "Update");

        if (!confirmed)
            return;

        AppUpdateService.StartUpdate();
    }

    private void OnReleasePageClick(object sender, RoutedEventArgs e) =>
        Open(AppUpdateService.Latest?.PageUrl is { Length: > 0 } page ? page : $"{AppInfo.RepositoryUrl}/releases");

    private async void OnCheckClick(object sender, RoutedEventArgs e) => await AppUpdateService.CheckAsync();

    private void OnCheckOnLaunchToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        SettingsService.Current.CheckForUpdates = CheckOnLaunch.IsChecked == true;
        SettingsService.Save();
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {target}: {ex.Message}");
        }
    }

    /// <summary>
    /// Attribution, not a copyright notice. Protection is automatic and needs
    /// no notice, and the GPL wants its notices in LICENSE - which ships beside
    /// the binary - rather than in a dialog. The assembly still carries a
    /// formal one for the exe's file properties, the installer and the winget
    /// manifest; what belongs here is who wrote it and how to reach them.
    /// </summary>
    private void ShowAuthor()
    {
        var link = new Hyperlink(new Run(AuthorName))
        {
            NavigateUri = new Uri(AuthorLink),
            Style = (Style)FindResource("LinkText"),
            ToolTip = "The author on GitHub",
        };
        link.RequestNavigate += OnLinkClicked;

        AuthorText.Inlines.Clear();
        AuthorText.Inlines.Add(new Run("Made by "));
        AuthorText.Inlines.Add(link);
    }

    /// <summary>
    /// Applies the clicked swatch. The palette brushes are shared, so this
    /// repaints the window in place - this page included, which is why there
    /// is nothing to reload afterwards.
    /// </summary>
    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        // Source has been re-mapped to the ItemsControl by now; the swatch is
        // only reachable from OriginalSource. See TreeSearch.FindAncestor.
        var swatch = TreeSearch.FindAncestor<RadioButton>(e.OriginalSource as DependencyObject);

        if (swatch?.DataContext is not ThemeOption theme || theme.Id == ThemeService.CurrentId)
            return;

        ThemeService.Apply(theme.Id);
        ShowBlurb(theme.Id);

        SettingsService.Current.Theme = theme.Id;
        SettingsService.Save();
    }

    private void ShowBlurb(string id) =>
        ThemeBlurb.Text = ThemeService.Options.FirstOrDefault(theme => theme.Id == id)?.Blurb ?? string.Empty;

    /// <summary>
    /// "license" resolves to the copy shipped beside the exe, so the licence is
    /// readable with no network - GPL section 4 wants it travelling with the
    /// binary. Anything else is a real URL.
    /// </summary>
    private void OnLinkClicked(object sender, RequestNavigateEventArgs e)
    {
        var target = e.Uri.OriginalString;

        if (target == "license")
        {
            var local = Path.Combine(AppContext.BaseDirectory, "LICENSE");
            target = File.Exists(local) ? local : LicenseUrl;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {target}: {ex.Message}");
        }

        e.Handled = true;
    }

    public override async Task LoadAsync()
    {
        // Opening About is asking. A launch with the check switched off still
        // gets an answer here, and only here.
        if (!AppUpdateService.HasChecked && !AppUpdateService.IsChecking)
            _ = AppUpdateService.CheckAsync();

        var wingetVersion = await WingetService.GetVersionAsync();

        var iconCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppCenter",
            "icons");

        var catalog = CatalogService.Load();
        var catalogCount = CatalogService.AllById().Count;

        Facts.ItemsSource = new[]
        {
            new Fact("App Center", AppInfo.Version + (AppInfo.IsInstalledCopy ? string.Empty : " (portable copy)")),
            new Fact("winget", string.IsNullOrWhiteSpace(wingetVersion)
                ? "Not found - install App Installer from the Microsoft Store"
                : wingetVersion),
            new Fact("Curated catalogue", $"{catalogCount} apps across {SectionCount(catalog)} sections"),
            new Fact("Catalogue file", CatalogService.CatalogPath),
            new Fact("Icon cache", iconCache),
            new Fact("Runtime", Environment.Version.ToString()),
        };
    }

    private static int SectionCount(CatalogRoot catalog)
    {
        var sections = new[]
        {
            catalog.Explore.Count, catalog.Featured.Count, catalog.Productivity.Count,
            catalog.Development.Count, catalog.Games.Count,
        };

        return sections.Count(count => count > 0);
    }
}
