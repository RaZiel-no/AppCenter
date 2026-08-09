using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AppCenter.Models;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The packages that close App Center when they are updated - the .NET runtime
/// it is running on, and its own installer. Getting this list wrong is not a
/// cosmetic failure: a package that belongs on it and is not gets updated with
/// no warning, Windows closes the app to free the files, and the rest of the
/// batch never runs. That is the bug this exists to answer, so what it names
/// and where it puts them are both worth holding to account here.
///
/// The band is read from the runtime the tests are themselves running on, which
/// is the same one the app is built for - so these follow a move to a new .NET
/// rather than having to be rewritten for it.
/// </summary>
public class SelfPackagesTests
{
    private static int Band => Environment.Version.Major;

    private static AppPackage Row(string id) => new() { Id = id, Name = id };

    [Fact]
    public void Names_the_desktop_runtime_this_build_runs_on()
    {
        // The exact id winget uses. A typo here is invisible in the app: the
        // row simply never says anything, which is indistinguishable from the
        // package not being upgradable.
        Assert.True(SelfPackages.Includes($"Microsoft.DotNet.DesktopRuntime.{Band}"));
    }

    [Theory]
    [InlineData("Microsoft.DotNet.Runtime")]
    [InlineData("Microsoft.DotNet.AspNetCore")]
    [InlineData("Microsoft.DotNet.SDK")]
    public void Names_the_rest_of_the_same_band_too(string prefix)
    {
        // Each of these bundles carries the same shared-framework MSIs, so any
        // of them replaces what this process has loaded.
        Assert.True(SelfPackages.Includes($"{prefix}.{Band}"));
    }

    [Fact]
    public void Leaves_other_bands_alone()
    {
        // A .NET 8 runtime on the same machine is nothing to do with a process
        // running on this one, and warning about it would be noise.
        Assert.False(SelfPackages.Includes($"Microsoft.DotNet.DesktopRuntime.{Band - 1}"));
        Assert.False(SelfPackages.Includes($"Microsoft.DotNet.DesktopRuntime.{Band + 1}"));
    }

    [Fact]
    public void Names_App_Centers_own_package()
    {
        // It updates by running an installer over a running copy of itself.
        Assert.True(SelfPackages.Includes("ArnsteinSkara.AppCenter"));
    }

    [Fact]
    public void Does_not_mind_how_the_id_is_cased()
    {
        // winget's own output is the source of these, and it prints the id as
        // the manifest spells it rather than as anything here expects.
        Assert.True(SelfPackages.Includes($"microsoft.dotnet.desktopruntime.{Band}"));
    }

    [Theory]
    [InlineData("7zip.7zip")]
    [InlineData("Mozilla.Firefox")]
    [InlineData("Microsoft.WSL")]
    [InlineData("Microsoft.VCRedist.2015+.x64")]
    [InlineData("")]
    public void Leaves_ordinary_packages_alone(string id)
    {
        // Microsoft.WSL is here on purpose: it ships as MSIX, which cannot
        // close an unrelated process, and it was the other candidate the first
        // time the app went away mid-batch.
        Assert.False(SelfPackages.Includes(id));
    }

    [Fact]
    public void Puts_the_ones_that_close_the_app_last()
    {
        var ordered = SelfPackages.LastInLine(
        [
            Row("7zip.7zip"),
            Row($"Microsoft.DotNet.DesktopRuntime.{Band}"),
            Row("Mozilla.Firefox"),
        ]);

        Assert.Equal(
            ["7zip.7zip", "Mozilla.Firefox", $"Microsoft.DotNet.DesktopRuntime.{Band}"],
            ordered.Select(p => p.Id));
    }

    [Fact]
    public void Leaves_the_order_of_everything_else_as_it_was()
    {
        // The list is what the user is looking at and what the batch works
        // down, so nothing may move except the packages that have to.
        var ordered = SelfPackages.LastInLine(
        [
            Row("Zoom.Zoom"),
            Row("Git.Git"),
            Row("7zip.7zip"),
        ]);

        Assert.Equal(["Zoom.Zoom", "Git.Git", "7zip.7zip"], ordered.Select(p => p.Id));
    }

    [Fact]
    public void Keeps_two_of_them_in_the_order_they_arrived()
    {
        var ordered = SelfPackages.LastInLine(
        [
            Row($"Microsoft.DotNet.SDK.{Band}"),
            Row("Git.Git"),
            Row($"Microsoft.DotNet.DesktopRuntime.{Band}"),
        ]);

        Assert.Equal(
            ["Git.Git", $"Microsoft.DotNet.SDK.{Band}", $"Microsoft.DotNet.DesktopRuntime.{Band}"],
            ordered.Select(p => p.Id));
    }

    [Fact]
    public void The_row_reads_its_note_out_of_the_class_that_owns_the_wording()
    {
        // The note is not typed into the template: it comes from the constant
        // by x:Static, so the row and the dialog cannot drift into saying
        // different things about the same package.
        //
        // Worth its own test because a DataTemplate's body is deferred - it is
        // not parsed until a row is actually built with it, so the app starts
        // perfectly well with a broken one and fails later, on the page, in
        // front of whoever opened it.
        Sta.Run(() =>
        {
            var template = (DataTemplate)XamlReader.Parse(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                              xmlns:services="clr-namespace:AppCenter.Services;assembly=AppCenter">
                    <TextBlock Text="{x:Static services:SelfPackages.RowNote}" />
                </DataTemplate>
                """);

            Assert.Equal(SelfPackages.RowNote, ((TextBlock)template.LoadContent()).Text);
        });
    }
}
