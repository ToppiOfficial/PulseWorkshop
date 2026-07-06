using System.Text.Json;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.Core.Models;

/// <summary>
/// Persisted state for the Package - Simple tab (the chosen game, the folder to pack, an optional
/// output name and extra packer options). Best-effort load/save that never throws - a missing or
/// corrupt file just seeds defaults (mirrors <see cref="CompileConfig"/>).
/// </summary>
public sealed class PackageConfig
{
    /// <summary>The folder packed into a single <c>.vpk</c>/<c>.gma</c>.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Optional override for the produced package's file name (the packer's extension is
    /// kept when omitted). Blank keeps the packer's default name (the folder name).</summary>
    public string OutputName { get; set; } = string.Empty;

    /// <summary>Extra packer options the user typed (appended after the folder for vpk / the args for gmad).</summary>
    public string ExtraOptions { get; set; } = string.Empty;

    /// <summary>vpk only: pack into multiple chunk files (<c>-M</c>) instead of one file.</summary>
    public bool MultiVpk { get; set; }

    /// <summary>gmad only: warn about non-whitelisted files and continue instead of failing (<c>-warninvalid</c>).</summary>
    public bool IgnoreWhitelistWarnings { get; set; }

    /// <summary>Also write the packer's console output to a <c>.log</c> file beside the folder.</summary>
    public bool WriteLogToFile { get; set; }

    // --- Garry's Mod addon.json (gmad only) ---------------------------------------------------

    /// <summary>The GMod addon title written to addon.json (blank falls back to the folder name).</summary>
    public string GModTitle { get; set; } = string.Empty;

    /// <summary>The GMod addon type (canonical lowercase token, e.g. "effects"). See <see cref="GModAddon"/>.</summary>
    public string GModType { get; set; } = "effects";

    /// <summary>The chosen GMod addon tags (canonical lowercase tokens; up to two).</summary>
    public List<string> GModTags { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static PackageConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.PackageFile))
            {
                var loaded = JsonSerializer.Deserialize<PackageConfig>(
                    File.ReadAllText(AppPaths.PackageFile), Options);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Missing or corrupt - start with defaults.
        }

        return new PackageConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.PackageFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Best-effort: failing to persist Package settings must never disrupt the app.
        }
    }
}
