using System.Buffers.Binary;

namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// A minimal VTF (Valve Texture Format) decoder - just enough to render a small preview. It reads
/// the header, picks a mip whose largest dimension is at least a requested size (so a 2048² texture
/// doesn't decode at full resolution for a thumbnail), and decodes that mip's first frame/face to
/// 32-bit BGRA pixels. This is deliberately not a full VTFLib: it covers the common high-res image
/// formats (the uncompressed layouts plus DXT1/3/5) and returns null for anything it can't handle
/// or any malformed/short input, rather than throwing.
/// </summary>
public sealed class VtfImage
{
    /// <summary>Decoded mip width (may be smaller than <see cref="SourceWidth"/> when a lower mip was
    /// chosen for a large texture).</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>The texture's full (mip 0) dimensions from the header - for display, even when a
    /// smaller mip was decoded.</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    /// <summary>Row-major BGRA (8 bits per channel), <see cref="Width"/> * <see cref="Height"/> * 4 bytes.</summary>
    public byte[] Bgra { get; }

    /// <summary>TEXTUREFLAGS_CLAMPS - sample U clamped to the edge instead of wrapping. Set on
    /// anything that must not tile: iris maps, decals, screen overlays.</summary>
    public bool ClampS { get; }

    /// <summary>TEXTUREFLAGS_CLAMPT - the same for V.</summary>
    public bool ClampT { get; }

    private VtfImage(int width, int height, int sourceWidth, int sourceHeight, byte[] bgra,
        bool clampS, bool clampT)
    {
        Width = width;
        Height = height;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Bgra = bgra;
        ClampS = clampS;
        ClampT = clampT;
    }

    /// <summary>Set in the header flags for cubemaps (which store six faces per mip).</summary>
    private const uint TextureFlagsEnvMap = 0x4000;

    private const uint TextureFlagsClampS = 0x0004, TextureFlagsClampT = 0x0008;

    // The subset of VTFImageFormat we can decode. Anything else -> no preview.
    private enum Fmt
    {
        Rgba8888 = 0, Abgr8888 = 1, Rgb888 = 2, Bgr888 = 3, Rgb565 = 4,
        I8 = 5, Ia88 = 6, A8 = 8, Argb8888 = 11, Bgra8888 = 12,
        Dxt1 = 13, Dxt3 = 14, Dxt5 = 15, Bgrx8888 = 16, Bgr565 = 17,
        Dxt1OneBitAlpha = 20,
    }

