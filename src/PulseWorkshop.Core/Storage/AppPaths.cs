namespace PulseWorkshop.Core.Storage;

/// <summary>Resolves the per-user data locations used for drafts and templates.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PulseWorkshop");

    public static string DraftsDir { get; } = Path.Combine(Root, "drafts");

    public static string TemplatesDir { get; } = Path.Combine(Root, "templates");

    /// <summary>Folder holding crash reports written by the App's global exception handlers.</summary>
    public static string CrashesDir { get; } = Path.Combine(Root, "crashes");

    /// <summary>Default destination for the Workshop -> Download tab, used until the user picks their
    /// own folder (see <see cref="UiSettings.WorkshopDownloadFolder"/>).</summary>
    public static string DownloadsDir { get; } = Path.Combine(Root, "downloads");

    /// <summary>Single JSON file holding persisted UI preferences (see <see cref="UiSettings"/>).</summary>
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    /// <summary>Single JSON file holding the Game Setup config (see <c>GameSetupConfig</c>).</summary>
    public static string GameSetupFile { get; } = Path.Combine(Root, "gamesetup.json");

    /// <summary>Single JSON file holding the Compile tab state (see <c>CompileConfig</c>).</summary>
    public static string CompileFile { get; } = Path.Combine(Root, "compile.json");

    /// <summary>Single JSON file holding the Package - Simple tab state (see <c>PackageConfig</c>).</summary>
    public static string PackageFile { get; } = Path.Combine(Root, "package.json");

    /// <summary>Single JSON file holding the Model View tab state (see <c>ModelViewConfig</c>).</summary>
    public static string ModelViewFile { get; } = Path.Combine(Root, "modelview.json");

    /// <summary>Single JSON file pointing at the last/recent Advanced compile projects
    /// (see <c>AdvancedCompileConfig</c>). The projects themselves live in their own folders.</summary>
    public static string AdvancedCompileFile { get; } = Path.Combine(Root, "compile-advanced.json");

    /// <summary>Single JSON file pointing at the last/recent Textures projects
    /// (see <c>AdvancedTextureConfig</c>). The projects themselves live in their own folders.</summary>
    public static string TextureProjectFile { get; } = Path.Combine(Root, "textures.json");

    /// <summary>Ensures the data directories exist; safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DraftsDir);
        Directory.CreateDirectory(TemplatesDir);
    }
}
