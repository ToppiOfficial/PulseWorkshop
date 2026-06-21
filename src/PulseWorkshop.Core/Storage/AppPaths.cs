namespace PulseWorkshop.Core.Storage;

/// <summary>Resolves the per-user data locations used for drafts and templates.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PulseWorkshop");

    public static string DraftsDir { get; } = Path.Combine(Root, "drafts");

    public static string TemplatesDir { get; } = Path.Combine(Root, "templates");

    /// <summary>Ensures the data directories exist; safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DraftsDir);
        Directory.CreateDirectory(TemplatesDir);
    }
}
