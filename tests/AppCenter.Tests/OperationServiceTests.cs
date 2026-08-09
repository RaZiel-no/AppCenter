using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The service owns every install, update and uninstall for as long as it runs,
/// because the work used to belong to whichever page started it and navigating
/// away killed winget mid-install. That makes it the only thing that remembers
/// an operation - so a row's busy state, its bar and the reason it was skipped
/// are all re-derived from here rather than carried on the row.
/// </summary>
public class OperationServiceTests : IDisposable
{
    public OperationServiceTests() => OperationService.Reset();

    public void Dispose() => OperationService.Reset();

    /// <summary>An operation that runs until the test lets it finish.</summary>
    private static (Operation? Operation, TaskCompletionSource<WingetResult> Finish) Start(
        string key,
        OperationKind kind = OperationKind.Update,
        string name = "Git")
    {
        var finish = new TaskCompletionSource<WingetResult>();

        return (OperationService.Start(key, name, kind, (_, _) => finish.Task), finish);
    }

    /// <summary>
    /// Lets it finish and waits for the service to have noticed. The service
    /// hops every mutation onto the UI thread, and in a test there is no
    /// dispatcher to hop to - but the continuation still lands wherever the
    /// runner's synchronisation context puts it, so waiting on the state is the
    /// only honest way to know it has happened.
    /// </summary>
    private static void Finish(
        TaskCompletionSource<WingetResult> finish,
        string key,
        WingetResult? result = null)
    {
        finish.SetResult(result ?? new WingetResult(0, string.Empty, string.Empty));

        Assert.True(
            SpinWait.SpinUntil(() => OperationService.For(key) is null, TimeSpan.FromSeconds(5)),
            $"{key} never finished");
    }

    // -----------------------------------------------------------------
    // What may run at once
    // -----------------------------------------------------------------

    [Fact]
    public void Lets_anything_start_when_nothing_is_running()
    {
        Assert.True(OperationService.CanStart("Git.Git"));
        Assert.True(OperationService.CanStart(Operation.UpdateAllKey));
    }

    [Fact]
    public void Refuses_a_second_command_against_the_same_package()
    {
        Start("Git.Git");

        Assert.False(OperationService.CanStart("Git.Git"));
    }

    [Fact]
    public void Lets_a_different_package_start_meanwhile()
    {
        Start("Git.Git");

        // Starting an install and going off to install something else is normal
        // use, not an error.
        Assert.True(OperationService.CanStart("7zip.7zip"));
    }

    [Fact]
    public void Refuses_everything_while_update_all_runs()
    {
        Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");

        // The batch is already touching every package; nothing else may be
        // moving underneath it.
        Assert.False(OperationService.CanStart("Git.Git"));
        Assert.False(OperationService.CanStart(Operation.UpdateAllKey));
    }

    [Fact]
    public void Refuses_update_all_while_anything_else_runs()
    {
        Start("Git.Git");

        Assert.False(OperationService.CanStart(Operation.UpdateAllKey));
    }

    [Fact]
    public void Hands_back_nothing_when_it_refuses_to_start()
    {
        Start("Git.Git");

        var (second, _) = Start("Git.Git");

        Assert.Null(second);
    }

    [Fact]
    public void Frees_the_package_again_once_it_finishes()
    {
        var (_, finish) = Start("Git.Git");

        Finish(finish, "Git.Git");

        Assert.True(OperationService.CanStart("Git.Git"));
        Assert.Null(OperationService.For("Git.Git"));
    }

    // -----------------------------------------------------------------
    // Painting a row
    // -----------------------------------------------------------------

