using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// When an installer fails, winget says so with one generic code and puts the
/// installer's own in its output. These pin down where that code is found,
/// and when a pending restart is worth mentioning beside it.
/// </summary>
public class WingetErrorsTests
{
    [Fact]
    public void Finds_the_installers_code_above_the_line_about_its_log()
    {
        // winget's real closing lines: the code, then where the log is.
        var output = "Starting package install...\nInstaller failed with exit code: 1\n" +
                     @"Installer log is available at: C:\Users\x\AppData\Local\Packages\x\LocalState\DiagOutputDir\x.log";

        var explained = WingetErrors.Explain(
            unchecked((int)0x8A150006), @"Installer log is available at: C:\x.log", output: output);

        Assert.NotNull(explained);
        Assert.EndsWith("(installer returned 1)", explained);
    }

    [Fact]
    public void Reads_the_installers_code_when_winget_prints_it_in_hex()
    {
        // An MSIX package's failure, as winget words it.
        var said = "Installer failed with exit code: 0x80073d28 : The package installation failed because " +
                   "administrator privileges are required. Please contact an administrator to install this package.";

        Assert.Equal(unchecked((int)0x80073D28), WingetErrors.InstallerCode(said));
        Assert.Equal(unchecked((int)0x80073D28), WingetErrors.InstallerCode("Installer failed with exit code: 2147958056"));
        Assert.Equal(1, WingetErrors.InstallerCode("Installer failed with exit code: 1"));
    }

    [Fact]
    public void Explains_an_msix_failure_winget_hands_through_as_its_own_exit_code()
    {
        var explained = WingetErrors.Explain(
            unchecked((int)0x80073D28),
            "Installer failed with exit code: 0x80073d28 : The package installation failed because administrator privileges are required.");

        Assert.NotNull(explained);
        Assert.Contains("administrator", explained);
        Assert.EndsWith("(0x80073D28)", explained);
    }

    [Theory]
    [InlineData(unchecked((int)0x80073D28), "", true)]                                        // MSIX: needs admin
    [InlineData(unchecked((int)0x8A150019), "", true)]                                        // winget: command needs admin
    [InlineData(unchecked((int)0x8A150006), "Installer failed with exit code: 740", true)]    // installer: elevation required
    [InlineData(unchecked((int)0x8A150006), "Installer failed with exit code: 1603", false)]
    [InlineData(unchecked((int)0x8A15005F), "", false)]
    [InlineData(1223, "", false)]                                                             // the prompt was declined
    public void Knows_which_failures_administrator_rights_would_put_right(int code, string said, bool wants) =>
        Assert.Equal(wants, WingetErrors.WantsAdmin(code, said));

    [Fact]
    public void Says_when_windows_is_waiting_to_be_restarted_as_an_installer_fails()
    {
        PendingRestart.Probe = () => true;

        try
        {
            var explained = WingetErrors.Explain(unchecked((int)0x8A150006), "Installer failed with exit code: 1");

            Assert.Contains("Windows is also waiting to be restarted", explained);
        }
        finally
        {
            PendingRestart.Probe = () => false;
        }
    }

    [Fact]
    public void Says_nothing_of_a_restart_for_a_failure_that_is_not_the_installers()
    {
        PendingRestart.Probe = () => true;

        try
        {
            // winget could not find the package: a restart would not help.
            var explained = WingetErrors.Explain(unchecked((int)0x8A150014), "No package found.");

            Assert.DoesNotContain("restarted", explained);
        }
        finally
        {
            PendingRestart.Probe = () => false;
        }
    }
}
