using System.Text.Json;

namespace PulseWorkshop.Core.Storage;

/// <summary>
/// Tiny app-level pointer to the user's Advanced-compile projects (which live in their own folders,
/// not here). Remembers the last opened project so the tab can reopen it, plus a most-recent list.
/// Kept separate from <see cref="UiSettings"/> so the two never clobber each other's file.
/// Best-effort load/save that never throws.
/// </summary>
public sealed class AdvancedCompileConfig
{
    private const int MaxRecent = 10;

    /// <summary>The <c>.pw_mdlproject</c> path to reopen on launch, or null.</summary>
    public string? LastProjectPath { get; set; }

    /// <summary>Most-recently-opened project paths, newest first.</summary>
    public List<string> RecentProjects { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AdvancedCompileConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.AdvancedCompileFile))
            {
                var loaded = JsonSerializer.Deserialize<AdvancedCompileConfig>(
                    File.ReadAllText(AppPaths.AdvancedCompileFile), Options);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Missing or corrupt - start with defaults.
        }
        return new AdvancedCompileConfig();
    }

    /// <summary>Records a project as the current/most-recent one and saves.</summary>
    public void Remember(string projectPath)
    {
        LastProjectPath = projectPath;
        RecentProjects.RemoveAll(p => string.Equals(p, projectPath, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, projectPath);
        if (RecentProjects.Count > MaxRecent)
            RecentProjects.RemoveRange(MaxRecent, RecentProjects.Count - MaxRecent);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.AdvancedCompileFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist must never disrupt the app.
        }
    }
}
