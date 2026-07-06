using System.Buffers.Binary;

namespace PulseWorkshop.Core.Unpack;

/// <summary>
/// Minimal LZMA-alone (".lzma") decoder, ported from the reference decoder in the LZMA SDK
/// (LzmaSpec.cpp, public domain). Used for GMod workshop .gma files from the legacy delivery
/// path, which are whole-file LZMA-compressed: 5-byte properties + 8-byte uncompressed size +
/// compressed stream. Decode-only; no encoder.
/// </summary>
public static class LzmaDecoder
{
    /// <summary>Cheap sniff for an LZMA-alone header: valid properties byte, plausible sizes.
    /// <paramref name="header"/> must hold at least the first 13 bytes of the file.</summary>
    public static bool LooksLikeLzma(ReadOnlySpan<byte> header)
    {
        if (header.Length < 13)
            return false;
        if (header[0] >= 9 * 5 * 5) // lc/lp/pb packed value has a hard upper bound
            return false;
        ulong unpacked = BinaryPrimitives.ReadUInt64LittleEndian(header[5..]);
        // Known size (the GMA case) within 32 GB, or the "unknown size" marker.
        return unpacked == ulong.MaxValue || (unpacked > 0 && unpacked <= 32L * 1024 * 1024 * 1024);
    }

    /// <summary>Decodes an LZMA-alone stream (header + data) from <paramref name="input"/> into
    /// <paramref name="output"/>. Throws <see cref="InvalidDataException"/> on corrupt data.</summary>
    public static void Decode(Stream input, Stream output, CancellationToken ct = default)
    {
        Span<byte> header = stackalloc byte[13];
        input.ReadExactly(header);

        byte props = header[0];
        if (props >= 9 * 5 * 5)
            throw new InvalidDataException("LZMA: invalid properties byte.");
        int lc = props % 9;
        int lp = props / 9 % 5;
        int pb = props / 9 / 5;

        uint dictSize = Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(header[1..]), 1u << 12);
        ulong unpackSize = BinaryPrimitives.ReadUInt64LittleEndian(header[5..]);
        bool sizeKnown = unpackSize != ulong.MaxValue;

