using System.Buffers.Binary;

namespace PulseWorkshop.Core.Imaging;

/// <summary>
/// Reads a preview out of a .psd/.psb without a PSD renderer - no Windows shell provider, no
/// Photoshop install, nothing platform-specific. Two ways in, because neither is always present:
/// <list type="bullet">
/// <item>the small JPEG preview Photoshop embeds as image resource 1036 (cheap, but the "Image
/// Previews" preference can turn it off, and plenty of files in the wild have none);</item>
/// <item>the flattened composite in the image data section, which is what every other tool - VTFCmd
/// included - actually imports. Present unless "Maximize Compatibility" was turned off.</item>
/// </list>
/// Layers are never touched; this only ever reads the already-flattened result.
/// </summary>
public sealed class PsdImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major BGRA (8 bits per channel), <see cref="Width"/> * <see cref="Height"/> * 4 bytes.</summary>
    public byte[] Bgra { get; }

    private PsdImage(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    /// <summary>Photoshop 5.0+ thumbnail resource. (1033 is the 4.0 form: same layout, but its JPEG
    /// stores BGR where 1036 stores RGB. Files that old aren't worth a channel swap.)</summary>
    private const int ThumbnailResourceId = 1036;

    /// <summary>Fixed part of a thumbnail resource, ahead of the JPEG: format, width, height,
    /// widthbytes, totalsize, compressedsize (4 bytes each), then bitspixel and planes (2 each).</summary>
    private const int ThumbnailHeaderSize = 28;

    /// <summary>The `kJpegRGB` value of the resource's format field - the only encoding Photoshop
    /// writes. (0 is an uncompressed raw thumbnail, which no shipping version produces.)</summary>
    private const uint FormatJpegRgb = 1;

    /// <summary>Sanity cap on the embedded JPEG. Real thumbnails are a few KB; anything wildly
    /// larger means we've mis-walked the resource blocks and shouldn't allocate on it.</summary>
    private const long MaxThumbnailBytes = 32 * 1024 * 1024;

    /// <summary>Refuse to allocate a composite bigger than this. 8192² is already past anything a
    /// Source texture wants to be, and it keeps a corrupt header from asking for gigabytes.</summary>
    private const long MaxCompositePixels = 8192L * 8192L;

    private const int ColorModeGrayscale = 1;
    private const int ColorModeRgb = 3;

    // --- Embedded thumbnail ---------------------------------------------------------------------

    /// <summary>
    /// The file's embedded JPEG preview, or null when the file isn't a PSD, carries no thumbnail
    /// resource, or is malformed/truncated. Never throws - callers treat null as "no preview".
    /// </summary>
    public static byte[]? TryReadThumbnailJpeg(string path) => Open(path, TryReadThumbnailJpeg);

    /// <summary>Stream overload - the caller owns <paramref name="stream"/>, which must be seekable.</summary>
    public static byte[]? TryReadThumbnailJpeg(Stream stream)
    {
        try
        {
            if (ReadHeader(stream) is not { } header)
                return null;

            stream.Position = header.ResourcesStart;
            long resourcesEnd = header.ResourcesStart + header.ResourcesLength;

            // Each resource block: "8BIM", id, a padded Pascal name, then length-prefixed data.
            Span<byte> signature = stackalloc byte[4];
            while (stream.Position + 12 <= resourcesEnd)
            {
                stream.ReadExactly(signature);
                if (signature[0] != (byte)'8' || signature[1] != (byte)'B'
                    || signature[2] != (byte)'I' || signature[3] != (byte)'M')
                    return null; // Out of step with the block chain - anything past here is garbage.

                int id = ReadU16(stream);
                SkipPascalName(stream);

                long size = ReadU32(stream);
                long nextBlock = stream.Position + size + (size % 2); // data is padded to even too

                if (id == ThumbnailResourceId
                    && size > ThumbnailHeaderSize && size <= MaxThumbnailBytes
                    && ReadU32(stream) == FormatJpegRgb)
                {
                    stream.Position += ThumbnailHeaderSize - 4; // the format field is already read
                    var jpeg = new byte[size - ThumbnailHeaderSize];
                    stream.ReadExactly(jpeg);
                    return jpeg;
                }

                stream.Position = nextBlock;
            }
        }
        catch
        {
            // Truncated or malformed - no preview rather than a throw.
        }
        return null;
    }

    // --- Flattened composite --------------------------------------------------------------------

    /// <summary>
    /// Decodes the flattened composite to BGRA, or null when the file has none (saved without
    /// "Maximize Compatibility") or uses a layout this doesn't cover - 8- and 16-bit RGB and
    /// greyscale, raw or RLE. CMYK, Lab, indexed colour, ZIP-compressed data and .psb all return
    /// null and leave the caller on the embedded thumbnail.
    /// </summary>
    public static PsdImage? TryDecodeComposite(string path) => Open(path, TryDecodeComposite);

    /// <summary>Stream overload - the caller owns <paramref name="stream"/>, which must be seekable.</summary>
    public static PsdImage? TryDecodeComposite(Stream stream)
    {
        try
        {
            if (ReadHeader(stream) is not { } header)
                return null;

            // PSB widens the layer section's length to 64 bits and the RLE row counts to 32, so the
            // walk below would desync. Its thumbnail resource is unaffected - that path still works.
            if (header.IsLarge)
                return null;
            if (header.ColorMode is not (ColorModeRgb or ColorModeGrayscale))
                return null;
            if (header.Depth is not (8 or 16))
                return null;
            if ((long)header.Width * header.Height > MaxCompositePixels)
                return null;

            // Image data sits past the resource section and the layer/mask section.
            stream.Position = header.ResourcesStart + header.ResourcesLength;
            uint layerLength = ReadU32(stream);
            stream.Position = stream.Position + layerLength;

            int compression = ReadU16(stream);
            if (compression is not (0 or 1))
                return null; // 2/3 are ZIP - Photoshop only writes those for 32-bit layer data.

            int width = header.Width, height = header.Height;
            int bytesPerSample = header.Depth / 8;
            int rowBytes = width * bytesPerSample;

            // The composite is planar: every row of channel 0, then every row of channel 1, and so on.
            // Only the channels we can interpret are read; the rest of the file is left alone.
            int colorChannels = header.ColorMode == ColorModeRgb ? 3 : 1;
            if (header.Channels < colorChannels)
                return null;
            bool hasAlpha = header.Channels > colorChannels;
            int wanted = colorChannels + (hasAlpha ? 1 : 0);

            // RLE stores every row's packed length up front, for all channels at once.
            int[]? packedRowLengths = null;
            if (compression == 1)
            {
                packedRowLengths = new int[header.Channels * height];
                for (int i = 0; i < packedRowLengths.Length; i++)
                    packedRowLengths[i] = ReadU16(stream);
            }

            var bgra = new byte[width * height * 4];

            // BGRA order, and greyscale writes its one channel into all three colour slots.
            ReadOnlySpan<int> slots = header.ColorMode == ColorModeRgb
                ? [2, 1, 0, 3]  // R, G, B, A
                : [0, 3];       // grey, A

            var row = new byte[rowBytes];
            for (int channel = 0; channel < wanted; channel++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (compression == 1)
                        ReadRleRow(stream, packedRowLengths![channel * height + y], row);
                    else
                        stream.ReadExactly(row);

                    // 16-bit samples are big-endian, so the high byte leads and is the one we keep.
                    int destination = y * width * 4 + slots[channel];
                    for (int x = 0; x < width; x++)
                        bgra[destination + x * 4] = row[x * bytesPerSample];

                    if (channel == 0 && header.ColorMode == ColorModeGrayscale)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte grey = bgra[destination + x * 4];
                            bgra[destination + x * 4 + 1] = grey;
                            bgra[destination + x * 4 + 2] = grey;
                        }
                    }
                }
            }

            if (!hasAlpha)
            {
                for (int i = 3; i < bgra.Length; i += 4)
                    bgra[i] = 255;
            }

            return new PsdImage(width, height, bgra);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Expands one PackBits-compressed row. Rows are padded with whatever the encoder felt
    /// like when they run short, so a row that doesn't fill is left as-is rather than failing.</summary>
    private static void ReadRleRow(Stream stream, int packedLength, byte[] row)
    {
        // PackBits can only expand a row, never shrink it below ~1 control byte per 128 output bytes,
        // so a packed length far past the row size means the counts table was misread.
        if (packedLength < 0 || packedLength > row.Length * 2 + 64)
            throw new InvalidDataException("PSD row length out of range");

        var packed = new byte[packedLength];
        stream.ReadExactly(packed);

        int at = 0, written = 0;
        while (at < packed.Length && written < row.Length)
        {
            sbyte control = (sbyte)packed[at++];
            if (control >= 0)
            {
                int count = Math.Min(control + 1, Math.Min(row.Length - written, packed.Length - at));
                packed.AsSpan(at, count).CopyTo(row.AsSpan(written));
                at += count;
                written += count;
            }
            else if (control != -128) // -128 is a no-op in PackBits
            {
                if (at >= packed.Length)
                    break;
                int count = Math.Min(1 - control, row.Length - written);
                row.AsSpan(written, count).Fill(packed[at++]);
                written += count;
            }
        }
        if (written < row.Length)
            row.AsSpan(written).Clear();
    }

    // --- Header -----------------------------------------------------------------------------------

    private readonly record struct Header(
        bool IsLarge, int Channels, int Width, int Height, int Depth, int ColorMode,
        long ResourcesStart, long ResourcesLength);

    /// <summary>Parses the fixed header and locates the image resource section, or null when this
    /// isn't a PSD at all.</summary>
    private static Header? ReadHeader(Stream stream)
    {
        // "8BPS", version, 6 reserved bytes, channels, height, width, depth, colour mode.
        Span<byte> header = stackalloc byte[26];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (header[0] != (byte)'8' || header[1] != (byte)'B'
            || header[2] != (byte)'P' || header[3] != (byte)'S')
            return null;

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        int channels = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);
        long height = BinaryPrimitives.ReadUInt32BigEndian(header[14..]);
        long width = BinaryPrimitives.ReadUInt32BigEndian(header[18..]);
        int depth = BinaryPrimitives.ReadUInt16BigEndian(header[22..]);
        int colorMode = BinaryPrimitives.ReadUInt16BigEndian(header[24..]);
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue || channels <= 0)
            return null;

        // Colour mode data comes first, then the image resources - both length-prefixed. Each length
        // is read before the seek, so don't fold these into `Position +=`: the property getter runs
        // before the right-hand side and the offsets come out 4 bytes short.
        uint colorModeLength = ReadU32(stream);
        stream.Position = stream.Position + colorModeLength;
        uint resourcesLength = ReadU32(stream);

        return new Header(version == 2, channels, (int)width, (int)height, depth, colorMode,
            stream.Position, resourcesLength);
    }

    /// <summary>Skips a resource block's Pascal name: a length byte plus its characters, padded so
    /// the pair occupies an even number of bytes.</summary>
    private static void SkipPascalName(Stream stream)
    {
        int nameLength = stream.ReadByte();
        if (nameLength < 0)
            throw new EndOfStreamException();
        stream.Position += nameLength + (nameLength % 2 == 0 ? 1 : 0);
    }

    private static T? Open<T>(string path, Func<Stream, T?> read) where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return read(stream);
        }
        catch
        {
            return null;
        }
    }

    // PSD is big-endian throughout.

    private static uint ReadU32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static ushort ReadU16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }
}
