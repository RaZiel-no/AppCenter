using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// winget has no machine-readable output for list/upgrade/search, so everything
/// the app knows about a package comes out of the fixed-width table it prints
/// for a terminal. These pin down what the parser promises: that a name keeps
/// its spaces, that a column it cannot name it can still find, and that the
/// prose winget prints around the table never becomes a package.
/// </summary>
public class WingetTableTests
{
    // Columns start at 0, 21, 43, 56 and 69 - which is the only thing the
    // parser reads the header for.
    private const string Upgrades =
        """
        Name                 Id                    Version      Available    Source
        ---------------------------------------------------------------------------
        7-Zip 22.01          7zip.7zip             22.01        26.02        winget
        Docker Desktop       Docker.DockerDesktop  4.77.0       4.85.0       winget
        Git                  Git.Git               2.47.0.2     2.55.0.3     winget

        22 upgrades available.
        """;

    [Fact]
    public void Reads_every_row_of_the_table()
    {
        var rows = WingetService.ParseTable(Upgrades);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Keeps_the_spaces_inside_a_name()
    {
        var rows = WingetService.ParseTable(Upgrades);

        Assert.Equal("7-Zip 22.01", rows[0].Name);
        Assert.Equal("Docker Desktop", rows[1].Name);
    }

    [Fact]
    public void Reads_every_column_of_a_row()
    {
        var row = WingetService.ParseTable(Upgrades)[0];

        Assert.Equal("7zip.7zip", row.Id);
        Assert.Equal("22.01", row.Version);
        Assert.Equal("26.02", row.Available);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void Keeps_the_order_winget_printed()
    {
        var rows = WingetService.ParseTable(Upgrades);

        // "Update all" walks the list in the order it is shown, and the list is
        // shown in the order it is parsed. Sorting here would silently decide
        // which package gets installed first.
        Assert.Equal(["7zip.7zip", "Docker.DockerDesktop", "Git.Git"], rows.Select(r => r.Id));
    }

    [Fact]
    public void Stops_at_the_blank_line_before_the_summary()
    {
        var rows = WingetService.ParseTable(Upgrades);

        // "22 upgrades available." sits below a blank line and is prose, not a
        // package - it must never arrive as a row with a name and no id.
        Assert.DoesNotContain(rows, r => r.Name.Contains("upgrades available"));
    }

    [Fact]
    public void Reads_a_table_that_has_no_available_column()
    {
        // `winget list` prints this shape: no upgrade to offer, so no column.
        const string installed =
            """
            Name                 Id                    Version      Source
            ------------------------------------------------------------
            Git                  Git.Git               2.47.0.2     winget
            """;

        var row = Assert.Single(WingetService.ParseTable(installed));

        Assert.Equal("Git.Git", row.Id);
        Assert.Equal("2.47.0.2", row.Version);
        Assert.Equal("winget", row.Source);
        Assert.Equal(string.Empty, row.Available);
    }

    [Fact]
    public void Falls_back_to_column_position_when_the_headers_are_not_english()
    {
        // A localised winget names its columns in its own language, so every
        // column of `upgrade` is found by where it sits. Without that the update
        // column comes back empty, and a German machine is shown no updates.
        const string german =
            """
            Name                 ID                    Version      Verfügbar    Quelle
            ---------------------------------------------------------------------------
            7-Zip 22.01          7zip.7zip             22.01        26.02        winget
            """;

        var row = Assert.Single(WingetService.ParseTable(german, installedTable: true));

        Assert.Equal("7-Zip 22.01", row.Name);
        Assert.Equal("7zip.7zip", row.Id);
        Assert.Equal("22.01", row.Version);
        Assert.Equal("26.02", row.Available);
        Assert.Equal("winget", row.Source);
    }

    [Fact]
    public void Finds_the_source_of_a_localised_list_with_no_updates_in_it()
    {
        // `list` drops Available when nothing has an update; Source is still last.
        const string german =
            """
            Name                 ID                    Version      Quelle
            ------------------------------------------------------------
            Git                  Git.Git               2.47.0.2     winget
            """;

        var row = Assert.Single(WingetService.ParseTable(german, installedTable: true));

        Assert.Equal("winget", row.Source);
        Assert.Equal(string.Empty, row.Available);
    }

    [Fact]
    public void Does_not_guess_at_the_columns_of_a_localised_search()
    {
        // A search's fourth column is Match, not Available, and Source is left
        // out when one source was asked for. Guessing would put the match in as
        // the source.
        const string french =
            """
            Nom                  Identifiant           Version      Correspondance
            ---------------------------------------------------------------------------
            Sept-Zip             7zip.7zip             22.01        Tag: zip
            """;

        var row = Assert.Single(WingetService.ParseTable(french));

        Assert.Equal("Sept-Zip", row.Name);
        Assert.Equal("7zip.7zip", row.Id);
        Assert.Equal("22.01", row.Version);
        Assert.Equal(string.Empty, row.Available);
        Assert.Equal(string.Empty, row.Source);
    }

    [Fact]
    public void Reads_nothing_from_output_that_has_no_table()
    {
        const string refusal =
            """
            No installed package found matching input criteria.
            """;

        Assert.Empty(WingetService.ParseTable(refusal));
    }

    [Fact]
    public void Reads_nothing_when_the_dashes_are_too_short_to_be_a_separator()
    {
        // Short runs of dashes turn up in package names and in winget's prose.
        // Treating one as the table's separator would read the line above it as
        // a header and invent columns.
        const string notATable =
            """
            Name                 Id
            ---
            Git                  Git.Git
            """;

        Assert.Empty(WingetService.ParseTable(notATable));
    }

    [Fact]
    public void Leaves_a_cell_empty_when_the_row_stops_short_of_it()
    {
        // A row that ends early - no source, say - must not reach past the end
        // of its own line.
        const string ragged =
            """
            Name                 Id                    Version      Available    Source
            ---------------------------------------------------------------------------
            Git                  Git.Git               2.47.0.2
            """;

        var row = Assert.Single(WingetService.ParseTable(ragged));

        Assert.Equal("2.47.0.2", row.Version);
        Assert.Equal(string.Empty, row.Available);
        Assert.Equal(string.Empty, row.Source);
    }

    [Fact]
    public void Skips_a_row_with_neither_a_name_nor_an_id()
    {
        const string withPadding =
            """
            Name                 Id                    Version      Available    Source
            ---------------------------------------------------------------------------
                                                       22.01        26.02        winget
            Git                  Git.Git               2.47.0.2     2.55.0.3     winget
            """;

        var row = Assert.Single(WingetService.ParseTable(withPadding));

        Assert.Equal("Git.Git", row.Id);
    }
}
