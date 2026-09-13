using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// A package's own page reads `winget list --id` for what is on the machine.
/// It used to reduce the answer to a bool and lose the Available column with
/// it - which is why the page could offer Uninstall for a package Manage was
/// offering an update for. These pin down what the answer now keeps.
/// </summary>
public class InstallStateTests
{
    private static WingetRow Row(string version, string available = "") =>
        new("7-Zip", "7zip.7zip", version, available, "winget");

    [Fact]
    public void Nothing_listed_means_not_installed()
    {
        var state = new InstallState([]);

        Assert.False(state.IsInstalled);
        Assert.False(state.HasUpdate);
        Assert.Empty(state.InstalledVersions);
    }

    [Fact]
    public void Keeps_the_update_winget_offers()
    {
        var state = new InstallState([Row("26.02.00.0", "26.03")]);

        Assert.True(state.IsInstalled);
        Assert.True(state.HasUpdate);
        Assert.Equal("26.03", state.AvailableVersion);
        Assert.Equal(["26.02.00.0"], state.InstalledVersions);
    }

    [Fact]
    public void Lists_every_installed_version_newest_first()
    {
        // winget puts the update on one row only - the one it would upgrade.
        var state = new InstallState([Row("10.0.201"), Row("10.0.302", "10.0.401")]);

        Assert.Equal(["10.0.302", "10.0.201"], state.InstalledVersions);
        Assert.Equal("10.0.401", state.AvailableVersion);
    }

    [Fact]
    public void Drops_blank_and_repeated_versions()
    {
        var state = new InstallState([Row(""), Row("1.0"), Row("1.0")]);

        Assert.Equal(["1.0"], state.InstalledVersions);
    }

    [Fact]
    public void Parses_the_available_column_from_a_real_list_table()
    {
        const string output = """
            Name                      Id        Version    Available Source
            ---------------------------------------------------------------
            7-Zip 26.02 (x64 edition) 7zip.7zip 26.02.00.0 26.03     winget
            """;

        var rows = WingetService.ParseTable(output);
        var state = new InstallState(rows);

        Assert.True(state.HasUpdate);
        Assert.Equal("26.03", state.AvailableVersion);
    }
}

/// <summary>
/// Versions are ordered the way a person reads them, so a list of installs
/// puts 10.0.2 after 9.0.317 rather than before it.
/// </summary>
public class VersionOrderTests
{
    [Theory]
    [InlineData("9.0.317", "10.0.201")]
    [InlineData("1.2", "1.2.1")]
    [InlineData("1.2-rc.1", "1.2")]
    [InlineData("10.0.100-preview.7", "10.0.100-rc.1")]
    [InlineData("3.11.9", "3.13.5")]
    [InlineData("26.02.00.0", "26.03")]
    public void Orders_older_before_newer(string older, string newer)
    {
        Assert.True(VersionOrder.Instance.Compare(older, newer) < 0, $"{older} should sort before {newer}");
        Assert.True(VersionOrder.Instance.Compare(newer, older) > 0, $"{newer} should sort after {older}");
    }

    [Fact]
    public void Treats_equal_versions_as_equal()
    {
        Assert.Equal(0, VersionOrder.Instance.Compare("1.2.3", "1.2.3"));
        Assert.Equal(0, VersionOrder.Instance.Compare("", ""));
    }

    [Fact]
    public void Survives_what_winget_prints_for_a_version_it_cannot_read()
    {
        // "Unknown" and "> 1.8.10" both turn up in the Version column.
        Assert.NotEqual(0, VersionOrder.Instance.Compare("Unknown", "1.0"));
        Assert.Equal(0, VersionOrder.Instance.Compare("> 1.8.10", "> 1.8.10"));
    }
}
