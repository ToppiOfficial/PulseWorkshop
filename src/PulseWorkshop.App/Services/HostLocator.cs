using System.IO;
using System.Reflection;

namespace PulseWorkshop.App.Services;

/// <summary>Resolves the path to the SteamHost helper executable next to the App.</summary>
public static class HostLocator
{
    public const string HostExeName = "PulseWorkshop.SteamHost.exe";

    /// <summary>
    /// Locates the SteamHost executable. Resolution order:
    /// <list type="number">
    ///   <item>Single-file build: the host is embedded in this assembly as a resource named
    ///         <see cref="HostExeName"/>; it is extracted next to the user's data and that path used.</item>
    ///   <item>Packaged folder build: the host sits next to the App.</item>
    ///   <item>Local development: the host lives in its own build output, which we walk up to find.</item>
    /// </list>
    /// </summary>
    public static string ResolveHostExePath()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1) Embedded (single-file distribution): extract once, then run from the cache.
        if (TryExtractEmbeddedHost(out var extracted))
            return extracted;

        var packaged = Path.Combine(baseDir, HostExeName);
        if (File.Exists(packaged))
            return packaged;

        // Dev fallback: ...\PulseWorkshop.App\bin\<plat>\<cfg>\net10.0-windows\ ->
        //               ...\PulseWorkshop.SteamHost\bin\<plat>\<cfg>\net10.0\win-x64\
        foreach (var devCandidate in EnumerateDevCandidates(baseDir))
        {
            if (File.Exists(devCandidate))
                return devCandidate;
        }

        // Return the packaged path anyway; the caller surfaces a clear "host not found" error.
        return packaged;
    }

    /// <summary>
    /// In a single-file build the host is bundled into this assembly as an embedded resource. Extract
    /// it once to a length-stamped cache folder under %LocalAppData% and reuse it on later launches.
    /// The extracted host is itself a self-extracting single file - it unpacks its own native and
    /// C++/CLI dependencies (steam_api64.dll, the bridge, Ijwhost) to a temp dir when it runs.
    /// </summary>
    private static bool TryExtractEmbeddedHost(out string hostExePath)
    {
        hostExePath = string.Empty;

        var asm = Assembly.GetExecutingAssembly();
        var resourceName = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith(HostExeName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return false;

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return false;

        // Stamp the cache folder with the payload length so a new host build extracts to a fresh
        // path instead of fighting a possibly-locked older copy.
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PulseWorkshop", "host", stream.Length.ToString());
        var target = Path.Combine(cacheDir, HostExeName);

        if (!File.Exists(target) || new FileInfo(target).Length != stream.Length)
        {
            Directory.CreateDirectory(cacheDir);
            // Write to a temp file in the same folder, then atomically move into place so a half-written
            // exe is never observed (and concurrent launches don't clobber each other).
            var temp = Path.Combine(cacheDir, HostExeName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = File.Create(temp))
                stream.CopyTo(file);

            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another launch won the race (or the target is in use by an equivalent copy). Use it.
                File.Delete(temp);
            }
        }

        hostExePath = target;
        return true;
    }

    private static IEnumerable<string> EnumerateDevCandidates(string appBaseDir)
    {
        // Walk up to the 'src' folder, then into the SteamHost project output.
        var dir = new DirectoryInfo(appBaseDir);
        while (dir is not null && !string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;

        if (dir is null)
            yield break;

        var hostBin = Path.Combine(dir.FullName, "PulseWorkshop.SteamHost", "bin");
        if (!Directory.Exists(hostBin))
            yield break;

        // Prefer the host build matching the App's own platform/config (e.g. App runs from
        // bin\x64\Debug\... so pick the host's bin\x64\Debug\... build), then fall back to the most
        // RECENTLY BUILT host. Without this, an older stale host copy under bin\Debug\ can be picked
        // up and run instead of the freshly built one.
        var configHint = appBaseDir.Contains(@"\x64\", StringComparison.OrdinalIgnoreCase) ? @"\x64\" : null;

        var matches = Directory.EnumerateFiles(hostBin, HostExeName, SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => configHint is not null && fi.FullName.Contains(configHint, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(fi => fi.LastWriteTimeUtc);

        foreach (var fi in matches)
            yield return fi.FullName;
    }
}
