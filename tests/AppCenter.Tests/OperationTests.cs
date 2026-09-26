using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// Redirect winget's output and the progress bar it draws for a terminal
/// disappears: an install yields six plain lines and nothing else. So the bar
/// advances on the milestones winget does print, and pulses over the stretches
/// where the next one is genuinely unknowable. These hold that reading of
/// winget's output to account.
/// </summary>
public class OperationTests
{
    private static Operation Update(string name = "Git") => new()
    {
        Key = "Git.Git",
        PackageName = name,
        Kind = OperationKind.Update,
    };

    private static Operation Uninstall() => new()
    {
        Key = "QtProject.QtCreator",
        PackageName = "Qt Creator",
        Kind = OperationKind.Uninstall,
    };

    private static Operation Batch() => new()
    {
        Key = Operation.UpdateAllKey,
        PackageName = "all packages",
        Kind = OperationKind.UpdateAll,
    };

    // -----------------------------------------------------------------
    // Phases
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("Found Git [Git.Git] Version 2.55.0.3", OperationPhase.Located)]
    [InlineData("Downloading https://example.invalid/git.exe", OperationPhase.Downloading)]
    [InlineData("Successfully verified installer hash", OperationPhase.Verified)]
    [InlineData("Starting package install...", OperationPhase.Installing)]
    [InlineData("Successfully installed", OperationPhase.Done)]
    [InlineData("Successfully upgraded", OperationPhase.Done)]
    [InlineData("Successfully uninstalled", OperationPhase.Done)]
    public void Recognises_wingets_milestones(string line, OperationPhase expected)
    {
        var operation = Update();

        operation.Report(line);

        Assert.Equal(expected, operation.Phase);
    }

    [Fact]
    public void Stays_at_the_start_for_output_it_cannot_read()
    {
        var operation = Update();

        // A localised winget matches no milestone at all. Claiming no progress
        // is right; claiming the wrong progress is not.
        operation.Report("Téléchargement en cours");

        Assert.Equal(OperationPhase.Starting, operation.Phase);
        Assert.True(operation.IsPulsing);
    }

    [Fact]
    public void Never_moves_a_phase_backwards()
    {
        var operation = Update();

        operation.Report("Starting package install...");
        operation.Report("Downloading https://example.invalid/git.exe");

        // A bar that slides backwards reads as a bug.
        Assert.Equal(OperationPhase.Installing, operation.Phase);
    }

    [Fact]
    public void Fills_the_bar_further_at_every_milestone()
    {
        var operation = Update();
        var seen = new List<double> { operation.Percent };

        foreach (var line in new[]
                 {
                     "Found Git [Git.Git]",
                     "Downloading https://example.invalid/git.exe",
                     "Successfully verified installer hash",
                     "Starting package install...",
                     "Successfully installed",
                 })
        {
            operation.Report(line);
            seen.Add(operation.Percent);
        }

        Assert.Equal(seen.OrderBy(p => p), seen);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public void Pulses_only_where_the_next_milestone_is_unknowable()
    {
        var operation = Update();

        // Nothing said yet.
        Assert.True(operation.IsPulsing);

        // Found, and possibly queued behind another install for who knows how long.
        operation.Report("Found Git [Git.Git] Version 2.55.0.3");
        Assert.True(operation.IsPulsing);

        operation.Report("Downloading https://example.invalid/git.exe");
        Assert.False(operation.IsPulsing);

        // The installer prints nothing at all between here and its result.
        operation.Report("Starting package install...");
        Assert.True(operation.IsPulsing);
    }

    [Fact]
    public void Stops_pulsing_once_it_has_finished()
    {
        var operation = Update();

        operation.Report("Starting package install...");
        operation.Complete(new WingetResult(0, string.Empty, string.Empty), null);

        Assert.False(operation.IsPulsing);
    }

    // -----------------------------------------------------------------
    // What a row says
    // -----------------------------------------------------------------

    [Fact]
    public void Says_downloading_on_the_row_while_it_downloads()
    {
        var operation = Update();

        operation.Report("Downloading https://example.invalid/git.exe");

        // The download is the one phase long enough that a row saying only
        // "Updating…" through it looks stuck.
        Assert.Equal("Downloading…", operation.RowLabel);
    }

    [Theory]
    [InlineData(OperationKind.Install, "Installing…")]
    [InlineData(OperationKind.Update, "Updating…")]
    [InlineData(OperationKind.Uninstall, "Uninstalling…")]
    [InlineData(OperationKind.UpdateAll, "Updating…")]
    public void Otherwise_says_what_it_is_doing(OperationKind kind, string expected)
    {
        var operation = new Operation { Key = "Git.Git", PackageName = "Git", Kind = kind };

        Assert.Equal(expected, operation.RowLabel);
    }

    // -----------------------------------------------------------------
    // How it ends
    // -----------------------------------------------------------------

    [Fact]
    public void Keeps_wingets_last_words_as_its_summary()
    {
        var operation = Update();

        operation.Report("Successfully installed. Restart the application to complete the upgrade.");
        operation.Complete(new WingetResult(0, string.Empty, string.Empty), null);

        // The useful part lives in winget's own closing line, not in the exit
        // code - it is the whole explanation for a package still listed as
        // upgradable afterwards.
        Assert.Equal(
            "Successfully installed. Restart the application to complete the upgrade.",
            operation.Summary);
        Assert.False(operation.Failed);
    }

    [Fact]
    public void Says_done_when_a_success_said_nothing_at_all()
    {
        var operation = Update();

        operation.Complete(new WingetResult(0, string.Empty, string.Empty), null);

        Assert.Equal("Done.", operation.Summary);
    }

    [Fact]
    public void Quotes_the_exit_code_and_winget_when_the_code_is_unfamiliar()
    {
        var operation = Update();

        operation.Report("Something new went wrong.");
        operation.Complete(new WingetResult(unchecked((int)0x8A15FFFF), string.Empty, string.Empty), null);

        Assert.True(operation.Failed);
        Assert.Equal("winget exited with 0x8A15FFFF. Something new went wrong.", operation.Summary);
    }

    [Fact]
    public void Explains_a_known_code_in_plain_words_and_keeps_the_code()
    {
        var operation = Update();

        operation.Report("A newer version was found, but the install technology is different from the current version installed. Please uninstall the package and install the newer version.");
        operation.Complete(new WingetResult(unchecked((int)0x8A15008E), string.Empty, string.Empty), null);

        Assert.True(operation.Failed);
        Assert.StartsWith("The new version comes as a different kind of installer", operation.Summary);
        Assert.Contains("Reinstall to update", operation.Summary);
        Assert.EndsWith("(0x8A15008E)", operation.Summary);
    }

    // -----------------------------------------------------------------
    // A reinstall: two runs end to end
    // -----------------------------------------------------------------

    private static Operation Reinstall() => new()
    {
        Key = "Git.Git",
        PackageName = "Git",
        Kind = OperationKind.Reinstall,
    };

    [Fact]
    public void A_reinstall_starts_the_bar_again_for_its_second_half()
    {
        var operation = Reinstall();

        operation.Report("Found Git [Git.Git]");
        operation.Report("Starting package uninstall...");
        operation.Report("Successfully uninstalled");
        var afterUninstall = operation.Percent;

        // The seam: the phase goes back to the start for the install, and the
        // bar carries on from where the uninstall left it rather than emptying.
        operation.Report(Operation.ReinstallMarker);

        Assert.Equal(OperationPhase.Starting, operation.Phase);
        Assert.True(operation.Percent >= afterUninstall);
        Assert.True(operation.IsPulsing);
        Assert.Equal("Reinstalling Git: installing the new version…", operation.Heading);
        Assert.Equal("Installing…", operation.RowLabel);
    }

    [Fact]
    public void A_reinstall_fills_the_bar_across_both_halves_without_going_back_within_one()
    {
        var operation = Reinstall();
        var seen = new List<double> { operation.Percent };

        foreach (var line in new[]
                 {
                     "Found Git [Git.Git]",
                     "Starting package uninstall...",
                     "Successfully uninstalled",
                 })
        {
            operation.Report(line);
            seen.Add(operation.Percent);
        }

        // The uninstall has no download and fills the first third.
        Assert.Equal(seen.OrderBy(p => p), seen);
        Assert.True(seen[^1] < 0.5);

        operation.Report(Operation.ReinstallMarker);
        seen = [operation.Percent];

        foreach (var line in new[]
                 {
                     "Found Git [Git.Git]",
                     "Downloading https://example.invalid/git.exe",
                     "Successfully verified installer hash",
                     "Starting package install...",
                     "Successfully installed",
                 })
        {
            operation.Report(line);
            seen.Add(operation.Percent);
        }

        Assert.Equal(seen.OrderBy(p => p), seen);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public void A_reinstall_says_uninstalling_through_its_first_half()
    {
        var operation = Reinstall();

        Assert.Equal("Reinstalling Git: uninstalling the old version…", operation.Heading);
        Assert.Equal("Uninstalling…", operation.RowLabel);
    }

    [Fact]
    public void A_reinstall_that_fails_after_the_old_copy_came_off_says_so()
    {
        var operation = Reinstall();

        operation.Report("Successfully uninstalled");
        operation.Report(Operation.ReinstallMarker);
        operation.Report("Installer failed with exit code: 1603");
        operation.Complete(new WingetResult(unchecked((int)0x8A150006), string.Empty, string.Empty), null);

        // The one fact the closing words cannot leave out: the package is gone.
        Assert.True(operation.Failed);
        Assert.StartsWith("The old version was removed, but the new one did not install.", operation.Summary);
        Assert.Contains("(installer returned 1603)", operation.Summary);
        Assert.EndsWith("Install it again from its page.", operation.Summary);
    }

    [Fact]
    public void A_reinstall_that_fails_to_uninstall_is_read_as_an_uninstall()
    {
        var operation = Reinstall();

        operation.Report("0x800401f5 : Application not found");
        operation.Complete(new WingetResult(unchecked((int)0x800401F5), string.Empty, string.Empty), null);

        // Nothing has come off and nothing was tried after it.
        Assert.StartsWith("Windows could not find the uninstaller", operation.Summary);
        Assert.DoesNotContain("The old version was removed", operation.Summary);
    }

    [Fact]
    public void An_update_refused_for_its_installer_kind_remembers_that_a_reinstall_would_do()
    {
        var operation = Update();

        operation.Complete(new WingetResult(unchecked((int)0x8A15008E), string.Empty, string.Empty), null);

        Assert.True(operation.NeedsReinstall("Git.Git"));
    }

    [Fact]
    public void A_batch_remembers_which_packages_want_a_reinstall()
    {
        var batch = Batch();

        batch.BeginItem("Git.Git", "Git");
        batch.EndItem("Git.Git", "The new version comes as a different kind of installer. (0x8A15008E)", needsReinstall: true);
        batch.BeginItem("7zip.7zip", "7-Zip");
        batch.EndItem("7zip.7zip", "Installer failed. (1603)");

        Assert.True(batch.NeedsReinstall("Git.Git"));
        Assert.False(batch.NeedsReinstall("7zip.7zip"));
    }

    [Fact]
    public void Explains_a_missing_uninstaller_as_the_uninstaller()
    {
        var operation = Uninstall();

        operation.Report("0x800401f5 : Application not found");
        operation.Complete(new WingetResult(unchecked((int)0x800401F5), string.Empty, string.Empty), null);

        Assert.StartsWith("Windows could not find the uninstaller", operation.Summary);
        Assert.EndsWith("(0x800401F5)", operation.Summary);
    }

    [Fact]
    public void Explains_the_installers_own_code_when_winget_wraps_it()
    {
        var operation = Update();

        // winget's generic "the installer failed" code, with the installer's
        // real one in the line - printed unsigned, so an HRESULT is ten digits.
        operation.Report("Installer failed with exit code: 2147942512");
        operation.Complete(new WingetResult(unchecked((int)0x8A150006), string.Empty, string.Empty), null);

        Assert.Equal("The disk is full. Free some space and try again. (installer returned 0x80070070)", operation.Summary);
    }

    [Fact]
    public void Writes_an_installers_own_code_in_decimal()
    {
        var operation = Update();

        operation.Complete(new WingetResult(1603, string.Empty, "Fatal error during installation."), null);

        Assert.StartsWith("The installer hit a fatal error.", operation.Summary);
        Assert.EndsWith("(1603)", operation.Summary);
    }

    [Fact]
    public void Lets_a_batch_close_with_its_own_tally()
    {
        var batch = Batch();

        batch.Report("1 of 3 updated. 2 failed: 7-Zip, Git.");
        batch.Complete(new WingetResult(1603, string.Empty, string.Empty), null);

        // A batch worked through every package and can name the ones it lost,
        // which is worth more than the exit code of whichever failed first.
        Assert.Equal("1 of 3 updated. 2 failed: 7-Zip, Git.", batch.Summary);
    }

    [Fact]
    public void Takes_a_thrown_error_as_the_whole_story()
    {
        var operation = Update();

        operation.Complete(null, "Cancelled.");

        Assert.True(operation.Failed);
        Assert.Equal("Cancelled.", operation.Summary);
    }

    [Fact]
    public void Flattens_and_caps_a_pathological_line()
    {
        var operation = Update();

        operation.Report("Downloading\r\n" + new string('x', 500));

        Assert.DoesNotContain("\n", operation.Detail);
        Assert.DoesNotContain("\r", operation.Detail);
        Assert.Equal(241, operation.Detail.Length);
        Assert.EndsWith("…", operation.Detail);
    }

    [Fact]
    public void Puts_wingets_line_after_its_own_heading()
    {
        var operation = Update();

        operation.Report("Downloading https://example.invalid/git.exe");

        Assert.Equal("Updating Git…  Downloading https://example.invalid/git.exe", operation.Status);
    }

    // -----------------------------------------------------------------
    // A batch, package by package
    // -----------------------------------------------------------------

    [Fact]
    public void A_batch_names_the_package_it_is_on()
    {
        var batch = Batch();

        Assert.Equal("Updating all packages…", batch.Heading);

        batch.BeginItem("7zip.7zip", "7-Zip");

        // Which package was in flight is the whole question after a silent
        // installer has done something to the machine.
        Assert.Equal("Updating 7-Zip…", batch.Heading);
    }

    [Fact]
    public void A_batch_starts_each_package_from_nothing()
    {
        var batch = Batch();

        batch.BeginItem("7zip.7zip", "7-Zip");
        batch.Report("Successfully installed");
        batch.EndItem("7zip.7zip", string.Empty);

        batch.BeginItem("Git.Git", "Git");

        // The phase and the detail belonged to the package before it; the row
        // for this one draws its own bar from its own milestones.
        Assert.Equal(OperationPhase.Starting, batch.Phase);
        Assert.Equal(string.Empty, batch.Detail);
    }

    [Fact]
    public void A_batch_remembers_the_packages_it_got_through()
    {
        var batch = Batch();

        batch.BeginItem("7zip.7zip", "7-Zip");
        batch.EndItem("7zip.7zip", string.Empty);

        Assert.True(batch.WasUpdated("7zip.7zip"));
        Assert.Equal(string.Empty, batch.FailureFor("7zip.7zip"));
    }

    [Fact]
    public void A_batch_remembers_why_it_left_a_package_behind()
    {
        var batch = Batch();

        batch.BeginItem("Git.Git", "Git");
        batch.EndItem("Git.Git", "Installer failed with exit code 1603. (1603)");

        Assert.False(batch.WasUpdated("Git.Git"));
        Assert.Equal("Installer failed with exit code 1603. (1603)", batch.FailureFor("Git.Git"));
    }

    [Fact]
    public void A_batch_is_on_no_package_between_two_of_them()
    {
        var batch = Batch();

        batch.BeginItem("7zip.7zip", "7-Zip");
        Assert.Equal("7zip.7zip", batch.CurrentItem);

        batch.EndItem("7zip.7zip", string.Empty);
        Assert.Equal(string.Empty, batch.CurrentItem);
    }

    [Fact]
    public void A_batch_matches_package_ids_however_they_are_cased()
    {
        var batch = Batch();

        batch.BeginItem("7zip.7zip", "7-Zip");
        batch.EndItem("7ZIP.7ZIP", "Access denied. (5)");

        // winget is inconsistent about the case of an id between commands.
        Assert.Equal("Access denied. (5)", batch.FailureFor("7zip.7zip"));
    }
}
