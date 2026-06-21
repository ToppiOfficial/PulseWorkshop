using System.IO;

namespace PulseWorkshop.App.Services;

/// <summary>Resolves the path to the SteamHost helper executable next to the App.</summary>
public static class HostLocator
{
    public const string HostExeName = "PulseWorkshop.SteamHost.exe";

    /// <summary>
    /// Locates the SteamHost executable. In a packaged build it sits next to the App; during local
    /// development it lives in the SteamHost project's own build output, so we fall back to that.
    /// </summary>
    public static string ResolveHostExePath()
    {
        var baseDir = AppContext.BaseDirectory;

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
