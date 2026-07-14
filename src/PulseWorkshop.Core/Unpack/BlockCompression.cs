using System.Buffers.Binary;

namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// Block-compression (BCn) decoders and a raw-LZ4 block decompressor, used to render Source 2
/// <c>.vtex_c</c> texture previews (see <see cref="Source2Texture"/>). Each block decoder writes a
/// single 4x4 tile as RGBA8 into a caller-provided buffer at a given row pitch.
///
/// The BCn decoders are a C# port of iOrange's public-domain <c>bcdec.h</c>
/// (https://github.com/iOrange/bcdec); the BC7 partition tables are taken verbatim from it. Only the
/// unsigned integer paths needed for previews are ported (no BC6H HDR, no signed BC4/BC5).
/// </summary>
internal static class BlockCompression
{
    // --- Bitstream (LSB-first over a 128-bit block, used by BC7) ---------------------------------

    private static int ReadBits(ref ulong low, ref ulong high, int n)
    {
        ulong mask = (1UL << n) - 1;
        int bits = (int)(low & mask);
        low >>= n;
        low |= (high & mask) << (64 - n);
        high >>= n;
        return bits;
    }

    private static int ReadBit(ref ulong low, ref ulong high) => ReadBits(ref low, ref high, 1);

    // --- BC1 / DXT1 color block (also the color half of BC3) -------------------------------------

