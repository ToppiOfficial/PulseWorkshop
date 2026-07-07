namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// Mounts every VPK a gameinfo.txt's SearchPaths reference, the way the Source engine's
/// filesystem does (a C# port of ModelTool's gameinfo.cpp): explicit .vpk entries plus every
/// <c>*_dir.vpk</c> inside each directory search path. Order matters - the engine takes the
/// FIRST search path that provides a file, so conflicting paths resolve to the topmost
/// gameinfo entry. Extraction dispatches to whichever mounted <see cref="VpkArchive"/> won.
/// </summary>
public sealed class GameInfoMount : IPackedArchive
{
    private readonly List<VpkArchive> _archives = new();
    private readonly List<PackedEntry> _entries = new();

    /// <summary>Locator: the winning archive, its own entry, and the archive's display label
    /// ("update/pak01_dir.vpk" - the bare file name alone is ambiguous, every search path tends
    /// to call its archive pak01_dir.vpk).</summary>
    private sealed record MountedEntry(VpkArchive Archive, PackedEntry Inner, string Label);

    public string DisplayName { get; }
    public string SourcePath { get; }
    public IReadOnlyList<PackedEntry> Entries => _entries;

    /// <summary>The mounted VPKs in priority order (full paths).</summary>
    public IReadOnlyList<string> MountedVpks { get; }

    /// <summary>How many entries were shadowed by a higher-priority archive providing the same path.</summary>
    public int ShadowedCount { get; }

    private GameInfoMount(string gameinfoPath, List<VpkArchive> archives, int shadowed)
    {
        SourcePath = gameinfoPath;
        var gameDir = Path.GetFileName(Path.GetDirectoryName(gameinfoPath));
        DisplayName = string.IsNullOrEmpty(gameDir) ? "gameinfo.txt" : $"{gameDir}/gameinfo.txt";
        _archives = archives;
        MountedVpks = archives.Select(a => a.SourcePath).ToList();
        ShadowedCount = shadowed;
    }

    public static GameInfoMount Open(string gameinfoPath)
    {
        var vpkPaths = GetVpkPaths(gameinfoPath);
        if (vpkPaths.Count == 0)
            throw new InvalidDataException(
                "This gameinfo.txt's SearchPaths reference no VPK archives that exist on disk.");

        var archives = new List<VpkArchive>();
        var byPath = new Dictionary<string, MountedEntry>(StringComparer.OrdinalIgnoreCase);
        int shadowed = 0;
        try
        {
            foreach (var vpk in vpkPaths)
            {
                var archive = new VpkArchive(vpk);
                archives.Add(archive);
                var parent = Path.GetFileName(Path.GetDirectoryName(vpk));
                var label = string.IsNullOrEmpty(parent)
                    ? archive.DisplayName
                    : $"{parent}/{archive.DisplayName}";
                foreach (var inner in archive.Entries)
                {
                    // First mount wins: a path already provided by an earlier (higher-priority)
                    // search-path archive shadows this one.
                    if (byPath.TryAdd(inner.Path, new MountedEntry(archive, inner, label)))
                        continue;
                    shadowed++;
                }
            }
        }
        catch
        {
            foreach (var archive in archives)
                archive.Dispose();
            throw;
        }

        var mount = new GameInfoMount(gameinfoPath, archives, shadowed);
        foreach (var (path, located) in byPath)
        {
            mount._entries.Add(new PackedEntry
            {
                Path = path,
                Size = located.Inner.Size,
                Crc = located.Inner.Crc,
                Source = located.Label,
                Handle = located,
            });
        }
        return mount;
    }

