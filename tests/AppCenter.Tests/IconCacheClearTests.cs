using System.IO;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The icon cache can be cleared of everything written before a moment -
/// the first lightweight patch does that - and these pin down that only what
/// is older goes, that it is safe to do twice, and that a cache that is not
/// there is no error. Run over a folder of their own, never the real cache.
/// </summary>
public class IconCacheClearTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AppCenter.Tests." + Guid.NewGuid().ToString("N"));

    public IconCacheClearTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // A temp folder left behind is Windows' to clear.
        }
    }

    private string Put(string name, DateTime writtenAt)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, writtenAt);
        return path;
    }

    [Fact]
    public void Discards_what_was_written_before_the_moment_and_keeps_the_rest()
    {
        var old = Put("Old.App.v3.png", Cutoff.AddDays(-30));
        var oldMiss = Put("Old.Miss.v4.none", Cutoff.AddMinutes(-1));
        var fresh = Put("New.App.v3.png", Cutoff.AddMinutes(1));
        var atTheMoment = Put("Exact.App.v3.png", Cutoff);

        Assert.Equal(2, IconService.ClearCacheBefore(_dir, Cutoff));

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldMiss));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(atTheMoment));
    }

    [Fact]
    public void Is_safe_to_run_twice()
    {
        Put("Old.App.v3.png", Cutoff.AddDays(-1));
        var fresh = Put("New.App.v3.png", Cutoff.AddHours(1));

        Assert.Equal(1, IconService.ClearCacheBefore(_dir, Cutoff));
        Assert.Equal(0, IconService.ClearCacheBefore(_dir, Cutoff));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void A_cache_that_is_not_there_is_nothing_to_clear()
    {
        Assert.Equal(0, IconService.ClearCacheBefore(Path.Combine(_dir, "missing"), Cutoff));
    }
}
