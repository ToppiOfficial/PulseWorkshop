using System.Text.Json;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// Persisted state for the Model View tab (the chosen game and the last-opened .mdl). Best-effort
/// load/save that never throws - a missing or corrupt file just seeds defaults (mirrors
/// <see cref="CompileConfig"/> / <c>UiSettings</c>).
/// </summary>
public sealed class ModelViewConfig
{
    /// <summary>The last .mdl that was open in the viewer tab.</summary>
    public string MdlPath { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ModelViewConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ModelViewFile))
            {
                var loaded = JsonSerializer.Deserialize<ModelViewConfig>(
                    File.ReadAllText(AppPaths.ModelViewFile), Options);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Missing or corrupt - start with defaults.
        }

        return new ModelViewConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.ModelViewFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist Model View settings must never disrupt the app.
        }
    }
}
