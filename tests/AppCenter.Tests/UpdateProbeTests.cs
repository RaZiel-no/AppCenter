using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The look ahead at updates winget will refuse for the kind of installer.
/// winget checks that only when the upgrade runs; since 1.28.190 its `list
/// --details` and `show` say enough to see it coming. These hold the reading
/// of both, the rule winget applies, and the run itself to account.
/// </summary>
public class UpdateProbeTests : IDisposable
{
    public UpdateProbeTests()
    {
        UpdateProbe.Reset();
        OperationService.Reset();
    }

    public void Dispose()
    {
        UpdateProbe.Reset();
        OperationService.Reset();
    }

    // As winget 1.29 prints them for Git, installed by winget from an Inno
    // Setup installer - which it files under "exe".
    private const string GitDetails =
        """
        Git [Git.Git]
        Version: 2.55.0.3
        Publisher: The Git Development Community
        Local Identifier: ARP\Machine\X64\Git_is1
        Product Code: git_is1
        Installer Category: exe
        Installed Scope: Machine
        Installed Architecture: X64
        Installed Location: C:\Program Files\Git\
        Origin Source: winget
        """;

    private const string GitShow =
        """
        Found Git [Git.Git]
        Version: 2.55.0.3
        Publisher: The Git Development Community
        Publisher Url: https://gitforwindows.org/
        Installer:
          Installer Type: inno
          Installer Url: https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.3/Git-2.55.0.3-64-bit.exe
          Installer SHA256: af12577d0fdff74243a5988197aa49b957d5044edc17004f6ddf0768996f1dca
        """;

    [Fact]
    public void Reads_the_kind_of_installer_the_installed_copy_came_from()
    {
        Assert.Equal("exe", UpdateProbe.InstalledKind(GitDetails));
    }

    [Fact]
    public void Reads_the_kind_of_installer_the_new_version_comes_as()
    {
        Assert.Equal("inno", UpdateProbe.OfferedKind(GitShow));
    }

    [Fact]
    public void Finds_the_kind_by_its_value_when_the_label_is_not_english()
    {
        // A localised winget translates the label and not the kind.
        const string german =
            """
            Git [Git.Git]
            Version: 2.55.0.3
            Installationsprogrammkategorie: msi
            Installierter Bereich: Computer
            """;

        Assert.Equal("msi", UpdateProbe.InstalledKind(german));
    }

    [Fact]
    public void Reads_nothing_from_output_that_names_no_kind()
    {
        Assert.Null(UpdateProbe.InstalledKind("No installed package found matching input criteria."));
        Assert.Null(UpdateProbe.OfferedKind(string.Empty));
    }

    [Fact]
    public void Looks_inside_an_archive_for_the_installer_it_carries()
    {
        const string zipped =
            """
            Installer:
              Installer Type: zip
              Nested Installer Type: nullsoft
            """;

        // An archive's own kind says nothing about what is in it.
        Assert.Equal("nullsoft", UpdateProbe.OfferedKind(zipped));
        Assert.Null(UpdateProbe.OfferedKind("Installer:\n  Installer Type: zip\n"));
    }

    [Theory]
    // winget's own rule: the two have to be of the same family.
    [InlineData("exe", "inno", true)]
    [InlineData("exe", "nullsoft", true)]
    [InlineData("exe", "burn", true)]
    [InlineData("msi", "wix", true)]
    [InlineData("msix", "msstore", true)]
    [InlineData("portable", "portable", true)]
    [InlineData("msi", "inno", false)]
    [InlineData("exe", "msi", false)]
    [InlineData("exe", "wix", false)]
    [InlineData("msix", "exe", false)]
    [InlineData("portable", "exe", false)]
    // What it does not class, it lets through: the probe advises only where it is sure.
    [InlineData("exe", "font", true)]
    [InlineData("something", "msi", true)]
    public void Applies_wingets_rule_for_updating_in_place(string installed, string offered, bool compatible) =>
        Assert.Equal(compatible, UpdateProbe.Compatible(installed, offered));

    [Theory]
    [InlineData("v1.29.380", true)]
    [InlineData("v1.28.190", true)]
    [InlineData("v1.28.110-preview", false)]
    [InlineData("v1.12.470", false)]
    [InlineData("v1.27.999", false)]
    [InlineData("", false)]
    [InlineData("Windows Package Manager v1.29.380", true)]
    public void Knows_which_winget_can_say_what_is_installed(string printed, bool supported) =>
        Assert.Equal(supported, UpdateProbe.VersionAtLeast(printed, 1, 28, 190));

    // -----------------------------------------------------------------
    // The run
    // -----------------------------------------------------------------

