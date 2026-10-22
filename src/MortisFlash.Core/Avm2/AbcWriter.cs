using System.Text;

namespace MortisFlash.Avm2;

/// <summary>Serializes an <see cref="AbcFile"/> (exact inverse of <see cref="AbcFile.Parse"/>).</summary>
public static class AbcWriter
{
    sealed class W
    {
        public readonly MemoryStream S = new();
        public void U8(int v) => S.WriteByte((byte)v);
        public void U16(int v) { S.WriteByte((byte)v); S.WriteByte((byte)(v >> 8)); }

        public void U32(uint v)
        {
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                S.WriteByte(b);
            } while (v != 0);
        }

        public void U30(int v) => U32((uint)v);

        /// <summary>s32: same variable-length encoding, using up to 5 bytes for negative values.</summary>
        public void S32(long v) => U32((uint)(int)v);

        public void D64(double d) => S.Write(BitConverter.GetBytes(d));

        public void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            U30(b.Length);
            S.Write(b);
        }

        public void Bytes(byte[] b) => S.Write(b);
    }

    public static byte[] Write(AbcFile abc)
    {
        var w = new W();
        w.U16(abc.Minor);
        w.U16(abc.Major);

        Pool(w, abc.Ints.Length, i => w.S32(abc.Ints[i]));
        Pool(w, abc.UInts.Length, i => w.U32((uint)abc.UInts[i]));
        Pool(w, abc.Doubles.Length, i => w.D64(abc.Doubles[i]));
        if (abc.HasFloatPool)
        {
            var f = abc.Floats ?? [0];
            Pool(w, f.Length, i => w.Bytes(BitConverter.GetBytes(f[i])));
        }
        Pool(w, abc.Strings.Length, i => w.Str(abc.Strings[i]));
        Pool(w, abc.Namespaces.Length, i =>
        {
            w.U8((int)abc.Namespaces[i].Kind);
            w.U30(StringIndex(abc, abc.Namespaces[i].Name, i));
        });
        Pool(w, abc.NsSets.Length, i =>
        {
            w.U30(abc.NsSets[i].Length);
            foreach (var ns in abc.NsSets[i]) w.U30(ns);
        });
        Pool(w, abc.Multinames.Length, i =>
        {
            var m = abc.Multinames[i];
            w.U8((int)m.Kind);
            switch (m.Kind)
            {
                case MnKind.QName:
                case MnKind.QNameA:
                    w.U30(m.Ns);
                    w.U30(m.Name);
                    break;
                case MnKind.RTQName:
                case MnKind.RTQNameA:
                    w.U30(m.Name);
                    break;
                case MnKind.Multiname:
                case MnKind.MultinameA:
                    w.U30(m.Name);
                    w.U30(m.NsSet);
                    break;
                case MnKind.MultinameL:
                case MnKind.MultinameLA:
                    w.U30(m.NsSet);
                    break;
                case MnKind.TypeName:
                    w.U30(m.QName);
                    w.U30(m.Params.Length);
                    foreach (var p in m.Params) w.U30(p);
                    break;
            }
        });

        w.U30(abc.Methods.Count);
        foreach (var m in abc.Methods)
        {
            w.U30(m.ParamTypes.Length);
            w.U30(m.ReturnType);
            foreach (var p in m.ParamTypes) w.U30(p);
            w.U30(m.Name);
            w.U8(m.Flags);
            if (m.HasOptional)
            {
                w.U30(m.Options.Count);
                foreach (var (v, k) in m.Options)
                {
                    w.U30(v);
                    w.U8(k);
                }
            }
            if (m.HasParamNames)
                for (int k = 0; k < m.ParamTypes.Length; k++) w.U30(k < m.ParamNames.Length ? m.ParamNames[k] : 0);
        }

        w.U30(abc.Metadata.Count);
        foreach (var md in abc.Metadata)
        {
            w.U30(md.Name);
            w.U30(md.Items.Count);
            foreach (var (k, _) in md.Items) w.U30(k);
            foreach (var (_, v) in md.Items) w.U30(v);
        }

        w.U30(abc.Instances.Count);
        foreach (var ii in abc.Instances)
        {
            w.U30(ii.Name);
            w.U30(ii.SuperName);
            w.U8(ii.Flags);
            if ((ii.Flags & 8) != 0) w.U30(ii.ProtectedNs);
            w.U30(ii.Interfaces.Length);
            foreach (var i in ii.Interfaces) w.U30(i);
            w.U30(ii.IInit);
            Traits(w, ii.Traits);
        }
        foreach (var c in abc.Classes)
        {
            w.U30(c.CInit);
            Traits(w, c.Traits);
        }

        w.U30(abc.Scripts.Count);
        foreach (var s in abc.Scripts)
        {
            w.U30(s.Init);
            Traits(w, s.Traits);
        }

        w.U30(abc.Bodies.Count);
        foreach (var b in abc.Bodies)
        {
            w.U30(b.Method);
            w.U30(b.MaxStack);
            w.U30(b.LocalCount);
            w.U30(b.InitScopeDepth);
            w.U30(b.MaxScopeDepth);
            w.U30(b.Code.Length);
            w.Bytes(b.Code);
            w.U30(b.Exceptions.Count);
            foreach (var e in b.Exceptions)
            {
                w.U30(e.From);
                w.U30(e.To);
                w.U30(e.Target);
                w.U30(e.ExcType);
                w.U30(e.VarName);
            }
            Traits(w, b.Traits);
        }
        return w.S.ToArray();
    }

    static void Pool(W w, int count, Action<int> item)
    {
        // An empty pool is written as 0 (entry 0 is implicit)
        w.U30(count <= 1 ? 0 : count);
        for (int i = 1; i < count; i++) item(i);
    }

    // Namespaces keep their original string index when it is known
    static int StringIndex(AbcFile abc, string name, int nsIndex)
    {
        if (abc.NamespaceNameIndex != null && nsIndex < abc.NamespaceNameIndex.Length) return abc.NamespaceNameIndex[nsIndex];
        return abc.InternString(name);
    }

    static void Traits(W w, List<Trait> traits)
    {
        w.U30(traits.Count);
        foreach (var t in traits)
        {
            w.U30(t.Name);
            w.U8((int)t.Kind | t.Attributes << 4);
            switch (t.Kind)
            {
                case TraitKind.Slot:
                case TraitKind.Const:
                    w.U30(t.SlotId);
                    w.U30(t.TypeName);
                    w.U30(t.VIndex);
                    if (t.VIndex != 0) w.U8(t.VKind);
                    break;
                default:
                    w.U30(t.SlotId);
                    w.U30(t.Index);
                    break;
            }
            if ((t.Attributes & 4) != 0)
            {
                w.U30(t.Metadata.Length);
                foreach (var m in t.Metadata) w.U30(m);
            }
        }
    }
}
