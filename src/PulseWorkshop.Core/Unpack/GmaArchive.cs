using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// Reads a Garry's Mod addon (.gma): the "GMAD" header (name, author, file table) followed by
/// the file contents packed back-to-back. Legacy workshop downloads are whole-file
/// LZMA-compressed; those are transparently decompressed to a temp file first (deleted on
/// dispose). Matches gmad.exe's reader.
/// </summary>
public sealed class GmaArchive : IPackedArchive
{
    private static readonly byte[] Magic = "GMAD"u8.ToArray();

    private readonly FileStream _stream;
    private readonly string? _tempFile; // set when the source was LZMA-compressed
    private readonly List<PackedEntry> _entries = new();
    private readonly Lock _streamLock = new();
    private bool _disposed;

    /// <summary>Locator for one entry: absolute offset into the (decompressed) gma.</summary>
    private sealed record GmaEntryData(long Offset, long Length);

    public string DisplayName { get; }
    public string SourcePath { get; }
    public IReadOnlyList<PackedEntry> Entries => _entries;

    public byte FormatVersion { get; private set; }
    public string AddonName { get; private set; } = string.Empty;
    public string AddonAuthor { get; private set; } = string.Empty;

    /// <summary>The header's description field: a JSON blob (description/type/tags) in modern gmas,
    /// or plain text in version-1 gmas. gmad bakes addon.json into this header - the file itself is
    /// never packed - so we reconstruct a synthetic addon.json entry from it (see
    /// <see cref="BuildSyntheticAddonJson"/>).</summary>
    public string AddonDescription { get; private set; } = string.Empty;

    /// <summary>Virtual path of the reconstructed addon.json injected into the entry list.</summary>
    public const string SyntheticAddonJsonPath = "addon.json";

    /// <summary>True when the file on disk was LZMA-compressed (legacy workshop delivery).</summary>
    public bool WasCompressed => _tempFile is not null;

