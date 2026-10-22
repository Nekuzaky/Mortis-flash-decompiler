using System.Text;

namespace MortisFlash.Avm2;

public enum NsKind : byte
{
    Namespace = 0x08, Package = 0x16, PackageInternal = 0x17, Protected = 0x18, Explicit = 0x19,
    StaticProtected = 0x1A, Private = 0x05,
}

public readonly record struct AbcNamespace(NsKind Kind, string Name);

public enum MnKind : byte
{
    QName = 0x07, QNameA = 0x0D, RTQName = 0x0F, RTQNameA = 0x10, RTQNameL = 0x11, RTQNameLA = 0x12,
    Multiname = 0x09, MultinameA = 0x0E, MultinameL = 0x1B, MultinameLA = 0x1C, TypeName = 0x1D,
}

public sealed class Multiname
{
    public MnKind Kind;
    public int Ns;      // QName
    public int Name;    // QName, RTQName, Multiname
    public int NsSet;   // Multiname, MultinameL
    public int QName;   // TypeName
    public int[] Params = [];

    public bool IsAttribute => Kind is MnKind.QNameA or MnKind.RTQNameA or MnKind.RTQNameLA or MnKind.MultinameA or MnKind.MultinameLA;
    /// <summary>Name supplied at runtime (on the stack).</summary>
    public bool HasRuntimeName => Kind is MnKind.RTQNameL or MnKind.RTQNameLA or MnKind.MultinameL or MnKind.MultinameLA;
    /// <summary>Namespace supplied at runtime (on the stack).</summary>
    public bool HasRuntimeNs => Kind is MnKind.RTQName or MnKind.RTQNameA or MnKind.RTQNameL or MnKind.RTQNameLA;
}

public sealed class MethodInfo
{
    public int Index;
    public int ReturnType;
    public int[] ParamTypes = [];
    public int Name;
    public byte Flags;
    public List<(int Value, byte Kind)> Options = [];
    public int[] ParamNames = [];
    public MethodBody? Body;

    public bool NeedArguments => (Flags & 0x01) != 0;
    public bool NeedActivation => (Flags & 0x02) != 0;
    public bool NeedRest => (Flags & 0x04) != 0;
    public bool HasOptional => (Flags & 0x08) != 0;
    public bool HasParamNames => (Flags & 0x80) != 0;
}

public sealed class MetadataInfo
{
    public int Name;
    public List<(int Key, int Value)> Items = [];
}

public enum TraitKind : byte { Slot = 0, Method = 1, Getter = 2, Setter = 3, Class = 4, Function = 5, Const = 6 }

public sealed class Trait
{
    public int Name;
    public TraitKind Kind;
    public byte Attributes; // 1 = final, 2 = override, 4 = metadata
    public int SlotId;      // slot / const / class / function; disp_id for methods
    public int TypeName;    // slot / const
    public int VIndex;
    public byte VKind;
    public int Index;       // classi, function, method
    public int[] Metadata = [];

    public bool IsFinal => (Attributes & 1) != 0;
    public bool IsOverride => (Attributes & 2) != 0;
}

public sealed class InstanceInfo
{
    public int Name;
    public int SuperName;
    public byte Flags; // 1 sealed, 2 final, 4 interface, 8 protectedNs
    public int ProtectedNs;
    public int[] Interfaces = [];
    public int IInit;
    public List<Trait> Traits = [];
    public bool IsSealed => (Flags & 1) != 0;
    public bool IsFinal => (Flags & 2) != 0;
    public bool IsInterface => (Flags & 4) != 0;
}

public sealed class ClassInfo
{
    public int CInit;
    public List<Trait> Traits = [];
}

public sealed class ScriptInfo
{
    public int Init;
    public List<Trait> Traits = [];
}

public sealed class ExceptionInfo
{
    public int From, To, Target;
    public int ExcType, VarName;
}

public sealed class MethodBody
{
    public int Method;
    public int MaxStack, LocalCount, InitScopeDepth, MaxScopeDepth;
    public byte[] Code = [];
    public List<ExceptionInfo> Exceptions = [];
    public List<Trait> Traits = [];
}

