namespace MortisFlash.Swf;

/// <summary>
/// LZMA decoder ("LZMA1" format, as used by "ZWS" SWF files), written from Igor Pavlov's
/// reference specification (public domain). Decodes entirely in memory: the output size
/// is known from the SWF header.
/// </summary>
public static class Lzma
{
    const int NumStates = 12;
    const int ProbInit = 1024;

    public static byte[] Decode(byte[] input, int offset, byte[] props, int outSize)
    {
        if (props.Length < 5) throw new InvalidDataException("Incomplete LZMA properties");
        int d = props[0];
        if (d >= 9 * 5 * 5) throw new InvalidDataException("Invalid LZMA properties");
        int lc = d % 9; d /= 9;
        int lp = d % 5;
        int pb = d / 5;
        var dec = new Decoder(input, offset, outSize, lc, lp, pb);
        dec.Run();
        return dec.Output;
    }

    sealed class RangeDecoder
    {
        readonly byte[] _in;
        int _pos;
        uint _range = 0xFFFFFFFF;
        uint _code;

        public RangeDecoder(byte[] input, int offset)
        {
            _in = input;
            _pos = offset;
            if (Next() != 0) { /* non-zero leading byte: tolerated, since some encoders write it incorrectly */ }
            for (int i = 0; i < 4; i++) _code = _code << 8 | Next();
        }

        byte Next() => _pos < _in.Length ? _in[_pos++] : (byte)0;

        void Normalize()
        {
            if (_range < 1u << 24)
            {
                _range <<= 8;
                _code = _code << 8 | Next();
            }
        }

        public int Bit(ref ushort prob)
        {
            uint bound = (_range >> 11) * prob;
            int bit;
            if (_code < bound)
            {
                prob += (ushort)((2048 - prob) >> 5);
                _range = bound;
                bit = 0;
            }
            else
            {
                prob -= (ushort)(prob >> 5);
                _code -= bound;
                _range -= bound;
                bit = 1;
            }
            Normalize();
            return bit;
        }

        public uint Direct(int numBits)
        {
            uint res = 0;
            do
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0 - (_code >> 31);
                _code += _range & t;
                Normalize();
                res <<= 1;
                res += t + 1;
            } while (--numBits > 0);
            return res;
        }

        public int Tree(ushort[] probs, int offset, int numBits)
        {
            int m = 1;
            for (int i = 0; i < numBits; i++) m = (m << 1) + Bit(ref probs[offset + m]);
            return m - (1 << numBits);
        }

