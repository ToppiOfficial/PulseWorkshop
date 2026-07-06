using System.Text.Json;

namespace PulseWorkshop.Core.Storage;

/// <summary>
/// Persisted UI preferences (the detached console window's open state and bounds). Stored as a
/// single <c>settings.json</c>; load and save are best-effort and never throw - a missing or corrupt
/// file just yields defaults.
/// </summary>
public sealed class UiSettings
{
    /// <summary>Whether the console window was open when the app last closed.</summary>
    public bool ConsoleVisible { get; set; }

    /// <summary>The console window's size in pixels (remembers the user's resize).</summary>
    public double ConsoleWindowWidth { get; set; } = 900;
    public double ConsoleWindowHeight { get; set; } = 400;

    /// <summary>The console window's last position; null centres it on the main window.</summary>
    public double? ConsoleWindowLeft { get; set; }
    public double? ConsoleWindowTop { get; set; }

    /// <summary>The index of the main window's top-level tab that was active when the app last closed.
    /// Defaults to the Workshop tab (index 1; Game Setup sits at index 0) so a fresh launch opens on Workshop.</summary>
    public int MainTabIndex { get; set; } = 1;

    /// <summary>The index of the Compile tab's Simple/Advanced sub-tab active when the app last closed.</summary>
    public int CompileSubTabIndex { get; set; }

    /// <summary>The index of the Package tab's Simple/Advanced sub-tab active when the app last closed.</summary>
    public int PackageSubTabIndex { get; set; }

    /// <summary>The archive (.vpk / .gma / gameinfo.txt) that was open in the Unpack tab when the app
    /// last closed; null if none. Reopened lazily the first time the user enters the Unpack tab (not
    /// at startup) so restoring a heavy gameinfo mount doesn't slow the launch.</summary>
    public string? UnpackLastArchive { get; set; }

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
