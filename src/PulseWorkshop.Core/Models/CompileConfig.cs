using System.Text.Json;
using System.Text.Json.Serialization;
using PulseWorkshop.Core.Services;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// Persisted state for the Compile tab (the chosen game, .qc, options and output destination).
/// Best-effort load/save that never throws - a missing or corrupt file just seeds defaults
/// (mirrors <see cref="GameSetupConfig"/> / <c>UiSettings</c>).
/// </summary>
public sealed class CompileConfig
{
    /// <summary>The Game Setup entry last used to compile (resolved back to a game on load).</summary>
    public Guid? LastGameId { get; set; }

    public string QcPath { get; set; } = string.Empty;

    /// <summary>Extra studiomdl options the user typed (sanitized at compile time).</summary>
    public string ExtraOptions { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompileOutputMode OutputMode { get; set; } = CompileOutputMode.Subfolder;

    /// <summary>Subfolder name (beside the .qc) for <see cref="CompileOutputMode.Subfolder"/>.</summary>
    public string SubfolderName { get; set; } = string.Empty;

    /// <summary>Absolute destination for <see cref="CompileOutputMode.WorkFolder"/>.</summary>
    public string WorkFolderPath { get; set; } = string.Empty;

    /// <summary>When true, materials are copied after each successful compile.</summary>
    public bool GetMaterialOnCompile { get; set; }

    /// <summary>When true, VTF files are placed beside their VMT instead of the game hierarchy.</summary>
    public bool LocalizeMaterials { get; set; }

    /// <summary>When true, Patch VMTs are flattened into their base shader.</summary>
    public bool FlatPatchShader { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static CompileConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.CompileFile))
            {
                var loaded = JsonSerializer.Deserialize<CompileConfig>(
                    File.ReadAllText(AppPaths.CompileFile), Options);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Missing or corrupt - start with defaults.
        }

        return new CompileConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.CompileFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist Compile settings must never disrupt the app.
        }
    }
}
