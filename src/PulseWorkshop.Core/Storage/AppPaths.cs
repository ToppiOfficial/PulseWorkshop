namespace PulseWorkshop.Core.Storage;

/// <summary>Resolves the per-user data locations used for drafts and templates.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PulseWorkshop");

    public static string DraftsDir { get; } = Path.Combine(Root, "drafts");

    public static string TemplatesDir { get; } = Path.Combine(Root, "templates");

    /// <summary>Single JSON file holding persisted UI preferences (see <see cref="UiSettings"/>).</summary>
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    /// <summary>Single JSON file holding the Game Setup config (see <c>GameSetupConfig</c>).</summary>
    public static string GameSetupFile { get; } = Path.Combine(Root, "gamesetup.json");

    /// <summary>Single JSON file holding the Compile tab state (see <c>CompileConfig</c>).</summary>
    public static string CompileFile { get; } = Path.Combine(Root, "compile.json");

    /// <summary>Single JSON file pointing at the last/recent Advanced compile projects
    /// (see <c>AdvancedCompileConfig</c>). The projects themselves live in their own folders.</summary>
    public static string AdvancedCompileFile { get; } = Path.Combine(Root, "compile-advanced.json");

    /// <summary>Ensures the data directories exist; safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DraftsDir);
        Directory.CreateDirectory(TemplatesDir);
    }
}
