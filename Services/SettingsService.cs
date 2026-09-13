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
}

/// <summary>
/// The handful of preferences that outlive a session, kept beside the icon
/// cache in LocalAppData. Nothing here is worth failing a launch over, so
/// every path degrades to the defaults.
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static Settings? _cached;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppCenter",
        "settings.json");

    public static Settings Current => _cached ??= Load();

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, Options));
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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable: start from the defaults.
        }

        return new Settings();
    }
}
