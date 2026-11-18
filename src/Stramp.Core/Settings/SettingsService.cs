using System.Text.Json;

namespace Stramp.Core.Settings;

/// <summary>Loads/saves AppSettings as JSON under the platform's per-user config directory.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Where settings and caches live. STRAMP_CONFIG_DIR redirects it, so test runs
    /// (stress tests against a huge synthetic library) never touch real data.</summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("STRAMP_CONFIG_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stramp");

    private static string SettingsPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings: fall back to defaults rather than crash the app.
        }

        // No config.json existed (or it was unreadable) - a genuinely fresh install, so the setup
        // wizard should run. AppSettings.SetupCompleted defaults to true for every other path here,
        // so an existing, merely-missing-this-field config never retriggers it. ArtColorMode is set
        // explicitly too: leaving it null would fall back to Inferred (see ThemeSettingsViewModel's
        // constructor), but Direct is the better first impression - sharper, more specific colors
        // rather than a generic palette guess.
        return new AppSettings { SetupCompleted = false, ArtColorMode = AlbumArtColorMode.Direct };
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
