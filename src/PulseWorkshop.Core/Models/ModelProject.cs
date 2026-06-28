using System.Text.Json;
using System.Text.Json.Serialization;
using PulseWorkshop.Core.Services;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// One Advanced-compile project, persisted as a <c>&lt;name&gt;.pw_mdlproject</c> file (plain JSON)
/// inside a user-chosen folder rather than the app's <c>%AppData%</c>. It owns its own game
/// selection (independent of the Simple tab and of other projects) plus an ordered list of model
/// entries that are compiled one at a time. Load/save are best-effort and never throw.
/// </summary>
public sealed class ModelProject
{
    /// <summary>The Game Setup entry this project compiles against (resolved back to a game on load).</summary>
    public Guid? GameId { get; set; }

    /// <summary>studiomdl options shared by every entry; each entry's own command is appended after it.</summary>
    public string GlobalCommand { get; set; } = string.Empty;

    /// <summary>When true, materials are copied after each successful entry compile.</summary>
    public bool GetMaterialOnCompile { get; set; }

    /// <summary>When true, VTF files are placed beside their VMT instead of the game hierarchy.</summary>
    public bool LocalizeMaterials { get; set; }

    /// <summary>When true, Patch VMTs are flattened into their base shader.</summary>
    public bool FlatPatchShader { get; set; }

    /// <summary>When true, the materials/ folder is written to <see cref="MaterialsOutputDir"/> (a folder
    /// under the project root) instead of beside the compiled models in the entry's output folder.</summary>
    public bool UseCustomMaterialsDir { get; set; }

    /// <summary>Destination folder for materials when <see cref="UseCustomMaterialsDir"/> is set. Always
    /// relative to the project root; absolute or outside-project paths are rejected. Empty means the
    /// project root itself.</summary>
    public string MaterialsOutputDir { get; set; } = string.Empty;

    /// <summary>When true, the previous in-game build is deleted before a non-in-game compile so stale
    /// files can't leak into the moved output.</summary>
    public bool CleanBeforeTransfer { get; set; }

    /// <summary>The model entries, in compile order (significant - the UI lets the user reorder them).</summary>
    public List<ModelEntry> Entries { get; set; } = new();

    /// <summary>The package entries (Package - Advanced tab), in package order. Stored alongside the
    /// compile <see cref="Entries"/> in the same project file; the two tabs share one project but
    /// never touch each other's list.</summary>
    public List<PackageEntry> PackageEntries { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Loads a project from the given <c>.pw_mdlproject</c> path; null if missing or corrupt.</summary>
    public static ModelProject? Load(string projectFilePath)
    {
        try
        {
            if (File.Exists(projectFilePath))
                return JsonSerializer.Deserialize<ModelProject>(File.ReadAllText(projectFilePath), Options);
        }
        catch
        {
            // Missing or corrupt - the caller decides what to do (usually: treat as "no project").
        }
        return null;
    }

    /// <summary>Writes the project to the given path. Best-effort: never throws.</summary>
    public void Save(string projectFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(projectFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(projectFilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist a project must never disrupt the app.
        }
    }
}

/// <summary>One model in an Advanced project: a .qc plus its per-entry compile options and output.</summary>
public sealed class ModelEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name for the entry (defaults to the .qc's file name when added).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Path to the .qc, relative to the project file's folder, or absolute.</summary>
    public string QcPath { get; set; } = string.Empty;

    /// <summary>Whether "Compile all" includes this entry.</summary>
    public bool CompileInAll { get; set; } = true;

    /// <summary>studiomdl options unique to this entry, appended after the project's global command.</summary>
    public string Command { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompileOutputMode OutputMode { get; set; } = CompileOutputMode.Subfolder;

    /// <summary>Output folder for <see cref="CompileOutputMode.WorkFolder"/>: relative to the project
    /// folder, or absolute.</summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>Subfolder name (under the project folder) for <see cref="CompileOutputMode.Subfolder"/>.</summary>
    public string SubfolderName { get; set; } = string.Empty;

    /// <summary>A deep copy with a fresh <see cref="Id"/> (for "clone entry").</summary>
    public ModelEntry Clone() => new()
    {
        Name = Name,
        QcPath = QcPath,
        CompileInAll = CompileInAll,
        Command = Command,
        OutputMode = OutputMode,
        OutputDir = OutputDir,
        SubfolderName = SubfolderName,
    };
}
