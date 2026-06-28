using System.Text.Json.Serialization;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// One entry in a Package - Advanced project: a content folder that is packed into a single
/// <c>.vpk</c>/<c>.gma</c> by the game's packer. Unlike a compile <see cref="ModelEntry"/> it points
/// at a <b>folder</b> (a packer takes a folder, not a file) and has no output mode - the packer writes
/// the package beside the folder. Before packing, its <see cref="Assets"/> are processed and copied
/// into the folder (see <c>AssetPipelineService</c>).
/// </summary>
public sealed class PackageEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name for the entry (defaults to the folder's name when added).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Path to the folder to package, relative to the project file's folder, or absolute.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Whether "Package all" includes this entry.</summary>
    public bool IncludeInAll { get; set; } = true;

    /// <summary>Extra packer options unique to this entry, appended after the project's global command.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Pre-assets applied into the folder (copy + transform) before packing.</summary>
    public List<PackageAsset> Assets { get; set; } = new();

    /// <summary>A deep copy with a fresh <see cref="Id"/> (for "clone entry").</summary>
    public PackageEntry Clone() => new()
    {
        Name = Name,
        FolderPath = FolderPath,
        IncludeInAll = IncludeInAll,
        Command = Command,
        Assets = Assets.Select(a => a.Clone()).ToList(),
    };
}

/// <summary>Whether a <see cref="PackageAsset"/> is processed as text or as an image.</summary>
public enum AssetKind
{
    Text,
    Image,
}

/// <summary>The output format an image asset is converted to. <see cref="Vtf"/> is produced by the
/// Game Setup VTF tool; the rest by WPF's built-in encoders.</summary>
public enum ImageTargetFormat
{
    /// <summary>Keep the input format (straight copy, no re-encode).</summary>
    Copy,
    Png,
    Jpg,
    Gif,
    Bmp,
    Tiff,
    Vtf,
}

/// <summary>
/// One pre-asset baked into a package entry's folder before packing: an input file (shared, so it is
/// never mutated) that is transformed and written to an output path <b>inside</b> the entry folder.
/// Text assets run a list of <see cref="RegexReplace"/>; image assets are converted to
/// <see cref="ImageFormat"/>.
/// </summary>
public sealed class PackageAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AssetKind Kind { get; set; } = AssetKind.Text;

    /// <summary>Path to the source file, relative to the project file's folder, or absolute. Shared
    /// across entries, so the pipeline never writes back to it.</summary>
    public string InputPath { get; set; } = string.Empty;

    /// <summary>Output subfolder, relative to the entry's folder (sandboxed - it can never escape the
    /// folder). Empty means the folder root.</summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>Output file name (with extension). The extension drives the output format/encoder.</summary>
    public string OutputFileName { get; set; } = string.Empty;

    /// <summary>Find/replace passes applied (in order) to a text asset before it is written.</summary>
    public List<RegexReplace> RegexReplaces { get; set; } = new();

    /// <summary>The target format for an image asset.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ImageTargetFormat ImageFormat { get; set; } = ImageTargetFormat.Copy;

    /// <summary>Per-asset extra VTF arguments. When non-empty and <see cref="ImageFormat"/> is
    /// <see cref="ImageTargetFormat.Vtf"/>, this is appended after the Game Setup VTF command when
    /// launching the VTF tool for this asset. Supports the same placeholders.</summary>
    public string VtfCommand { get; set; } = string.Empty;

    /// <summary>A deep copy with a fresh <see cref="Id"/>.</summary>
    public PackageAsset Clone() => new()
    {
        Kind = Kind,
        InputPath = InputPath,
        OutputDir = OutputDir,
        OutputFileName = OutputFileName,
        RegexReplaces = RegexReplaces.Select(r => r.Clone()).ToList(),
        ImageFormat = ImageFormat,
        VtfCommand = VtfCommand,
    };
}

/// <summary>A single regex find/replace pass applied to a text asset.</summary>
public sealed class RegexReplace
{
    /// <summary>The regex pattern to match.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The replacement string (supports $1, ${name}, etc.).</summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>Match case-insensitively.</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>Treat the input as multiple lines (^ and $ match at line breaks).</summary>
    public bool Multiline { get; set; }

    /// <summary>When true, treat <see cref="Pattern"/> as a plain literal string instead of a regex.
    /// Useful for tokens like $CHARA$ that contain regex metacharacters.</summary>
    public bool IsLiteral { get; set; }

    public RegexReplace Clone() => new()
    {
        Pattern = Pattern,
        Replacement = Replacement,
        IgnoreCase = IgnoreCase,
        Multiline = Multiline,
        IsLiteral = IsLiteral,
    };
}