        public int ReverseTree(ushort[] probs, int offset, int numBits)
        {
            int m = 1, symbol = 0;
            for (int i = 0; i < numBits; i++)
            {
                int bit = Bit(ref probs[offset + m]);
                m = (m << 1) + bit;
                symbol |= bit << i;
            }
            return symbol;
        }
    }

    sealed class LenDecoder
    {
        ushort _choice = ProbInit, _choice2 = ProbInit;
        readonly ushort[] _low = Fill(16 << 3), _mid = Fill(16 << 3), _high = Fill(256);

        public int Decode(RangeDecoder rc, int posState)
        {
            if (rc.Bit(ref _choice) == 0) return rc.Tree(_low, posState << 3, 3);
            if (rc.Bit(ref _choice2) == 0) return 8 + rc.Tree(_mid, posState << 3, 3);
            return 16 + rc.Tree(_high, 0, 8);
        }
    }

    static ushort[] Fill(int n)
    {
        var a = new ushort[n];
        Array.Fill(a, (ushort)ProbInit);
        return a;
    }

    sealed class Decoder
    {
        readonly RangeDecoder _rc;
        readonly int _lc, _lp, _pb;
        public readonly byte[] Output;
        int _outPos;

        readonly ushort[] _lit;
        readonly ushort[] _isMatch = Fill(NumStates << 4), _isRep = Fill(NumStates), _isRepG0 = Fill(NumStates),
            _isRepG1 = Fill(NumStates), _isRepG2 = Fill(NumStates), _isRep0Long = Fill(NumStates << 4);
        readonly ushort[] _posSlot = Fill(4 << 6), _posDecoders = Fill(1 + 114), _align = Fill(1 << 4);
        readonly LenDecoder _len = new(), _repLen = new();

        public Decoder(byte[] input, int offset, int outSize, int lc, int lp, int pb)
        {
            _rc = new RangeDecoder(input, offset);
            _lc = lc; _lp = lp; _pb = pb;
            Output = new byte[outSize];
            _lit = Fill(0x300 << (lc + lp));
        }

        byte GetByte(uint dist) => Output[_outPos - (int)dist];

        void DecodeLiteral(int state, uint rep0)
        {
            int prev = _outPos > 0 ? Output[_outPos - 1] : 0;
            int symbol = 1;
            int litState = ((_outPos & ((1 << _lp) - 1)) << _lc) + (prev >> (8 - _lc));
            int baseOff = 0x300 * litState;
            if (state >= 7 && rep0 + 1 <= _outPos)
            {
                int matchByte = GetByte(rep0 + 1);
                do
                {
                    int matchBit = matchByte >> 7 & 1;
                    matchByte <<= 1;
                    int bit = _rc.Bit(ref _lit[baseOff + ((1 + matchBit) << 8) + symbol]);
                    symbol = symbol << 1 | bit;
                    if (matchBit != bit) break;
                } while (symbol < 0x100);
            }
            while (symbol < 0x100) symbol = symbol << 1 | _rc.Bit(ref _lit[baseOff + symbol]);
            Output[_outPos++] = (byte)(symbol - 0x100);
        }

        uint DecodeDistance(int len)
        {
            int lenState = Math.Min(len, 3);
            int posSlot = _rc.Tree(_posSlot, lenState << 6, 6);
            if (posSlot < 4) return (uint)posSlot;
            int numDirectBits = (posSlot >> 1) - 1;
            uint dist = (uint)((2 | (posSlot & 1)) << numDirectBits);
            if (posSlot < 14)
                dist += (uint)_rc.ReverseTree(_posDecoders, (int)dist - posSlot, numDirectBits);
            else
            {
                dist += _rc.Direct(numDirectBits - 4) << 4;
                dist += (uint)_rc.ReverseTree(_align, 0, 4);
            }
            return dist;
        }

        public void Run()
        {
            int state = 0;
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            int pbMask = (1 << _pb) - 1;
            while (_outPos < Output.Length)
            {
                int posState = _outPos & pbMask;
                if (_rc.Bit(ref _isMatch[(state << 4) + posState]) == 0)
                {
                    DecodeLiteral(state, rep0);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }
                int len;
                if (_rc.Bit(ref _isRep[state]) != 0)
                {
                    if (_outPos == 0) throw new InvalidDataException("Corrupted LZMA stream");
                    if (_rc.Bit(ref _isRepG0[state]) == 0)
                    {
                        if (_rc.Bit(ref _isRep0Long[(state << 4) + posState]) == 0)
                        {
                            state = state < 7 ? 9 : 11;
                            Output[_outPos] = GetByte(rep0 + 1);
                            _outPos++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (_rc.Bit(ref _isRepG1[state]) == 0) dist = rep1;
                        else
                        {
                            if (_rc.Bit(ref _isRepG2[state]) == 0) dist = rep2;
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
                    len = _repLen.Decode(_rc, posState);
                    state = state < 7 ? 8 : 11;
                }
                else
                {
                    rep3 = rep2; rep2 = rep1; rep1 = rep0;
                    len = _len.Decode(_rc, posState);
                    state = state < 7 ? 7 : 10;
                    rep0 = DecodeDistance(len);
                    if (rep0 == 0xFFFFFFFF) break; // end marker
                    if (rep0 >= _outPos) throw new InvalidDataException("Invalid LZMA distance");
                }
                len += 2;
                int src = _outPos - (int)rep0 - 1;
                for (int i = 0; i < len && _outPos < Output.Length; i++) Output[_outPos++] = Output[src + i];
            }
        }
    }
}
