using System.Buffers.Binary;
using System.Text;

namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// A lite decoder for Source 2 compiled textures (<c>.vtex_c</c>) - enough to render a preview
/// thumbnail. It walks the Source 2 resource container to the DATA block, reads the texture header,
/// picks a mip whose largest side is at least a requested size, LZ4-decompresses that mip if needed,
/// and decodes it (via <see cref="BlockCompression"/>) to 32-bit BGRA. PNG/JPEG-wrapped textures are
/// returned as their raw image stream for the caller to decode.
///
/// Layout facts follow ValveResourceFormat (https://github.com/ValveResourceFormat/ValveResourceFormat).
/// Unsupported formats (BC6H HDR, ETC2, the *_DXT5/WEBP wrappers, ...) and malformed input return null.
/// </summary>
public sealed class Source2Texture
{
    /// <summary>Decoded mip width (may be smaller than <see cref="SourceWidth"/>).</summary>
    public int Width { get; private init; }
    public int Height { get; private init; }

    /// <summary>The texture's full (mip 0) dimensions, for display.</summary>
    public int SourceWidth { get; private init; }
    public int SourceHeight { get; private init; }

    /// <summary>Short format name for the caption (e.g. "BC7", "DXT5", "PNG").</summary>
    public string FormatName { get; private init; } = "?";

    /// <summary>Decoded BGRA pixels (Width*Height*4), or null when <see cref="RawImage"/> is set.</summary>
    public byte[]? Bgra { get; private init; }

    /// <summary>For PNG/JPEG-wrapped textures: the raw image stream to hand to the platform decoder.
    /// Null when <see cref="Bgra"/> is set.</summary>
    public byte[]? RawImage { get; private init; }

    // VTexFormat (subset we care about).
    private enum Fmt
    {
        Unknown = 0, Dxt1 = 1, Dxt5 = 2, I8 = 3, Rgba8888 = 4,
        JpegRgba8888 = 15, PngRgba8888 = 16,
        Bc6h = 19, Bc7 = 20, Ati2n = 21, Ia88 = 22,
        Ati1n = 27, Bgra8888 = 28,
    }

    private const uint TexFlagCube = 0x10;
    private const uint ExtraCompressedMipSize = 4;

    /// <summary>Decodes a .vtex_c file's bytes to a preview image, choosing the smallest mip whose
    /// larger side is at least <paramref name="minSize"/>. Returns null on unsupported/malformed input.</summary>
    public static Source2Texture? Decode(byte[] data, int minSize = 256)
    {
        try
        {
            return DecodeCore(data, minSize);
        }
        catch
        {
            return null; // any indexing/format surprise -> no preview rather than a crash
        }
    }

