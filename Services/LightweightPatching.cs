using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AppCenter.Services;

/// <summary>
/// A one-off change to what the app keeps on disk, with the name the record
/// knows it by. What it does has to be safe to do twice: the record is the
/// only thing that keeps it to once, and a record that cannot be read or
/// written is treated as empty rather than as a reason not to patch.
/// </summary>
internal sealed record Patch(string Name, Action Apply);

/// <summary>What has been patched on this machine: each patch's name, and when it ran, in UTC.</summary>
public sealed class PatchRecord
{
    public Dictionary<string, DateTime> Applied { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Lightweight patching: the one-off changes a new build has to make to what
/// an older one left on disk - a cache to clear, a file to move - run once
/// per machine, at the start of warm-up, before anything reads what they
/// change. Each patch has a name, and patches.json beside the settings
/// records the names that have run and when, which is what makes each run
/// once. A patch that throws is not recorded and is tried again at the next
/// launch; the others still run.
/// </summary>
public static class LightweightPatching
{
    public static string RecordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppCenter",
        "patches.json");

    /// <summary>Every patch there has been, oldest first. A name, once recorded, is never reused.</summary>
    private static readonly Patch[] Patches =
    [
        // The manifest's own icons came ahead of the homepage in the icon
        // hunt on 2026-10-19; what was on disk from homepages would have kept
        // the earlier choice on every machine that had run the app before.
        new("clear-icon-cache-before-2026-10-19",
            () => IconService.ClearCacheBefore(IconService.CacheDirectory, new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Utc))),
    ];

    /// <summary>Runs whichever of the patches this machine has not had.</summary>
    public static void Run() => Run(Patches, RecordPath);

    /// <summary>
    /// Runs the patches the record at <paramref name="recordPath"/> does not
    /// list, in order, recording each that succeeds. Returns the names that
    /// ran this time.
    /// </summary>
    internal static IReadOnlyList<string> Run(IReadOnlyList<Patch> patches, string recordPath)
    {
        var record = Load(recordPath);
        var applied = new List<string>();

        foreach (var patch in patches)
        {
            if (record.Applied.ContainsKey(patch.Name))
                continue;

            try
            {
                patch.Apply();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Patch {patch.Name} failed and will be tried again: {ex.Message}");
                continue;
            }

            record.Applied[patch.Name] = DateTime.UtcNow;
            applied.Add(patch.Name);
        }

        if (applied.Count > 0)
            Save(record, recordPath);

        return applied;
    }

    private static PatchRecord Load(string recordPath)
    {
        try
        {
            if (!File.Exists(recordPath))
                return new PatchRecord();

            return JsonSerializer.Deserialize(File.ReadAllBytes(recordPath), AppJsonContext.Default.PatchRecord) ?? new PatchRecord();
        }
        catch (Exception)
        {
            // An unreadable record is no record: the patches run again, which
            // each of them is written to withstand, and a good record is
            // written after.
            return new PatchRecord();
        }
    }

    private static void Save(PatchRecord record, string recordPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, AppJsonContext.Default.PatchRecord));
        }
        catch (Exception)
        {
            // A read-only or roaming-blocked profile: the patches run again
            // next launch, to the same end.
        }
    }
}