    /// <summary>
    /// Decodes a .vtf file's bytes to a preview image. Chooses the smallest mip whose largest
    /// dimension is still at least <paramref name="minSize"/> (falling back to the full-resolution
    /// mip for textures smaller than that), so previews stay cheap on big textures. Returns null on
    /// any unsupported format or malformed/truncated input.
    /// </summary>
    public static VtfImage? Decode(ReadOnlySpan<byte> data, int minSize = 256)
    {
        if (data.Length < 64)
            return null;
        if (data[0] != (byte)'V' || data[1] != (byte)'T' || data[2] != (byte)'F' || data[3] != 0)
            return null;

        uint verMajor = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint verMinor = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        int frames = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        int highResFormat = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        int mipCount = data[56];
        uint lowResFormat = BinaryPrimitives.ReadUInt32LittleEndian(data[57..]);
        int lowResW = data[61];
        int lowResH = data[62];

        int depth = 1;
        if ((verMajor == 7 && verMinor >= 2) || verMajor > 7)
        {
            if (data.Length < 65)
                return null;
            depth = BinaryPrimitives.ReadUInt16LittleEndian(data[63..]);
            if (depth < 1)
                depth = 1;
        }

        if (width <= 0 || height <= 0 || frames <= 0 || mipCount <= 0)
            return null;
        if (!Enum.IsDefined(typeof(Fmt), highResFormat))
            return null;
        var fmt = (Fmt)highResFormat;

        int faces = (flags & TextureFlagsEnvMap) != 0 ? 6 : 1;

        // Where the high-res image data begins. For v7.3+ the header carries a resource directory
        // and the real offset lives in the {0x30,0,0} entry; otherwise it's the low-res thumbnail
        // (an optional DXT1 image) laid out right after the fixed header.
        long highResStart = -1;
        if (((verMajor == 7 && verMinor >= 3) || verMajor > 7) && data.Length >= 80)
        {
            uint numResources = BinaryPrimitives.ReadUInt32LittleEndian(data[68..]);
            long p = 80;
            for (uint i = 0; i < numResources && p + 8 <= data.Length; i++, p += 8)
            {
                if (data[(int)p] == 0x30 && data[(int)p + 1] == 0 && data[(int)p + 2] == 0)
                {
                    highResStart = BinaryPrimitives.ReadUInt32LittleEndian(data[((int)p + 4)..]);
                    break;
                }
            }
        }
        if (highResStart < 0)
        {
            long lowResSize = lowResFormat == 0xFFFFFFFF || lowResW <= 0 || lowResH <= 0
                ? 0
                : DxtSize(lowResW, lowResH, blockBytes: 8); // low-res thumbnail is always DXT1
            highResStart = headerSize + lowResSize;
        }

        // Pick the target mip: the smallest image whose larger side still reaches minSize.
        int sel = 0;
        for (int i = mipCount - 1; i >= 0; i--)
        {
            if (Math.Max(MipDim(width, i), MipDim(height, i)) >= minSize)
            {
                sel = i;
                break;
            }
        }

        // Mips are stored smallest-first; skip every mip below the target (all their frames/faces/
        // slices) to reach the target mip's first frame, face and slice.
        long offset = highResStart;
        for (int i = mipCount - 1; i > sel; i--)
        {
            long slice = SliceSize(MipDim(width, i), MipDim(height, i), fmt);
            if (slice < 0)
                return null;
            offset += slice * frames * faces * depth;
        }

        int selW = MipDim(width, sel);
        int selH = MipDim(height, sel);
        long selSize = SliceSize(selW, selH, fmt);
        if (selSize < 0 || offset < 0 || offset + selSize > data.Length)
            return null;

        var src = data.Slice((int)offset, (int)selSize);
        var bgra = new byte[selW * selH * 4];
        if (!DecodeInto(src, selW, selH, fmt, bgra))
            return null;
        return new VtfImage(selW, selH, width, height, bgra,
            (flags & TextureFlagsClampS) != 0, (flags & TextureFlagsClampT) != 0);
    }

    private static int MipDim(int dim, int level) => Math.Max(1, dim >> level);

