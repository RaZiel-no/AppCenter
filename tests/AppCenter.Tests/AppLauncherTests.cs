using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// Open finds the installed app in the Start menu by name, since nothing winget
/// knows says how to run a package. These hold that match to account against
/// entries as a real Start menu lists them - the app among its uninstaller,
/// its manuals and its web links.
/// </summary>
public class AppLauncherTests
{
    private static readonly StartEntry[] Start =
    [
        new("Firefox", "308046B0AF4A39CB"),
        new("Firefox Privat nettlesing", "308046B0AF4A39CB;PrivateBrowsingAUMID"),
        new("Qt Creator 15.0.1 (Community)", @"C:\Qt\qtcreator-15.0.1\bin\qtcreator.exe"),
        new("Python 3.14", @"C:\Users\x\AppData\Local\Python\pythoncore-3.14-64\python.exe"),
        new("Python 3.14 Manuals", @"C:\Users\x\AppData\Local\Python\pythoncore-3.14-64\Doc\html\index.html"),
        new("Python 3.14 Online Documentation", "https://docs.python.org/3.14/"),
        new("Uninstall Winamp", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Winamp\UninstWA.exe"),
        new("Winamp", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Winamp\winamp.exe"),
        new("Python install manager", "PythonSoftwareFoundation.PythonManager_3847v3x7pw1km!Py.Manager.Exe"),
        new("Git Bash", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Git\git-bash.exe"),
    ];

    [Fact]
    public void Prefers_the_entry_called_just_what_the_package_is_called()
    {
        // Not the private window, though its name goes on from the app's.
        var found = AppLauncher.Find(Start, ["Mozilla Firefox (x64 nb-NO)", "Firefox"]);

        Assert.Equal("Firefox", found?.Name);
    }

    [Fact]
    public void Finds_an_entry_that_goes_on_from_the_packages_name()
    {
        var found = AppLauncher.Find(Start, ["Qt Creator 15.0.1"]);

        Assert.Equal("Qt Creator 15.0.1 (Community)", found?.Name);
    }

    [Fact]
    public void Finds_an_entry_the_packages_name_goes_on_from_and_not_its_manuals()
    {
        var found = AppLauncher.Find(Start, ["Python 3.14.5"]);

        Assert.Equal("Python 3.14", found?.Name);
    }

    [Fact]
    public void Leaves_out_the_uninstaller()
    {
        var found = AppLauncher.Find(Start, ["Winamp"]);

        Assert.Equal(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Winamp\winamp.exe", found?.AppId);
    }

    [Fact]
    public void Finds_a_store_app_by_its_package_family()
    {
        var found = AppLauncher.Find(Start, ["Something Else Entirely"], "PythonSoftwareFoundation.PythonManager_3847v3x7pw1km");

        Assert.Equal("Python install manager", found?.Name);
    }

    [Fact]
    public void Finds_nothing_rather_than_something_that_only_shares_a_short_word()
    {
        // "Git" is what Git Bash goes on from, not what GitHub Desktop is called.
        Assert.Null(AppLauncher.Find(Start, ["GitHub Desktop"]));
    }
}