    private static Source2Texture? DecodeCore(byte[] data, int minSize)
    {
        var span = data.AsSpan();
        if (span.Length < 16)
            return null;

        uint blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        long blockStart = 8 + blockOffset;

        long dataStart = -1, dataSize = 0;
        for (uint i = 0; i < blockCount; i++)
        {
            long entry = blockStart + i * 12;
            if (entry + 12 > span.Length)
                return null;
            var type = Encoding.ASCII.GetString(span.Slice((int)entry, 4));
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(span[((int)entry + 4)..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(span[((int)entry + 8)..]);
            if (type == "DATA")
            {
                dataStart = entry + 4 + off;
                dataSize = size;
                break;
            }
        }
        if (dataStart < 0 || dataStart + 40 > span.Length)
            return null;

        long imageDataStart = dataStart + dataSize;
        int d = (int)dataStart;

        // Texture header.
        int width = BinaryPrimitives.ReadUInt16LittleEndian(span[(d + 20)..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(span[(d + 22)..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(span[(d + 24)..]);
        uint flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(d + 2)..]);
        int format = span[d + 26];
        int numMips = span[d + 27];
        uint extraDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[(d + 32)..]);
        uint extraDataCount = BinaryPrimitives.ReadUInt32LittleEndian(span[(d + 36)..]);

        if (width <= 0 || height <= 0 || numMips <= 0)
            return null;
        if (depth < 1)
            depth = 1;
        var fmt = (Fmt)format;
        int faces = (flags & TexFlagCube) != 0 ? 6 : 1;

        // Per-mip on-disk sizes (present when mips are individually compressed).
        int[]? compressedMips = null;
        if (extraDataCount > 0)
        {
            long table = dataStart + 32 + extraDataOffset;
            for (uint k = 0; k < extraDataCount; k++)
            {
                long entry = table + k * 12;
                if (entry + 12 > span.Length)
                    break;
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)entry..]);
                uint rawOff = BinaryPrimitives.ReadUInt32LittleEndian(span[((int)entry + 4)..]);
                long entryData = entry + 4 + rawOff;
                if (type == ExtraCompressedMipSize)
                {
                    if (entryData + 12 > span.Length)
                        return null;
                    uint mipsRawOff = BinaryPrimitives.ReadUInt32LittleEndian(span[((int)entryData + 4)..]);
                    uint mips = BinaryPrimitives.ReadUInt32LittleEndian(span[((int)entryData + 8)..]);
                    long arrayPos = entryData + 4 + mipsRawOff;
                    if (mips == 0 || arrayPos + mips * 4 > span.Length)
                        return null;
                    compressedMips = new int[mips];
                    for (uint m = 0; m < mips; m++)
                        compressedMips[m] = BinaryPrimitives.ReadInt32LittleEndian(span[((int)arrayPos + (int)m * 4)..]);
                    break;
                }
            }
        }

        // Choose the target mip: smallest image whose larger side still reaches minSize.
        int sel = 0;
        for (int i = numMips - 1; i >= 0; i--)
        {
            if (Math.Max(MipDim(width, i), MipDim(height, i)) >= minSize)
            {
                sel = i;
                break;
            }
        }
        int selW = MipDim(width, sel), selH = MipDim(height, sel);

        // PNG/JPEG-wrapped textures: hand the mip's raw stream back for the platform decoder.
        if (fmt is Fmt.PngRgba8888 or Fmt.JpegRgba8888)
        {
            (long off, long size) = MipRegion(imageDataStart, sel, numMips, width, height, depth, faces, fmt, compressedMips);
            if (off < 0 || size <= 0 || off + size > span.Length)
            {
                // No per-mip table: treat the whole trailing blob as a single image.
                off = imageDataStart;
                size = span.Length - imageDataStart;
                if (size <= 0)
                    return null;
            }
            return new Source2Texture
            {
                Width = selW,
                Height = selH,
                SourceWidth = width,
                SourceHeight = height,
                FormatName = fmt == Fmt.PngRgba8888 ? "PNG" : "JPEG",
                RawImage = span.Slice((int)off, (int)size).ToArray(),
            };
        }

        if (!IsSupported(fmt))
            return null;

        // Locate and (if needed) LZ4-decompress the target mip.
        (long mipOff, long mipOnDisk) = MipRegion(imageDataStart, sel, numMips, width, height, depth, faces, fmt, compressedMips);
        if (mipOff < 0 || mipOnDisk <= 0 || mipOff + mipOnDisk > span.Length)
            return null;

        long fullUncompressed = OneImageBytes(selW, selH, fmt) * depth * faces;
        byte[] mip;
        var onDisk = span.Slice((int)mipOff, (int)mipOnDisk);
        if (compressedMips is not null && mipOnDisk != fullUncompressed)
        {
            mip = new byte[fullUncompressed];
            if (!BlockCompression.Lz4Decode(onDisk, mip))
                return null;
        }
        else
        {
            if (mipOnDisk < fullUncompressed)
                return null;
            mip = onDisk[..(int)fullUncompressed].ToArray();
        }

        var bgra = Decode2D(mip, selW, selH, fmt);
        if (bgra is null)
            return null;

        return new Source2Texture
        {
            Width = selW,
            Height = selH,
            SourceWidth = width,
            SourceHeight = height,
            FormatName = FormatLabel(fmt),
            Bgra = bgra,
        };
    }

    private static int MipDim(int dim, int level) => Math.Max(1, dim >> level);

    private static int CeilDiv4(int v) => Math.Max(1, (v + 3) / 4);

    /// <summary>Bytes for one 2D face image at the given size and format.</summary>
    private static long OneImageBytes(int w, int h, Fmt fmt) => fmt switch
    {
        Fmt.Dxt1 or Fmt.Ati1n => (long)CeilDiv4(w) * CeilDiv4(h) * 8,
        Fmt.Dxt5 or Fmt.Bc7 or Fmt.Ati2n or Fmt.Bc6h => (long)CeilDiv4(w) * CeilDiv4(h) * 16,
        Fmt.Rgba8888 or Fmt.Bgra8888 => (long)w * h * 4,
        Fmt.Ia88 => (long)w * h * 2,
        Fmt.I8 => (long)w * h,
        _ => (long)w * h * 4,
    };

