using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// "Update all" drives the list itself, one winget process per package, rather
/// than handing the lot to `winget upgrade --all` and letting winget choose.
/// The order it works in is the order the user is looking at, which is what
/// makes it possible to say which package was in flight when something went
/// wrong on the machine; and one package failing must not end the run.
///
/// Every test here hands the batch a stand-in for "upgrade one package", so
/// what is under test is the batch, not winget.
/// </summary>
public class UpdateAllBatchTests
{
    private static readonly (string Id, string Name)[] Three =
    [
        ("7zip.7zip", "7-Zip"),
        ("Docker.DockerDesktop", "Docker Desktop"),
        ("Git.Git", "Git"),
    ];

    private static WingetResult Ok(string said = "Successfully installed") =>
        new(0, said, string.Empty);

    private static WingetResult Failed(int code, string said) =>
        new(code, said, string.Empty);

    /// <summary>An upgrade that records what it was asked to do and always works.</summary>
    private static Func<string, Action<string>?, CancellationToken, Task<WingetResult>> Recording(
        List<string> attempted,
        Func<string, WingetResult>? outcome = null) =>
        (id, _, _) =>
        {
            attempted.Add(id);
            return Task.FromResult(outcome?.Invoke(id) ?? Ok());
        };

    [Fact]
    public async Task Works_down_the_list_in_the_order_it_was_given()
    {
        List<string> attempted = [];

        await WingetService.UpgradeEachAsync(Three, null, null, null, Recording(attempted), default);

        Assert.Equal(["7zip.7zip", "Docker.DockerDesktop", "Git.Git"], attempted);
    }

    [Fact]
    public async Task Carries_on_past_a_package_that_fails()
    {
        List<string> attempted = [];

        await WingetService.UpgradeEachAsync(
            Three, null, null, null,
            Recording(attempted, id => id == "7zip.7zip" ? Failed(1603, "Installer failed.") : Ok()),
            default);

        // The one that failed is first: a batch that stopped there would leave
        // the other two untouched and say nothing about why.
        Assert.Equal(["7zip.7zip", "Docker.DockerDesktop", "Git.Git"], attempted);
    }

    [Fact]
    public async Task Announces_each_package_before_it_starts_on_it()
    {
        List<(string Id, string Name)> announced = [];
        List<string> attempted = [];

        await WingetService.UpgradeEachAsync(
            Three, null,
            (id, name) =>
            {
                // Announced first, so the row is already marked as the one being
                // worked on before winget says anything about it.
                Assert.Equal(announced.Count, attempted.Count);
                announced.Add((id, name));
            },
            null, Recording(attempted), default);

        Assert.Equal(Three, announced);
    }

    [Fact]
    public async Task Reports_a_package_that_went_through_with_no_reason_against_it()
    {
        List<(string Id, string Reason, RestartNeed Restart)> finished = [];

        await WingetService.UpgradeEachAsync(
            Three, null, null, (id, reason, restart, _) => finished.Add((id, reason, restart)),
            Recording([]), default);

        Assert.Equal(3, finished.Count);
        Assert.All(finished, f => Assert.Equal(string.Empty, f.Reason));
    }

    [Fact]
    public async Task Reports_a_package_it_could_not_update_in_wingets_own_words()
    {
        List<(string Id, string Reason, RestartNeed Restart)> finished = [];

        await WingetService.UpgradeEachAsync(
            Three, null, null, (id, reason, restart, _) => finished.Add((id, reason, restart)),
            Recording([], id => id == "Git.Git"
                ? Failed(unchecked((int)0x8A15FFFF), "Found Git [Git.Git]\nSomething new went wrong.")
                : Ok()),
            default);

        var git = Assert.Single(finished, f => f.Id == "Git.Git");

        // An unfamiliar code is quoted as winget said it: the explanation is its
        // last line - the preamble comes first - and the code is kept because
        // that is what the documentation is indexed by.
        Assert.Equal("Something new went wrong. (0x8A15FFFF)", git.Reason);
    }

    [Fact]
    public async Task Writes_wingets_own_failure_codes_as_hex()
    {
        List<(string Id, string Reason, RestartNeed Restart)> finished = [];

        await WingetService.UpgradeEachAsync(
            [("Git.Git", "Git")], null, null, (id, reason, restart, _) => finished.Add((id, reason, restart)),
            Recording([], _ => Failed(unchecked((int)0x8A15002B), "No applicable upgrade found.")),
            default);

        // A known code is explained rather than quoted, and kept on the end -
        // the 0x8A15xxxx family is winget's own and reads as hex everywhere it
        // is documented.
        Assert.StartsWith("winget listed a newer version but has no installer for it that fits this copy", finished[0].Reason);
        Assert.EndsWith("(0x8A15002B)", finished[0].Reason);
    }