        var decoder = new Decoder(input, output, lc, lp, pb, dictSize);
        decoder.Run(sizeKnown, unpackSize, ct);
    }

    private sealed class Decoder
    {
        private const int NumBitModelTotalBits = 11;
        private const int NumMoveBits = 5;
        private const uint TopValue = 1u << 24;
        private const ushort ProbInit = (1 << NumBitModelTotalBits) / 2;
        private const int NumPosBitsMax = 4;
        private const int NumStates = 12;
        private const int MatchMinLen = 2;
        private const int NumLenToPosStates = 4;
        private const int NumAlignBits = 4;
        private const int EndPosModelIndex = 14;
        private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);

        private readonly Stream _in;
        private readonly int _lc, _lp, _pb;
        private readonly uint _dictSize;

        // Range decoder state
        private uint _range, _code;

        // Output window (circular dictionary that also writes through to the output stream)
        private readonly Stream _out;
        private readonly byte[] _window;
        private uint _winPos;
        private bool _winFull;
        private ulong _totalOut;

        // Probability models
        private readonly ushort[] _isMatch = NewProbs(NumStates << NumPosBitsMax);
        private readonly ushort[] _isRep = NewProbs(NumStates);
        private readonly ushort[] _isRepG0 = NewProbs(NumStates);
        private readonly ushort[] _isRepG1 = NewProbs(NumStates);
        private readonly ushort[] _isRepG2 = NewProbs(NumStates);
        private readonly ushort[] _isRep0Long = NewProbs(NumStates << NumPosBitsMax);
        private readonly ushort[] _posSlot = NewProbs(NumLenToPosStates * (1 << 6));
        private readonly ushort[] _posDecoders = NewProbs(1 + NumFullDistances - EndPosModelIndex);
        private readonly ushort[] _align = NewProbs(1 << NumAlignBits);
        private readonly ushort[] _litProbs;
        private readonly LenDecoder _lenDec = new();
        private readonly LenDecoder _repLenDec = new();

        public Decoder(Stream input, Stream output, int lc, int lp, int pb, uint dictSize)
        {
            _in = input;
            _out = output;
            _lc = lc;
            _lp = lp;
            _pb = pb;
            _dictSize = dictSize;
            _window = new byte[dictSize];
            _litProbs = NewProbs(0x300 << (lc + lp));
        }

        private static ushort[] NewProbs(int count)
        {
            var probs = new ushort[count];
            Array.Fill(probs, ProbInit);
            return probs;
        }

        // --- Range decoder ----------------------------------------------------------------

        private byte NextByte()
        {
            int b = _in.ReadByte();
            if (b < 0)
                throw new InvalidDataException("LZMA: unexpected end of compressed data.");
            return (byte)b;
        }

        private void InitRange()
        {
            if (NextByte() != 0)
                throw new InvalidDataException("LZMA: corrupt stream (bad first byte).");
            _range = 0xFFFFFFFF;
            _code = 0;
            for (int i = 0; i < 4; i++)
                _code = (_code << 8) | NextByte();
        }

        private void Normalize()
        {
            if (_range < TopValue)
            {
                _range <<= 8;
                _code = (_code << 8) | NextByte();
            }
        }

        private uint DecodeBit(ushort[] probs, int index)
        {
            uint v = probs[index];
            uint bound = (_range >> NumBitModelTotalBits) * v;
            uint symbol;
            if (_code < bound)
            {
                v += ((1u << NumBitModelTotalBits) - v) >> NumMoveBits;
                _range = bound;
                symbol = 0;
            }
            else
            {
                v -= v >> NumMoveBits;
                _code -= bound;
                _range -= bound;
                symbol = 1;
            }
            probs[index] = (ushort)v;
            Normalize();
            return symbol;
        }

        private uint DecodeDirectBits(int numBits)
        {
            uint res = 0;
            do
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0 - (_code >> 31);
                _code += _range & t;
                if (_code == _range)
                    throw new InvalidDataException("LZMA: corrupt stream (direct bits).");
                Normalize();
                res = (res << 1) + t + 1;
            }
            while (--numBits > 0);
            return res;
        }

        private uint BitTreeDecode(ushort[] probs, int offset, int numBits)
        {
            uint m = 1;
            for (int i = 0; i < numBits; i++)
                m = (m << 1) + DecodeBit(probs, offset + (int)m);
            return m - (1u << numBits);
        }

        private uint BitTreeReverseDecode(ushort[] probs, int offset, int numBits)
        {
            uint m = 1, symbol = 0;
            for (int i = 0; i < numBits; i++)
            {
                uint bit = DecodeBit(probs, offset + (int)m);
                m = (m << 1) + bit;
                symbol |= bit << i;
            }
            return symbol;
        }

        // --- Output window ----------------------------------------------------------------

        private void PutByte(byte b)
        {
            _totalOut++;
            _window[_winPos++] = b;
            if (_winPos == _window.Length)
            {
                _winPos = 0;
                _winFull = true;
            }
            _out.WriteByte(b);
        }

        private byte WindowByte(uint dist) =>
            _window[dist <= _winPos ? _winPos - dist : _window.Length - dist + _winPos];

        private bool CheckDistance(uint dist) => dist <= _winPos || _winFull;

        // --- Length decoder -----------------------------------------------------------------

        private sealed class LenDecoder
        {
            public readonly ushort[] Choice = NewProbs(2);
            public readonly ushort[] LowCoder = NewProbs((1 << NumPosBitsMax) * 8);
            public readonly ushort[] MidCoder = NewProbs((1 << NumPosBitsMax) * 8);
            public readonly ushort[] HighCoder = NewProbs(256);
        }

        private uint DecodeLen(LenDecoder len, uint posState)
        {
            if (DecodeBit(len.Choice, 0) == 0)
                return BitTreeDecode(len.LowCoder, (int)posState * 8, 3);
            if (DecodeBit(len.Choice, 1) == 0)
                return 8 + BitTreeDecode(len.MidCoder, (int)posState * 8, 3);
            return 16 + BitTreeDecode(len.HighCoder, 0, 8);
        }

        // --- Main decode ----------------------------------------------------------------------

        private uint DecodeDistance(uint len)
        {
            uint lenState = Math.Min(len, NumLenToPosStates - 1);
            uint posSlot = BitTreeDecode(_posSlot, (int)lenState * (1 << 6), 6);
            if (posSlot < 4)
                return posSlot;

            int numDirectBits = (int)(posSlot >> 1) - 1;
            uint dist = (2 | (posSlot & 1)) << numDirectBits;
            if (posSlot < EndPosModelIndex)
            {
                dist += BitTreeReverseDecode(_posDecoders, (int)(dist - posSlot), numDirectBits);
            }
            else
            {
                dist += DecodeDirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
                dist += BitTreeReverseDecode(_align, 0, NumAlignBits);
            }
            return dist;
        }

        private void DecodeLiteral(uint state, uint rep0)
        {
            uint prevByte = _totalOut == 0 ? 0u : WindowByte(1);
            uint litState = (uint)(((_totalOut & ((1u << _lp) - 1)) << _lc) + (prevByte >> (8 - _lc)));
            int offset = 0x300 * (int)litState;

            uint symbol = 1;
            if (state >= 7)
            {
                uint matchByte = WindowByte(rep0 + 1);
                do
                {
                    uint matchBit = (matchByte >> 7) & 1;
                    matchByte <<= 1;
                    uint bit = DecodeBit(_litProbs, offset + (int)(((1 + matchBit) << 8) + symbol));
                    symbol = (symbol << 1) | bit;
                    if (matchBit != bit)
                        break;
                }
                while (symbol < 0x100);
            }
            while (symbol < 0x100)
                symbol = (symbol << 1) | DecodeBit(_litProbs, offset + (int)symbol);

            PutByte((byte)(symbol & 0xFF));
        }

        public void Run(bool sizeKnown, ulong unpackSize, CancellationToken ct)
        {
            InitRange();

            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            uint state = 0;
            uint posMask = (1u << _pb) - 1;
            long sinceCancelCheck = 0;

            while (true)
            {
                if (sizeKnown && unpackSize == 0)
                    return; // decoded exactly the promised bytes (GMA streams have no end marker)

                if ((sinceCancelCheck++ & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();

                uint posState = (uint)_totalOut & posMask;

                if (DecodeBit(_isMatch, (int)((state << NumPosBitsMax) + posState)) == 0)
                {
                    DecodeLiteral(state, rep0);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    if (sizeKnown) unpackSize--;
                    continue;
                }

                uint len;
                if (DecodeBit(_isRep, (int)state) != 0)
                {
                    if (_totalOut == 0)
                        throw new InvalidDataException("LZMA: rep match before any output.");

                    if (DecodeBit(_isRepG0, (int)state) == 0)
                    {
                        if (DecodeBit(_isRep0Long, (int)((state << NumPosBitsMax) + posState)) == 0)
                        {
                            state = state < 7 ? 9u : 11u;
                            PutByte(WindowByte(rep0 + 1));
                            if (sizeKnown) unpackSize--;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (DecodeBit(_isRepG1, (int)state) == 0)
                        {
                            dist = rep1;
                        }
                        else
                        {
                            if (DecodeBit(_isRepG2, (int)state) == 0)
                            {
                                dist = rep2;
                            }
                            else
                            {
                                dist = rep3;
                                rep3 = rep2;
                            }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }
                    len = DecodeLen(_repLenDec, posState);
                    state = state < 7 ? 8u : 11u;
                }
                else
                {
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    len = DecodeLen(_lenDec, posState);
                    state = state < 7 ? 7u : 10u;
                    rep0 = DecodeDistance(len);
                    if (rep0 == 0xFFFFFFFF)
                        return; // end-of-stream marker
                    if (rep0 >= _dictSize || !CheckDistance(rep0))
                        throw new InvalidDataException("LZMA: match distance outside the dictionary.");
                }

                len += MatchMinLen;
                if (sizeKnown && unpackSize < len)
                    throw new InvalidDataException("LZMA: output exceeds the declared size.");

                for (uint i = 0; i < len; i++)
                    PutByte(WindowByte(rep0 + 1));
                if (sizeKnown) unpackSize -= len;
            }
        }
    }
}