    /// <summary>Decodes a BC1 color block (8 bytes) to RGBA. When <paramref name="onlyOpaque"/> the
    /// 3-color/1-bit-alpha branch is skipped (BC2/BC3 always use the 4-color interpolation).</summary>
    private static void ColorBlock(ReadOnlySpan<byte> src, Span<byte> dst, int pitch, bool onlyOpaque)
    {
        int c0 = src[0] | (src[1] << 8);
        int c1 = src[2] | (src[3] << 8);
        int r0 = (c0 >> 11) & 0x1F, g0 = (c0 >> 5) & 0x3F, b0 = c0 & 0x1F;
        int r1 = (c1 >> 11) & 0x1F, g1 = (c1 >> 5) & 0x3F, b1 = c1 & 0x1F;

        Span<uint> refc = stackalloc uint[4]; // 0xAABBGGRR
        int r, g, b;
        r = (r0 * 527 + 23) >> 6; g = (g0 * 259 + 33) >> 6; b = (b0 * 527 + 23) >> 6;
        refc[0] = Pack(r, g, b, 255);
        r = (r1 * 527 + 23) >> 6; g = (g1 * 259 + 33) >> 6; b = (b1 * 527 + 23) >> 6;
        refc[1] = Pack(r, g, b, 255);

        if (c0 > c1 || onlyOpaque)
        {
            r = ((2 * r0 + r1) * 351 + 61) >> 7; g = ((2 * g0 + g1) * 2763 + 1039) >> 11; b = ((2 * b0 + b1) * 351 + 61) >> 7;
            refc[2] = Pack(r, g, b, 255);
            r = ((r0 + 2 * r1) * 351 + 61) >> 7; g = ((g0 + 2 * g1) * 2763 + 1039) >> 11; b = ((b0 + 2 * b1) * 351 + 61) >> 7;
            refc[3] = Pack(r, g, b, 255);
        }
        else
        {
            r = ((r0 + r1) * 1053 + 125) >> 8; g = ((g0 + g1) * 4145 + 1019) >> 11; b = ((b0 + b1) * 1053 + 125) >> 8;
            refc[2] = Pack(r, g, b, 255);
            refc[3] = 0; // transparent black
        }

        uint indices = (uint)(src[4] | (src[5] << 8) | (src[6] << 16) | (src[7] << 24));
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                uint c = refc[(int)(indices & 3)];
                int o = i * pitch + j * 4;
                dst[o] = (byte)(c & 0xFF); dst[o + 1] = (byte)((c >> 8) & 0xFF);
                dst[o + 2] = (byte)((c >> 16) & 0xFF); dst[o + 3] = (byte)((c >> 24) & 0xFF);
                indices >>= 2;
            }
        }
    }

    private static uint Pack(int r, int g, int b, int a) =>
        (uint)((r & 0xFF) | ((g & 0xFF) << 8) | ((b & 0xFF) << 16) | ((a & 0xFF) << 24));

    /// <summary>Decodes the 8-byte "smooth alpha" block (BC3 alpha / BC4 / BC5 channel) into 16
    /// row-major texel values.</summary>
    private static void DecodeAlpha(ReadOnlySpan<byte> src, Span<byte> vals)
    {
        ulong block = BinaryPrimitives.ReadUInt64LittleEndian(src);
        Span<int> a = stackalloc int[8];
        a[0] = (int)(block & 0xFF);
        a[1] = (int)((block >> 8) & 0xFF);
        if (a[0] > a[1])
        {
            a[2] = (6 * a[0] + a[1]) / 7; a[3] = (5 * a[0] + 2 * a[1]) / 7;
            a[4] = (4 * a[0] + 3 * a[1]) / 7; a[5] = (3 * a[0] + 4 * a[1]) / 7;
            a[6] = (2 * a[0] + 5 * a[1]) / 7; a[7] = (a[0] + 6 * a[1]) / 7;
        }
        else
        {
            a[2] = (4 * a[0] + a[1]) / 5; a[3] = (3 * a[0] + 2 * a[1]) / 5;
            a[4] = (2 * a[0] + 3 * a[1]) / 5; a[5] = (a[0] + 4 * a[1]) / 5;
            a[6] = 0; a[7] = 255;
        }

        ulong indices = block >> 16;
        for (int i = 0; i < 16; i++)
        {
            vals[i] = (byte)a[(int)(indices & 0x7)];
            indices >>= 3;
        }
    }

    // --- Public block entry points (write RGBA at row pitch) -------------------------------------

    public static void Bc1(ReadOnlySpan<byte> src, Span<byte> dst, int pitch) =>
        ColorBlock(src, dst, pitch, onlyOpaque: false);

    public static void Bc3(ReadOnlySpan<byte> src, Span<byte> dst, int pitch)
    {
        ColorBlock(src[8..], dst, pitch, onlyOpaque: true);
        Span<byte> alpha = stackalloc byte[16];
        DecodeAlpha(src, alpha);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                dst[i * pitch + j * 4 + 3] = alpha[i * 4 + j];
    }

    /// <summary>BC4 (single channel, e.g. ATI1N): shown as greyscale.</summary>
    public static void Bc4(ReadOnlySpan<byte> src, Span<byte> dst, int pitch)
    {
        Span<byte> vals = stackalloc byte[16];
        DecodeAlpha(src, vals);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                byte v = vals[i * 4 + j];
                int o = i * pitch + j * 4;
                dst[o] = v; dst[o + 1] = v; dst[o + 2] = v; dst[o + 3] = 255;
            }
    }

    /// <summary>BC5 (two channels, e.g. ATI2N normal maps): X in red, Y in green, Z reconstructed
    /// into blue so a normal map previews as a recognizable bluish surface.</summary>
    public static void Bc5(ReadOnlySpan<byte> src, Span<byte> dst, int pitch)
    {
        Span<byte> xv = stackalloc byte[16];
        Span<byte> yv = stackalloc byte[16];
        DecodeAlpha(src, xv);
        DecodeAlpha(src[8..], yv);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                int t = i * 4 + j;
                int o = i * pitch + j * 4;
                double nx = xv[t] / 127.5 - 1.0;
                double ny = yv[t] / 127.5 - 1.0;
                double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                dst[o] = xv[t]; dst[o + 1] = yv[t];
                dst[o + 2] = (byte)Math.Clamp((nz * 0.5 + 0.5) * 255.0, 0, 255);
                dst[o + 3] = 255;
            }
    }

    // --- BC7 -------------------------------------------------------------------------------------

    private static int Interpolate(int a, int b, ReadOnlySpan<int> weights, int index) =>
        (a * (64 - weights[index]) + b * weights[index] + 32) >> 6;

    public static void Bc7(ReadOnlySpan<byte> src, Span<byte> dst, int pitch)
    {
        ReadOnlySpan<byte> bitsRgb = [4, 6, 5, 7, 5, 7, 7, 5];
        ReadOnlySpan<byte> bitsA = [0, 0, 0, 0, 6, 8, 7, 5];
        ReadOnlySpan<int> aWeight2 = [0, 21, 43, 64];
        ReadOnlySpan<int> aWeight3 = [0, 9, 18, 27, 37, 46, 55, 64];
        ReadOnlySpan<int> aWeight4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
        const int modeHasPBits = 0b11001011;

        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(src);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(src[8..]);

        int mode = 0;
        while (mode < 8 && ReadBit(ref low, ref high) == 0)
            mode++;

        if (mode >= 8) // invalid: emit transparent black
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    int o = i * pitch + j * 4;
                    dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 0;
                }
            return;
        }

        int partition = 0, numPartitions = 1, rotation = 0, indexSelectionBit = 0;
        if (mode is 0 or 1 or 2 or 3 or 7)
        {
            numPartitions = (mode is 0 or 2) ? 3 : 2;
            partition = ReadBits(ref low, ref high, mode == 0 ? 4 : 6);
        }
        int numEndpoints = numPartitions * 2;

        if (mode is 4 or 5)
        {
            rotation = ReadBits(ref low, ref high, 2);
            if (mode == 4)
                indexSelectionBit = ReadBit(ref low, ref high);
        }

        Span<int> ep = stackalloc int[6 * 4]; // endpoints[6][4], RGBA
        // RGB
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < numEndpoints; j++)
                ep[j * 4 + i] = ReadBits(ref low, ref high, bitsRgb[mode]);
        // Alpha (if any)
        if (bitsA[mode] > 0)
            for (int j = 0; j < numEndpoints; j++)
                ep[j * 4 + 3] = ReadBits(ref low, ref high, bitsA[mode]);

        // P-bits
        if (mode is 0 or 1 or 3 or 6 or 7)
        {
            for (int i = 0; i < numEndpoints; i++)
                for (int j = 0; j < 4; j++)
                    ep[i * 4 + j] <<= 1;

            if (mode == 1)
            {
                int pi = ReadBit(ref low, ref high);
                int pj = ReadBit(ref low, ref high);
                for (int k = 0; k < 3; k++)
                {
                    ep[0 * 4 + k] |= pi; ep[1 * 4 + k] |= pi;
                    ep[2 * 4 + k] |= pj; ep[3 * 4 + k] |= pj;
                }
            }
            else if ((modeHasPBits & (1 << mode)) != 0)
            {
                for (int i = 0; i < numEndpoints; i++)
                {
                    int p = ReadBit(ref low, ref high);
                    for (int k = 0; k < 4; k++)
                        ep[i * 4 + k] |= p;
                }
            }
        }

        int pBit = (modeHasPBits >> mode) & 1;
        for (int i = 0; i < numEndpoints; i++)
        {
            int jc = bitsRgb[mode] + pBit;
            for (int k = 0; k < 3; k++)
            {
                ep[i * 4 + k] <<= (8 - jc);
                ep[i * 4 + k] |= ep[i * 4 + k] >> jc;
            }
            int ja = bitsA[mode] + pBit;
            ep[i * 4 + 3] <<= (8 - ja);
            ep[i * 4 + 3] |= ep[i * 4 + 3] >> ja;
        }

        if (bitsA[mode] == 0)
            for (int j = 0; j < numEndpoints; j++)
                ep[j * 4 + 3] = 0xFF;

        int indexBits = (mode is 0 or 1) ? 3 : (mode == 6 ? 4 : 2);
        int indexBits2 = mode == 4 ? 3 : (mode == 5 ? 2 : 0);
        ReadOnlySpan<int> weights = indexBits == 2 ? aWeight2 : (indexBits == 3 ? aWeight3 : aWeight4);
        ReadOnlySpan<int> weights2 = indexBits2 == 2 ? aWeight2 : aWeight3;

        ReadOnlySpan<byte> partTable = numPartitions == 2 ? Bc7Partitions2 : Bc7Partitions3;

        // Pass 1: primary indices.
        Span<byte> indices = stackalloc byte[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                int partitionSet = numPartitions == 1
                    ? ((i | j) != 0 ? 0 : 128)
                    : partTable[partition * 16 + i * 4 + j];
                int ib = indexBits;
                if ((partitionSet & 0x80) != 0)
                    ib--;
                indices[i * 4 + j] = (byte)ReadBits(ref low, ref high, ib);
            }

        // Pass 2: secondary indices + interpolation + rotation.
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                int partitionSet = numPartitions == 1
                    ? ((i | j) != 0 ? 0 : 128)
                    : partTable[partition * 16 + i * 4 + j];
                int set = partitionSet & 0x03;
                int index = indices[i * 4 + j];
                int e0 = set * 2 * 4, e1 = (set * 2 + 1) * 4;

                int r, g, b, a;
                if (indexBits2 == 0)
                {
                    r = Interpolate(ep[e0], ep[e1], weights, index);
                    g = Interpolate(ep[e0 + 1], ep[e1 + 1], weights, index);
                    b = Interpolate(ep[e0 + 2], ep[e1 + 2], weights, index);
                    a = Interpolate(ep[e0 + 3], ep[e1 + 3], weights, index);
                }
                else
                {
                    int index2 = ReadBits(ref low, ref high, (i | j) != 0 ? indexBits2 : indexBits2 - 1);
                    if (indexSelectionBit == 0)
                    {
                        r = Interpolate(ep[e0], ep[e1], weights, index);
                        g = Interpolate(ep[e0 + 1], ep[e1 + 1], weights, index);
                        b = Interpolate(ep[e0 + 2], ep[e1 + 2], weights, index);
                        a = Interpolate(ep[e0 + 3], ep[e1 + 3], weights2, index2);
                    }
                    else
                    {
                        r = Interpolate(ep[e0], ep[e1], weights2, index2);
                        g = Interpolate(ep[e0 + 1], ep[e1 + 1], weights2, index2);
                        b = Interpolate(ep[e0 + 2], ep[e1 + 2], weights2, index2);
                        a = Interpolate(ep[e0 + 3], ep[e1 + 3], weights, index);
                    }
                }

                switch (rotation)
                {
                    case 1: (a, r) = (r, a); break;
                    case 2: (a, g) = (g, a); break;
                    case 3: (a, b) = (b, a); break;
                }

                int o = i * pitch + j * 4;
                dst[o] = (byte)r; dst[o + 1] = (byte)g; dst[o + 2] = (byte)b; dst[o + 3] = (byte)a;
            }
        }
    }

    // --- Raw LZ4 block decompression -------------------------------------------------------------

    /// <summary>Decompresses a raw LZ4 block (no frame header, as Source 2 stores compressed mips)
    /// into <paramref name="dst"/>. Returns true only when the whole destination was produced.</summary>
    public static bool Lz4Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s < src.Length)
        {
            int token = src[s++];
            int litLen = token >> 4;
            if (litLen == 15)
            {
                int b;
                do
                {
                    if (s >= src.Length) return false;
                    b = src[s++];
                    litLen += b;
                } while (b == 255);
            }

            if (litLen > 0)
            {
                if (s + litLen > src.Length || d + litLen > dst.Length) return false;
                src.Slice(s, litLen).CopyTo(dst.Slice(d));
                s += litLen; d += litLen;
            }

            if (s >= src.Length) break; // final sequence carries literals only

            if (s + 2 > src.Length) return false;
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            if (offset == 0 || offset > d) return false;

            int matchLen = token & 0x0F;
            if (matchLen == 15)
            {
                int b;
                do
                {
                    if (s >= src.Length) return false;
                    b = src[s++];
                    matchLen += b;
                } while (b == 255);
            }
            matchLen += 4; // minmatch

            if (d + matchLen > dst.Length) return false;
            int m = d - offset;
            for (int k = 0; k < matchLen; k++)
                dst[d++] = dst[m++];
        }
        return d == dst.Length;
    }

    // --- BC7 partition tables (verbatim from bcdec.h; fix-up indices keep their 0x80 MSB flag) ----

