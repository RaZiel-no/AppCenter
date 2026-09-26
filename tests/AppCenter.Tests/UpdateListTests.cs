using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// `winget upgrade` prints up to three tables - the updates, then the packages
/// whose publishers ask to be updated one at a time, then the ones a pin holds
/// back - and a line or two of prose between them. These pin down that every
/// table is read, that each row ends up in the right list, and that the pins
/// winget keeps are read back the same way.
/// </summary>
public class UpdateListTests
{
    private static readonly Dictionary<string, string> NoPins = new(StringComparer.OrdinalIgnoreCase);

    // As winget 1.29 prints it with --include-unknown --include-pinned: the
    // main table, its count, then the explicit-targeting table under its
    // sentence, then the blocking-pin table under its own.
    private const string ThreeTables =
        """
        Name                 Id                        Version      Available    Source
        --------------------------------------------------------------------------------
        7-Zip 22.01          7zip.7zip                 22.01        26.02        winget
        Git                  Git.Git                   Unknown      2.55.0.3     winget
        Oh My Posh           JanDeDobbeleer.OhMyPosh   24.8.0       25.19.0      winget

        3 upgrades available.

        The following packages have an upgrade available, but require explicit targeting for upgrade:
        Name                 Id                        Version      Available    Source
        --------------------------------------------------------------------------------
        Discord              Discord.Discord           1.0.9150     1.0.9200     winget
        Microsoft Teams      Microsoft.Teams           24.1         25.1         winget

        1 package(s) have a pin that needs to be removed before upgrade
        Name                 Id                        Version      Available    Source
        --------------------------------------------------------------------------------
        PowerToys            Microsoft.PowerToys       0.80.0       0.95.0       winget
        """;

    [Fact]
    public void Reads_every_table_winget_prints()
    {
        var tables = WingetService.ParseTables(ThreeTables, installedTable: true);

        Assert.Equal(3, tables.Count);
        Assert.Equal(["7zip.7zip", "Git.Git", "JanDeDobbeleer.OhMyPosh"], tables[0].Select(r => r.Id));
        Assert.Equal(["Discord.Discord", "Microsoft.Teams"], tables[1].Select(r => r.Id));
        Assert.Equal(["Microsoft.PowerToys"], tables[2].Select(r => r.Id));
    }

    [Fact]
    public void The_first_table_is_still_what_parse_table_reads()
    {
        // Everything else that reads one table - search, list - is unchanged.
        Assert.Equal(3, WingetService.ParseTable(ThreeTables, installedTable: true).Count);
    }

    [Fact]
    public void Puts_the_first_table_in_the_updates_and_a_later_one_apart()
    {
        var upgrades = WingetService.ReadUpgrades(ThreeTables, NoPins);

        var sevenZip = Assert.Single(upgrades, p => p.Id == "7zip.7zip");
        var discord = Assert.Single(upgrades, p => p.Id == "Discord.Discord");

        Assert.False(sevenZip.RequiresExplicitUpdate);
        Assert.Equal(UpdateGroup.Pending, sevenZip.Group);

        // A publisher's request to be updated one at a time is what a
        // per-package upgrade already does, so the row is an ordinary update
        // with a note - not a package "update all" has to leave behind.
        Assert.True(discord.RequiresExplicitUpdate);
        Assert.Equal(UpdateGroup.Pending, discord.Group);
        Assert.Contains("Updates itself", discord.UpdateNote);
    }

    [Fact]
    public void Tells_a_pinned_package_from_an_explicit_one_by_the_pins_not_the_sentence()
    {
        // The sentence above a table is winget's language; the pins are not.
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.PowerToys"] = "Blocking",
            ["7zip.7zip"] = "Pinning",
        };

        var upgrades = WingetService.ReadUpgrades(ThreeTables, pins);

        var powerToys = Assert.Single(upgrades, p => p.Id == "Microsoft.PowerToys");
        var sevenZip = Assert.Single(upgrades, p => p.Id == "7zip.7zip");
        var discord = Assert.Single(upgrades, p => p.Id == "Discord.Discord");

        Assert.Equal(UpdateGroup.Skipped, powerToys.Group);
        Assert.Equal("Blocking", powerToys.PinKind);
        Assert.False(powerToys.RequiresExplicitUpdate);

