using System.IO;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Finds the game's own vpk.exe so the material copy can tell "missing" apart from "shipped
/// inside the game's VPK archives" (a file packed in a VPK is provided natively by the game).
/// </summary>
public static class VpkLocator
{
    /// <summary>
    /// Resolution order:
    /// <list type="number">
    ///   <item>The Game Setup packer tool itself, when it already is vpk.exe.</item>
    ///   <item>A vpk.exe next to the packer tool (GMod ships vpk.exe beside gmad.exe in bin\).</item>
    ///   <item>The game's bin folders derived from gameinfo.txt: gameinfo sits in
    ///         &lt;root&gt;\&lt;mod&gt;\gameinfo.txt and vpk.exe in &lt;root&gt;\bin\ (or bin\win64\, bin\x64\).</item>
    /// </list>
    /// Returns null when no vpk.exe exists anywhere - the VPK check is then simply skipped.
    /// </summary>
    public static string? FindVpkExe(string? packerToolPath, string? gameInfoPath)
    {
        if (!string.IsNullOrWhiteSpace(packerToolPath) && File.Exists(packerToolPath))
        {
            if (string.Equals(Path.GetFileName(packerToolPath), "vpk.exe", StringComparison.OrdinalIgnoreCase))
                return packerToolPath;

            if (Path.GetDirectoryName(packerToolPath) is { Length: > 0 } packerDir)
            {
                var sibling = Path.Combine(packerDir, "vpk.exe");
                if (File.Exists(sibling))
                    return sibling;
            }
        }

        if (!string.IsNullOrWhiteSpace(gameInfoPath)
            && Path.GetDirectoryName(gameInfoPath) is { Length: > 0 } modDir
            && Path.GetDirectoryName(modDir) is { Length: > 0 } gameRoot)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(gameRoot, "bin", "vpk.exe"),
                Path.Combine(gameRoot, "bin", "win64", "vpk.exe"),
                Path.Combine(gameRoot, "bin", "x64", "vpk.exe"),
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
