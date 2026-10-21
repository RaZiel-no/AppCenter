using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The update rows out of what the API read of the machine, which have to
/// say what the rows out of `winget upgrade --include-unknown
/// --include-pinned` say - see <see cref="UpdateListTests"/> for those.
/// These pin down which of the API's rows become updates, what each carries
/// over, and which pins send the refresh back to winget.exe.
/// </summary>
public class WingetUpdateCheckTests
{
    private static readonly IReadOnlyList<WingetInstalled> Installed =
    [
        new("Git.Git", "Git", "2.47.0.2", "2.55.0.3", "winget", IsUpdate: true),
        new("7zip.7zip", "7-Zip 22.01", "22.01", "26.02", "winget", IsUpdate: true),
        new("Docker.DockerDesktop", "Docker Desktop", "4.85.0", "4.85.0", "winget", IsUpdate: false),
        new("Cheat.Engine", "Cheat Engine 7.6", "Unknown", "7.6", "winget", IsUpdate: true),
        new(@"ARP\Machine\X64\Mozilla Firefox", "Mozilla Firefox (x64 en-US)", "158.0", "", "", IsUpdate: false),
        new("9WZDNCRFJBMP", "Microsoft Store", "22410.1401.6.0", "22410.1401.7.0", "msstore", IsUpdate: true),
    ];

    private static readonly IReadOnlyDictionary<string, string> NoPins = new Dictionary<string, string>();

    [Fact]
    public void Lists_the_rows_winget_counts_as_updates_and_nothing_else()
    {
        var updates = WingetService.ReadUpgrades(Installed, NoPins);

        Assert.Equal(["7zip.7zip", "Cheat.Engine", "Git.Git", "9WZDNCRFJBMP"], updates.Select(p => p.Id));
    }

    [Fact]
    public void Carries_over_what_the_table_would_have_said()
    {
        var git = WingetService.ReadUpgrades(Installed, NoPins).Single(p => p.Id == "Git.Git");

        Assert.Equal("Git", git.Name);
        Assert.Equal("2.47.0.2", git.Version);
        Assert.Equal("2.55.0.3", git.AvailableVersion);
        Assert.Equal("winget", git.Source);
        Assert.True(git.IsInstalled);
        Assert.False(git.IsPinned);
        Assert.False(git.RequiresExplicitUpdate);
        Assert.Equal(UpdateGroup.Pending, git.Group);
    }

    [Fact]
    public void Orders_by_name_as_winget_prints()
    {
        var names = WingetService.ReadUpgrades(Installed, NoPins).Select(p => p.Name).ToList();

        Assert.Equal(["7-Zip 22.01", "Cheat Engine 7.6", "Git", "Microsoft Store"], names);
    }

    [Fact]
    public void Puts_a_package_whose_version_winget_cannot_read_in_the_unknown_list()
    {
        var cheatEngine = WingetService.ReadUpgrades(Installed, NoPins).Single(p => p.Id == "Cheat.Engine");

        Assert.True(cheatEngine.HasUnknownVersion);
        Assert.Equal(UpdateGroup.Unknown, cheatEngine.Group);
        Assert.Equal("7.6", cheatEngine.AvailableVersion);
    }

    [Fact]
    public void Marks_a_pinned_package_from_the_pins()
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["7ZIP.7zip"] = "Pinning" };

        var updates = WingetService.ReadUpgrades(Installed, pins);
        var sevenZip = updates.Single(p => p.Id == "7zip.7zip");

        Assert.True(sevenZip.IsPinned);
        Assert.Equal("Pinning", sevenZip.PinKind);
        Assert.Equal(UpdateGroup.Skipped, sevenZip.Group);
        Assert.False(updates.Single(p => p.Id == "Git.Git").IsPinned);
    }

    [Fact]
    public void Lists_an_id_once()
    {
        IReadOnlyList<WingetInstalled> twice =
        [
            new("Git.Git", "Git", "2.47.0.2", "2.55.0.3", "winget", IsUpdate: true),
            new("git.git", "Git", "2.46.0", "2.55.0.3", "winget", IsUpdate: true),
        ];

        Assert.Single(WingetService.ReadUpgrades(twice, NoPins));
    }

    [Fact]
    public void Reads_nothing_from_a_machine_with_nothing_to_update()
    {
        Assert.Empty(WingetService.ReadUpgrades([], NoPins));
        Assert.Empty(WingetService.ReadUpgrades(Installed.Where(r => !r.IsUpdate).ToList(), NoPins));
    }

    [Theory]
    [InlineData("Pinning", false)]
    [InlineData("pinning", false)]
    [InlineData("Blocking", true)]
    [InlineData("Gating", true)]
    public void Only_a_plain_pin_leaves_the_update_check_to_the_api(string kind, bool needsWinget)
    {
        var pins = new Dictionary<string, string> { ["Git.Git"] = kind };

        Assert.Equal(needsWinget, WingetService.UpgradesNeedWinget(pins));
    }

    [Fact]
    public void No_pins_at_all_leave_the_update_check_to_the_api()
    {
        Assert.False(WingetService.UpgradesNeedWinget(NoPins));
    }
}