    /// <summary>On-disk (offset,size) of a mip level. Mips are stored smallest-first, so the target
    /// mip is reached by skipping every higher-index (smaller) mip.</summary>
    private static (long Offset, long Size) MipRegion(long imageDataStart, int sel, int numMips,
        int width, int height, int depth, int faces, Fmt fmt, int[]? compressedMips)
    {
        long offset = imageDataStart;
        for (int j = numMips - 1; j > sel; j--)
            offset += OnDiskSize(j, width, height, depth, faces, fmt, compressedMips);
        return (offset, OnDiskSize(sel, width, height, depth, faces, fmt, compressedMips));
    }

    private static long OnDiskSize(int level, int width, int height, int depth, int faces, Fmt fmt, int[]? compressedMips)
    {
        if (compressedMips is not null && level < compressedMips.Length)
            return compressedMips[level];
        return OneImageBytes(MipDim(width, level), MipDim(height, level), fmt) * depth * faces;
    }

    private static bool IsSupported(Fmt fmt) => fmt is Fmt.Dxt1 or Fmt.Dxt5 or Fmt.Bc7
        or Fmt.Ati2n or Fmt.Ati1n or Fmt.Rgba8888 or Fmt.Bgra8888 or Fmt.I8 or Fmt.Ia88;

    private static string FormatLabel(Fmt fmt) => fmt switch
    {
        Fmt.Dxt1 => "DXT1", Fmt.Dxt5 => "DXT5", Fmt.Bc7 => "BC7",
        Fmt.Ati2n => "BC5", Fmt.Ati1n => "BC4",
        Fmt.Rgba8888 => "RGBA8888", Fmt.Bgra8888 => "BGRA8888",
        Fmt.I8 => "I8", Fmt.Ia88 => "IA88", _ => fmt.ToString(),
    };

    /// <summary>Decodes one 2D image (face 0) from the start of <paramref name="mip"/> to BGRA.</summary>
    private static byte[]? Decode2D(byte[] mip, int w, int h, Fmt fmt)
    {
        var bgra = new byte[w * h * 4];

        if (fmt is Fmt.Rgba8888 or Fmt.Bgra8888 or Fmt.I8 or Fmt.Ia88)
        {
            var src = mip.AsSpan();
            for (int p = 0; p < w * h; p++)
            {
                byte b, g, r, a;
                switch (fmt)
                {
                    case Fmt.Rgba8888: r = src[p * 4]; g = src[p * 4 + 1]; b = src[p * 4 + 2]; a = src[p * 4 + 3]; break;
                    case Fmt.Bgra8888: b = src[p * 4]; g = src[p * 4 + 1]; r = src[p * 4 + 2]; a = src[p * 4 + 3]; break;
                    case Fmt.Ia88: b = g = r = src[p * 2]; a = src[p * 2 + 1]; break;
                    default: b = g = r = src[p]; a = 255; break; // I8
                }
                bgra[p * 4] = b; bgra[p * 4 + 1] = g; bgra[p * 4 + 2] = r; bgra[p * 4 + 3] = a;
            }
            return bgra;
        }

        // Block-compressed formats.
        int blockBytes = fmt is Fmt.Dxt1 or Fmt.Ati1n ? 8 : 16;
        int blocksX = CeilDiv4(w), blocksY = CeilDiv4(h);
        Span<byte> scratch = stackalloc byte[64]; // 4x4 RGBA, pitch 16
        var data = mip.AsSpan();

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int blockOffset = (by * blocksX + bx) * blockBytes;
                if (blockOffset + blockBytes > data.Length)
                    return null;
                var block = data.Slice(blockOffset, blockBytes);
                switch (fmt)
                {
                    case Fmt.Dxt1: BlockCompression.Bc1(block, scratch, 16); break;
                    case Fmt.Dxt5: BlockCompression.Bc3(block, scratch, 16); break;
                    case Fmt.Bc7: BlockCompression.Bc7(block, scratch, 16); break;
                    case Fmt.Ati2n: BlockCompression.Bc5(block, scratch, 16); break;
                    case Fmt.Ati1n: BlockCompression.Bc4(block, scratch, 16); break;
                    default: return null;
                }

                // Blit the 4x4 tile (RGBA) into the image, swapping to BGRA and clipping edges.
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= h)
                        break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= w)
                            continue;
                        int so = py * 16 + px * 4;
                        int di = (y * w + x) * 4;
                        bgra[di] = scratch[so + 2];     // B <- R
                        bgra[di + 1] = scratch[so + 1]; // G
                        bgra[di + 2] = scratch[so];     // R <- B
                        bgra[di + 3] = scratch[so + 3]; // A
                    }
                }
            }
        }
        return bgra;
    }
}
