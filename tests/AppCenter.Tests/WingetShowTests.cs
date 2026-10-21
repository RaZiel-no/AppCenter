using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// An app's page reads `winget show` by winget's labels, whether the text
/// came out of winget.exe or was written from what the API said. These pin
/// down both halves: what the parser takes from winget's output, and that a
/// manifest is rendered the way winget prints one - so that the same parser,
/// on the same package, yields the same fields whichever path answered.
/// </summary>
public class WingetShowTests
{
    // `winget show --id X --exact` as App Installer 1.29 prints it: a
    // description of two lines, a documentation entry, tags, and the
    // installer block. The values are made up; the shape is winget's.
    private const string Shown =
        """
        Found Tidy Editor [Example.TidyEditor]
        Version: 8.8.9
        Publisher: Example Team
        Publisher Url: https://example.org/
        Author: A. Author
        Moniker: tidy
        Description:
          Tidy Editor is a free source code editor that supports several languages.
          Its use is governed by the GNU General Public License.
        Homepage: https://example.org/tidy
        License: GPL-3.0
        License Url: https://example.org/tidy/LICENSE
        Copyright: Copyright (C) 2026 A. Author
        Release Notes Url: https://example.org/tidy/news
        Documentation:
          Wiki: https://example.org/tidy/wiki
        Tags:
          editor
          text-editor
        Installer:
          Installer Type: exe
          Installer Locale: en-US
          Installer Url: https://example.org/tidy/setup.exe
          Installer SHA256: 0000000000000000000000000000000000000000000000000000000000000000
          Release Date: 2026-10-01
        """;

    /// <summary>The same package as the API hands it over.</summary>
    private static readonly WingetManifest Manifest = new(
        Id: "Example.TidyEditor",
        Name: "Tidy Editor",
        Version: "8.8.9",
        Publisher: "Example Team",
        PublisherUrl: "https://example.org/",
        PublisherSupportUrl: "",
        Author: "A. Author",
        Description: "Tidy Editor is a free source code editor that supports several languages.\nIts use is governed by the GNU General Public License.",
        ShortDescription: "A tidy editor.",
        Homepage: "https://example.org/tidy",
        License: "GPL-3.0",
        LicenseUrl: "https://example.org/tidy/LICENSE",
        PrivacyUrl: "",
        Copyright: "Copyright (C) 2026 A. Author",
        CopyrightUrl: "",
        ReleaseNotes: "",
        ReleaseNotesUrl: "https://example.org/tidy/news",
        PurchaseUrl: "",
        InstallationNotes: "",
        Tags: ["editor", "text-editor"],
        Documentation: [new WingetLink("Wiki", "https://example.org/tidy/wiki")],
        Agreements: []);

    // ---------------------------------------------------------------
    // The parser
    // ---------------------------------------------------------------

    [Fact]
    public void Reads_a_field_from_each_labelled_line()
    {
        var fields = WingetService.ParseShow(Shown);

        Assert.Equal("8.8.9", fields["Version"]);
        Assert.Equal("Example Team", fields["Publisher"]);
        Assert.Equal("https://example.org/tidy", fields["Homepage"]);
        Assert.Equal("A. Author", fields["Author"]);
    }

    [Fact]
    public void Joins_the_lines_of_a_description()
    {
        var fields = WingetService.ParseShow(Shown);

        Assert.Equal(
            "Tidy Editor is a free source code editor that supports several languages. Its use is governed by the GNU General Public License.",
            fields["Description"]);
    }

    [Fact]
    public void Joins_the_tags_and_passes_over_indented_labels()
    {
        var fields = WingetService.ParseShow(Shown);

        Assert.Equal("editor text-editor", fields["Tags"]);

        // The installer block's details and the documentation entry are
        // indented lines with labels of their own: neither a field nor a
        // continuation.
        Assert.Equal(string.Empty, fields["Installer"]);
        Assert.False(fields.ContainsKey("Installer Type"));
        Assert.False(fields.ContainsKey("Release Date"));
        Assert.False(fields.ContainsKey("Wiki"));
        Assert.Equal(string.Empty, fields["Documentation"]);
    }

    [Fact]
    public void Takes_nothing_from_a_failed_or_empty_output()
    {
        Assert.Empty(WingetService.ParseShow(string.Empty));
        Assert.Empty(WingetService.ParseShow("No package found matching input criteria."));
    }

    // ---------------------------------------------------------------
    // The renderer
    // ---------------------------------------------------------------

    [Fact]
    public void Renders_what_the_parser_reads_from_winget()
    {
        var fromWinget = WingetService.ParseShow(Shown);
        var fromApi = WingetService.ParseShow(WingetService.RenderShow(Manifest));

        // The moniker is the one field winget prints that the API does not carry.
        fromWinget.Remove("Moniker");

        Assert.Equal(fromWinget.OrderBy(f => f.Key), fromApi.OrderBy(f => f.Key));
    }

    [Fact]
    public void Prints_in_wingets_shapes()
    {
        var manifest = new WingetManifest(
            Id: "Example.Small",
            Name: "Small",
            Version: "1.0",
            Publisher: "Example",
            PublisherUrl: "",
            PublisherSupportUrl: "",
            Author: "",
            Description: "One line.",
            ShortDescription: "",
            Homepage: "",
            License: "MIT",
            LicenseUrl: "",
            PrivacyUrl: "",
            Copyright: "",
            CopyrightUrl: "",
            ReleaseNotes: "First.\nSecond.",
            ReleaseNotesUrl: "",
            PurchaseUrl: "",
            InstallationNotes: "",
            Tags: ["one"],
            Documentation: [new WingetLink("", "https://example.org/docs"), new WingetLink("Gone", "")],
            Agreements:
            [
                new WingetAgreement("Terms", "", "https://example.org/terms"),
                new WingetAgreement("EULA", "Some text.", "https://example.org/eula"),
            ]);

        const string expected =
            """
            Found Small [Example.Small]
            Version: 1.0
            Publisher: Example
            Description: One line.
            License: MIT
            Release Notes:
              First.
              Second.
            Documentation:
              https://example.org/docs
            Tags: one
            Agreements:
              Terms: https://example.org/terms
              EULA: Some text.
            https://example.org/eula
            Installer:

            """;

        Assert.Equal(expected.Replace("\r\n", "\n"), WingetService.RenderShow(manifest));
    }

    [Fact]
    public void Leaves_out_what_the_manifest_does_not_have()
    {
        var manifest = Manifest with
        {
            Publisher = "",
            PublisherUrl = "",
            Author = "",
            Description = "",
            ShortDescription = "",
            Homepage = "",
            License = "",
            LicenseUrl = "",
            Copyright = "",
            ReleaseNotesUrl = "",
            Tags = [],
            Documentation = [],
        };

        Assert.Equal("Found Tidy Editor [Example.TidyEditor]\nVersion: 8.8.9\nInstaller:\n", WingetService.RenderShow(manifest));
    }

    [Fact]
    public void Falls_back_to_the_short_description()
    {
        var fields = WingetService.ParseShow(WingetService.RenderShow(Manifest with { Description = "" }));

        Assert.Equal("A tidy editor.", fields["Description"]);
        Assert.False(fields.ContainsKey("Short Description"));
    }
}
