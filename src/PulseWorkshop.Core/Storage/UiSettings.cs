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

    /// <summary>Where in <see cref="UnpackLastArchive"/> the user was: the browsed folder and the
    /// highlighted file, both archive-relative. Only restored when the same archive is reopened.</summary>
    public string? UnpackLastFolder { get; set; }

    public string? UnpackLastFile { get; set; }

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

    /// <summary>When true, the Unpack hover preview honors a texture's alpha channel (transparent
    /// areas show the checkerboard). When false it forces the preview opaque - useful because many
    /// Source 2/VTF masks (metalness, roughness, ...) store data with alpha 0, which would otherwise
    /// render the thumbnail invisible. Defaults to opaque so every preview is visible out of the box.</summary>
    public bool UnpackPreviewAlpha { get; set; }

    /// <summary>When true, the Unpack tab's 3D model preview draws the bind-pose skeleton over the
    /// mesh as an x-ray. Off by default - most previews are of what the model looks like, not how it
    /// is rigged.</summary>
    public bool UnpackShowSkeleton { get; set; }

    /// <summary>The same switch for the Textures tab's match grid, where the sources are .psd/.tga/
    /// .dds/... rather than already-converted textures. Off by default for the same reason: a Source
    /// texture's alpha is usually a mask, and honoring it leaves an otherwise fine image invisible.</summary>
    public bool TexturePreviewAlpha { get; set; }

    /// <summary>Whether the Unpack tab's Explorer-style Details pane (thumbnail + file info on the
    /// right of the file list) is shown. Defaults to on.</summary>
    public bool UnpackDetailsPaneOpen { get; set; } = true;

    /// <summary>The width in pixels of the Unpack Details pane, so the user's resize (via its
    /// splitter) is remembered across sessions.</summary>
    public double UnpackDetailsPaneWidth { get; set; } = 260;

    /// <summary>Height in pixels of the Unpack Details pane's preview box - the texture thumbnail and
    /// the 3D model viewer share it, and it is dragged taller from the grip beneath it.</summary>
    public double UnpackDetailsThumbHeight { get; set; } = 180;

    /// <summary>Whether the Package - Advanced tab's read-only content tree (right of the entry
    /// editor) is shown. Defaults to on. The Simple tab's tree is always shown.</summary>
    public bool PackageTreePaneOpen { get; set; } = true;

    /// <summary>The width in pixels of that content tree pane (remembers the splitter drag).</summary>
    public double PackageTreePaneWidth { get; set; } = 340;

    /// <summary>Whether the Compile - Advanced tab's read-only .qc preview (right of the entry
    /// editor) is shown. Defaults to on.</summary>
    public bool CompileQcPaneOpen { get; set; } = true;

    /// <summary>The width in pixels of that .qc preview pane (remembers the splitter drag).</summary>
    public double CompileQcPaneWidth { get; set; } = 360;

    /// <summary>Splitter pane sizes, keyed by the <c>PaneSize.Key</c> tag on the grid definition
    /// (e.g. <c>"unpack.tree"</c> -> <c>"320"</c>). Values are GridLength text so star-sized panes
    /// round-trip as "2.4*". Unknown keys are simply ignored, so retagging the UI is harmless.</summary>
    public Dictionary<string, string> PaneSizes { get; set; } = new();

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
