using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class SearchView : PageView
{
    /// <summary>How many hits get a follow-up `winget show` for their blurb.</summary>
    private const int DetailBudget = 12;

    /// <summary>
    /// The card grid is not virtualised, and a loose query like "firefox"
    /// returns hundreds of locale variants. Render a sensible page of them
    /// and say plainly how many were left off.
    /// </summary>
    private const int MaxResults = 100;

    private readonly string _query;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>What is on screen, for re-marking when the machine moves.</summary>
    private List<AppPackage> _results = [];

    public SearchView(string query)
    {
        InitializeComponent();

        _query = query;
        Title.Text = $"Results for “{query}”";
        Status.Text = "Searching winget…";

        Cards.ItemClick += package => Host.ShowDetail(package);
        MachineState.Changed += OnMachineChanged;

        Unloaded += (_, _) =>
        {
            _cts.Cancel();
            MachineState.Changed -= OnMachineChanged;
        };
    }

    private void OnMachineChanged(object? sender, EventArgs e) => MachineState.Apply(_results);

    public override async Task LoadAsync()
    {
        try
        {
            var allResults = await WingetService.SearchAsync(_query, _cts.Token);
            _cts.Token.ThrowIfCancellationRequested();

            var results = allResults.Take(MaxResults).ToList();

            if (results.Count == 0)
            {
                Status.Text = WingetService.IsAvailable
                    ? "No packages matched that search."
                    : "winget could not be started. Install App Installer from the Microsoft Store.";

                Cards.ShowEmpty(false);
                return;
            }

            var catalog = CatalogService.AllById();
            foreach (var package in results)
            {
                if (!catalog.TryGetValue(package.Id, out var entry))
                    continue;

                package.Name = entry.Name;
                package.Publisher = entry.Publisher;
                package.Summary = entry.Summary;
                package.Homepage = entry.Homepage;
                package.IconUrl = entry.Icon;
                package.Badge = CatalogService.ToPackage(entry).Badge;
            }

            Status.Text = allResults.Count > results.Count
                ? $"{allResults.Count} packages found — showing the first {results.Count}. Narrow the search to see the rest."
                : $"{results.Count} package{(results.Count == 1 ? string.Empty : "s")} found.";

            // A search hit carries the version in the source; what the cards
            // say about the machine comes from the machine.
            _results = results;
            MachineState.Apply(results);

            Cards.ItemsSource = results;

            // Show cards straight away, then fill in the blurbs and icons.
            await FillInDetailsAsync(results);
        }
        catch (OperationCanceledException)
        {
            // Navigated away or the query changed under us.
        }
        catch (Exception ex)
        {
            Status.Text = $"Search failed: {ex.Message}";
        }
    }

    /// <summary>
    /// `winget search` returns only name/id/version, so the top results each
    /// get one `winget show` to pick up a publisher, summary and homepage -
    /// the last of which is what makes an icon fetchable.
    /// </summary>
    private async Task FillInDetailsAsync(List<AppPackage> results)
    {
        var pending = results
            .Where(p => string.IsNullOrWhiteSpace(p.Summary))
            .Take(DetailBudget)
            .ToList();

        using var gate = new SemaphoreSlim(3);

        var tasks = pending.Select(async package =>
        {
            await gate.WaitAsync(_cts.Token);
            try
            {
                var fields = await WingetService.ShowAsync(package.Id, _cts.Token);
                if (fields.Count == 0)
                    return;

                await Dispatcher.BeginInvoke(() =>
                {
                    if (fields.TryGetValue("Publisher", out var publisher))
                        package.Publisher = publisher;

                    if (fields.TryGetValue("Description", out var description))
                        package.Summary = description;
                    else if (fields.TryGetValue("Short Description", out var shortDescription))
                        package.Summary = shortDescription;

                    if (fields.TryGetValue("Homepage", out var homepage))
                        package.Homepage = homepage;
                });
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Homepages are known now, so icons finally have somewhere to come from.
        Host.Icons.BeginLoad(results, Dispatcher);
    }
}
