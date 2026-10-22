using System.Text;

namespace MortisFlash.Swf;

/// <summary>
/// Binary reader for the SWF format: little-endian integers, bit fields (UB/SB/FB) and
/// common records (RECT, MATRIX, CXFORM). Bit fields are realigned to the byte boundary
/// as soon as a non-bit field is read, as required by the specification.
/// </summary>
public sealed class SwfReader
{
    readonly byte[] _data;
    int _pos;
    readonly int _end;
    int _bitBuf;
    int _bitCount;

    public SwfReader(byte[] data, int offset = 0, int length = -1)
    {
        _data = data;
        _pos = offset;
        _end = length < 0 ? data.Length : Math.Min(data.Length, offset + length);
    }

    public int Version { get; set; } = 10;
    public int Position { get => _pos; set { _pos = value; _bitCount = 0; } }
    public int End => _end;
    public int Remaining => Math.Max(0, _end - _pos);
    public bool Eof => _pos >= _end;
    public byte[] Buffer => _data;

    public void Align() => _bitCount = 0;

    void Need(int n)
    {
        if (_pos + n > _end) throw new EndOfStreamException($"Unexpected end of SWF data (pos {_pos}, need {n}, end {_end})");
    }

    public byte U8()
    {
        Align();
        Need(1);
        return _data[_pos++];
    }

    public sbyte S8() => (sbyte)U8();

    public ushort U16()
    {
        Align();
        Need(2);
        ushort v = (ushort)(_data[_pos] | _data[_pos + 1] << 8);
        _pos += 2;
        return v;
    }

    public short S16() => (short)U16();

    public uint U32()
    {
        Align();
        Need(4);
        uint v = (uint)(_data[_pos] | _data[_pos + 1] << 8 | _data[_pos + 2] << 16 | _data[_pos + 3] << 24);
        _pos += 4;
        return v;
    }

    public int S32() => (int)U32();

    public float Float()
    {
        uint v = U32();
        return BitConverter.Int32BitsToSingle((int)v);
    }

    public double Double()
    {
        Align();
        Need(8);
        double v = BitConverter.ToDouble(_data, _pos);
        _pos += 8;
        return v;
    }

    /// <summary>AVM1 double: two 32-bit words, high word first.</summary>
    public double DoubleAvm1()
    {
        uint hi = U32();
        uint lo = U32();
        return BitConverter.Int64BitsToDouble((long)((ulong)hi << 32 | lo));
    }

    public float Fixed8() => S16() / 256f;
    public double Fixed() => S32() / 65536.0;

    public float Float16()
    {
        ushort h = U16();
        return (float)BitConverter.UInt16BitsToHalf(h);
    }

