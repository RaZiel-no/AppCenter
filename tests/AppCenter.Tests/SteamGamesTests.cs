using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// Steam games are handed to Steam rather than to winget, which waits on the
/// Steam client for as long as it runs and holds up every other install while
/// it does. Which rows count as one is decided from winget's id alone, so that
/// reading is what these hold to account.
/// </summary>
public class SteamGamesTests
{
    [Theory]
    [InlineData(@"ARP\Machine\X64\Steam App 730", "730")]
    [InlineData(@"ARP\Machine\X86\Steam App 1172470", "1172470")]
    [InlineData(@"ARP\User\X64\Steam App 440", "440")]
    public void Reads_the_game_from_the_id_steam_registered(string id, string expected)
    {
        Assert.Equal(expected, SteamGames.AppIdOf(id));
    }

    [Theory]
    [InlineData("Valve.Steam")]
    [InlineData(@"ARP\Machine\X64\Steam")]
    [InlineData(@"ARP\Machine\X64\Steam App")]
    [InlineData(@"ARP\Machine\X64\Steam App 730 Beta")]
    [InlineData(@"ARP\Machine\X64\{E4A5B7C2-0000-0000-0000-000000000000}")]
    [InlineData("Overwolf.CurseForge")]
    public void Leaves_everything_else_to_winget(string id)
    {
        // Steam itself included: it has an uninstaller of its own, and winget
        // runs that like any other.
        Assert.Null(SteamGames.AppIdOf(id));
    }

    [Fact]
    public void Says_that_steam_is_where_it_will_be_confirmed()
    {
        var question = SteamGames.UninstallQuestion("Counter-Strike 2");

        Assert.Equal("Uninstall Counter-Strike 2?", question.Title);
        Assert.Contains("Steam will open", question.Message);
        Assert.Equal("Open Steam", question.Confirm);
    }
}