    [Fact]
    public async Task Says_so_when_a_package_failed_without_saying_anything()
    {
        List<(string Id, string Reason, RestartNeed Restart)> finished = [];

        await WingetService.UpgradeEachAsync(
            [("Git.Git", "Git")], null, null, (id, reason, restart, _) => finished.Add((id, reason, restart)),
            Recording([], _ => new WingetResult(unchecked((int)0x8A15FFFF), string.Empty, string.Empty)),
            default);

        Assert.Equal("winget exited with 0x8A15FFFF.", finished[0].Reason);
    }

    [Fact]
    public async Task Says_what_kind_of_failure_each_one_was()
    {
        List<(string Id, FailureKind Kind)> finished = [];

        await WingetService.UpgradeEachAsync(
            Three, null, null, (id, _, _, kind) => finished.Add((id, kind)),
            Recording([], id => id switch
            {
                "7zip.7zip" => Failed(unchecked((int)0x80073D28),
                    "Installer failed with exit code: 0x80073d28 : The package installation failed because administrator privileges are required."),
                "Git.Git" => Failed(unchecked((int)0x8A15008E),
                    "A newer version was found, but the install technology is different from the current version installed."),
                _ => Ok(),
            }),
            default);

        // The one a retry with the rights could put right, the one a reinstall
        // gets round, and the one that went through - each row offers what fits.
        Assert.Equal(
            [("7zip.7zip", FailureKind.WantsAdmin), ("Docker.DockerDesktop", FailureKind.Other), ("Git.Git", FailureKind.NeedsReinstall)],
            finished);
    }

    [Fact]
    public async Task Groups_the_closing_tally_by_what_to_do_about_each_failure()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            [
                ("7zip.7zip", "7-Zip"),
                ("Docker.DockerDesktop", "Docker Desktop"),
                ("Git.Git", "Git"),
                ("Oracle.VirtualBox", "VirtualBox"),
                ("Microsoft.WSL", "WSL"),
            ],
            said.Add, null, null,
            Recording([], id => id switch
            {
                "Git.Git" or "Oracle.VirtualBox" => Failed(unchecked((int)0x8A15008E), "A newer version was found, but the install technology is different."),
                "Microsoft.WSL" => Failed(unchecked((int)0x8A150019), "The command requires administrator privileges."),
                "7zip.7zip" => Failed(1603, "Installer failed."),
                _ => Ok(),
            }),
            default);

        // A tally that only counted failures put a package waiting on a
        // reinstall next to one whose installer crashed, and left the reader to
        // open every row to find out which was which.
        Assert.Equal(
            "1 of 5 updated. 2 need a reinstall: Git, VirtualBox. 1 needs administrator rights: WSL. 1 failed: 7-Zip.",
            said[^1]);
    }

    [Fact]
    public async Task Ends_by_counting_what_it_updated()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(Three, said.Add, null, null, Recording([]), default);

        Assert.Equal("Updated 3 packages.", said[^1]);
    }

    [Fact]
    public async Task Counts_one_package_as_one_package()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            [("Git.Git", "Git")], said.Add, null, null, Recording([]), default);

        Assert.Equal("Updated 1 package.", said[^1]);
    }

    [Fact]
    public async Task Ends_by_naming_the_packages_it_could_not_update()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            Recording([], id => id == "7zip.7zip" || id == "Git.Git"
                ? Failed(1603, "Installer failed.")
                : Ok()),
            default);

        // Names, not ids: the tally is the one line a person reads.
        Assert.Equal("1 of 3 updated. 2 failed: 7-Zip, Git.", said[^1]);
    }

    [Fact]
    public async Task Comes_back_with_the_first_failures_exit_code()
    {
        var result = await WingetService.UpgradeEachAsync(
            Three, null, null, null,
            Recording([], id => id switch
            {
                "Docker.DockerDesktop" => Failed(1603, "Installer failed."),
                "Git.Git" => Failed(5, "Access denied."),
                _ => Ok(),
            }),
            default);

        Assert.False(result.Success);
        Assert.Equal(1603, result.ExitCode);
    }

    [Fact]
    public async Task Comes_back_successful_when_every_package_went_through()
    {
        var result = await WingetService.UpgradeEachAsync(
            Three, null, null, null, Recording([]), default);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Cancelling_stops_the_batch_where_it_stands()
    {
        using var cts = new CancellationTokenSource();
        List<string> attempted = [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WingetService.UpgradeEachAsync(
                Three, null, null, null,
                (id, _, _) =>
                {
                    attempted.Add(id);
                    cts.Cancel();
                    return Task.FromResult(Ok());
                },
                cts.Token));

        // Cancellation means the app is going away, which is the one thing that
        // does stop the run - unlike a package that merely misbehaved.
        Assert.Equal(["7zip.7zip"], attempted);
    }

    [Fact]
    public async Task Hands_wingets_output_for_each_package_straight_through()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            [("Git.Git", "Git")], said.Add, null, null,
            (_, output, _) =>
            {
                output?.Invoke("Downloading https://example.invalid/git.exe");
                return Task.FromResult(Ok());
            },
            default);

        // Each package's own lines are what move its row's bar along.
        Assert.Contains("Downloading https://example.invalid/git.exe", said);
    }
}