    public void Extract(PackedEntry entry, Stream destination, CancellationToken ct = default)
    {
        if (entry.Handle is not MountedEntry located)
            throw new ArgumentException("Entry does not belong to this mount.", nameof(entry));
        located.Archive.Extract(located.Inner, destination, ct);
    }

    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
        _archives.Clear();
    }

    // --- SearchPaths resolution (port of gameinfo.cpp) ------------------------------------------

    /// <summary>
    /// Every VPK archive the gameinfo mounts, in engine priority order: explicit .vpk SearchPaths
    /// entries first-come-first-kept, plus every *_dir.vpk found inside directory search paths
    /// (L4D2-era gameinfos list only directories and mount their VPKs implicitly).
    /// </summary>
    public static IReadOnlyList<string> GetVpkPaths(string gameinfoPath)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string archive)
        {
            if (!File.Exists(archive))
            {
                // Some gameinfos reference "garrysmod.vpk"; the filesystem opens "garrysmod_dir.vpk".
                var withDir = Path.Combine(Path.GetDirectoryName(archive) ?? ".",
                    Path.GetFileNameWithoutExtension(archive) + "_dir" + Path.GetExtension(archive));
                if (!File.Exists(withDir))
                    return;
                archive = withDir;
            }
            archive = Path.GetFullPath(archive);
            if (seen.Add(archive))
                result.Add(archive);
        }

        // Explicit .vpk entries in SearchPaths (GMod-style gameinfos).
        foreach (var p in CollectSearchEntries(gameinfoPath, wantVpks: true))
            Add(p);

        // Directory search paths: mount every "*_dir.vpk" archive inside, like the engine does.
        foreach (var dir in CollectSearchEntries(gameinfoPath, wantVpks: false))
        {
            if (!Directory.Exists(dir))
                continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*_dir.vpk", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
                Add(file);
        }

        return result;
    }

    /// <summary>
    /// The directory search paths a gameinfo.txt mounts (the game's content roots), in engine
    /// priority order, deduped and existing-on-disk only. These are the folders a <c>materials/</c>
    /// tree lives under; the "Make material's directory" feature lets the user pick which one to
    /// create the model's folders in. Returns an empty list if the file is missing or malformed.
    /// </summary>
    public static IReadOnlyList<string> GetGameRoots(string gameinfoPath)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dir in CollectSearchEntries(gameinfoPath, wantVpks: false))
            {
                if (!Directory.Exists(dir))
                    continue;
                var full = Path.GetFullPath(dir);
                if (seen.Add(full))
                    result.Add(full);
            }
        }
        catch
        {
            // A missing or malformed gameinfo.txt yields no roots rather than throwing.
        }
        return result;
    }

    /// <summary>Shared SearchPaths walk: either the directory entries or the .vpk references.</summary>
    private static List<string> CollectSearchEntries(string gameinfoPath, bool wantVpks)
    {
        var root = KeyValues.Parse(File.ReadAllText(gameinfoPath));

        var gi = KeyValues.Find(root.Children, "GameInfo")
            ?? throw new InvalidDataException("gameinfo.txt: missing GameInfo block.");
        var fsBlock = KeyValues.Find(gi.Children, "FileSystem")
            ?? throw new InvalidDataException("gameinfo.txt: missing FileSystem block.");
        var spBlock = KeyValues.Find(fsBlock.Children, "SearchPaths")
            ?? throw new InvalidDataException("gameinfo.txt: missing SearchPaths block.");

        string giDir = Path.GetDirectoryName(Path.GetFullPath(gameinfoPath))!;
        // Paths WITHOUT |gameinfo_path| (e.g. "left4dead2_dlc3", "hl2") are relative to the app
        // root - one directory above the gameinfo directory (the Steam game folder).
        string appRoot = Path.GetDirectoryName(giDir) ?? giDir;

        var result = new List<string>();
        foreach (var entry in spBlock.Children)
        {
            string val = entry.Value;
            if (string.IsNullOrEmpty(val))
                continue;

            bool isVpk = val.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase);
            if (isVpk != wantVpks)
                continue;

            // |all_source_engine_paths| means "every Source install" - too broad to enumerate for
            // directories. For VPK entries the referenced archives live under the game's own root,
            // so stripping the macro resolves them.
            if (val.Contains("|all_source_engine_paths|", StringComparison.OrdinalIgnoreCase))
            {
                if (!wantVpks)
                    continue;
                val = ReplaceMacro(val, "|all_source_engine_paths|", string.Empty);
                if (val.Length == 0)
                    continue;
            }

            // Skip wildcard glob entries
            if (val.Contains('*'))
                continue;

            bool hasMacro = val.Contains("|gameinfo_path|", StringComparison.OrdinalIgnoreCase);
            string resolved = ReplaceMacro(val, "|gameinfo_path|", giDir + "/").Replace('\\', '/');

            if (!Path.IsPathRooted(resolved))
                resolved = Path.Combine(hasMacro ? giDir : appRoot, resolved);

            try
            {
                result.Add(Path.GetFullPath(resolved));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // A malformed entry shouldn't sink the whole mount.
            }
        }
        return result;
    }

    private static string ReplaceMacro(string value, string macro, string replacement) =>
        value.Replace(macro, replacement, StringComparison.OrdinalIgnoreCase);
}