/// <summary>ABC file (AVM2 bytecode): constant pool, methods, classes, scripts and method bodies.</summary>
public sealed class AbcFile
{
    public int Minor, Major;
    public long[] Ints = [0];
    public ulong[] UInts = [0];
    public double[] Doubles = [double.NaN];
    /// <summary>32-bit float pool (ABC 47.16+, recent AIR), kept raw for rewriting.</summary>
    public uint[]? Floats;
    public string[] Strings = [""];
    public AbcNamespace[] Namespaces = [default];
    public int[][] NsSets = [[]];
    public Multiname[] Multinames = [new()];
    public List<MethodInfo> Methods = [];
    public List<MetadataInfo> Metadata = [];
    public List<InstanceInfo> Instances = [];
    public List<ClassInfo> Classes = [];
    public List<ScriptInfo> Scripts = [];
    public List<MethodBody> Bodies = [];
    public string Name = "";

    public static AbcFile Parse(byte[] data, string name = "")
    {
        var r = new AbcReader(data);
        var abc = new AbcFile { Name = name, Minor = r.U16(), Major = r.U16() };

        int n = r.U30();
        abc.Ints = new long[Math.Max(1, n)];
        for (int i = 1; i < n; i++) abc.Ints[i] = r.S32();
        n = r.U30();
        abc.UInts = new ulong[Math.Max(1, n)];
        for (int i = 1; i < n; i++) abc.UInts[i] = r.U32();
        n = r.U30();
        abc.Doubles = new double[Math.Max(1, n)];
        abc.Doubles[0] = double.NaN;
        for (int i = 1; i < n; i++) abc.Doubles[i] = r.D64();
        // "float" SWFs (AIR 50+, Harman) add a float pool for version >= 47.16
        if (abc.HasFloatPool)
        {
            n = r.U30();
            abc.Floats = new uint[Math.Max(1, n)];
            for (int i = 1; i < n; i++) abc.Floats[i] = (uint)(r.U8() | r.U8() << 8 | r.U8() << 16 | r.U8() << 24);
        }
        n = r.U30();
        abc.Strings = new string[Math.Max(1, n)];
        abc.Strings[0] = "";
        for (int i = 1; i < n; i++) abc.Strings[i] = r.Str();
        n = r.U30();
        abc.Namespaces = new AbcNamespace[Math.Max(1, n)];
        abc.NamespaceNameIndex = new int[Math.Max(1, n)];
        for (int i = 1; i < n; i++)
        {
            var kind = (NsKind)r.U8();
            int si = r.U30();
            abc.NamespaceNameIndex[i] = si;
            abc.Namespaces[i] = new AbcNamespace(kind, abc.Str(si));
        }
        n = r.U30();
        abc.NsSets = new int[Math.Max(1, n)][];
        abc.NsSets[0] = [];
        for (int i = 1; i < n; i++)
        {
            int c = r.U30();
            var set = new int[c];
            for (int k = 0; k < c; k++) set[k] = r.U30();
            abc.NsSets[i] = set;
        }
        n = r.U30();
        abc.Multinames = new Multiname[Math.Max(1, n)];
        abc.Multinames[0] = new Multiname { Kind = MnKind.QName };
        for (int i = 1; i < n; i++)
        {
            var m = new Multiname { Kind = (MnKind)r.U8() };
            switch (m.Kind)
            {
                case MnKind.QName:
                case MnKind.QNameA:
                    m.Ns = r.U30();
                    m.Name = r.U30();
                    break;
                case MnKind.RTQName:
                case MnKind.RTQNameA:
                    m.Name = r.U30();
                    break;
                case MnKind.RTQNameL:
                case MnKind.RTQNameLA:
                    break;
                case MnKind.Multiname:
                case MnKind.MultinameA:
                    m.Name = r.U30();
                    m.NsSet = r.U30();
                    break;
                case MnKind.MultinameL:
                case MnKind.MultinameLA:
                    m.NsSet = r.U30();
                    break;
                case MnKind.TypeName:
                    m.QName = r.U30();
                    int pc = r.U30();
                    m.Params = new int[pc];
                    for (int k = 0; k < pc; k++) m.Params[k] = r.U30();
                    break;
                default:
                    throw new InvalidDataException($"Unknown multiname kind 0x{(int)m.Kind:X2}");
            }
            abc.Multinames[i] = m;
        }

        n = r.U30();
        for (int i = 0; i < n; i++)
        {
            var mi = new MethodInfo { Index = i };
            int pc = r.U30();
            mi.ReturnType = r.U30();
            mi.ParamTypes = new int[pc];
            for (int k = 0; k < pc; k++) mi.ParamTypes[k] = r.U30();
            mi.Name = r.U30();
            mi.Flags = r.U8();
            if (mi.HasOptional)
            {
                int oc = r.U30();
                for (int k = 0; k < oc; k++) mi.Options.Add((r.U30(), r.U8()));
            }
            if (mi.HasParamNames)
            {
                mi.ParamNames = new int[pc];
                for (int k = 0; k < pc; k++) mi.ParamNames[k] = r.U30();
            }
            abc.Methods.Add(mi);
        }

        n = r.U30();
        for (int i = 0; i < n; i++)
        {
            var md = new MetadataInfo { Name = r.U30() };
            int ic = r.U30();
            var keys = new int[ic];
            for (int k = 0; k < ic; k++) keys[k] = r.U30();
            for (int k = 0; k < ic; k++) md.Items.Add((keys[k], r.U30()));
            abc.Metadata.Add(md);
        }

        int classCount = r.U30();
        for (int i = 0; i < classCount; i++)
        {
            var ii = new InstanceInfo { Name = r.U30(), SuperName = r.U30(), Flags = r.U8() };
            if ((ii.Flags & 8) != 0) ii.ProtectedNs = r.U30();
            int ic = r.U30();
            ii.Interfaces = new int[ic];
            for (int k = 0; k < ic; k++) ii.Interfaces[k] = r.U30();
            ii.IInit = r.U30();
            ii.Traits = ReadTraits(r);
            abc.Instances.Add(ii);
        }
        for (int i = 0; i < classCount; i++)
            abc.Classes.Add(new ClassInfo { CInit = r.U30(), Traits = ReadTraits(r) });

        n = r.U30();
        for (int i = 0; i < n; i++) abc.Scripts.Add(new ScriptInfo { Init = r.U30(), Traits = ReadTraits(r) });

        n = r.U30();
        for (int i = 0; i < n; i++)
        {
            var b = new MethodBody
            {
                Method = r.U30(), MaxStack = r.U30(), LocalCount = r.U30(), InitScopeDepth = r.U30(), MaxScopeDepth = r.U30(),
            };
            int len = r.U30();
            b.Code = r.Bytes(len);
            int ec = r.U30();
            for (int k = 0; k < ec; k++)
                b.Exceptions.Add(new ExceptionInfo { From = r.U30(), To = r.U30(), Target = r.U30(), ExcType = r.U30(), VarName = r.U30() });
            b.Traits = ReadTraits(r);
            abc.Bodies.Add(b);
            if (b.Method < abc.Methods.Count) abc.Methods[b.Method].Body = b;
        }
        return abc;
    }

