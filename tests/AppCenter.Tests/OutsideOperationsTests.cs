using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// winget commands App Center did not start are followed as if it had, found
/// by their command lines. A command read wrongly puts the wrong row to sleep,
/// and a command of App Center's own read under a different key than it runs
/// under would be followed twice - so what is read, and what is left alone,
/// are both held to account here.
/// </summary>
public class OutsideOperationsTests
{
    private static OutsideCommand? Parse(string line) =>
        OutsideOperations.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData("uninstall --id Overwolf.CurseForge --exact --silent --accept-source-agreements --disable-interactivity",
        OperationKind.Uninstall, "Overwolf.CurseForge")]
    [InlineData("install --id Git.Git --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
        OperationKind.Install, "Git.Git")]
    [InlineData("upgrade --id Git.Git --exact --silent --include-unknown --accept-package-agreements",
        OperationKind.Update, "Git.Git")]
    public void Reads_app_centers_own_commands_under_the_keys_they_run_under(string line, OperationKind kind, string key)
    {
        var command = Parse(line);

        Assert.NotNull(command);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(key, command.Key);
    }

    [Fact]
    public void Keys_an_uninstall_of_one_version_as_that_versions_row()
    {
        // What App Center passes for a row that is one of several installs,
        // and what AppPackage.OperationKey is for that row.
        var command = Parse("uninstall --id 7zip.7zip --exact --silent --version 22.01");

        Assert.Equal("7zip.7zip@22.01", command?.Key);
    }

    [Fact]
    public void Keys_an_update_by_id_alone_whatever_version_it_names()
    {
        Assert.Equal("Git.Git", Parse("upgrade --id Git.Git --version 2.50.0")?.Key);
    }

    [Fact]
    public void Reads_an_id_with_spaces_in_it_as_one()
    {
        var command = OutsideOperations.Parse(
            ["uninstall", "--id", @"ARP\Machine\X64\Steam App 730", "--exact", "--silent"]);

        Assert.Equal(@"ARP\Machine\X64\Steam App 730", command?.Id);
    }

    [Theory]
    [InlineData("install Git.Git", "Git.Git")]
    [InlineData("add -e --id=Git.Git", "Git.Git")]
    [InlineData("rm --source winget Git.Git", "Git.Git")]
    [InlineData("update -q Git.Git", "Git.Git")]
    public void Reads_the_ways_a_person_types_it(string line, string id)
    {
        Assert.Equal(id, Parse(line)?.Id);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("upgrade --include-unknown --accept-source-agreements")]
    [InlineData("upgrade --all --silent")]
    [InlineData("show --id Git.Git --exact")]
    [InlineData("search git")]
    [InlineData("--version")]
    [InlineData("install git")]
    [InlineData("install --manifest C:\\manifests\\Git.Git")]
    [InlineData("")]
    public void Leaves_alone_what_changes_nothing_or_cannot_be_told_from_the_line(string line)
    {
        Assert.Null(Parse(line));
    }
}
