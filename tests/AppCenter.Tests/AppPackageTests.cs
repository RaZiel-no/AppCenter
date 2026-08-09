using System.Windows.Media;
using AppCenter.Models;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The same type backs curated catalogue entries, winget search hits and rows in
/// Manage, and search fills half of it in after the cards are already on screen.
/// So most of what it promises is about telling the bindings that something
/// changed, and about looking right before anything has.
/// </summary>
public class AppPackageTests
{
    [Theory]
    [InlineData("Git", "G")]
    [InlineData("git", "G")]
    [InlineData("  Firefox", "F")]
    [InlineData("7-Zip", "7")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void Shows_an_initial_when_there_is_no_icon_to_show(string name, string expected)
    {
        // Drawn in the placeholder tile, so it has to hold for a name that is
        // missing as well as one that is merely odd.
        Assert.Equal(expected, new AppPackage { Name = name }.Initial);
    }

    [Fact]
    public void Re_reads_the_initial_when_the_name_arrives_late()
    {
        var package = new AppPackage();
        var changed = Changes(package);

        package.Name = "Git";

        // Search fills the name in after the card is on screen; the tile has to
        // be told, or it keeps showing "?".
        Assert.Contains(nameof(AppPackage.Initial), changed);
    }

    [Fact]
    public void Reads_a_version_on_its_own_when_there_is_nothing_to_upgrade_to()
    {
        var package = new AppPackage { Version = "2.47.0.2" };

        Assert.Equal("2.47.0.2", package.VersionTransition);
    }

    [Fact]
    public void Reads_as_a_transition_when_there_is()
    {
        var package = new AppPackage { Version = "2.47.0.2", AvailableVersion = "2.55.0.3" };

        Assert.Equal("2.47.0.2 → 2.55.0.3", package.VersionTransition);
    }

    [Fact]
    public void Re_reads_the_transition_when_the_available_version_arrives()
    {
        var package = new AppPackage { Version = "2.47.0.2" };
        var changed = Changes(package);

        package.AvailableVersion = "2.55.0.3";

        Assert.Contains(nameof(AppPackage.VersionTransition), changed);
    }

    [Fact]
    public void Says_nothing_when_a_property_is_set_to_what_it_already_was()
    {
        var package = new AppPackage { Name = "Git" };
        var changed = Changes(package);

        package.Name = "Git";

        // Every operation repaints every row it can see; a notification per
        // unchanged property would be a redraw per winget output line.
        Assert.Empty(changed);
    }

    [Fact]
    public void Knows_whether_it_has_an_icon_yet()
    {
        Sta.Run(() =>
        {
            var package = new AppPackage { Id = "Git.Git", Name = "Git" };
            var changed = Changes(package);

            Assert.False(package.HasIcon);

            package.Icon = new System.Windows.Media.Imaging.BitmapImage();

            Assert.True(package.HasIcon);
            Assert.Contains(nameof(AppPackage.HasIcon), changed);
        });
    }

    [Fact]
    public void Gives_a_package_the_same_placeholder_colour_every_time()
    {
        Sta.Run(() =>
        {
            var first = new AppPackage { Id = "Git.Git", Name = "Git" }.FallbackBrush;
            var second = new AppPackage { Id = "Git.Git", Name = "Something else" }.FallbackBrush;

            // Seeded from the id, so the tile for a given app looks the same on
            // every launch - and does not change when search fills the name in.
            Assert.Equal(Colours(first), Colours(second));
        });
    }

    [Fact]
    public void Gives_two_packages_different_placeholder_colours()
    {
        Sta.Run(() =>
        {
            var git = new AppPackage { Id = "Git.Git" }.FallbackBrush;
            var sevenZip = new AppPackage { Id = "7zip.7zip" }.FallbackBrush;

            Assert.NotEqual(Colours(git), Colours(sevenZip));
        });
    }

    [Fact]
    public void Falls_back_to_the_name_for_a_colour_when_it_has_no_id()
    {
        Sta.Run(() =>
        {
            var brush = new AppPackage { Name = "Git" }.FallbackBrush;

            Assert.NotNull(brush);
        });
    }

    /// <summary>
    /// The two ends of the tile's gradient. ToString on a brush gives its type
    /// and nothing else, so the colours have to be read out to be compared.
    /// </summary>
    private static IEnumerable<Color> Colours(Brush brush) =>
        ((LinearGradientBrush)brush).GradientStops.Select(stop => stop.Color);

    private static List<string> Changes(AppPackage package)
    {
        List<string> changed = [];

        package.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name)
                changed.Add(name);
        };

        return changed;
    }
}
