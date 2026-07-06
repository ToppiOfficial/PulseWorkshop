namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// One file inside a packed archive (VPK/GMA) or a gameinfo mount. <see cref="Path"/> is the
/// archive-relative path, normalized to forward slashes with no leading slash.
/// </summary>
public sealed class PackedEntry
{
    public required string Path { get; init; }

    /// <summary>Uncompressed size in bytes (VPK/GMA store files uncompressed).</summary>
    public long Size { get; init; }

    /// <summary>CRC32 recorded in the archive (0 when absent). Informational only.</summary>
    public uint Crc { get; init; }

    /// <summary>Which archive provides this file - the VPK/GMA file name. For a gameinfo mount
    /// this is the winning archive after search-path priority.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Provider-specific locator (offsets etc.), owned by the archive that created it.</summary>
    internal object? Handle { get; init; }

    public string FileName => Path is { Length: > 0 } p && p.LastIndexOf('/') is var i && i >= 0
        ? p[(i + 1)..]
        : Path;

    /// <summary>The directory part of <see cref="Path"/> ("" for root-level files).</summary>
    public string Directory
    {
        get
        {
            int i = Path.LastIndexOf('/');
            return i < 0 ? string.Empty : Path[..i];
        }
    }

    public string Extension
    {
        get
        {
            var name = FileName;
            int i = name.LastIndexOf('.');
            return i < 0 ? string.Empty : name[(i + 1)..];
        }
    }
}

/// <summary>
/// A read-only view over packed Source content: a single .vpk, a .gma, or a whole gameinfo.txt
/// mount. Entries are listed once at open; extraction streams bytes out on demand.
/// </summary>
public interface IPackedArchive : IDisposable
{
    /// <summary>Short display name (usually the file name that was opened).</summary>
    string DisplayName { get; }

    /// <summary>The path that was opened (the _dir vpk, the .gma, or gameinfo.txt).</summary>
    string SourcePath { get; }

    IReadOnlyList<PackedEntry> Entries { get; }

    /// <summary>Writes the entry's bytes to <paramref name="destination"/>.</summary>
    void Extract(PackedEntry entry, Stream destination, CancellationToken ct = default);
}

/// <summary>Opens the right <see cref="IPackedArchive"/> for a picked file.</summary>
public static class PackedArchiveLoader
{
    /// <summary>True when <paramref name="path"/> is a file type the Unpack tab can open.</summary>
    public static bool CanOpen(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".gma", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gameinfo.txt", StringComparison.OrdinalIgnoreCase);
    }

    public static IPackedArchive Open(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.Equals("gameinfo.txt", StringComparison.OrdinalIgnoreCase))
            return GameInfoMount.Open(path);
        if (name.EndsWith(".gma", StringComparison.OrdinalIgnoreCase))
            return new GmaArchive(path);
        if (name.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
            return new VpkArchive(path);
        throw new InvalidDataException($"Unsupported file type: {name}. Open a .vpk, .gma, or gameinfo.txt.");
    }
}
