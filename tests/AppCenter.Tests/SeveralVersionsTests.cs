using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// One id can name more than one install. `winget list` prints a row per
/// installed version, so a machine with 7-Zip 22.01 and 26.02 on it shows two
/// rows of 7zip.7zip - and `uninstall --id 7zip.7zip` answers both with
/// 0x8A150016 rather than choosing between them.
///
/// These pin down the way out of that: the version tells the rows apart, so it
/// is what winget is given, and what keeps the two rows from sharing one
/// operation - a busy mark, and a failure, belong to the version they happened
/// to and not to the id.
/// </summary>
public class SeveralVersionsTests : IDisposable
{
    public SeveralVersionsTests() => OperationService.Reset();

    public void Dispose() => OperationService.Reset();

    private static List<AppPackage> Installed(params (string Id, string Version)[] rows) =>
        rows.Select(r => new AppPackage { Id = r.Id, Name = r.Id, Version = r.Version }).ToList();

    // -----------------------------------------------------------------
    // Spotting them in the installed list
    // -----------------------------------------------------------------

    [Fact]
    public void Marks_every_row_of_an_id_that_is_installed_twice()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));

        WingetService.MarkSeveralVersions(packages);

        Assert.All(packages, p => Assert.True(p.IsOneOfSeveralVersions));
    }

    [Fact]
    public void Leaves_an_id_that_names_one_install_alone()
    {
        var packages = Installed(("Git.Git", "2.47.0.2"), ("7zip.7zip", "26.02"));

        WingetService.MarkSeveralVersions(packages);

        // Naming a version winget did not ask about is one more thing to get
        // wrong, and the id is answer enough here.
        Assert.All(packages, p => Assert.False(p.IsOneOfSeveralVersions));
        Assert.All(packages, p => Assert.Null(p.IdentifyingVersion));
    }

    [Fact]
    public void Leaves_rows_alone_when_they_share_a_version_as_well_as_an_id()
    {
        var packages = Installed(("7zip.7zip", "26.02"), ("7zip.7zip", "26.02"));

        WingetService.MarkSeveralVersions(packages);

        // The version cannot separate these either. Passing it would move
        // winget's refusal rather than answer it.
        Assert.All(packages, p => Assert.False(p.IsOneOfSeveralVersions));
    }

    [Fact]
    public void Leaves_a_row_alone_when_winget_could_not_read_its_version()
    {
        var packages = Installed(("7zip.7zip", string.Empty), ("7zip.7zip", "26.02"));

        WingetService.MarkSeveralVersions(packages);

        // The one that has a version can still be asked for by name; the one
        // that has none has nothing to be asked for by.
        Assert.False(packages[0].IsOneOfSeveralVersions);
        Assert.True(packages[1].IsOneOfSeveralVersions);
    }

    // -----------------------------------------------------------------
    // What winget is told
    // -----------------------------------------------------------------

    [Fact]
    public void Names_the_version_when_the_id_names_more_than_one_install()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        WingetService.MarkSeveralVersions(packages);

        var args = WingetService.UninstallArgs(packages[1].Id, packages[1].IdentifyingVersion);

        Assert.Contains("--version", args);
        Assert.Equal("26.02", args[Array.IndexOf(args, "--version") + 1]);
    }

    [Fact]
    public void Says_nothing_about_a_version_for_a_package_installed_once()
    {
        var args = WingetService.UninstallArgs("Git.Git", null);

        Assert.DoesNotContain("--version", args);
        Assert.Contains("Git.Git", args);
    }

    // -----------------------------------------------------------------
    // What the row is called while it is being removed
    // -----------------------------------------------------------------

    [Fact]
    public void Names_the_version_that_is_going_when_the_name_does_not()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        packages[1].Name = "7-Zip";

        WingetService.MarkSeveralVersions(packages);

        // "Uninstall 7-Zip?" on a row whose neighbour is also 7-Zip asks nothing
        // the user can answer.
        Assert.Equal("7-Zip 26.02", packages[1].NameAndVersion);
    }

    [Fact]
    public void Says_the_version_once_when_the_name_already_carries_it()
    {
        var packages = Installed(("7zip.7zip", "26.02"), ("7zip.7zip", "22.01"));
        packages[0].Name = "7-Zip 26.02 (x64)";

        WingetService.MarkSeveralVersions(packages);

        // Which is where most of these come from: Add/Remove Programs puts the
        // version in the name, and "7-Zip 26.02 (x64) 26.02" reads as a stutter.
        Assert.Equal("7-Zip 26.02 (x64)", packages[0].NameAndVersion);
    }

    [Fact]
    public void Leaves_the_name_of_a_package_installed_once_alone()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git", Version = "2.47.0.2" };

        Assert.Equal("Git", package.NameAndVersion);
    }

    // -----------------------------------------------------------------
    // Two rows, two operations
    // -----------------------------------------------------------------

    [Fact]
    public void Gives_each_installed_version_a_key_of_its_own()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));

        WingetService.MarkSeveralVersions(packages);

        Assert.NotEqual(packages[0].OperationKey, packages[1].OperationKey);
        Assert.All(packages, p => Assert.Contains(p.Id, p.OperationKey));
    }

    [Fact]
    public void Keys_a_package_installed_once_by_its_id_alone()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };

        // Everything else in the app - a search hit, a catalogue entry, a row in
        // the updates list - is keyed the way it always was.
        Assert.Equal("Git.Git", package.OperationKey);
    }

    [Fact]
    public void Marks_only_the_version_being_uninstalled_busy()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        WingetService.MarkSeveralVersions(packages);

        var finish = new TaskCompletionSource<WingetResult>();
        OperationService.Start(
            packages[1].OperationKey, "7-Zip 26.02", OperationKind.Uninstall, (_, _) => finish.Task);

        OperationService.Paint(packages[0], OperationKind.Uninstall);
        OperationService.Paint(packages[1], OperationKind.Uninstall);

        Assert.False(packages[0].IsBusy);
        Assert.True(packages[1].IsBusy);
    }

    [Fact]
    public void Marks_every_installed_version_while_the_package_is_updated()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        WingetService.MarkSeveralVersions(packages);

        // An update comes off the updates list, where an id names one thing, so
        // it is keyed to the package rather than to either install.
        var finish = new TaskCompletionSource<WingetResult>();
        OperationService.Start("7zip.7zip", "7-Zip", OperationKind.Update, (_, _) => finish.Task);

        OperationService.Paint(packages[0], OperationKind.Uninstall);
        OperationService.Paint(packages[1], OperationKind.Uninstall);

        // winget is working on the package itself, so neither row may offer to
        // uninstall underneath it.
        Assert.True(packages[0].IsBusy);
        Assert.True(packages[1].IsBusy);
    }

    [Fact]
    public void Marks_every_installed_version_the_batch_has_reached()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        WingetService.MarkSeveralVersions(packages);

        var finish = new TaskCompletionSource<WingetResult>();
        OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll, (_, _) => finish.Task);
        OperationService.NoteBatchStart("7zip.7zip", "7-Zip");

        OperationService.Paint(packages[0], OperationKind.Uninstall);
        OperationService.Paint(packages[1], OperationKind.Uninstall);

        // The batch names the package it is on; a row that named a version
        // matched none of them and sat there looking untouched.
        Assert.True(packages[0].IsBusy);
        Assert.True(packages[1].IsBusy);
    }

    [Fact]
    public void Leaves_the_versions_of_another_package_alone()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        WingetService.MarkSeveralVersions(packages);

        var finish = new TaskCompletionSource<WingetResult>();
        OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll, (_, _) => finish.Task);
        OperationService.NoteBatchStart("Git.Git", "Git");

        OperationService.Paint(packages[0], OperationKind.Uninstall);
        OperationService.Paint(packages[1], OperationKind.Uninstall);

        Assert.False(packages[0].IsBusy);
        Assert.False(packages[1].IsBusy);
    }

    [Fact]
    public void Explains_a_failure_on_the_version_it_happened_to()
    {
        var packages = Installed(("7zip.7zip", "22.01"), ("7zip.7zip", "26.02"));
        WingetService.MarkSeveralVersions(packages);

        var key = packages[1].OperationKey;
        var finish = new TaskCompletionSource<WingetResult>();
        var operation = OperationService.Start(
            key, "7-Zip 26.02", OperationKind.Uninstall, (_, _) => finish.Task);

        operation!.Report("Uninstall failed with exit code: 1603");
        finish.SetResult(new WingetResult(1603, string.Empty, string.Empty));

        Assert.True(
            SpinWait.SpinUntil(() => OperationService.For(key) is null, TimeSpan.FromSeconds(5)),
            $"{key} never finished");

        OperationService.Paint(packages[0], OperationKind.Uninstall);
        OperationService.Paint(packages[1], OperationKind.Uninstall);

        // Keyed by id, both rows carried the same red sentence and neither of
        // them was the one that had been clicked.
        Assert.Equal(string.Empty, packages[0].Error);
        Assert.Equal(operation.Summary, packages[1].Error);
    }
}
