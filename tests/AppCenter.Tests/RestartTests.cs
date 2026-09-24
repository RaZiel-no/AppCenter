using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// Some updates are not finished when winget stops: a file was locked, the
/// installer put the new one aside for boot to pick up, and until Windows
/// restarts the machine is running the old one.
///
/// Nothing says so in advance - winget publishes no such thing, and no manifest
/// field carries it - so the exit code is the whole of what there is to go on.
/// These pin down reading it: which codes mean it, that a package wanting a
/// restart is not a package that failed, and that the difference reaches the row
/// rather than being averaged into a tally.
/// </summary>
public class RestartTests : IDisposable
{
    public RestartTests() => OperationService.Reset();

    public void Dispose() => OperationService.Reset();

    private static WingetResult Exited(int code) => new(code, string.Empty, string.Empty);

    // -----------------------------------------------------------------
    // Reading the code
    // -----------------------------------------------------------------

    [Theory]
    // winget's own, from its documented return codes.
    [InlineData(unchecked((int)0x8A150109), RestartNeed.ToFinish)]   // REBOOT_REQUIRED_TO_FINISH
    [InlineData(unchecked((int)0x8A15010A), RestartNeed.ToRetry)]    // REBOOT_REQUIRED_FOR_INSTALL
    [InlineData(unchecked((int)0x8A15010B), RestartNeed.Underway)]   // REBOOT_INITIATED
    // Windows Installer's own, for the manifests that let them through.
    [InlineData(3010, RestartNeed.ToFinish)]                         // ERROR_SUCCESS_REBOOT_REQUIRED
    [InlineData(1641, RestartNeed.Underway)]                         // ERROR_SUCCESS_REBOOT_INITIATED
    // Everything else says nothing about restarting.
    [InlineData(0, RestartNeed.None)]
    [InlineData(1603, RestartNeed.None)]
    [InlineData(unchecked((int)0x8A15002B), RestartNeed.None)]
    public void Reads_what_an_exit_code_says_about_restarting(int code, RestartNeed expected)
    {
        Assert.Equal(expected, Exited(code).Restart);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3010)]
    [InlineData(1641)]
    [InlineData(unchecked((int)0x8A150109))]
    [InlineData(unchecked((int)0x8A15010B))]
    public void Counts_a_package_that_only_wants_a_restart_as_installed(int code)
    {
        // The files are in place. Calling it failed puts a red line under a
        // package that installed correctly.
        Assert.True(Exited(code).Installed);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(unchecked((int)0x8A15010A))]
    public void Counts_one_that_needs_a_restart_before_it_will_install_as_failed(int code)
    {
        // "Installation failed. Restart your PC then try again" is a failure that
        // happens to have a remedy, not an install waiting to be finished.
        Assert.False(Exited(code).Installed);
    }

    // -----------------------------------------------------------------
    // One update
    // -----------------------------------------------------------------

    [Fact]
    public void Does_not_call_an_update_that_wants_a_restart_a_failure()
    {
        var operation = new Operation
        {
            Key = "Docker.DockerDesktop",
            PackageName = "Docker Desktop",
            Kind = OperationKind.Update,
        };

        operation.Report("Restart your PC to finish installation.");
        operation.Complete(Exited(unchecked((int)0x8A150109)), null);

        Assert.False(operation.Failed);
        Assert.Equal("Restart your PC to finish installation.", operation.Summary);
        Assert.True(operation.NeedsRestart("Docker.DockerDesktop"));
    }

    [Fact]
    public void Still_calls_one_that_could_not_install_a_failure()
    {
        var operation = new Operation
        {
            Key = "Docker.DockerDesktop",
            PackageName = "Docker Desktop",
            Kind = OperationKind.Update,
        };

        operation.Complete(Exited(unchecked((int)0x8A15010A)), null);

        Assert.True(operation.Failed);
        Assert.False(operation.NeedsRestart("Docker.DockerDesktop"));
    }

    [Fact]
    public void Says_nothing_about_restarting_for_an_ordinary_update()
    {
        var operation = new Operation
        {
            Key = "Git.Git",
            PackageName = "Git",
            Kind = OperationKind.Update,
        };

        operation.Complete(Exited(0), null);

        Assert.False(operation.NeedsRestart("Git.Git"));
        Assert.False(operation.AnyNeedRestart);
    }

    // -----------------------------------------------------------------
    // A batch
    // -----------------------------------------------------------------

    private static readonly (string Id, string Name)[] Three =
    [
        ("7zip.7zip", "7-Zip"),
        ("Docker.DockerDesktop", "Docker Desktop"),
        ("Git.Git", "Git"),
    ];

    private static Func<string, Action<string>?, CancellationToken, Task<WingetResult>> Exits(
        Func<string, int> code) =>
        (id, _, _) => Task.FromResult(Exited(code(id)));

    [Fact]
    public async Task Counts_a_package_that_wants_a_restart_among_the_updated()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            Exits(id => id == "Docker.DockerDesktop" ? 3010 : 0),
            default);

        Assert.StartsWith("Updated 3 packages.", said[^1]);
    }

    [Fact]
    public async Task Names_the_packages_the_batch_left_waiting_on_a_restart()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            Exits(id => id == "Docker.DockerDesktop" ? 3010 : 0),
            default);

        // Named rather than counted: which packages are waiting is what makes a
        // restart worth doing now rather than at some point.
        Assert.Equal(
            "Updated 3 packages. Restart Windows to finish: Docker Desktop.",
            said[^1]);
    }

    [Fact]
    public async Task Says_nothing_about_restarting_when_nothing_asked_for_one()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null, Exits(_ => 0), default);

        Assert.DoesNotContain("Restart", said[^1]);
    }

    [Fact]
    public async Task Keeps_the_failures_and_the_restarts_apart_in_the_tally()
    {
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            Exits(id => id switch
            {
                "Docker.DockerDesktop" => 3010,
                "Git.Git" => 1603,
                _ => 0,
            }),
            default);

        Assert.Equal(
            "2 of 3 updated. 1 failed: Git. Restart Windows to finish: Docker Desktop.",
            said[^1]);
    }

    [Fact]
    public async Task Comes_back_successful_when_the_only_thing_owed_is_a_restart()
    {
        var result = await WingetService.UpgradeEachAsync(
            Three, null, null, null,
            Exits(id => id == "Docker.DockerDesktop" ? 3010 : 0),
            default);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Tells_the_batch_which_package_is_waiting_on_a_restart()
    {
        List<(string Id, string Reason, RestartNeed Restart)> finished = [];

        await WingetService.UpgradeEachAsync(
            Three, null, null, (id, reason, restart, _) => finished.Add((id, reason, restart)),
            Exits(id => id == "Docker.DockerDesktop" ? 3010 : 0),
            default);

        var docker = Assert.Single(finished, f => f.Id == "Docker.DockerDesktop");

        Assert.Equal(RestartNeed.ToFinish, docker.Restart);
        Assert.Equal(string.Empty, docker.Reason);
    }

    // -----------------------------------------------------------------
    // What the list is left saying
    // -----------------------------------------------------------------

    [Fact]
    public void Remembers_which_package_of_a_batch_is_waiting()
    {
        var finish = new TaskCompletionSource<WingetResult>();
        var batch = OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll, (_, _) => finish.Task);

        OperationService.NoteBatchStart("Docker.DockerDesktop", "Docker Desktop");
        OperationService.NoteBatchDone("Docker.DockerDesktop", string.Empty, RestartNeed.ToFinish);
        OperationService.NoteBatchStart("Git.Git", "Git");
        OperationService.NoteBatchDone("Git.Git", string.Empty, RestartNeed.None);

        // Which is what lets the row say Windows rather than the app, once the
        // reload has rebuilt it.
        Assert.True(batch!.NeedsRestart("Docker.DockerDesktop"));
        Assert.False(batch.NeedsRestart("Git.Git"));
        Assert.True(batch.WasUpdated("Docker.DockerDesktop"));
    }

    [Fact]
    public void Does_not_wait_on_a_restart_for_a_package_it_could_not_install()
    {
        var finish = new TaskCompletionSource<WingetResult>();
        var batch = OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll, (_, _) => finish.Task);

        OperationService.NoteBatchStart("Docker.DockerDesktop", "Docker Desktop");
        OperationService.NoteBatchDone(
            "Docker.DockerDesktop",
            "Installation failed. Restart your PC then try again. (0x8A15010A)",
            RestartNeed.ToRetry);

        // The row carries winget's own sentence, which already says to restart
        // and try again. Listing it as finished-but-for-a-restart would say the
        // opposite of what happened.
        Assert.False(batch!.NeedsRestart("Docker.DockerDesktop"));
        Assert.False(batch.WasUpdated("Docker.DockerDesktop"));
        Assert.NotEqual(string.Empty, batch.FailureFor("Docker.DockerDesktop"));
    }

    [Fact]
    public void Keeps_what_is_owed_after_the_batch_is_over()
    {
        var package = new AppPackage { Id = "Docker.DockerDesktop", Name = "Docker Desktop" };
        var finish = new TaskCompletionSource<WingetResult>();

        var batch = OperationService.Start(
            Operation.UpdateAllKey, "all packages", OperationKind.UpdateAll, (_, _) => finish.Task);

        OperationService.NoteBatchStart(package.Id, package.Name);
        OperationService.NoteBatchDone(package.Id, string.Empty, RestartNeed.ToFinish);
        finish.SetResult(new WingetResult(0, string.Empty, string.Empty));

        Assert.True(
            SpinWait.SpinUntil(
                () => OperationService.For(Operation.UpdateAllKey) is null,
                TimeSpan.FromSeconds(5)),
            "the batch never finished");

        // The reload that follows rebuilds every row, and the row is where this
        // has to be said - so it has to outlive the run, exactly as the failures
        // already do.
        Assert.True(batch!.NeedsRestart(package.OperationKey));
    }
}
