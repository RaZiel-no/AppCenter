using System.IO;
using System.Text.Json;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// Lightweight patching runs each named one-off change once per machine
/// and keeps a record of which ran and when. These pin down the once, the
/// record, what a failing patch does to the others, and what an unreadable
/// record means. Run against a record file of their own, never the real one.
/// </summary>
public class LightweightPatchingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AppCenter.Tests." + Guid.NewGuid().ToString("N"));

    private string RecordPath => Path.Combine(_dir, "patches.json");

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

    [Fact]
    public void Runs_each_patch_once_and_records_when()
    {
        var runs = 0;
        var patches = new[] { new Patch("first", () => runs++) };
        var before = DateTime.UtcNow;

        Assert.Equal(["first"], LightweightPatching.Run(patches, RecordPath));
        Assert.Empty(LightweightPatching.Run(patches, RecordPath));
        Assert.Equal(1, runs);

        using var document = JsonDocument.Parse(File.ReadAllText(RecordPath));
        var when = document.RootElement.GetProperty("Applied").GetProperty("first").GetDateTime().ToUniversalTime();
        Assert.InRange(when, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Runs_only_what_the_record_does_not_list_in_order()
    {
        var order = new List<string>();
        LightweightPatching.Run([new Patch("first", () => order.Add("first"))], RecordPath);

        var applied = LightweightPatching.Run(
        [
            new Patch("first", () => order.Add("first again")),
            new Patch("second", () => order.Add("second")),
            new Patch("third", () => order.Add("third")),
        ], RecordPath);

        Assert.Equal(["second", "third"], applied);
        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void A_patch_that_fails_is_not_recorded_and_the_rest_still_run()
    {
        var attempts = 0;
        Patch[] patches =
        [
            new("breaks", () => { attempts++; throw new IOException("locked"); }),
            new("works", () => { }),
        ];

        Assert.Equal(["works"], LightweightPatching.Run(patches, RecordPath));

        // Tried again at the next launch; the one that worked is not.
        Assert.Empty(LightweightPatching.Run(patches, RecordPath));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Writes_no_record_when_there_was_nothing_to_do()
    {
        Assert.Empty(LightweightPatching.Run([], RecordPath));
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void Treats_an_unreadable_record_as_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath, "not json at all");
        var runs = 0;

        Assert.Equal(["first"], LightweightPatching.Run([new Patch("first", () => runs++)], RecordPath));
        Assert.Equal(1, runs);

        // And leaves a good record behind.
        Assert.Empty(LightweightPatching.Run([new Patch("first", () => runs++)], RecordPath));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Keeps_what_the_record_already_held()
    {
        LightweightPatching.Run([new Patch("first", () => { })], RecordPath);
        LightweightPatching.Run([new Patch("second", () => { })], RecordPath);

        using var document = JsonDocument.Parse(File.ReadAllText(RecordPath));
        var applied = document.RootElement.GetProperty("Applied");
        Assert.True(applied.TryGetProperty("first", out _));
        Assert.True(applied.TryGetProperty("second", out _));
    }
}