    private static long DxtSize(int w, int h, int blockBytes) =>
        (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;

    /// <summary>Bytes for one 2D image (a single slice) at the given size and format, or -1 if the
    /// format has no known layout.</summary>
    private static long SliceSize(int w, int h, Fmt fmt) => fmt switch
    {
        Fmt.Dxt1 or Fmt.Dxt1OneBitAlpha => DxtSize(w, h, 8),
        Fmt.Dxt3 or Fmt.Dxt5 => DxtSize(w, h, 16),
        Fmt.Rgba8888 or Fmt.Abgr8888 or Fmt.Argb8888 or Fmt.Bgra8888 or Fmt.Bgrx8888 => (long)w * h * 4,
        Fmt.Rgb888 or Fmt.Bgr888 => (long)w * h * 3,
        Fmt.Rgb565 or Fmt.Bgr565 or Fmt.Ia88 => (long)w * h * 2,
        Fmt.I8 or Fmt.A8 => (long)w * h,
        _ => -1,
    };

    // --- Decoding -------------------------------------------------------------------------------

    private static bool DecodeInto(ReadOnlySpan<byte> src, int w, int h, Fmt fmt, byte[] dst)
    {
        switch (fmt)
        {
            case Fmt.Dxt1 or Fmt.Dxt1OneBitAlpha:
                return DecodeDxt(src, w, h, dst, alphaMode: 0);
            case Fmt.Dxt3:
                return DecodeDxt(src, w, h, dst, alphaMode: 3);
            case Fmt.Dxt5:
                return DecodeDxt(src, w, h, dst, alphaMode: 5);
            default:
                return DecodeUncompressed(src, w, h, fmt, dst);
        }
    }

    private static bool DecodeUncompressed(ReadOnlySpan<byte> src, int w, int h, Fmt fmt, byte[] dst)
    {
        int px = w * h;
        for (int i = 0; i < px; i++)
        {
            byte b, g, r, a;
            switch (fmt)
            {
                case Fmt.Rgba8888:
                    r = src[i * 4]; g = src[i * 4 + 1]; b = src[i * 4 + 2]; a = src[i * 4 + 3];
                    break;
                case Fmt.Abgr8888:
                    a = src[i * 4]; b = src[i * 4 + 1]; g = src[i * 4 + 2]; r = src[i * 4 + 3];
                    break;
                case Fmt.Argb8888:
                    a = src[i * 4]; r = src[i * 4 + 1]; g = src[i * 4 + 2]; b = src[i * 4 + 3];
                    break;
                case Fmt.Bgra8888:
                    b = src[i * 4]; g = src[i * 4 + 1]; r = src[i * 4 + 2]; a = src[i * 4 + 3];
                    break;
                case Fmt.Bgrx8888:
                    b = src[i * 4]; g = src[i * 4 + 1]; r = src[i * 4 + 2]; a = 255;
                    break;
                case Fmt.Rgb888:
                    r = src[i * 3]; g = src[i * 3 + 1]; b = src[i * 3 + 2]; a = 255;
                    break;
                case Fmt.Bgr888:
                    b = src[i * 3]; g = src[i * 3 + 1]; r = src[i * 3 + 2]; a = 255;
                    break;
                case Fmt.Rgb565:
                {
                    ushort v = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
                    r = Expand5((v >> 11) & 0x1F); g = Expand6((v >> 5) & 0x3F); b = Expand5(v & 0x1F); a = 255;
                    break;
                }
                case Fmt.Bgr565:
                {
                    ushort v = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
                    b = Expand5((v >> 11) & 0x1F); g = Expand6((v >> 5) & 0x3F); r = Expand5(v & 0x1F); a = 255;
                    break;
                }
                case Fmt.Ia88:
                    b = g = r = src[i * 2]; a = src[i * 2 + 1];
                    break;
                case Fmt.I8:
                    b = g = r = src[i]; a = 255;
                    break;
                case Fmt.A8:
                    // Alpha-only: show the alpha as grey so the thumbnail isn't blank.
                    b = g = r = src[i]; a = 255;
                    break;
                default:
                    return false;
            }
            dst[i * 4] = b; dst[i * 4 + 1] = g; dst[i * 4 + 2] = r; dst[i * 4 + 3] = a;
        }
        return true;
    }

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
    private static byte Expand6(int v) => (byte)((v << 2) | (v >> 4));

    /// <summary>Decodes a DXT1/3/5 image. <paramref name="alphaMode"/>: 0 = DXT1 (1-bit punch-through
    /// alpha), 3 = DXT3 (explicit 4-bit alpha), 5 = DXT5 (interpolated alpha).</summary>
    private static bool DecodeDxt(ReadOnlySpan<byte> src, int w, int h, byte[] dst, int alphaMode)
    {
        int blockBytes = alphaMode == 0 ? 8 : 16;
        int blocksW = Math.Max(1, (w + 3) / 4);
        int blocksH = Math.Max(1, (h + 3) / 4);
        if (src.Length < (long)blocksW * blocksH * blockBytes)
            return false;

        Span<byte> colors = stackalloc byte[16]; // 4 colors * BGRA
        Span<byte> alpha = stackalloc byte[16];  // 16 texels' alpha
        int block = 0;
        for (int by = 0; by < blocksH; by++)
        {
            for (int bx = 0; bx < blocksW; bx++, block += blockBytes)
            {
                var b = src.Slice(block, blockBytes);
                int colorOffset = alphaMode == 0 ? 0 : 8;

                if (alphaMode == 5)
                    DecodeDxt5Alpha(b, alpha);
                else if (alphaMode == 3)
                    DecodeDxt3Alpha(b, alpha);

                // Color endpoints (565).
                ushort c0 = (ushort)(b[colorOffset] | (b[colorOffset + 1] << 8));
                ushort c1 = (ushort)(b[colorOffset + 2] | (b[colorOffset + 3] << 8));
                BuildDxtColors(c0, c1, alphaMode == 0, colors);

                uint bits = (uint)(b[colorOffset + 4] | (b[colorOffset + 5] << 8)
                                   | (b[colorOffset + 6] << 16) | (b[colorOffset + 7] << 24));
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
                        int ci = (int)((bits >> ((py * 4 + px) * 2)) & 0x3);
                        int di = (y * w + x) * 4;
                        dst[di] = colors[ci * 4];
                        dst[di + 1] = colors[ci * 4 + 1];
                        dst[di + 2] = colors[ci * 4 + 2];
                        byte outA = colors[ci * 4 + 3]; // DXT1 punch-through alpha (0 for the 3rd index)
                        if (alphaMode == 3 || alphaMode == 5)
                            outA = alpha[py * 4 + px];
                        dst[di + 3] = outA;
                    }
                }
            }
        }
        return true;
    }

    /// <summary>Fills <paramref name="colors"/> (4 * BGRA) from two 565 endpoints. In DXT1 (opaque=
    /// false when the block may carry 1-bit alpha) c0 &lt;= c1 selects a 3-colour block whose 4th
    /// entry is transparent black.</summary>
    private static void BuildDxtColors(ushort c0, ushort c1, bool dxt1, Span<byte> colors)
    {
        byte r0 = Expand5((c0 >> 11) & 0x1F), g0 = Expand6((c0 >> 5) & 0x3F), b0 = Expand5(c0 & 0x1F);
        byte r1 = Expand5((c1 >> 11) & 0x1F), g1 = Expand6((c1 >> 5) & 0x3F), b1 = Expand5(c1 & 0x1F);

        Set(colors, 0, b0, g0, r0, 255);
        Set(colors, 1, b1, g1, r1, 255);

        if (!dxt1 || c0 > c1)
        {
            Set(colors, 2, (byte)((2 * b0 + b1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * r0 + r1) / 3), 255);
            Set(colors, 3, (byte)((b0 + 2 * b1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((r0 + 2 * r1) / 3), 255);
        }
        else
        {
            Set(colors, 2, (byte)((b0 + b1) / 2), (byte)((g0 + g1) / 2), (byte)((r0 + r1) / 2), 255);
            Set(colors, 3, 0, 0, 0, 0); // 1-bit alpha: transparent black
        }

        static void Set(Span<byte> c, int i, byte b, byte g, byte r, byte a)
        {
            c[i * 4] = b; c[i * 4 + 1] = g; c[i * 4 + 2] = r; c[i * 4 + 3] = a;
        }
    }

    private static void DecodeDxt3Alpha(ReadOnlySpan<byte> block, Span<byte> alpha)
    {
        // 16 texels, 4 bits each, low nibble first.
        for (int i = 0; i < 8; i++)
        {
            byte two = block[i];
            alpha[i * 2] = (byte)((two & 0x0F) * 17);
            alpha[i * 2 + 1] = (byte)((two >> 4) * 17);
        }
    }

    private static void DecodeDxt5Alpha(ReadOnlySpan<byte> block, Span<byte> alpha)
    {
        byte a0 = block[0], a1 = block[1];
        Span<byte> table = stackalloc byte[8];
        table[0] = a0;
        table[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
                table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        }
        else
        {
            for (int i = 1; i < 5; i++)
                table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            table[6] = 0;
            table[7] = 255;
        }

        // 16 * 3-bit indices packed into 6 bytes (block[2..8]).
        long bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (long)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            alpha[i] = table[(int)((bits >> (3 * i)) & 0x7)];
    }
}