    private static AppPackage Pending(string id, string version = "1.0", string available = "2.0") => new()
    {
        Id = id,
        Name = id,
        Version = version,
        AvailableVersion = available,
        Source = "winget",
        IsInstalled = true,
    };

    private static void Stand(
        string version = "v1.29.380",
        Func<string, string>? details = null,
        Func<string, string>? show = null)
    {
        UpdateProbe.WingetVersion = _ => Task.FromResult(version);
        UpdateProbe.Details = (id, _) => Task.FromResult(details?.Invoke(id) ?? GitDetails);
        UpdateProbe.Show = (id, _) => Task.FromResult(show?.Invoke(id) ?? GitShow);
        UpdateProbe.MachineBusy = () => false;
    }

    private static bool Settled(AppPackage package, bool mismatch) =>
        SpinWait.SpinUntil(() => package.InstallerMismatch == mismatch && package.InstalledKind.Length > 0, TimeSpan.FromSeconds(5));

    [Fact]
    public void Flags_a_row_whose_new_version_is_a_different_kind_of_installer()
    {
        var msiDetails = GitDetails.Replace("Installer Category: exe", "Installer Category: msi");
        Stand(details: _ => msiDetails);

        var package = Pending("Git.Git");
        UpdateProbe.Begin([package]);

        Assert.True(Settled(package, mismatch: true), "the probe never painted the row");
        Assert.Equal("msi", package.InstalledKind);
        Assert.Equal("inno", package.OfferedKind);
        Assert.True(package.OffersReinstall);
    }

    [Fact]
    public void Leaves_a_row_alone_when_the_kinds_agree()
    {
        Stand();

        var package = Pending("Git.Git");
        UpdateProbe.Begin([package]);

        Assert.True(Settled(package, mismatch: false));
        Assert.Equal("exe", package.InstalledKind);
        Assert.False(package.OffersReinstall);
    }

    [Fact]
    public void Says_nothing_on_a_winget_too_old_to_ask()
    {
        var asked = 0;
        Stand(version: "v1.12.470", details: _ => { asked++; return GitDetails; });

        var package = Pending("Git.Git");
        UpdateProbe.Begin([package]);

        // No way to know, so no winget process is started to find out.
        Assert.False(SpinWait.SpinUntil(() => asked > 0, TimeSpan.FromMilliseconds(500)));
        Assert.False(package.InstallerMismatch);
    }

    [Fact]
    public void Looks_only_at_the_updates_winget_is_sure_of()
    {
        var asked = new List<string>();
        Stand(details: id => { asked.Add(id); return GitDetails; });

        var pending = Pending("Git.Git");
        var unknown = Pending("Vendor.Unknown", version: "Unknown");
        var pinned = Pending("Vendor.Pinned");
        pinned.IsPinned = true;
        var store = Pending("9NBLGGH4NNS1");
        store.Source = "msstore";

        UpdateProbe.Begin([pending, unknown, pinned, store]);

        Assert.True(Settled(pending, mismatch: false));

        // A pinned package is not going anywhere, one with an unreadable
        // version is reinstalled outright, and the Store handles its own.
        Assert.Equal(["Git.Git"], asked);
    }

    [Fact]
    public void Remembers_what_it_found_for_the_session()
    {
        var asked = 0;
        Stand(details: _ => { asked++; return GitDetails; });

        var first = Pending("Git.Git");
        UpdateProbe.Begin([first]);
        Assert.True(Settled(first, mismatch: false));

        // The next read of the machine makes new rows for the same updates:
        // painted from memory, with no winget process started for them.
        var second = Pending("Git.Git");
        UpdateProbe.Begin([second]);

        Assert.Equal("exe", second.InstalledKind);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Asks_again_when_a_newer_version_is_on_offer()
    {
        var asked = 0;
        Stand(details: _ => { asked++; return GitDetails; });

        var older = Pending("Git.Git", available: "2.0");
        UpdateProbe.Begin([older]);
        Assert.True(Settled(older, mismatch: false));

        var newer = Pending("Git.Git", available: "3.0");
        UpdateProbe.Begin([newer]);
        Assert.True(Settled(newer, mismatch: false));

        // A new version may come as a new kind of installer.
        Assert.Equal(2, asked);
    }

    [Fact]
    public void Waits_while_winget_is_busy_on_the_machines_behalf()
    {
        var busy = true;
        var asked = 0;
        Stand(details: _ => { asked++; return GitDetails; });
        UpdateProbe.MachineBusy = () => busy;

        var package = Pending("Git.Git");
        UpdateProbe.Begin([package]);

        // Two winget processes contend; the look-ahead is the one that can wait.
        Assert.False(SpinWait.SpinUntil(() => asked > 0, TimeSpan.FromMilliseconds(300)));

        busy = false;

        Assert.True(Settled(package, mismatch: false));
    }
}