    [Fact]
    public void Leaves_a_row_alone_when_nothing_is_happening_to_it()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };

        OperationService.Paint(package);

        Assert.False(package.IsBusy);
        Assert.Equal(string.Empty, package.Status);
        Assert.Equal(0, package.Progress);
        Assert.Equal(string.Empty, package.Error);
    }

    [Fact]
    public void Marks_the_row_of_a_package_it_is_working_on()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };
        var (operation, _) = Start("Git.Git");
        operation!.Report("Downloading https://example.invalid/git.exe");

        OperationService.Paint(package);

        Assert.True(package.IsBusy);
        Assert.Equal("Downloading…", package.Status);
        Assert.Equal(operation.Percent, package.Progress);
    }

    [Fact]
    public void Marks_the_row_the_batch_has_reached()
    {
        var sevenZip = new AppPackage { Id = "7zip.7zip", Name = "7-Zip" };
        var git = new AppPackage { Id = "Git.Git", Name = "Git" };

        Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");
        OperationService.NoteBatchStart("7zip.7zip", "7-Zip");

        OperationService.Paint(sevenZip);
        OperationService.Paint(git);

        // A batch runs under its own key, so without this the row it has reached
        // would find nothing and sit there looking untouched.
        Assert.True(sevenZip.IsBusy);
        Assert.False(git.IsBusy);
    }

    [Fact]
    public void Moves_off_a_row_when_the_batch_moves_on()
    {
        var package = new AppPackage { Id = "7zip.7zip", Name = "7-Zip" };

        Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");
        OperationService.NoteBatchStart("7zip.7zip", "7-Zip");
        OperationService.NoteBatchDone("7zip.7zip", string.Empty, RestartNeed.None);

        OperationService.Paint(package);

        Assert.False(package.IsBusy);
    }

    // -----------------------------------------------------------------
    // Explaining a failure on the row it happened to
    // -----------------------------------------------------------------

    [Fact]
    public void Puts_a_batchs_reason_on_the_row_it_belongs_to()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };

        Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");
        OperationService.NoteBatchStart("Git.Git", "Git");
        OperationService.NoteBatchDone("Git.Git", "Installer failed. (1603)", RestartNeed.None);

        OperationService.Paint(package, OperationKind.Update);

        // A name in a tally is not a reason; the row it happened to is where
        // anyone reading a list will look.
        Assert.Equal("Installer failed. (1603)", package.Error);
    }

    [Fact]
    public void Keeps_a_batchs_reasons_after_the_batch_is_over()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };

        var (_, finish) = Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");
        OperationService.NoteBatchStart("Git.Git", "Git");
        OperationService.NoteBatchDone("Git.Git", "Installer failed. (1603)", RestartNeed.None);
        Finish(finish, Operation.UpdateAllKey);

        OperationService.Paint(package, OperationKind.Update);

        // The rows are rebuilt by the reload that follows the batch, so a reason
        // kept on the row would not survive being explained.
        Assert.Equal("Installer failed. (1603)", package.Error);
    }

    [Fact]
    public void Keeps_an_update_failure_off_a_row_that_offers_an_uninstall()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };

        Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");
        OperationService.NoteBatchStart("Git.Git", "Git");
        OperationService.NoteBatchDone("Git.Git", "Installer failed. (1603)", RestartNeed.None);

        OperationService.Paint(package, OperationKind.Uninstall);

        // The same package is both upgradable and installed. On the installed
        // list the button says Uninstall, and an update's failure makes no sense
        // underneath it.
        Assert.Equal(string.Empty, package.Error);
    }

    [Fact]
    public void Puts_a_single_failure_on_its_own_row()
    {
        var package = new AppPackage { Id = "Git.Git", Name = "Git" };
        var (operation, finish) = Start("Git.Git");

        operation!.Report("No applicable upgrade found.");
        Finish(finish, "Git.Git", new WingetResult(1603, string.Empty, string.Empty));

        OperationService.Paint(package, OperationKind.Update);

        Assert.Equal(operation.Summary, package.Error);
        Assert.NotEqual(string.Empty, package.Error);
    }

    [Fact]
    public void Retires_the_last_outcome_when_something_new_starts()
    {
        var (_, first) = Start("Git.Git");
        Finish(first, "Git.Git");
        Assert.NotNull(OperationService.LastOutcome);

        Start("7zip.7zip");

        // Otherwise a stale reason would still be sitting under a row long after
        // it stopped being true.
        Assert.Null(OperationService.LastOutcome);
    }

    // -----------------------------------------------------------------
    // Telling the pages
    // -----------------------------------------------------------------

    [Fact]
    public void Tells_the_pages_when_something_starts_moves_and_ends()
    {
        List<string> heard = [];
        var finish = new TaskCompletionSource<WingetResult>();

        OperationService.Started += (_, _) => heard.Add("started");
        OperationService.Progressed += (_, _) => heard.Add("progressed");
        OperationService.Finished += (_, _) => heard.Add("finished");

        OperationService.Start(
            "Git.Git", "Git", OperationKind.Update,
            (progress, _) =>
            {
                progress("Downloading https://example.invalid/git.exe");
                return finish.Task;
            });

        Finish(finish, "Git.Git");

        // A page is built, thrown away and built again while winget works, and
        // asks what is in flight every time - these are what it asks on.
        Assert.Equal(["started", "progressed", "finished"], heard);
    }

    [Fact]
    public void Tells_the_pages_each_time_the_batch_moves()
    {
        var moves = 0;

        Start(Operation.UpdateAllKey, OperationKind.UpdateAll, "all packages");
        OperationService.Progressed += (_, _) => moves++;

        OperationService.NoteBatchStart("Git.Git", "Git");
        OperationService.NoteBatchDone("Git.Git", string.Empty, RestartNeed.None);

        // Which is what takes a finished row off the list as the batch passes
        // it, rather than only once the whole run is over.
        Assert.Equal(2, moves);
    }

    [Fact]
    public void Ignores_batch_notes_when_no_batch_is_running()
    {
        OperationService.NoteBatchStart("Git.Git", "Git");
        OperationService.NoteBatchDone("Git.Git", "Installer failed. (1603)", RestartNeed.None);

        var package = new AppPackage { Id = "Git.Git", Name = "Git" };
        OperationService.Paint(package);

        Assert.False(package.IsBusy);
        Assert.Equal(string.Empty, package.Error);
    }
}
