using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// A shell extension cannot have its DLLs replaced while explorer.exe holds
/// them, so a silent installer has the Restart Manager close the shell - and not
/// every installer starts it again. The taskbar and the desktop go, nothing says
/// which package did it, and App Center's window is the only thing left standing
/// that could.
///
/// Nothing here touches the machine it runs on: the watch is handed its own
/// answer to "is there a shell" and its own way to start one.
/// </summary>
public class ShellWatchTests
{
    /// <summary>A shell that can be closed and started, and no waiting about.</summary>
    private sealed class FakeShell
    {
        public bool IsUp { get; set; } = true;
        public int Starts { get; private set; }

        /// <summary>Set to close the shell again the moment the grace period is waited out.</summary>
        public bool ComesBackOnItsOwn { get; set; }

        public ShellWatch Watch() => new(
            () => IsUp,
            () =>
            {
                Starts++;
                IsUp = true;
            },
            (_, _) =>
            {
                if (ComesBackOnItsOwn)
                    IsUp = true;

                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Starts_the_shell_again_when_an_installer_took_it()
    {
        var shell = new FakeShell();
        var watch = shell.Watch();

        watch.Before();
        shell.IsUp = false;

        Assert.True(await watch.AfterAsync());
        Assert.True(shell.IsUp);
        Assert.Equal(1, shell.Starts);
    }

    [Fact]
    public async Task Says_nothing_when_the_shell_was_never_touched()
    {
        var shell = new FakeShell();
        var watch = shell.Watch();

        watch.Before();

        Assert.False(await watch.AfterAsync());
        Assert.Equal(0, shell.Starts);
    }

    [Fact]
    public async Task Waits_for_the_shell_to_come_back_on_its_own_first()
    {
        var shell = new FakeShell { ComesBackOnItsOwn = true };
        var watch = shell.Watch();

        watch.Before();
        shell.IsUp = false;

        // The Restart Manager restarts what it closed. Starting a second shell
        // on top of one already on its way back is how a File Explorer window
        // appears out of nowhere.
        Assert.False(await watch.AfterAsync());
        Assert.Equal(0, shell.Starts);
        Assert.True(shell.IsUp);
    }

    [Fact]
    public async Task Leaves_a_shell_that_was_already_gone_alone()
    {
        var shell = new FakeShell { IsUp = false };
        var watch = shell.Watch();

        watch.Before();

        // The user may have closed it themselves. An update is not the moment
        // to overrule that.
        Assert.False(await watch.AfterAsync());
        Assert.Equal(0, shell.Starts);
        Assert.False(shell.IsUp);
    }

    [Fact]
    public async Task Claims_nothing_when_the_shell_will_not_start()
    {
        // Up when Before looks, gone every time after: the only way through to
        // the attempt to start one.
        var answers = new Queue<bool>([true, false, false]);

        var watch = new ShellWatch(
            answers.Dequeue,
            () => throw new InvalidOperationException("explorer.exe would not start"),
            (_, _) => Task.CompletedTask);

        watch.Before();

        // Saying it was put back when it was not is worse than saying nothing.
        Assert.False(await watch.AfterAsync());
    }

    [Fact]
    public void Names_the_package_that_closed_it()
    {
        Assert.Equal(
            "Started Windows Explorer again after 7-Zip closed it.",
            ShellWatch.Note(["7-Zip"]));
    }

    [Fact]
    public void Names_all_of_them_when_a_batch_lost_the_shell_twice()
    {
        Assert.Equal(
            "Started Windows Explorer again after 7-Zip, TortoiseGit closed it.",
            ShellWatch.Note(["7-Zip", "TortoiseGit"]));
    }

    // -----------------------------------------------------------------
    // Inside a batch
    // -----------------------------------------------------------------

    private static readonly (string Id, string Name)[] Three =
    [
        ("7zip.7zip", "7-Zip"),
        ("Docker.DockerDesktop", "Docker Desktop"),
        ("Git.Git", "Git"),
    ];

    [Fact]
    public async Task Names_the_package_of_the_batch_that_closed_the_shell()
    {
        var shell = new FakeShell();
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            (id, _, _) =>
            {
                // 7-Zip is a shell extension; the others are not.
                if (id == "7zip.7zip")
                    shell.IsUp = false;

                return Task.FromResult(new WingetResult(0, string.Empty, string.Empty));
            },
            default,
            shell.Watch());

        // Which package did it is the part a batch would otherwise lose - and
        // the whole of what makes it possible to avoid next time.
        Assert.Equal(
            "Updated 3 packages. Started Windows Explorer again after 7-Zip closed it.",
            said[^1]);
        Assert.Equal(1, shell.Starts);
    }

    [Fact]
    public async Task Says_it_as_it_happens_as_well_as_at_the_end()
    {
        var shell = new FakeShell();
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            (id, _, _) =>
            {
                if (id == "7zip.7zip")
                    shell.IsUp = false;

                return Task.FromResult(new WingetResult(0, string.Empty, string.Empty));
            },
            default,
            shell.Watch());

        // The taskbar has just come back on a machine where it vanished. Waiting
        // until the run is over to account for that is too late to be reassuring.
        Assert.Contains("Started Windows Explorer again after 7-Zip closed it.", said);
    }

    [Fact]
    public async Task Puts_the_shell_back_before_carrying_on_to_the_next_package()
    {
        var shell = new FakeShell();
        List<bool> shellDuring = [];

        await WingetService.UpgradeEachAsync(
            Three, null, null, null,
            (id, _, _) =>
            {
                shellDuring.Add(shell.IsUp);

                if (id == "7zip.7zip")
                    shell.IsUp = false;

                return Task.FromResult(new WingetResult(0, string.Empty, string.Empty));
            },
            default,
            shell.Watch());

        // A batch of twenty would otherwise run the remaining nineteen with no
        // taskbar on screen.
        Assert.Equal([true, true, true], shellDuring);
    }

    [Fact]
    public async Task Says_nothing_about_the_shell_when_no_package_touched_it()
    {
        var shell = new FakeShell();
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            (_, _, _) => Task.FromResult(new WingetResult(0, string.Empty, string.Empty)),
            default,
            shell.Watch());

        Assert.Equal("Updated 3 packages.", said[^1]);
        Assert.Equal(0, shell.Starts);
    }

    [Fact]
    public async Task Keeps_the_shell_and_the_failures_apart()
    {
        var shell = new FakeShell();
        List<string> said = [];

        await WingetService.UpgradeEachAsync(
            Three, said.Add, null, null,
            (id, _, _) =>
            {
                if (id == "7zip.7zip")
                    shell.IsUp = false;

                return Task.FromResult(id == "Git.Git"
                    ? new WingetResult(1603, "Installer failed.", string.Empty)
                    : new WingetResult(0, string.Empty, string.Empty));
            },
            default,
            shell.Watch());

        // Closing the shell is not a failure to update: 7-Zip went in.
        Assert.Equal(
            "2 of 3 updated. 1 failed: Git. Started Windows Explorer again after 7-Zip closed it.",
            said[^1]);
    }
}