    static List<Trait> ReadTraits(AbcReader r)
    {
        int n = r.U30();
        var list = new List<Trait>(n);
        for (int i = 0; i < n; i++)
        {
            var t = new Trait { Name = r.U30() };
            byte kind = r.U8();
            t.Kind = (TraitKind)(kind & 0x0F);
            t.Attributes = (byte)(kind >> 4);
            switch (t.Kind)
            {
                case TraitKind.Slot:
                case TraitKind.Const:
                    t.SlotId = r.U30();
                    t.TypeName = r.U30();
                    t.VIndex = r.U30();
                    if (t.VIndex != 0) t.VKind = r.U8();
                    break;
                case TraitKind.Class:
                case TraitKind.Function:
                case TraitKind.Method:
                case TraitKind.Getter:
                case TraitKind.Setter:
                    t.SlotId = r.U30();
                    t.Index = r.U30();
                    break;
                default:
                    throw new InvalidDataException($"Unknown trait kind {kind & 0x0F}");
            }
            if ((t.Attributes & 4) != 0)
            {
                int mc = r.U30();
                t.Metadata = new int[mc];
                for (int k = 0; k < mc; k++) t.Metadata[k] = r.U30();
            }
            list.Add(t);
        }
        return list;
    }

    public bool HasFloatPool => Major > 47 || (Major == 47 && Minor >= 16);

