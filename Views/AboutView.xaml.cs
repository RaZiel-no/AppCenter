using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using AppCenter.Controls;
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

    public AboutView()
    {
        InitializeComponent();

        VersionText.Text = $"Version {AppVersion()} · a winget front-end";

        ShowAuthor();

        ThemeList.ItemsSource = ThemeService.Options;
        ShowBlurb(ThemeService.CurrentId);
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
        var wingetVersion = await WingetService.GetVersionAsync();

        var iconCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AppCenter",
            "icons");

        var catalog = CatalogService.Load();
        var catalogCount = CatalogService.AllById().Count;

        Facts.ItemsSource = new[]
        {
            new Fact("App Center", AppVersion()),
            new Fact("winget", string.IsNullOrWhiteSpace(wingetVersion)
                ? "Not found - install App Installer from the Microsoft Store"
                : wingetVersion),
            new Fact("Curated catalogue", $"{catalogCount} apps across {SectionCount(catalog)} sections"),
            new Fact("Catalogue file", CatalogService.CatalogPath),
            new Fact("Icon cache", iconCache),
            new Fact("Runtime", Environment.Version.ToString()),
        };
    }

    /// <summary>
    /// The number deploy.bat stamped on this build. Informational version is
    /// asked for first because that is what <Version> in the csproj becomes
    /// verbatim - AssemblyVersion is padded out to four parts, so 1.0.4 would
    /// otherwise read as 1.0.4.0 here and match nothing the user was given.
    /// </summary>
    private static string AppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // A source-linked build appends "+<commit>"; the release number is the
        // part in front of it.
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
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
