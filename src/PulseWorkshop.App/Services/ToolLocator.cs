using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace PulseWorkshop.App.Services;

/// <summary>Resolves the path to the ModelTool native helper executable.</summary>
public static class ToolLocator
{
    public const string ModelToolExeName = "PulseWorkshop.ModelTool.exe";

    /// <summary>
    /// Locates the ModelTool executable. Resolution order:
    /// <list type="number">
    ///   <item>Single-file build: the exe is embedded in this assembly as a resource named
    ///         <see cref="ModelToolExeName"/>; it is extracted to a length-stamped cache under
    ///         %LocalAppData%\PulseWorkshop\tools\ and that path is returned.</item>
    ///   <item>Packaged folder build: the exe sits next to the App.</item>
    ///   <item>Local development: walks up to the src\ folder and searches the ModelTool build output.</item>
    /// </list>
    /// </summary>
    public static string ResolveModelToolPath()
    {
        var baseDir = AppContext.BaseDirectory;

        if (TryExtractEmbeddedTool(out var extracted))
            return extracted;

        var packaged = Path.Combine(baseDir, ModelToolExeName);
        if (File.Exists(packaged))
            return packaged;

        foreach (var candidate in EnumerateDevCandidates(baseDir))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return packaged; // caller surfaces a clear "tool not found" error
    }

    /// <summary>
    /// In a single-file build the ModelTool exe is embedded as a manifest resource. Extract it once
    /// to a content-stamped cache folder under %LocalAppData% and reuse it on later launches.
    /// The cache key is a hash of the embedded bytes, not their length: a rebuilt tool that happens
    /// to keep the same file size (PE output is aligned/padded, so small source changes often do)
    /// would otherwise collide with the stale copy and never be picked up. Unlike SteamHost, this
    /// is a pure native C++ exe with no self-extraction step of its own.
    /// </summary>
    private static bool TryExtractEmbeddedTool(out string exePath)
    {
        exePath = string.Empty;

        var asm = Assembly.GetExecutingAssembly();
        var resourceName = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith(ModelToolExeName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return false;

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return false;

        // Read the embedded bytes once and key the cache on their content hash.
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        var bytes = mem.ToArray();
        var hash  = Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16).ToLowerInvariant();

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseWorkshop", "tools", hash);
        var target = Path.Combine(cacheDir, ModelToolExeName);

        if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
        {
            Directory.CreateDirectory(cacheDir);
            var temp = Path.Combine(cacheDir, ModelToolExeName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temp, bytes);

            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another launch won the race (or the target is locked by an equivalent copy).
                File.Delete(temp);
            }
        }

        exePath = target;
        return true;
    }

    private static IEnumerable<string> EnumerateDevCandidates(string appBaseDir)
    {
        // Walk up to the src\ folder, then into the ModelTool C++ build output.
        var dir = new DirectoryInfo(appBaseDir);
        while (dir is not null && !string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;

        if (dir is null)
            yield break;

        // ModelTool is a pure C++ project: output is bin\x64\<Configuration>\
        var toolBin = Path.Combine(dir.FullName, "PulseWorkshop.ModelTool", "bin", "x64");
        if (!Directory.Exists(toolBin))
            yield break;

        var matches = Directory.EnumerateFiles(toolBin, ModelToolExeName, SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.LastWriteTimeUtc);

        foreach (var fi in matches)
            yield return fi.FullName;
    }
}
