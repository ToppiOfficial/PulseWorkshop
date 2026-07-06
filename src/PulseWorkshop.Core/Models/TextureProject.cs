using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// One Textures project, persisted as a <c>&lt;name&gt;.pw_textureproject</c> file (plain JSON) inside a
/// user-chosen folder. It converts loose image files (PNG/TGA/...) into Source <c>.vtf</c> textures in
/// bulk, driven by regex/literal patterns matched against a source folder - the successor to
/// KitsuneResource's <c>ValveTexturePipeline</c>. Each <see cref="TextureGroup"/> is a pattern + output
/// rule; the actual VTF encode is delegated to the selected game's VTF tool (see
/// <c>TextureConversionService</c>). Load/save are best-effort and never throw.
/// </summary>
public sealed class TextureProject
{
    /// <summary>The Game Setup entry this project converts against - supplies the VTF tool path and its
    /// command template (resolved back to a game on load).</summary>
    public Guid? GameId { get; set; }

    /// <summary>The root folder scanned for input images, relative to the project file's folder or
    /// absolute. Blank means the project file's own folder.</summary>
    public string SourceFolder { get; set; } = string.Empty;

    /// <summary>When true, skip a file whose <c>.vtf</c> already exists and is newer than the source
    /// (an incremental re-run). "Force" on a convert overrides this per run.</summary>
    public bool SkipUpToDate { get; set; } = true;

    /// <summary>Extra VTF arguments applied to <b>every</b> group, appended after the game's base VTF
    /// command and before each group's own command. Supports the same placeholders
    /// (<c>{input} {output} {outputdir} {outputname}</c>).</summary>
    public string GlobalVtfCommand { get; set; } = string.Empty;

    /// <summary>The texture groups, in run order (the UI lets the user reorder them).</summary>
    public List<TextureGroup> Groups { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Loads a project from the given <c>.pw_textureproject</c> path; null if missing or corrupt.</summary>
    public static TextureProject? Load(string projectFilePath)
    {
        try
        {
            if (File.Exists(projectFilePath))
                return JsonSerializer.Deserialize<TextureProject>(File.ReadAllText(projectFilePath), Options);
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

/// <summary>
/// One texture group in a Textures project: a pattern that selects source images under the project's
/// source folder, plus where their converted <c>.vtf</c> files are written and any per-group VTF
/// arguments. Runs one at a time; the UI can reorder them.
/// </summary>
public sealed class TextureGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name for the group (defaults to the pattern when added).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The pattern matched against each candidate file's <b>name</b> (not its full path). A
    /// regex when <see cref="IsRegex"/> is set; otherwise a case-insensitive substring of the name.</summary>
    public string InputPattern { get; set; } = @"\.(png|tga|jpg|jpeg|bmp|tiff?)$";

    /// <summary>Treat <see cref="InputPattern"/> as a regular expression (default). When false it is a
    /// plain case-insensitive substring - handy for a literal name fragment with regex metacharacters.</summary>
    public bool IsRegex { get; set; } = true;

    /// <summary>Search sub-folders of the source folder recursively (default). When false only the
    /// source folder's top level is scanned.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>Where converted <c>.vtf</c> files go, relative to the project's source folder. Blank
    /// writes each <c>.vtf</c> beside its source; otherwise the source tree is mirrored under this
    /// sub-folder. It can never escape the source folder.</summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>Per-group extra VTF arguments, appended after the game's base command and the project's
    /// global command. Same placeholders (<c>{input} {output} {outputdir} {outputname}</c>).</summary>
    public string VtfCommand { get; set; } = string.Empty;

    /// <summary>Whether "Convert all" includes this group.</summary>
    public bool IncludeInAll { get; set; } = true;

    /// <summary>A deep copy with a fresh <see cref="Id"/> (for "clone group").</summary>
    public TextureGroup Clone() => new()
    {
        Name = Name,
        InputPattern = InputPattern,
        IsRegex = IsRegex,
        Recursive = Recursive,
        OutputDir = OutputDir,
        VtfCommand = VtfCommand,
        IncludeInAll = IncludeInAll,
    };
}
