using System.IO;
using System.Text.Json;

namespace AppCenter.Services;

public sealed class Settings
{
    public string Theme { get; set; } = ThemeService.DefaultId;

    /// <summary>
    /// Whether to ask GitHub for a newer App Center on launch. One request to
    /// api.github.com per start; off, About still checks when asked.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Where the main window was when it was last closed. Null until it has
    /// been closed once.
    /// </summary>
    public WindowPlacement? Window { get; set; }
}

/// <summary>
/// The handful of preferences that outlive a session, kept beside the icon
/// cache in LocalAppData. Nothing here is worth failing a launch over, so
/// every path degrades to the defaults.
/// </summary>
public static class SettingsService
{
    private static Settings? _cached;
    private static Task<Settings>? _loading;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppCenter",
        "settings.json");

    /// <summary>
    /// The settings, read on first use - or already read, if <see cref="Preload"/>
    /// was called in time.
    /// </summary>
    public static Settings Current => _cached ??= _loading is { } loading ? loading.Result : Load();

    /// <summary>
    /// Starts reading the file on a thread-pool thread. The theme is needed
    /// before the first pixel, and this puts the disk read alongside the
    /// launch rather than in it.
    /// </summary>
    public static void Preload() => _loading ??= Task.Run(Load);

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, AppJsonContext.Default.Settings));
        }
        catch (Exception)
        {
            // A read-only or roaming-blocked profile means the choice just
            // does not survive the session.
        }
    }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), AppJsonContext.Default.Settings) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable: start from the defaults.
        }

        return new Settings();
    }
}
