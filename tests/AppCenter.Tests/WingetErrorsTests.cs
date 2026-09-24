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