    /// <summary>String index of each namespace's name (for byte-identical rewriting).</summary>
    public int[]? NamespaceNameIndex;

    // --- Adding constant pool entries (p-code assembler): reuse an existing entry when possible

    static int Add<T>(ref T[] pool, T value)
    {
        Array.Resize(ref pool, pool.Length + 1);
        pool[^1] = value;
        return pool.Length - 1;
    }

    public int InternString(string s)
    {
        for (int i = 1; i < Strings.Length; i++) if (Strings[i] == s) return i;
        return Add(ref Strings, s);
    }

    public int InternInt(long v)
    {
        for (int i = 1; i < Ints.Length; i++) if (Ints[i] == v) return i;
        return Add(ref Ints, v);
    }

    public int InternUInt(ulong v)
    {
        for (int i = 1; i < UInts.Length; i++) if (UInts[i] == v) return i;
        return Add(ref UInts, v);
    }

    public int InternDouble(double v)
    {
        for (int i = 1; i < Doubles.Length; i++)
            if (BitConverter.DoubleToInt64Bits(Doubles[i]) == BitConverter.DoubleToInt64Bits(v)) return i;
        return Add(ref Doubles, v);
    }

    public int InternNamespace(NsKind kind, string name)
    {
        for (int i = 1; i < Namespaces.Length; i++)
            if (Namespaces[i].Kind == kind && Namespaces[i].Name == name) return i;
        int si = InternString(name);
        if (NamespaceNameIndex != null)
        {
            var idx = NamespaceNameIndex;
            Add(ref idx, si);
            NamespaceNameIndex = idx;
        }
        return Add(ref Namespaces, new AbcNamespace(kind, name));
    }

    public int InternNsSet(int[] set)
    {
        for (int i = 1; i < NsSets.Length; i++) if (NsSets[i].SequenceEqual(set)) return i;
        return Add(ref NsSets, set);
    }

    public int InternMultiname(Multiname m)
    {
        for (int i = 1; i < Multinames.Length; i++)
        {
            var o = Multinames[i];
            if (o.Kind == m.Kind && o.Ns == m.Ns && o.Name == m.Name && o.NsSet == m.NsSet && o.QName == m.QName && o.Params.SequenceEqual(m.Params))
                return i;
        }
        return Add(ref Multinames, m);
    }

    /// <summary>Original DoABC tag (used to write back the modified bytecode).</summary>
    public Swf.SwfTag? SourceTag;
    public uint DoAbcFlags;

    public string Str(int i) => i > 0 && i < Strings.Length ? Strings[i] : "";
    public Multiname Mn(int i) => i >= 0 && i < Multinames.Length ? Multinames[i] : Multinames[0];
    public AbcNamespace Ns(int i) => i > 0 && i < Namespaces.Length ? Namespaces[i] : default;
}

/// <summary>ABC reader: variable-length u30/s32 integers (7 bits per byte).</summary>
public sealed class AbcReader(byte[] data, int pos = 0)
{
    public int Position = pos;
    public int Length => data.Length;
    public bool Eof => Position >= data.Length;

    void Need(int n)
    {
        if (Position + n > data.Length) throw new EndOfStreamException("Unexpected end of ABC bytecode");
    }

    public byte U8()
    {
        Need(1);
        return data[Position++];
    }

    public int U16()
    {
        Need(2);
        int v = data[Position] | data[Position + 1] << 8;
        Position += 2;
        return v;
    }

    public int S24()
    {
        Need(3);
        int v = data[Position] | data[Position + 1] << 8 | data[Position + 2] << 16;
        Position += 3;
        return (v << 8) >> 8;
    }

    public uint U32()
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

    public int U30() => (int)(U32() & 0x3FFFFFFF);
    public int S32() => (int)U32();

    public double D64()
    {
        Need(8);
        double v = BitConverter.ToDouble(data, Position);
        Position += 8;
        return v;
    }

    public string Str()
    {
        int len = U30();
        Need(len);
        var s = Encoding.UTF8.GetString(data, Position, len);
        Position += len;
        return s;
    }

    public byte[] Bytes(int len)
    {
        Need(len);
        var b = data[Position..(Position + len)];
        Position += len;
        return b;
    }

    public void Skip(int n) => Position += n;
}
