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

    /// <summary>
    /// Fields from `winget show` worth surfacing, in display order. Version is
    /// not among them: what `show` calls the version is the newest one in the
    /// source, and which of that and the installed one to call "the version"
    /// depends on the install state - see <see cref="VersionRows"/>.
    /// </summary>
    private static readonly (string Key, string Label)[] InterestingFields =
    [
        ("Publisher", "Publisher"),
        ("Author", "Author"),
        ("License", "Licence"),
        ("Homepage", "Homepage"),
        ("Publisher Url", "Publisher website"),
        ("Release Date", "Released"),
        ("Installer Type", "Installer type"),
    ];

    private readonly AppPackage _package;

    /// <summary>What winget last said about this package being on the machine.</summary>
    private InstallState _state = InstallState.NotInstalled;

    /// <summary>The newest version in the source, as `winget show` reports it.</summary>
    private string _latestVersion = string.Empty;

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
            await RefreshInstallStateAsync();

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

    /// <summary>
    /// Asks winget what is installed under this id and paints the answer:
    /// which buttons to offer, the state line under the publisher, and the
    /// version rows in Details. Rerun after every operation on the package,
    /// because installed-or-not is winget's to say, not ours to infer from an
    /// exit code.
    /// </summary>
    private async Task RefreshInstallStateAsync()
    {
        var state = await WingetService.InstallStateAsync(_package.Id, _cts.Token);
        _cts.Token.ThrowIfCancellationRequested();

        // winget failing to answer is not winget saying "no": the page keeps
        // what it knew rather than offering to install something it was
        // offering to uninstall a moment ago.
        if (state is null)
            return;

        _state = state;

        _package.IsInstalled = _state.IsInstalled;
        _package.AvailableVersion = _state.AvailableVersion;

        // A page opened from a card knows no version yet; the installed one is
        // the version of what the user actually has.
        if (_state.InstalledVersions.Count > 0 && _package.Version.Length == 0)
            _package.Version = _state.InstalledVersions[0];

        ShowInstallState();
        ShowVersionRows();
    }

    private void ApplyFields(Dictionary<string, string> fields)
    {
        if (fields.Count == 0)
        {
            Description.Text = string.IsNullOrWhiteSpace(_package.Summary)
                ? "No description is published for this package."
                : _package.Summary;

            Metadata.ItemsSource = VersionRows().Concat(
            [
                Row("Package ID", _package.Id),
                Row("Source", string.IsNullOrWhiteSpace(_package.Source) ? "winget" : _package.Source),
            ]).ToList();

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

        if (fields.TryGetValue("Version", out var latest))
            _latestVersion = latest.Trim();

        var rows = VersionRows();
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
    /// The version rows at the top of Details. An installed package says what
    /// is on the machine first - every version, when there are several - and
    /// what the source has only when that is something else. A package that is
    /// not installed has one version to speak of.
    /// </summary>
    private List<MetadataRow> VersionRows()
    {
        var rows = new List<MetadataRow>();

        if (_state.IsInstalled)
        {
            var installed = _state.InstalledVersions.Count > 0
                ? string.Join(", ", _state.InstalledVersions)
                : "Unknown";

            rows.Add(Row("Installed version", installed));

            var latest = _state.HasUpdate ? _state.AvailableVersion : _latestVersion;

            if (latest.Length > 0 && !_state.InstalledVersions.Contains(latest, StringComparer.OrdinalIgnoreCase))
                rows.Add(Row("Latest version", latest));
        }
        else if (_latestVersion.Length > 0)
        {
            rows.Add(Row("Version", _latestVersion));
        }

        return rows;
    }

    /// <summary>
    /// Redraws the version rows in place, for when the install state moves
    /// after the rest of Details is already on screen.
    /// </summary>
    private void ShowVersionRows()
    {
        if (Metadata.ItemsSource is not IEnumerable<MetadataRow> current)
            return;

        var others = current.Where(r => r.Label is not ("Installed version" or "Latest version" or "Version"));
        Metadata.ItemsSource = VersionRows().Concat(others).ToList();
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

    /// <summary>
    /// One primary button from the state - Install, or Update when one is
    /// waiting - with Uninstall as the quiet alternative once anything is on
    /// the machine. The line under the publisher says the same in words.
    /// </summary>
    private void ShowInstallState()
    {
        var installed = _state.IsInstalled;
        var update = _state.HasUpdate;

        InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.Visibility = installed && update ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;

        StateRow.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;

        if (!installed)
            return;

        var versions = string.Join(", ", _state.InstalledVersions);

        if (update)
        {
            StateDot.Fill = (System.Windows.Media.Brush)FindResource("AccentBrush");
            StateText.Text = versions.Length > 0
                ? $"Update available  ·  {versions} → {_state.AvailableVersion}"
                : $"Update available  ·  {_state.AvailableVersion}";
        }
        else
        {
            StateDot.Fill = (System.Windows.Media.Brush)FindResource("GreenBrush");
            StateText.Text = versions.Length > 0 ? $"Installed  ·  {versions}" : "Installed";
        }
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

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        // Keyed to the id, as an update from a Manage row is: winget upgrades
        // the package, not one install of it, so every row of it goes busy.
        if (!OperationService.CanStart(_package.Id))
            return;

        var from = _state.InstalledVersions.Count > 0 ? _state.InstalledVersions[0] : "installed version";

        var confirmed = Host.ConfirmAction(
            $"Update {_package.Name}?",
            $"winget will install {_state.AvailableVersion} over the installed {from}.\n\n" +
            "Windows may prompt for administrator permission." +
            (SelfPackages.Includes(_package.Id) ? $"\n\n{SelfPackages.Warning}" : string.Empty),
            "Update");

        if (!confirmed)
            return;

        OperationService.Start(
            _package.Id, _package.Name, OperationKind.Update,
            (progress, token) => WingetService.UpgradeAsync(_package.Id, progress, token));
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
            "Windows may prompt for administrator permission." +
            (SelfPackages.Includes(_package.Id) ? $"\n\n{SelfPackages.RemovalWarning}" : string.Empty),
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
        var mine = OperationService.For(_package.OperationKey) ?? OperationService.For(_package.Id);
        var canStart = OperationService.CanStart(_package.OperationKey) && OperationService.CanStart(_package.Id);

        InstallButton.IsEnabled = canStart;
        UpdateButton.IsEnabled = canStart;
        UninstallButton.IsEnabled = canStart;

        // Drives the progress bar bound to this page's package. Re-derived on
        // every call, which is what lets a page opened - or returned to -
        // halfway through an install draw the bar in the right place.
        OperationService.Paint(_package);

        if (mine is not null)
            SetProgress(mine.Status);
        else if (OperationService.LastOutcome is { } outcome && IsAboutThis(outcome))
            SetProgress(outcome.Summary);

        // Nothing running and nothing finished recently leaves the line as it
        // is, rather than blanking whatever it is already saying.
    }

    /// <summary>
    /// Whether an operation was on this package: keyed to this install, or to
    /// the id - which is how an update, from here or from Manage, is keyed.
    /// </summary>
    private bool IsAboutThis(Operation operation) =>
        string.Equals(operation.Key, _package.OperationKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(operation.Key, _package.Id, StringComparison.OrdinalIgnoreCase);

    private void OnOperationChanged(object? sender, Operation operation) => ShowOperationState();

    private async void OnOperationFinished(object? sender, Operation operation)
    {
        if (!IsAboutThis(operation))
        {
            // Somebody else's operation, but it may have freed the buttons.
            ShowOperationState();
            return;
        }

        SetProgress(operation.Summary);
        ShowOperationState();

        try
        {
            await RefreshInstallStateAsync();
        }
        catch (OperationCanceledException)
        {
            // Navigated away while re-checking.
        }
        catch (Exception ex)
        {
            SetProgress($"Could not re-read the install state: {ex.Message}");
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