    public GmaArchive(string path)
    {
        SourcePath = path;
        DisplayName = Path.GetFileName(path);

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Span<byte> head = stackalloc byte[13];
            int got = stream.Read(head);
            stream.Seek(0, SeekOrigin.Begin);

            if (got >= 4 && head[..4].SequenceEqual(Magic))
            {
                _stream = stream;
            }
            else if (got == 13 && LzmaDecoder.LooksLikeLzma(head))
            {
                // Legacy workshop gma: the entire file is one LZMA-alone stream. Decompress to a
                // temp file so huge addons never have to fit in memory.
                _tempFile = Path.Combine(Path.GetTempPath(),
                    $"PulseWorkshop-gma-{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var temp = new FileStream(_tempFile, FileMode.CreateNew, FileAccess.Write,
                                                     FileShare.None, 1 << 16))
                    using (var buffered = new BufferedStream(temp, 1 << 16))
                    using (var input = new BufferedStream(stream, 1 << 16))
                        LzmaDecoder.Decode(input, buffered);
                }
                catch
                {
                    File.Delete(_tempFile);
                    throw;
                }
                finally
                {
                    stream.Dispose();
                }
                _stream = new FileStream(_tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                Span<byte> head2 = stackalloc byte[4];
                _stream.ReadExactly(head2);
                _stream.Seek(0, SeekOrigin.Begin);
                if (!head2.SequenceEqual(Magic))
                    throw new InvalidDataException($"{DisplayName}: LZMA data is not a GMA addon.");
            }
            else
            {
                throw new InvalidDataException($"{DisplayName} is not a GMA addon (bad magic).");
            }
        }
        catch
        {
            stream.Dispose();
            if (_tempFile is not null && File.Exists(_tempFile))
                File.Delete(_tempFile);
            Dispose();
            throw;
        }

        try
        {
            ParseHeader();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Mirrors gmad's AddonReader: magic, version, steamid, timestamp, required-content strings
    // (version > 1), name/description/author, addon version, then the file table. File contents
    // follow the table back-to-back in table order.
    private void ParseHeader()
    {
        using var reader = new BinaryReader(new BufferedStream(_stream, 1 << 16),
                                            Encoding.UTF8, leaveOpen: true);
        reader.BaseStream.Seek(4, SeekOrigin.Begin); // past "GMAD"

        FormatVersion = reader.ReadByte();
        reader.ReadUInt64(); // author steamid (unused)
        reader.ReadUInt64(); // timestamp

        if (FormatVersion > 1)
        {
            while (ReadCString(reader).Length != 0)
            {
                // required-content strings; unused, list ends at ""
            }
        }

        AddonName = ReadCString(reader);
        AddonDescription = ReadCString(reader); // json blob (modern) or plain text (v1)
        AddonAuthor = ReadCString(reader);
        reader.ReadInt32(); // addon version (unused)

        var sizes = new List<(string Name, long Size, uint Crc)>();
        while (reader.ReadUInt32() != 0)
        {
            string name = ReadCString(reader);
            long size = reader.ReadInt64();
            uint crc = reader.ReadUInt32();
            sizes.Add((name, size, crc));
        }

        // BinaryReader over BufferedStream: the buffered position is the real one.
        long offset = reader.BaseStream.Position;
        foreach (var (name, size, crc) in sizes)
        {
            _entries.Add(new PackedEntry
            {
                Path = name.Replace('\\', '/').TrimStart('/'),
                Size = size,
                Crc = crc,
                Source = DisplayName,
                Handle = new GmaEntryData(offset, size),
            });
            offset += size;
        }

        InjectSyntheticAddonJson();
    }

    /// <summary>gmad does not pack addon.json into the .gma - it bakes the title/description/type/tags
    /// into the header instead. Reconstruct that as a virtual addon.json entry so it shows up in the
    /// browser and can be previewed/exported, unless the archive already carries a real one.</summary>
    private void InjectSyntheticAddonJson()
    {
        if (_entries.Any(e => e.Path.Equals(SyntheticAddonJsonPath, StringComparison.OrdinalIgnoreCase)))
            return; // an actual addon.json is packed - leave it be

        byte[] json = BuildSyntheticAddonJson();
        // The inline byte[] handle marks this as a synthetic entry (see Extract).
        _entries.Insert(0, new PackedEntry
        {
            Path = SyntheticAddonJsonPath,
            Size = json.Length,
            Crc = 0,
            Source = DisplayName,
            Handle = json,
        });
    }

    private byte[] BuildSyntheticAddonJson()
    {
        // The modern header stores a JSON object (description/type/tags); version-1 stores plain text.
        JsonObject fields;
        if (AddonDescription.TrimStart().StartsWith('{'))
        {
            try { fields = JsonNode.Parse(AddonDescription) as JsonObject ?? new JsonObject(); }
            catch { fields = new JsonObject { ["description"] = AddonDescription }; }
        }
        else
        {
            fields = new JsonObject();
            if (AddonDescription.Length > 0)
                fields["description"] = AddonDescription;
        }

        // Rebuild in the familiar addon.json order, title/author first, then whatever the header held.
        var result = new JsonObject
        {
            ["_note"] = "Reconstructed by PulseWorkshop from the GMA header - a .gma does not store addon.json itself.",
            ["title"] = AddonName,
        };
        if (AddonAuthor.Length > 0 && !AddonAuthor.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            result["author"] = AddonAuthor;
        foreach (var (key, value) in fields)
        {
            if (!result.ContainsKey(key))
                result[key] = value?.DeepClone();
        }

        return Encoding.UTF8.GetBytes(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>(32);
        byte b;
        while ((b = reader.ReadByte()) != 0)
            bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public void Extract(PackedEntry entry, Stream destination, CancellationToken ct = default)
    {
        // Synthetic entries (the reconstructed addon.json) carry their bytes inline.
        if (entry.Handle is byte[] inline)
        {
            destination.Write(inline, 0, inline.Length);
            return;
        }

        if (entry.Handle is not GmaEntryData data)
            throw new ArgumentException("Entry does not belong to this archive.", nameof(entry));

        var buffer = new byte[(int)Math.Min(data.Length, 256 * 1024)];
        lock (_streamLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Seek(data.Offset, SeekOrigin.Begin);
            long remaining = data.Length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int got = _stream.Read(buffer, 0, want);
                if (got <= 0)
                    throw new EndOfStreamException($"{DisplayName}: addon data ended early.");
                destination.Write(buffer, 0, got);
                remaining -= got;
            }
        }
    }

    public void Dispose()
    {
        lock (_streamLock)
        {
            if (_disposed) return;
            _disposed = true;
            _stream?.Dispose();
            if (_tempFile is not null)
            {
                try { File.Delete(_tempFile); }
                catch { /* best effort - %TEMP% cleanup will get it eventually */ }
            }
        }
    }
}
