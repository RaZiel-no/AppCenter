using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// App Center offers its own newer releases from GitHub, ahead of the winget
/// package that follows weeks later. These pin down how GitHub's answer is
/// read, when a release counts as newer, when the GitHub route stays out of
/// winget's way, and how the published digest is read off its file.
/// </summary>
public class AppUpdateTests : IDisposable
{
    public AppUpdateTests() => AppUpdateService.Reset();

    public void Dispose() => AppUpdateService.Reset();

    /// <summary>The shape of api.github.com/repos/…/releases/latest, trimmed.</summary>
    private static string Latest(
        string tag = "v1.0.4",
        bool prerelease = false,
        bool draft = false,
        bool withSetup = true,
        bool withHash = true)
    {
        var assets = new List<string>
        {
            """{"name":"AppCenter-1.0.4-portable.zip","browser_download_url":"https://github.com/RaZiel-no/AppCenter/releases/download/v1.0.4/AppCenter-1.0.4-portable.zip","size":360345}""",
        };

        if (withSetup)
            assets.Add("""{"name":"AppCenter-1.0.4-Setup.exe","browser_download_url":"https://github.com/RaZiel-no/AppCenter/releases/download/v1.0.4/AppCenter-1.0.4-Setup.exe","size":2358577}""");

        if (withHash)
            assets.Add("""{"name":"AppCenter-1.0.4-Setup.exe.sha256","browser_download_url":"https://github.com/RaZiel-no/AppCenter/releases/download/v1.0.4/AppCenter-1.0.4-Setup.exe.sha256","size":91}""");

        return $$"""
            {
              "html_url": "https://github.com/RaZiel-no/AppCenter/releases/tag/{{tag}}",
              "tag_name": "{{tag}}",
              "name": "App Center 1.0.4",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "published_at": "2026-09-12T18:43:59Z",
              "assets": [{{string.Join(",", assets)}}],
              "body": "- Families on Manage\n- Update from an app's page"
            }
            """;
    }

    // -----------------------------------------------------------------
    // Reading GitHub's answer
    // -----------------------------------------------------------------

    [Fact]
    public void Reads_the_release_and_finds_the_installer_and_its_hash()
    {
        var release = AppUpdateService.Parse(Latest());

        Assert.NotNull(release);
        Assert.Equal("1.0.4", release.Version);
        Assert.Equal("v1.0.4", release.Tag);
        Assert.Equal("AppCenter-1.0.4-Setup.exe", release.SetupName);
        Assert.EndsWith("/v1.0.4/AppCenter-1.0.4-Setup.exe", release.SetupUrl);
        Assert.Equal(2358577, release.SetupSize);
        Assert.EndsWith("AppCenter-1.0.4-Setup.exe.sha256", release.HashUrl);
        Assert.True(release.HasInstaller);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 18, 43, 59, TimeSpan.Zero), release.PublishedAt);
        Assert.Contains("Families on Manage", release.Notes);
    }

    [Fact]
    public void Takes_the_v_off_the_tag()
    {
        Assert.Equal("2.1.0", AppUpdateService.Parse(Latest(tag: "V2.1.0"))!.Version);
        Assert.Equal("2.1.0", AppUpdateService.Parse(Latest(tag: "2.1.0"))!.Version);
    }

    [Fact]
    public void Ignores_drafts_and_pre_releases()
    {
        // GitHub's "latest" never returns these, but a hand-picked URL might.
        Assert.Null(AppUpdateService.Parse(Latest(draft: true)));
        Assert.Null(AppUpdateService.Parse(Latest(prerelease: true)));
    }

    [Fact]
    public void Keeps_a_release_that_has_no_installer_attached()
    {
        // Its page can still be opened; it just cannot be run from here.
        var release = AppUpdateService.Parse(Latest(withSetup: false));

        Assert.NotNull(release);
        Assert.False(release.HasInstaller);
        Assert.Null(release.HashUrl);
    }

    [Fact]
    public void Has_no_hash_url_when_none_was_published()
    {
        var release = AppUpdateService.Parse(Latest(withHash: false));

        Assert.NotNull(release);
        Assert.True(release.HasInstaller);
        Assert.Null(release.HashUrl);
    }

    [Fact]
    public void Gives_up_on_an_answer_that_is_not_a_release()
    {
        Assert.Null(AppUpdateService.Parse("""{"message":"Not Found"}"""));
        Assert.Null(AppUpdateService.Parse("[]"));
    }

    // -----------------------------------------------------------------
    // Newer or not
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("1.0.4", "1.0.3", true)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.1.0", "1.0.12", true)]
    [InlineData("1.0.3", "1.0.3", false)]
    [InlineData("1.0.3", "1.0.4", false)]
    [InlineData("1.0.4", "1.0.4-rc.1", true)]
    public void Counts_as_newer_only_when_it_is(string published, string running, bool newer)
    {
        var release = AppUpdateService.Parse(Latest(tag: "v" + published))!;

        Assert.Equal(newer, release.IsNewerThan(running));
    }

    [Fact]
    public void Offers_nothing_before_a_check_has_answered()
    {
        Assert.False(AppUpdateService.IsAvailable);
        Assert.False(AppUpdateService.OffersMoreThan(null));
    }

    // -----------------------------------------------------------------
    // The published digest
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b  AppCenter-1.0.4-Setup.exe\n")]
    [InlineData("3A7BD3E2360A3D29EEA436FCFB7E44C735D117C42D1C1835420B6B9942DD4F1B *AppCenter-1.0.4-Setup.exe")]
    [InlineData("3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b")]
    [InlineData("SHA256: 3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b")]
    public void Reads_the_digest_whatever_else_is_on_the_line(string text)
    {
        Assert.Equal(
            "3A7BD3E2360A3D29EEA436FCFB7E44C735D117C42D1C1835420B6B9942DD4F1B",
            AppUpdateService.ParseHash(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f")]
    public void Reads_no_digest_from_something_that_is_not_one(string text)
    {
        Assert.Null(AppUpdateService.ParseHash(text));
    }

    // -----------------------------------------------------------------
    // The app knows itself
    // -----------------------------------------------------------------

    [Fact]
    public void Knows_its_own_version_and_package()
    {
        // The test host runs the unpublished assembly; the version is whatever
        // the csproj says, and it has to be a real number rather than "1.0.0.0".
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.Equal("ArnsteinSkara.AppCenter", AppInfo.PackageId);
        Assert.True(SelfPackages.Includes(AppInfo.PackageId));

        // A test run is never an installed copy.
        Assert.False(AppInfo.IsInstalledCopy);
    }
}