#pragma warning disable format
    private static readonly byte[] Bc7Partitions2 =
    [
        128, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 129,
        128, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 129,
        128, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 129,
        128, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 129,
        128, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 129,
        128, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 129,
        128, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 129,
        128, 1, 129, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        128, 0, 0, 0, 0, 0, 0, 0, 129, 0, 0, 0, 1, 1, 1, 0,
        128, 1, 129, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        128, 0, 129, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        128, 0, 0, 0, 1, 0, 0, 0, 129, 1, 0, 0, 1, 1, 1, 0,
        128, 0, 0, 0, 0, 0, 0, 0, 129, 0, 0, 0, 1, 1, 0, 0,
        128, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 129,
        128, 0, 129, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        128, 0, 0, 0, 1, 0, 0, 0, 129, 0, 0, 0, 1, 1, 0, 0,
        128, 1, 129, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0,
        128, 0, 129, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0,
        128, 0, 0, 1, 0, 1, 1, 1, 129, 1, 1, 0, 1, 0, 0, 0,
        128, 0, 0, 0, 1, 1, 1, 1, 129, 1, 1, 1, 0, 0, 0, 0,
        128, 1, 129, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0,
        128, 0, 129, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0,
        128, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 129,
        128, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 129,
        128, 1, 0, 1, 1, 0, 129, 0, 0, 1, 0, 1, 1, 0, 1, 0,
        128, 0, 1, 1, 0, 0, 1, 1, 129, 1, 0, 0, 1, 1, 0, 0,
        128, 0, 129, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0,
        128, 1, 0, 1, 0, 1, 0, 1, 129, 0, 1, 0, 1, 0, 1, 0,
        128, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 129,
        128, 1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 129,
        128, 1, 129, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 0,
        128, 0, 0, 1, 0, 0, 1, 1, 129, 1, 0, 0, 1, 0, 0, 0,
        128, 0, 129, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 1, 0, 0,
        128, 0, 129, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 0, 0,
        128, 1, 129, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0,
        128, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 129,
        128, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 129,
        128, 0, 0, 0, 0, 1, 129, 0, 0, 1, 1, 0, 0, 0, 0, 0,
        128, 1, 0, 0, 1, 1, 129, 0, 0, 1, 0, 0, 0, 0, 0, 0,
        128, 0, 129, 0, 0, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0,
        128, 0, 0, 0, 0, 0, 129, 0, 0, 1, 1, 1, 0, 0, 1, 0,
        128, 0, 0, 0, 0, 1, 0, 0, 129, 1, 1, 0, 0, 1, 0, 0,
        128, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 129,
        128, 0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 129,
        128, 1, 129, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0,
        128, 0, 129, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0,
        128, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 129,
        128, 1, 1, 0, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0, 129,
        128, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 129,
        128, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 129,
        128, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 129,
        128, 0, 129, 1, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0,
        128, 0, 129, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0,
        128, 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 129,
    ];

    private static readonly byte[] Bc7Partitions3 =
    [
        128, 0, 1, 129, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 130,
        128, 0, 0, 129, 0, 0, 1, 1, 130, 2, 1, 1, 2, 2, 2, 1,
        128, 0, 0, 0, 2, 0, 0, 1, 130, 2, 1, 1, 2, 2, 1, 129,
        128, 2, 2, 130, 0, 0, 2, 2, 0, 0, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 129, 1, 2, 2, 1, 1, 2, 130,
        128, 0, 1, 129, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 2, 130,
        128, 0, 2, 130, 0, 0, 2, 2, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 1, 1, 0, 0, 1, 1, 130, 2, 1, 1, 2, 2, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 129, 1, 1, 1, 2, 2, 2, 130,
        128, 0, 0, 0, 1, 1, 1, 1, 129, 1, 1, 1, 2, 2, 2, 130,
        128, 0, 0, 0, 1, 1, 129, 1, 2, 2, 2, 2, 2, 2, 2, 130,
        128, 0, 1, 2, 0, 0, 129, 2, 0, 0, 1, 2, 0, 0, 1, 130,
        128, 1, 1, 2, 0, 1, 129, 2, 0, 1, 1, 2, 0, 1, 1, 130,
        128, 1, 2, 2, 0, 129, 2, 2, 0, 1, 2, 2, 0, 1, 2, 130,
        128, 0, 1, 129, 0, 1, 1, 2, 1, 1, 2, 2, 1, 2, 2, 130,
        128, 0, 1, 129, 2, 0, 0, 1, 130, 2, 0, 0, 2, 2, 2, 0,
        128, 0, 0, 129, 0, 0, 1, 1, 0, 1, 1, 2, 1, 1, 2, 130,
        128, 1, 1, 129, 0, 0, 1, 1, 130, 0, 0, 1, 2, 2, 0, 0,
        128, 0, 0, 0, 1, 1, 2, 2, 129, 1, 2, 2, 1, 1, 2, 130,
        128, 0, 2, 130, 0, 0, 2, 2, 0, 0, 2, 2, 1, 1, 1, 129,
        128, 1, 1, 129, 0, 1, 1, 1, 0, 2, 2, 2, 0, 2, 2, 130,
        128, 0, 0, 129, 0, 0, 0, 1, 130, 2, 2, 1, 2, 2, 2, 1,
        128, 0, 0, 0, 0, 0, 129, 1, 0, 1, 2, 2, 0, 1, 2, 130,
        128, 0, 0, 0, 1, 1, 0, 0, 130, 2, 129, 0, 2, 2, 1, 0,
        128, 1, 2, 130, 0, 129, 2, 2, 0, 0, 1, 1, 0, 0, 0, 0,
        128, 0, 1, 2, 0, 0, 1, 2, 129, 1, 2, 2, 2, 2, 2, 130,
        128, 1, 1, 0, 1, 2, 130, 1, 129, 2, 2, 1, 0, 1, 1, 0,
        128, 0, 0, 0, 0, 1, 129, 0, 1, 2, 130, 1, 1, 2, 2, 1,
        128, 0, 2, 2, 1, 1, 0, 2, 129, 1, 0, 2, 0, 0, 2, 130,
        128, 1, 1, 0, 0, 129, 1, 0, 2, 0, 0, 2, 2, 2, 2, 130,
        128, 0, 1, 1, 0, 1, 2, 2, 0, 1, 130, 2, 0, 0, 1, 129,
        128, 0, 0, 0, 2, 0, 0, 0, 130, 2, 1, 1, 2, 2, 2, 129,
        128, 0, 0, 0, 0, 0, 0, 2, 129, 1, 2, 2, 1, 2, 2, 130,
        128, 2, 2, 130, 0, 0, 2, 2, 0, 0, 1, 2, 0, 0, 1, 129,
        128, 0, 1, 129, 0, 0, 1, 2, 0, 0, 2, 2, 0, 2, 2, 130,
        128, 1, 2, 0, 0, 129, 2, 0, 0, 1, 130, 0, 0, 1, 2, 0,
        128, 0, 0, 0, 1, 1, 129, 1, 2, 2, 130, 2, 0, 0, 0, 0,
        128, 1, 2, 0, 1, 2, 0, 1, 130, 0, 129, 2, 0, 1, 2, 0,
        128, 1, 2, 0, 2, 0, 1, 2, 129, 130, 0, 1, 0, 1, 2, 0,
        128, 0, 1, 1, 2, 2, 0, 0, 1, 1, 130, 2, 0, 0, 1, 129,
        128, 0, 1, 1, 1, 1, 130, 2, 2, 2, 0, 0, 0, 0, 1, 129,
        128, 1, 0, 129, 0, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 130,
        128, 0, 0, 0, 0, 0, 0, 0, 130, 1, 2, 1, 2, 1, 2, 129,
        128, 0, 2, 2, 1, 129, 2, 2, 0, 0, 2, 2, 1, 1, 2, 130,
        128, 0, 2, 130, 0, 0, 1, 1, 0, 0, 2, 2, 0, 0, 1, 129,
        128, 2, 2, 0, 1, 2, 130, 1, 0, 2, 2, 0, 1, 2, 2, 129,
        128, 1, 0, 1, 2, 2, 130, 2, 2, 2, 2, 2, 0, 1, 0, 129,
        128, 0, 0, 0, 2, 1, 2, 1, 130, 1, 2, 1, 2, 1, 2, 129,
        128, 1, 0, 129, 0, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 130,
        128, 2, 2, 130, 0, 1, 1, 1, 0, 2, 2, 2, 0, 1, 1, 129,
        128, 0, 0, 2, 1, 129, 1, 2, 0, 0, 0, 2, 1, 1, 1, 130,
        128, 0, 0, 0, 2, 129, 1, 2, 2, 1, 1, 2, 2, 1, 1, 130,
        128, 2, 2, 2, 0, 129, 1, 1, 0, 1, 1, 1, 0, 2, 2, 130,
        128, 0, 0, 2, 1, 1, 1, 2, 129, 1, 1, 2, 0, 0, 0, 130,
        128, 1, 1, 0, 0, 129, 1, 0, 0, 1, 1, 0, 2, 2, 2, 130,
        128, 0, 0, 0, 0, 0, 0, 0, 2, 1, 129, 2, 2, 1, 1, 130,
        128, 1, 1, 0, 0, 129, 1, 0, 2, 2, 2, 2, 2, 2, 2, 130,
        128, 0, 2, 2, 0, 0, 1, 1, 0, 0, 129, 1, 0, 0, 2, 130,
        128, 0, 2, 2, 1, 1, 2, 2, 129, 1, 2, 2, 0, 0, 2, 130,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 129, 1, 130,
        128, 0, 0, 130, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 129,
        128, 2, 2, 2, 1, 2, 2, 2, 0, 2, 2, 2, 129, 2, 2, 130,
        128, 1, 0, 129, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 130,
        128, 1, 1, 129, 2, 0, 1, 1, 130, 2, 0, 1, 2, 2, 2, 0,
    ];
#pragma warning restore format
}
