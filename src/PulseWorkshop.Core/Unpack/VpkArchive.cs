using System.Buffers.Binary;
using System.Text;

namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// Reads a Valve VPK package (versions 1 and 2): parses the directory tree of a
/// <c>*_dir.vpk</c> (or a standalone single-file .vpk) and extracts entries, following
/// preload data in the dir file and file data spread across the numbered chunk files
/// (<c>pak01_000.vpk</c>, ...). Opening a numbered chunk redirects to its _dir file.
/// </summary>
public sealed class VpkArchive : IPackedArchive
{
    private const uint Signature = 0x55aa1234;
    private const ushort DirArchiveIndex = 0x7fff; // data embedded in the _dir file itself

    private readonly string _dirPath;
    private readonly long _embeddedDataOffset; // where after-tree data starts in the dir file
    private readonly List<PackedEntry> _entries = new();

    // Chunk streams are opened lazily and kept for the archive's lifetime (an export touches
    // the same chunk thousands of times). Keyed by archive index; DirArchiveIndex = dir file.
    private readonly Dictionary<ushort, FileStream> _chunks = new();
    private readonly Lock _chunkLock = new();
    private bool _disposed;

    /// <summary>Locator for one entry: where its bytes live.</summary>
    private sealed record VpkEntryData(long PreloadOffset, ushort PreloadLength,
                                       ushort ArchiveIndex, uint EntryOffset, uint EntryLength);

    public string DisplayName { get; }
    public string SourcePath => _dirPath;
    public IReadOnlyList<PackedEntry> Entries => _entries;

    /// <summary>VPK format version (1 or 2).</summary>
    public uint Version { get; }

    public VpkArchive(string path)
    {
        _dirPath = RedirectChunkToDir(path);
        DisplayName = Path.GetFileName(_dirPath);

        using var stream = new FileStream(_dirPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[12];
        stream.ReadExactly(head);

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Signature)
            throw new InvalidDataException($"{DisplayName} is not a VPK (bad signature).");

        Version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        if (Version is not (1 or 2))
            throw new InvalidDataException($"{DisplayName}: unsupported VPK version {Version}.");

        uint treeSize = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        int headerSize = Version == 1 ? 12 : 28;
        if (Version == 2)
            stream.Seek(headerSize, SeekOrigin.Begin); // skip the four v2 section-size fields

        var tree = new byte[treeSize];
        stream.ReadExactly(tree);
        _embeddedDataOffset = headerSize + (long)treeSize;

        ParseTree(tree, headerSize);
    }

    /// <summary>If the user picked a numbered chunk (pak01_042.vpk), open its _dir instead.</summary>
    private static string RedirectChunkToDir(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length > 4 && name[^4] == '_'
            && char.IsAsciiDigit(name[^3]) && char.IsAsciiDigit(name[^2]) && char.IsAsciiDigit(name[^1]))
        {
            var dir = Path.Combine(Path.GetDirectoryName(path) ?? ".", name[..^4] + "_dir.vpk");
            if (File.Exists(dir))
                return dir;
        }
        return path;
    }

    // The tree is three nested null-terminated string levels: extension -> path -> filename,
    // each level ending at an empty string. A single space means "blank" at every level.
    private void ParseTree(byte[] tree, int headerSize)
    {
        int pos = 0;
        while (true)
        {
            string ext = ReadCString(tree, ref pos);
            if (ext.Length == 0) break;

            while (true)
            {
                string dir = ReadCString(tree, ref pos);
                if (dir.Length == 0) break;

                while (true)
                {
                    string name = ReadCString(tree, ref pos);
                    if (name.Length == 0) break;

                    uint crc = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos));
                    ushort preloadBytes = BinaryPrimitives.ReadUInt16LittleEndian(tree.AsSpan(pos + 4));
                    ushort archiveIndex = BinaryPrimitives.ReadUInt16LittleEndian(tree.AsSpan(pos + 6));
                    uint entryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos + 8));
                    uint entryLength = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos + 12));
                    // pos + 16 is the 0xffff terminator
                    pos += 18;

                    long preloadOffset = headerSize + pos; // preload bytes sit right here in the tree
                    pos += preloadBytes;

                    var sb = new StringBuilder(dir.Length + name.Length + ext.Length + 2);
                    if (dir != " ") sb.Append(dir).Append('/');
                    sb.Append(name == " " ? string.Empty : name);
                    if (ext != " ") sb.Append('.').Append(ext);

                    _entries.Add(new PackedEntry
                    {
                        Path = sb.ToString().Replace('\\', '/'),
                        Size = (long)entryLength + preloadBytes,
                        Crc = crc,
                        Source = DisplayName,
                        Handle = new VpkEntryData(preloadOffset, preloadBytes,
                                                  archiveIndex, entryOffset, entryLength),
                    });
                }
            }
        }
    }

    private static string ReadCString(byte[] buf, ref int pos)
    {
        int start = pos;
        while (pos < buf.Length && buf[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(buf, start, pos - start);
        pos++; // skip the terminator
        return s;
    }

    public void Extract(PackedEntry entry, Stream destination, CancellationToken ct = default)
    {
        if (entry.Handle is not VpkEntryData data)
            throw new ArgumentException("Entry does not belong to this archive.", nameof(entry));

        if (data.PreloadLength > 0)
        {
            var dir = GetChunk(DirArchiveIndex);
            CopyRange(dir, data.PreloadOffset, data.PreloadLength, destination, ct);
        }

        if (data.EntryLength > 0)
        {
            var chunk = GetChunk(data.ArchiveIndex);
            long offset = data.ArchiveIndex == DirArchiveIndex
                ? _embeddedDataOffset + data.EntryOffset
                : data.EntryOffset;
            CopyRange(chunk, offset, data.EntryLength, destination, ct);
        }
    }

    private FileStream GetChunk(ushort archiveIndex)
    {
        lock (_chunkLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_chunks.TryGetValue(archiveIndex, out var cached))
                return cached;

            string path;
            if (archiveIndex == DirArchiveIndex)
            {
                path = _dirPath;
            }
            else
            {
                var name = Path.GetFileName(_dirPath);
                if (!name.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"{DisplayName} references chunk {archiveIndex} but is not a _dir.vpk.");
                path = Path.Combine(Path.GetDirectoryName(_dirPath) ?? ".",
                                    $"{name[..^8]}_{archiveIndex:D3}.vpk");
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Missing VPK chunk: {Path.GetFileName(path)}", path);
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _chunks[archiveIndex] = stream;
            return stream;
        }
    }

    // Seek + copy under the chunk lock: extraction may be called from a worker while the UI
    // owns the same archive, and the streams' positions are shared state.
    private void CopyRange(FileStream source, long offset, long length, Stream destination,
                           CancellationToken ct)
    {
        var buffer = new byte[Math.Min(length, 256 * 1024)];
        lock (_chunkLock)
        {
            source.Seek(offset, SeekOrigin.Begin);
            long remaining = length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = source.Read(buffer, 0, want);
                if (got <= 0)
                    throw new EndOfStreamException($"{DisplayName}: archive data ended early.");
                destination.Write(buffer, 0, got);
                remaining -= got;
            }
        }
    }

    public void Dispose()
    {
        lock (_chunkLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stream in _chunks.Values)
                stream.Dispose();
            _chunks.Clear();
        }
    }
}
