using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class DetailView : PageView
{
    /// <summary><see cref="Link"/> is set only for rows the browser can open.</summary>
    private sealed record MetadataRow(string Label, string Value, Uri? Link = null)
    {
        public bool IsLink => Link is not null;
    }

    /// <summary>Fields from `winget show` worth surfacing, in display order.</summary>
    private static readonly (string Key, string Label)[] InterestingFields =
    [
        ("Version", "Version"),
        ("Publisher", "Publisher"),
        ("Author", "Author"),
        ("License", "Licence"),
        ("Homepage", "Homepage"),
        ("Publisher Url", "Publisher website"),
        ("Release Date", "Released"),
        ("Installer Type", "Installer type"),
    ];

    private readonly AppPackage _package;

    /// <summary>
    /// Covers this page's own winget lookups only. Installs and uninstalls
    /// belong to OperationService and deliberately outlive the page.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();

    public DetailView(AppPackage package)
    {
        InitializeComponent();

        _package = package;
        DataContext = package;

        Description.Text = string.IsNullOrWhiteSpace(package.Summary)
            ? "Loading description…"
            : package.Summary;

        BadgeVerified.Visibility = package.Badge == BadgeKind.Verified ? Visibility.Visible : Visibility.Collapsed;
        BadgeStar.Visibility = package.Badge == BadgeKind.Star ? Visibility.Visible : Visibility.Collapsed;

        // Whatever winget was already doing to this package when the page
        // opened, it shows here - the buttons and the progress line are
        // driven entirely by what the service reports.
        OperationService.Started += OnOperationChanged;
        OperationService.Progressed += OnOperationChanged;
        OperationService.Finished += OnOperationFinished;

        ShowOperationState();

        Unloaded += (_, _) =>
        {
            OperationService.Started -= OnOperationChanged;
            OperationService.Progressed -= OnOperationChanged;
            OperationService.Finished -= OnOperationFinished;
            _cts.Cancel();
        };
    }

    public override async Task LoadAsync()
    {
        Host.Icons.BeginLoad([_package], Dispatcher);

        try
        {
            var installed = await WingetService.IsInstalledAsync(_package.Id, _cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            _package.IsInstalled = installed;
            ShowInstallState(installed);

            var fields = await WingetService.ShowAsync(_package.Id, _cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            ApplyFields(fields);
        }
        catch (OperationCanceledException)
        {
            // Navigated away while loading.
        }
        catch (Exception ex)
        {
            SetProgress($"Could not read package details: {ex.Message}");
        }
    }

    private void ApplyFields(Dictionary<string, string> fields)
    {
        if (fields.Count == 0)
        {
            Description.Text = string.IsNullOrWhiteSpace(_package.Summary)
                ? "No description is published for this package."
                : _package.Summary;

            Metadata.ItemsSource = new[]
            {
                Row("Package ID", _package.Id),
                Row("Source", string.IsNullOrWhiteSpace(_package.Source) ? "winget" : _package.Source),
            };

            return;
        }

        if (fields.TryGetValue("Description", out var description) && description.Length > 0)
            Description.Text = description;
        else if (fields.TryGetValue("Short Description", out var shortDescription))
            Description.Text = shortDescription;
        else if (string.IsNullOrWhiteSpace(_package.Summary))
            Description.Text = "No description is published for this package.";
        else
            Description.Text = _package.Summary;

        if (fields.TryGetValue("Publisher", out var publisher) && publisher.Length > 0)
            _package.Publisher = publisher;

        if (fields.TryGetValue("Homepage", out var homepage) && homepage.Length > 0)
        {
            _package.Homepage = homepage;

            // A homepage is what makes an icon fetchable, so retry now.
            Host.Icons.BeginLoad([_package], Dispatcher);
        }

        var rows = new List<MetadataRow>();
        foreach (var (key, label) in InterestingFields)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                rows.Add(Row(label, value));
        }

        rows.Add(Row("Package ID", _package.Id));
        rows.Add(Row("Source", string.IsNullOrWhiteSpace(_package.Source) ? "winget" : _package.Source));

        Metadata.ItemsSource = rows;
    }

    /// <summary>
    /// winget prints homepages and publisher sites as bare text, so any value
    /// that is an http(s) address becomes a link. Other schemes are left as
    /// plain text - the catalogue is data, and it should not be able to hand
    /// ShellExecute a file path or a protocol handler.
    /// </summary>
    private static MetadataRow Row(string label, string value)
    {
        var trimmed = value.Trim();

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new MetadataRow(label, trimmed, uri)
            : new MetadataRow(label, value);
    }

    private void ShowInstallState(bool installed)
    {
        InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (!OperationService.CanStart(_package.OperationKey))
            return;

        var confirmed = Host.ConfirmAction(
            $"Install {_package.Name}?",
            $"winget will download and install {_package.Id} from the {(_package.Source.Length > 0 ? _package.Source : "winget")} source.\n\n" +
            "Windows may prompt for administrator permission.",
            "Install");

        if (!confirmed)
            return;

        OperationService.Start(
            _package.OperationKey, _package.Name, OperationKind.Install,
            (progress, token) => WingetService.InstallAsync(_package.Id, progress, token));
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (!OperationService.CanStart(_package.OperationKey))
            return;

        // A page opened from a Manage row for a package with several versions
        // installed is about that version, not about the id: it says which one
        // it means, and winget is told the same.
        var what = _package.NameAndVersion;

        var confirmed = Host.ConfirmAction(
            $"Uninstall {what}?",
            $"This removes {what} from this computer. " +
            (_package.IsOneOfSeveralVersions
                ? "Other versions of it stay installed. "
                : string.Empty) +
            "Windows may prompt for administrator permission.",
            "Uninstall");

        if (!confirmed)
            return;

        OperationService.Start(
            _package.OperationKey, what, OperationKind.Uninstall,
            (progress, token) => WingetService.UninstallAsync(
                _package.Id, _package.IdentifyingVersion, progress, token));
    }

    // ---------------------------------------------------------------
    // Operation state
    // ---------------------------------------------------------------

    /// <summary>
    /// Paints whatever the service currently knows about this package. Called
    /// when the page is built as well as on every event, so a page opened
    /// halfway through an install looks the same as one that watched it start.
    /// </summary>
    private void ShowOperationState()
    {
        var mine = OperationService.For(_package.OperationKey);
        var canStart = OperationService.CanStart(_package.OperationKey);

        InstallButton.IsEnabled = canStart;
        UninstallButton.IsEnabled = canStart;

        // Drives the progress bar bound to this page's package. Re-derived on
        // every call, which is what lets a page opened - or returned to -
        // halfway through an install draw the bar in the right place.
        OperationService.Paint(_package);

        if (mine is not null)
            SetProgress(mine.Status);
        else if (OperationService.LastOutcome is { } outcome && outcome.Key == _package.OperationKey)
            SetProgress(outcome.Summary);

        // Nothing running and nothing finished recently leaves the line as it
        // is, rather than blanking whatever it is already saying.
    }

    private void OnOperationChanged(object? sender, Operation operation) => ShowOperationState();

    private async void OnOperationFinished(object? sender, Operation operation)
    {
        if (operation.Key != _package.OperationKey)
        {
            // Somebody else's operation, but it may have freed the buttons.
            ShowOperationState();
            return;
        }

        SetProgress(operation.Summary);
        ShowOperationState();

        try
        {
            // Installed or not is now a question for winget, not for us to
            // infer from the exit code.
            var installed = await WingetService.IsInstalledAsync(_package.Id, _cts.Token);

            _package.IsInstalled = installed;
            ShowInstallState(installed);
        }
        catch (OperationCanceledException)
        {
            // Navigated away while re-checking.
        }
    }

    private void SetProgress(string? text)
    {
        ProgressText.Text = text ?? string.Empty;
        ProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Host.GoBack();

    /// <summary>Hands the URL to whatever the machine has set as its browser.</summary>
    private void OnLinkClicked(object sender, RequestNavigateEventArgs e)
    {
        var target = e.Uri.AbsoluteUri;

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetProgress($"Could not open {target}: {ex.Message}");
        }

        e.Handled = true;
    }
}