    public uint EncodedU32()
    {
        uint result = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = U8();
            result |= (uint)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) break;
        }
        return result;
    }

    public byte[] Bytes(int count)
    {
        Align();
        count = Math.Max(0, Math.Min(count, Remaining));
        var r = new byte[count];
        Array.Copy(_data, _pos, r, 0, count);
        _pos += count;
        return r;
    }

    public byte[] Rest() => Bytes(Remaining);

    public void Skip(int count)
    {
        Align();
        _pos = Math.Min(_end, _pos + count);
    }

    /// <summary>Null-terminated string: UTF-8 from SWF 6 onward, ANSI before that.</summary>
    public string String()
    {
        Align();
        int start = _pos;
        while (_pos < _end && _data[_pos] != 0) _pos++;
        int len = _pos - start;
        if (_pos < _end) _pos++;
        return Decode(_data, start, len, Version);
    }

    public static string Decode(byte[] data, int start, int len, int version)
    {
        if (version >= 6) return Encoding.UTF8.GetString(data, start, len);
        return Encoding.Latin1.GetString(data, start, len);
    }

    public string FixedString(int len)
    {
        var b = Bytes(len);
        int n = Array.IndexOf(b, (byte)0);
        return Decode(b, 0, n < 0 ? b.Length : n, Version);
    }

    // --- Bit fields ---

    public uint UB(int bits)
    {
        uint v = 0;
        for (int i = 0; i < bits; i++)
        {
            if (_bitCount == 0)
            {
                Need(1);
                _bitBuf = _data[_pos++];
                _bitCount = 8;
            }
            _bitCount--;
            v = v << 1 | (uint)(_bitBuf >> _bitCount & 1);
        }
        return v;
    }

    public int SB(int bits)
    {
        if (bits == 0) return 0;
        uint v = UB(bits);
        int shift = 32 - bits;
        return (int)(v << shift) >> shift;
    }

    public double FB(int bits) => SB(bits) / 65536.0;

    public bool Bit() => UB(1) == 1;

    // --- Records ---

    public Rect Rect()
    {
        Align();
        int n = (int)UB(5);
        var r = new Rect(SB(n), SB(n), SB(n), SB(n));
        Align();
        return r;
    }

    public Matrix Matrix()
    {
        Align();
        double sx = 1, sy = 1, r0 = 0, r1 = 0;
        if (Bit())
        {
            int n = (int)UB(5);
            sx = FB(n);
            sy = FB(n);
        }
        if (Bit())
        {
            int n = (int)UB(5);
            r0 = FB(n);
            r1 = FB(n);
        }
        int tn = (int)UB(5);
        int tx = SB(tn), ty = SB(tn);
        Align();
        return new Matrix(sx, r0, r1, sy, tx, ty);
    }

    public ColorTransform CxForm(bool withAlpha)
    {
        Align();
        bool hasAdd = Bit(), hasMult = Bit();
        int n = (int)UB(4);
        var ct = ColorTransform.Identity;
        if (hasMult)
        {
            ct.RMul = SB(n); ct.GMul = SB(n); ct.BMul = SB(n);
            if (withAlpha) ct.AMul = SB(n);
        }
        if (hasAdd)
        {
            ct.RAdd = SB(n); ct.GAdd = SB(n); ct.BAdd = SB(n);
            if (withAlpha) ct.AAdd = SB(n);
        }
        Align();
        return ct;
    }

    public Rgba Rgb() => new(U8(), U8(), U8(), 255);
    public Rgba Rgba() => new(U8(), U8(), U8(), U8());
    public Rgba Argb()
    {
        byte a = U8();
        return new(U8(), U8(), U8(), a);
    }
}

/// <summary>Rectangle in twips (1/20 of a pixel).</summary>
public readonly record struct Rect(int XMin, int XMax, int YMin, int YMax)
{
    public int Width => XMax - XMin;
    public int Height => YMax - YMin;
    public override string ToString() => $"{XMin / 20.0:0.##},{YMin / 20.0:0.##} → {XMax / 20.0:0.##},{YMax / 20.0:0.##}";
}

/// <summary>SWF affine matrix: x' = a·x + c·y + tx; y' = b·x + d·y + ty (tx/ty in twips).</summary>
public readonly record struct Matrix(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);

    /// <summary>this ∘ other: applies <paramref name="other"/> first, then this.</summary>
    public Matrix Multiply(Matrix o) => new(
        A * o.A + C * o.B,
        B * o.A + D * o.B,
        A * o.C + C * o.D,
        B * o.C + D * o.D,
        A * o.Tx + C * o.Ty + Tx,
        B * o.Tx + D * o.Ty + Ty);
}

public struct ColorTransform
{
    public int RMul, GMul, BMul, AMul;
    public int RAdd, GAdd, BAdd, AAdd;

    public static ColorTransform Identity => new() { RMul = 256, GMul = 256, BMul = 256, AMul = 256 };

    public readonly bool IsIdentity => RMul == 256 && GMul == 256 && BMul == 256 && AMul == 256
                                       && RAdd == 0 && GAdd == 0 && BAdd == 0 && AAdd == 0;

    /// <summary>this ∘ inner: the inner transform is applied first.</summary>
    public readonly ColorTransform Concat(ColorTransform inner) => new()
    {
        RMul = RMul * inner.RMul / 256, GMul = GMul * inner.GMul / 256,
        BMul = BMul * inner.BMul / 256, AMul = AMul * inner.AMul / 256,
        RAdd = RAdd + RMul * inner.RAdd / 256, GAdd = GAdd + GMul * inner.GAdd / 256,
        BAdd = BAdd + BMul * inner.BAdd / 256, AAdd = AAdd + AMul * inner.AAdd / 256,
    };

    public readonly Rgba Apply(Rgba c) => new(
        Clamp(c.R * RMul / 256 + RAdd), Clamp(c.G * GMul / 256 + GAdd),
        Clamp(c.B * BMul / 256 + BAdd), Clamp(c.A * AMul / 256 + AAdd));

    static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}

public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public uint Argb => (uint)(A << 24 | R << 16 | G << 8 | B);
    public override string ToString() => A == 255 ? Hex : $"{Hex} α{A}";
}
