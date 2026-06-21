using System.Text.Json;

namespace PulseWorkshop.Core.Storage;

/// <summary>
/// Persisted UI preferences (currently the console drawer's open state and height). Stored as a
/// single <c>settings.json</c>; load and save are best-effort and never throw - a missing or corrupt
/// file just yields defaults.
/// </summary>
public sealed class UiSettings
{
    /// <summary>Whether the console drawer was open when the app last closed.</summary>
    public bool ConsoleVisible { get; set; }

    /// <summary>The console drawer's height in pixels (remembers the user's drag).</summary>
    public double ConsoleHeight { get; set; } = 180;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(AppPaths.SettingsFile))
                    ?? new UiSettings();
        }
        catch
        {
            // Missing or corrupt settings - fall back to defaults.
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist UI prefs must never disrupt the app.
        }
    }
}