        // A plain pin lands its package in the first table when pins are
        // included, so every row is checked against the pins.
        Assert.Equal(UpdateGroup.Skipped, sevenZip.Group);
        Assert.Equal(UpdateGroup.Pending, discord.Group);
    }

    [Fact]
    public void Puts_a_package_whose_version_winget_cannot_read_in_the_unknown_list()
    {
        var git = Assert.Single(WingetService.ReadUpgrades(ThreeTables, NoPins), p => p.Id == "Git.Git");

        Assert.True(git.HasUnknownVersion);
        Assert.Equal(UpdateGroup.Unknown, git.Group);
        Assert.Contains("cannot read the installed version", git.UpdateNote);
        Assert.Equal("Update", git.ActionLabel);
    }

    [Fact]
    public void Lists_a_package_once_however_many_tables_it_is_in()
    {
        const string repeated =
            """
            Name                 Id                    Version      Available    Source
            ---------------------------------------------------------------------------
            Git                  Git.Git               2.47.0.2     2.55.0.3     winget

            1 upgrades available.

            The following packages have an upgrade available, but require explicit targeting for upgrade:
            Name                 Id                    Version      Available    Source
            ---------------------------------------------------------------------------
            Git                  Git.Git               2.47.0.2     2.55.0.3     winget
            """;

        Assert.Single(WingetService.ReadUpgrades(repeated, NoPins));
    }

    [Fact]
    public void Reads_nothing_from_a_machine_with_nothing_to_update()
    {
        Assert.Empty(WingetService.ReadUpgrades("No installed package found matching input criteria.\n", NoPins));
    }

    // -----------------------------------------------------------------
    // Pins
    // -----------------------------------------------------------------

    [Fact]
    public void Reads_the_pins_winget_keeps()
    {
        // "Pin type" and "Pinned version" are two words each, which the column
        // reader takes for two columns; the id comes before them, and the kind
        // is the first word from where its heading starts.
        const string pins =
            """
            Name        Id                    Version   Source  Pin type  Pinned version
            -----------------------------------------------------------------------------
            PowerToys   Microsoft.PowerToys   0.80.0    winget  Blocking
            7-Zip       7zip.7zip             22.01     winget  Pinning
            Git         Git.Git               2.47.0.2  winget  Gating    2.47.*
            """;

        var read = WingetService.ReadPins(pins);

        Assert.Equal(3, read.Count);
        Assert.Equal("Blocking", read["Microsoft.PowerToys"]);
        Assert.Equal("Pinning", read["7zip.7zip"]);
        Assert.Equal("Gating", read["git.git"]);
    }

    [Fact]
    public void Reads_no_pins_from_a_machine_with_none()
    {
        Assert.Empty(WingetService.ReadPins("There are no pins configured.\n"));
    }

    // -----------------------------------------------------------------
    // The rows themselves
    // -----------------------------------------------------------------

    [Fact]
    public void A_skipped_row_offers_to_resume_and_nothing_else()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git", Version = "2.47", AvailableVersion = "2.55", IsPinned = true };

        Assert.Equal("Resume updates", package.ActionLabel);
        Assert.Equal("resume", package.ActionTag);
        Assert.False(package.OffersSkip);
        Assert.StartsWith("Skipped:", package.UpdateNote);
    }

    [Fact]
    public void An_ordinary_row_offers_update_and_skip()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git", Version = "2.47", AvailableVersion = "2.55" };

        Assert.Equal("Update", package.ActionLabel);
        Assert.Equal("update", package.ActionTag);
        Assert.True(package.OffersSkip);
        Assert.Equal(string.Empty, package.UpdateNote);
    }

    [Fact]
    public void A_row_the_look_ahead_flagged_offers_a_reinstall_beside_update()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git", Version = "2.47", AvailableVersion = "2.55" };

        package.InstalledKind = "msi";
        package.OfferedKind = "inno";
        package.InstallerMismatch = true;

        // Advice, not a refusal: Update stays, a reinstall is offered beside it.
        Assert.Equal("Update", package.ActionLabel);
        Assert.True(package.OffersReinstall);
        Assert.Equal(
            "The new version comes as an inno installer and the installed copy is an msi one, " +
            "so winget will most likely refuse to update it in place. Reinstall to update.",
            package.UpdateNote);
    }

    [Fact]
    public void A_row_already_installed_over_says_when_and_offers_a_reinstall()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git", Version = "Unknown", AvailableVersion = "2.55" };

        package.RecordedInstall = new RecordedInstall("2.55", new DateTime(2026, 9, 26));

        Assert.True(package.IsRecordedAsCurrent);
        Assert.Equal("Reinstall", package.ActionLabel);
        Assert.StartsWith("2.55 was installed on 26 September.", package.UpdateNote);

        // A newer version on offer than the one that went in is an update again.
        package.AvailableVersion = "2.56";

        Assert.False(package.IsRecordedAsCurrent);
        Assert.Equal("Update", package.ActionLabel);
    }
}
