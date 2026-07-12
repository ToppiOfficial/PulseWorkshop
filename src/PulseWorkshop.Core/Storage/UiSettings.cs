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

    /// <summary>App-wide compile toggle shared by both Compile tabs: when true, a successful compile
    /// <b>copies</b> the model files to the output folder instead of moving them, so the just-built
    /// model also stays in the game folder. Has no effect on the "leave in game" output mode.</summary>
    public bool CompileCopyToDestination { get; set; }

    /// <summary>The archive (.vpk / .gma / gameinfo.txt) that was open in the Unpack tab when the app
    /// last closed; null if none. Reopened lazily the first time the user enters the Unpack tab (not
    /// at startup) so restoring a heavy gameinfo mount doesn't slow the launch.</summary>
    public string? UnpackLastArchive { get; set; }

    /// <summary>The archives (.vpk / .gma / gameinfo.txt) most recently opened in the Unpack tab,
    /// newest first, capped at <see cref="MaxRecentArchives"/>. Backs the empty state's "Open recent"
    /// list. Use <see cref="RememberUnpackArchive"/> to add one.</summary>
    public List<string> UnpackRecentArchives { get; set; } = new();

    private const int MaxRecentArchives = 10;

    /// <summary>Records an archive as the most-recently-opened one (moving it to the front) and saves.</summary>
    public void RememberUnpackArchive(string path)
    {
        UnpackLastArchive = path;
        UnpackRecentArchives.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        UnpackRecentArchives.Insert(0, path);
        if (UnpackRecentArchives.Count > MaxRecentArchives)
            UnpackRecentArchives.RemoveRange(MaxRecentArchives, UnpackRecentArchives.Count - MaxRecentArchives);
        Save();
    }

    /// <summary>The folder the Workshop -> Download tab writes downloaded items into; null until the
    /// user picks one (then a <c>downloads</c> subfolder under the app data root is offered as default).</summary>
    public string? WorkshopDownloadFolder { get; set; }

    /// <summary>When true, the Unpack tab exports to a fixed location beside the opened package
    /// (a gameinfo mount -> an <c>unpack_files</c> subfolder next to gameinfo.txt; a bare .vpk/.gma
    /// -> a <c>&lt;package name&gt;_unpack</c> subfolder next to it) instead of prompting for a
    /// destination folder.</summary>
    public bool UnpackExportBesidePackage { get; set; }

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
