using System.Diagnostics;
using AppCenter.Services;
using Xunit;
using Xunit.Abstractions;

namespace AppCenter.Tests;

/// <summary>
/// The check that the API answers what winget.exe answers, run against the
/// real winget on this machine - which is why it is not in the suite
/// test.bat runs by default: `test.bat machine` runs it, taking a quarter
/// of a second per package for the winget.exe half. Every package in the
/// sample is read both ways and the fields compared one by one; a
/// difference is a regression the fallback would never have caught, since
/// nothing failed.
/// </summary>
[Trait("Category", "Machine")]
public class WingetApiParityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Show_answers_what_winget_exe_answers()
    {
        WingetApi.Reset();

        var differences = new List<string>();
        long apiTotal = 0, wingetTotal = 0;

        foreach (var id in Sample())
        {
            var clock = Stopwatch.StartNew();
            var answer = await WingetApi.ShowAsync(id);
            var apiMs = clock.ElapsedMilliseconds;

            clock.Restart();
            var fromWinget = await WingetService.ShowWithWingetAsync(id);
            var wingetMs = clock.ElapsedMilliseconds;

            apiTotal += apiMs;
            wingetTotal += wingetMs;
            output.WriteLine($"{id,-48} api {apiMs,5} ms   winget.exe {wingetMs,5} ms   {(answer.Answered ? (answer.Value is null ? "not found" : "found") : "UNANSWERED")}");

            if (!answer.Answered)
            {
                differences.Add($"{id}: the API gave no answer, so winget.exe would have been asked ({WingetApi.LastError ?? "no error recorded"})");

                if (differences.Count == 1 && WingetApi.LastFailure is { } failure)
                    output.WriteLine(failure.ToString());

                continue;
            }

            var fromApi = answer.Value is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : WingetService.ParseShow(WingetService.RenderShow(answer.Value));

            // Two fields are winget.exe's alone. The moniker is not in the
            // API. "Installer" is what the parser scrapes off the installer
            // block - the names of the package's dependencies, which are the
            // block's only lines without a label - and the API's installer has
            // no dependency list. Nothing reads either.
            fromWinget.Remove("Moniker");
            fromWinget.Remove("Installer");
            fromApi.Remove("Installer");

            foreach (var key in fromApi.Keys.Union(fromWinget.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k))
            {
                var api = fromApi.TryGetValue(key, out var a) ? a : null;
                var winget = fromWinget.TryGetValue(key, out var w) ? w : null;

                if (!string.Equals(api, winget, StringComparison.Ordinal))
                    differences.Add($"{id} [{key}]\n    api:    {api ?? "(absent)"}\n    winget: {winget ?? "(absent)"}");
            }
        }

        output.WriteLine($"total: api {apiTotal} ms, winget.exe {wingetTotal} ms");
        foreach (var difference in differences)
            output.WriteLine(difference);

        Assert.True(WingetApi.IsAvailable, "the API turned itself off during the run");
        Assert.Empty(differences);
    }

    [Fact]
    public async Task Update_check_answers_what_winget_exe_answers()
    {
        WingetApi.Reset();

        // The refresh as it was: three winget processes, one after the other.
        var clock = Stopwatch.StartNew();
        var pins = await WingetService.ListPinsAsync();
        var pinsMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var fromWinget = await WingetService.ListUpgradesAsync(pins);
        var upgradeMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var installed = await WingetService.ListInstalledAsync();
        var listMs = clock.ElapsedMilliseconds;

        // The API's check on its own, cold and then warm.
        clock.Restart();
        var answer = await WingetApi.CheckForUpdatesAsync();
        var coldMs = clock.ElapsedMilliseconds;
        clock.Restart();
        answer = await WingetApi.CheckForUpdatesAsync();
        var warmMs = clock.ElapsedMilliseconds;

        // The refresh as it is now: the check beside the two reads that stay.
        clock.Restart();
        var check = WingetApi.CheckForUpdatesAsync();
        await WingetService.ListPinsAsync();
        await WingetService.ListInstalledAsync();
        var beside = await check;
        var refreshMs = clock.ElapsedMilliseconds;

        output.WriteLine($"winget.exe: pin list {pinsMs} ms, upgrade {upgradeMs} ms, list {listMs} ms = {pinsMs + upgradeMs + listMs} ms; {installed.Count} installed rows, {fromWinget.Count} updates");
        output.WriteLine($"api check: cold {coldMs} ms, warm {warmMs} ms, {(answer.Answered ? answer.Value.Count + " rows" : "UNANSWERED")}");
        output.WriteLine($"refresh now: {refreshMs} ms ({(beside.Answered ? "api answered" : "api UNANSWERED")})");

        Assert.True(answer.Answered, $"the API gave no answer ({WingetApi.LastError ?? "no error recorded"})");
        Assert.True(beside.Answered, $"the API gave no answer beside winget.exe ({WingetApi.LastError ?? "no error recorded"})");
        Assert.False(WingetService.UpgradesNeedWinget(pins), "a blocking or gating pin on this machine sends the refresh to winget.exe; nothing to compare");

        var fromApi = WingetService.ReadUpgrades(answer.Value, pins);
        var differences = new List<string>();

        foreach (var id in fromApi.Select(p => p.Id).Union(fromWinget.Select(p => p.Id), StringComparer.OrdinalIgnoreCase).OrderBy(i => i))
        {
            var api = fromApi.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var winget = fromWinget.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

            if (api is null || winget is null)
            {
                differences.Add($"{id}: {(api is null ? "winget.exe lists it, the API does not" : "the API lists it, winget.exe does not")}");
                continue;
            }

            // RequiresExplicitUpdate is winget.exe's alone: the API has no second table.
            foreach (var (field, a, w) in new[]
            {
                ("Name", api.Name, winget.Name),
                ("Version", api.Version, winget.Version),
                ("AvailableVersion", api.AvailableVersion, winget.AvailableVersion),
                ("Source", api.Source, winget.Source),
                ("IsPinned", api.IsPinned.ToString(), winget.IsPinned.ToString()),
                ("PinKind", api.PinKind, winget.PinKind),
            })
            {
                if (!string.Equals(a, w, StringComparison.Ordinal))
                    differences.Add($"{id} [{field}]\n    api:    {a}\n    winget: {w}");
            }
        }

        output.WriteLine($"{fromApi.Count} updates both ways; names in the API rows: {string.Join(", ", fromApi.Select(p => p.Name))}");
        foreach (var difference in differences)
            output.WriteLine(difference);

        Assert.True(WingetApi.IsAvailable, "the API turned itself off during the run");
        Assert.Empty(differences);
    }

    [Fact]
    public async Task Manifest_icons_come_back_for_the_sample()
    {
        WingetApi.Reset();

        // No winget.exe equivalent to compare with: `winget show` does not
        // print icons. What is checked is that the lookup answers for every
        // id, and how many manifests carry an icon - a third, when sampled
        // in October 2026.
        var clock = Stopwatch.StartNew();
        var withIcons = 0;
        var unanswered = new List<string>();

        foreach (var id in Sample())
        {
            var icons = await WingetApi.IconsAsync(id);
            if (!icons.Answered)
            {
                unanswered.Add($"{id}: {WingetApi.LastError ?? "no error recorded"}");
                continue;
            }

            var candidates = IconService.ManifestCandidates(icons.Value);
            if (candidates.Count > 0)
                withIcons++;

            output.WriteLine($"{id,-48} {icons.Value.Count,2} icons   {(candidates.Count > 0 ? candidates[0] : "-")}");
        }

        output.WriteLine($"{withIcons} of {Sample().Count} ids carry a manifest icon; {clock.ElapsedMilliseconds} ms for the lot");
        foreach (var line in unanswered)
            output.WriteLine(line);

        Assert.True(WingetApi.IsAvailable, "the API turned itself off during the run");
        Assert.Empty(unanswered);
    }

    /// <summary>
    /// Every thirtieth catalogue id, so the sample spans the catalogue rather
    /// than one category; a Store id, since the Store is a source too; and
    /// one that no source has.
    /// </summary>
    private static List<string> Sample()
    {
        var ids = CatalogService.AllById().Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var step = Math.Max(1, ids.Count / 30);

        var sample = ids.Where((_, i) => i % step == 0).Take(30).ToList();
        sample.Add("9WZDNCRFJBMP");
        sample.Add("No.Such.Package.Anywhere");
        return sample;
    }
}
