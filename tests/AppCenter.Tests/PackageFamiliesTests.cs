using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The installed list is folded into families before it is shown: eight
/// Visual C++ redistributables are one row, every .NET SDK is one row, and a
/// package installed once is a row of its own. These pin down what counts as
/// a family, what a family is called, and what each install is called inside
/// it - all of which is decided from names and ids winget hands over, with
/// no list of known families anywhere.
/// </summary>
public class PackageFamiliesTests
{
    private static AppPackage Package(string id, string name, string version = "1.0") =>
        new() { Id = id, Name = name, Version = version };

    // -----------------------------------------------------------------
    // Which rows belong together
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("Microsoft.VCRedist.2010.x64", "Microsoft.VCRedist")]
    [InlineData("Microsoft.VCRedist.2015+.x86", "Microsoft.VCRedist")]
    [InlineData("Microsoft.DotNet.SDK.10", "Microsoft.DotNet.SDK")]
    [InlineData("Microsoft.DotNet.SDK.Preview", "Microsoft.DotNet.SDK")]
    [InlineData("Microsoft.VisualStudioCode.Insiders", "Microsoft.VisualStudioCode")]
    [InlineData("Microsoft.WindowsAppRuntime.1.8", "Microsoft.WindowsAppRuntime")]
    [InlineData("Microsoft.UI.Xaml.2.8", "Microsoft.UI.Xaml")]
    [InlineData("Python.Python.3.13", "Python.Python")]
    public void Takes_the_version_architecture_and_channel_off_the_end_of_an_id(string id, string family)
    {
        Assert.Equal(family, PackageFamilies.StripVersionSegments(id));
    }

    [Theory]
    [InlineData("7zip.7zip")]
    [InlineData("Git.Git")]
    [InlineData("Microsoft.VisualStudio.2022.Professional")]
    [InlineData("SomePublisher.Dev")]
    public void Leaves_an_id_alone_when_it_does_not_end_in_a_version(string id)
    {
        Assert.Equal(id, PackageFamilies.StripVersionSegments(id));
    }

    [Fact]
    public void Never_strips_an_id_below_publisher_and_name()
    {
        // "Publisher.2" is a name, however it looks.
        Assert.Equal("Foo.2", PackageFamilies.StripVersionSegments("Foo.2"));
    }

    [Fact]
    public void Groups_the_installs_of_one_family_and_leaves_the_rest_alone()
    {
        var groups = PackageFamilies.Group(
        [
            Package("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219"),
            Package("Git.Git", "Git", "2.47.0.2"),
            Package("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433", "14.42.34433.0"),
        ]);

        Assert.Equal(2, groups.Count);

        var vc = Assert.Single(groups, g => g.IsGroup);
        Assert.Equal(2, vc.Members.Count);
        Assert.Equal("Microsoft.VCRedist", vc.Key);

        var git = Assert.Single(groups, g => !g.IsGroup);
        Assert.Equal("Git", git.Title);
    }

    [Fact]
    public void Groups_by_name_what_winget_could_not_match_to_a_package()
    {
        // The x64 and x86 halves of an MSIX framework: two ids, one thing.
        var groups = PackageFamilies.Group(
        [
            Package(@"MSIX\Microsoft.VCLibs.110.00.UWPDesktop_11.0.61135.0_x64__8wekyb3d8bbwe",
                "Microsoft Visual C++ 2012 UWP Desktop Runtime Package", "11.0.61135.0"),
            Package(@"MSIX\Microsoft.VCLibs.110.00.UWPDesktop_11.0.61135.0_x86__8wekyb3d8bbwe",
                "Microsoft Visual C++ 2012 UWP Desktop Runtime Package", "11.0.61135.0"),
        ]);

        var family = Assert.Single(groups);
        Assert.True(family.IsGroup);
        Assert.Equal("Microsoft Visual C++ 2012 UWP Desktop Runtime Package", family.Title);
    }

    [Fact]
    public void Does_not_group_unrelated_installs_that_happen_to_share_a_name_prefix()
    {
        var groups = PackageFamilies.Group(
        [
            Package("Microsoft.Edge", "Microsoft Edge"),
            Package("Microsoft.EdgeWebView2Runtime", "Microsoft Edge WebView2 Runtime"),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsGroup));
    }

    [Fact]
    public void Puts_the_newest_install_first()
    {
        var groups = PackageFamilies.Group(
        [
            Package("Microsoft.DotNet.SDK.9", "Microsoft .NET SDK 9.0.317 (x64)", "9.0.317"),
            Package("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.201 (x64)", "10.0.201"),
            Package("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.302 (x64)", "10.0.302"),
        ]);

        var sdk = Assert.Single(groups);

        // A string sort would put 9 after 10; a person would not.
        Assert.Equal(["10.0.302", "10.0.201", "9.0.317"], sdk.Members.Select(m => m.Version));
        Assert.Same(sdk.Members[0], sdk.Lead);
    }

    // -----------------------------------------------------------------
    // What a family is called
    // -----------------------------------------------------------------

    [Fact]
    public void Names_a_family_by_what_its_members_share()
    {
        var name = PackageFamilies.FamilyName(
        [
            "Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219",
            "Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.61030",
            "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433",
        ]);

        // Not "Microsoft Visual C++ 201", where the letters stop matching, and
        // not without the word every one of them goes on to.
        Assert.Equal("Microsoft Visual C++ Redistributable", name);
    }

    [Theory]
    [InlineData("Microsoft .NET SDK 10.0.201 (x64)", "Microsoft .NET SDK 9.0.203 (x64)", "Microsoft .NET SDK")]
    [InlineData("AIDA64 Extreme v7.00", "AIDA64 Extreme v8.35 (64-bit)", "AIDA64 Extreme")]
    [InlineData("WindowsAppRuntime.1.4", "WindowsAppRuntime.2", "WindowsAppRuntime")]
    [InlineData("Microsoft.UI.Xaml.2.7", "Microsoft.UI.Xaml.2.8", "Microsoft.UI.Xaml")]
    [InlineData("Microsoft .NET Framework 4.8 SDK", "Microsoft .NET Framework 4.8 Targeting Pack", "Microsoft .NET Framework")]
    [InlineData("Microsoft OLE DB Driver 19 for SQL Server", "Microsoft OLE DB Driver for SQL Server", "Microsoft OLE DB Driver for SQL Server")]
    [InlineData("Python Launcher", "Python Launcher", "Python Launcher")]
    public void Drops_the_version_from_the_end_of_a_family_name(string first, string second, string expected)
    {
        Assert.Equal(expected, PackageFamilies.FamilyName([first, second]));
    }

    [Fact]
    public void Falls_back_to_the_first_name_when_the_names_share_nothing()
    {
        Assert.Equal("Alpha", PackageFamilies.FamilyName(["Alpha", "Beta"]));
    }

    // -----------------------------------------------------------------
    // What each install is called inside its family
    // -----------------------------------------------------------------

    [Fact]
    public void Labels_a_member_by_what_its_name_adds_to_the_family()
    {
        var groups = PackageFamilies.Group(
        [
            Package("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219"),
            Package("Microsoft.VCRedist.2015+.x64", "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "14.51.36247.0"),
        ]);

        var labels = Assert.Single(groups).Members.Select(m => m.VariantLabel).ToList();

        // The family's words are gone from the middle as well as the front.
        Assert.Contains("2010 x64 - 10.0.40219", labels);
        Assert.Contains("v14 (x64) - 14.51.36247", labels);
    }

    [Fact]
    public void Labels_a_member_by_its_version_when_its_name_adds_nothing()
    {
        var groups = PackageFamilies.Group(
        [
            Package("Python.Launcher", "Python Launcher", "3.13.5"),
            Package("Python.Launcher", "Python Launcher", "3.11.9"),
        ]);

        Assert.Equal(["3.13.5", "3.11.9"], Assert.Single(groups).Members.Select(m => m.VariantLabel));
    }

    [Fact]
    public void Labels_msix_halves_by_the_architecture_in_their_id()
    {
        var groups = PackageFamilies.Group(
        [
            Package(@"MSIX\Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", "Microsoft Visual C++ 2015 UWP Runtime Package", "14.0.33519.0"),
            Package(@"MSIX\Microsoft.VCLibs.140.00_14.0.33519.0_x86__8wekyb3d8bbwe", "Microsoft Visual C++ 2015 UWP Runtime Package", "14.0.33519.0"),
        ]);

        var labels = Assert.Single(groups).Members.Select(m => m.VariantLabel).ToList();

        Assert.Contains("14.0.33519.0 (x64)", labels);
        Assert.Contains("14.0.33519.0 (x86)", labels);
    }

    [Fact]
    public void Leaves_a_package_on_its_own_unlabelled()
    {
        var groups = PackageFamilies.Group([Package("Git.Git", "Git", "2.47.0.2")]);

        Assert.Equal(string.Empty, Assert.Single(groups).Lead.VariantLabel);
    }

    [Fact]
    public void Prints_a_member_id_only_where_it_adds_to_the_family_id()
    {
        var groups = PackageFamilies.Group(
        [
            Package("Python.Launcher", "Python Launcher", "3.13.5"),
            Package("Python.Launcher", "Python Launcher", "3.11.9"),
            Package("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010 x64 Redistributable", "10.0.40219"),
            Package("Microsoft.VCRedist.2010.x86", "Microsoft Visual C++ 2010 x86 Redistributable", "10.0.40219"),
        ]);

        var python = groups.Single(g => g.Key == "Python.Launcher");
        var vc = groups.Single(g => g.Key == "Microsoft.VCRedist");

        // One id installed twice: the family row said it; the rows need not.
        Assert.All(python.Members, m => Assert.Equal(string.Empty, m.VariantId));

        // Two ids under one family: each is worth its column.
        Assert.Contains("Microsoft.VCRedist.2010.x64", vc.Members.Select(m => m.VariantId));
    }

    [Fact]
    public void Hides_the_handles_winget_makes_up_for_unmatched_installs()
    {
        Assert.Equal(string.Empty, Package(@"ARP\Machine\X64\{8CDD9228-86FF-4604-8D31-91096F53D8D8}", "Quickmenu").DisplayId);
        Assert.Equal(string.Empty, Package(@"MSIX\NotepadPlusPlus_1.0.0.0_neutral__2247w0bft5rqr", "Notepad++").DisplayId);
        Assert.Equal("Git.Git", Package("Git.Git", "Git").DisplayId);
    }

    // -----------------------------------------------------------------
    // The line under the label
    // -----------------------------------------------------------------

    [Fact]
    public void Says_the_version_under_a_label_that_does_not()
    {
        var package = Package("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010 x64 Redistributable", "10.0.40219");
        package.VariantLabel = "2010 x64";

        Assert.Equal("10.0.40219", package.VariantDetail);
    }

    [Fact]
    public void Says_nothing_under_a_label_that_already_carries_the_version()
    {
        var package = Package("Microsoft.VCRedist.2015+.x64", "…", "14.51.36247.0");
        package.VariantLabel = "v14 (x64) - 14.51.36247";

        // "14.51.36247" is the same number winget lists as 14.51.36247.0.
        Assert.Equal(string.Empty, package.VariantDetail);
    }

    [Fact]
    public void Says_the_update_waiting_under_a_member()
    {
        var package = Package("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0.302 (x64)", "10.0.302");
        package.AvailableVersion = "10.0.401";
        package.VariantLabel = "10.0.302 (x64)";

        Assert.Equal("Update available: 10.0.401", package.VariantDetail);
    }

    // -----------------------------------------------------------------
    // The row
    // -----------------------------------------------------------------

    [Fact]
    public void A_family_row_says_how_many_it_covers_and_hides_made_up_ids()
    {
        var vc = PackageFamilies.Group(
        [
            Package("Microsoft.VCRedist.2010.x64", "Microsoft Visual C++ 2010 x64 Redistributable"),
            Package("Microsoft.VCRedist.2010.x86", "Microsoft Visual C++ 2010 x86 Redistributable"),
        ]).Single();

        var msix = PackageFamilies.Group(
        [
            Package(@"MSIX\A_1.0_x64__abc", "Thing"),
            Package(@"MSIX\A_1.0_x86__abc", "Thing"),
        ]).Single();

        Assert.Equal("2 versions installed", vc.Subtitle);
        Assert.Equal("Microsoft.VCRedist", vc.IdText);
        Assert.Equal(string.Empty, msix.IdText);
    }

    [Fact]
    public void A_family_counts_as_system_only_when_every_member_does()
    {
        var mixed = PackageFamilies.Group(
        [
            new AppPackage { Id = "A.B.1", Name = "A B 1", IsSystemPackage = true },
            new AppPackage { Id = "A.B.2", Name = "A B 2", IsSystemPackage = false },
        ]).Single();

        Assert.False(mixed.IsSystemPackage);
    }
}
