using System.Globalization;
using System.Text;

namespace MortisFlash.Avm2;

public sealed class AssemblerException(int line, string message) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// AVM2 p-code assembler: parses the text produced by <see cref="As3Printer.PCodeOf"/> (possibly edited)
/// and replaces the method body. Missing constants are added to the pool.
/// </summary>
public static class Avm2Assembler
{
    static readonly Dictionary<string, int> OpByName = BuildNames();

    static Dictionary<string, int> BuildNames()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int op = 0; op < 256; op++)
        {
            string n = Opcodes.Name(op);
            if (!n.StartsWith("op_")) d.TryAdd(n, op);
        }
        return d;
    }

    sealed record Line(int Number, string? Label, string? Op, string Args);

    /// <summary>Assembles the p-code and replaces the method body (code, exceptions, stack sizes).</summary>
    public static void Replace(AbcFile abc, MethodInfo method, string pcode)
    {
        var body = method.Body ?? throw new InvalidOperationException("Method has no body (native)");
        var lines = Parse(pcode);
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var tries = new List<(int Line, string From, string To, string Target, int Type, int Name)>();
        var instrs = new List<(Line L, int Op)>();

        foreach (var l in lines)
        {
            if (l.Op == null) continue;
            if (l.Op == "try")
            {
                tries.Add(ParseTry(abc, l));
                continue;
            }
            if (!OpByName.TryGetValue(l.Op, out int op)) throw new AssemblerException(l.Number, $"unknown instruction '{l.Op}'");
            instrs.Add((l, op));
        }

        // Pass 1: sizes (jumps are always 4 bytes, so addresses do not depend on labels)
        var encoded = new List<Func<Dictionary<string, int>, int, byte[]>>();
        int offset = 0;
        var labelAt = new List<(string Label, int Index)>();
        int idx = 0;
        foreach (var l in lines)
        {
            if (l.Label != null)
            {
                if (labels.ContainsKey(l.Label)) throw new AssemblerException(l.Number, $"duplicate label '{l.Label}'");
                labels[l.Label] = -1;
                labelAt.Add((l.Label, idx));
            }
            if (l.Op != null && l.Op != "try") idx++;
        }
        var offsets = new int[instrs.Count + 1];
        for (int i = 0; i < instrs.Count; i++)
        {
            var (l, op) = instrs[i];
            var enc = Encode(abc, l, op);
            encoded.Add(enc);
            offsets[i] = offset;
            offset += enc(new Dictionary<string, int>(), 0).Length;
        }
        offsets[instrs.Count] = offset;
        foreach (var (label, at) in labelAt) labels[label] = offsets[at];

        // Pass 2: emit with the actual addresses
        var code = new MemoryStream();
        for (int i = 0; i < instrs.Count; i++) code.Write(encoded[i](labels, offsets[i]));

        int Resolve(string name, int line) => labels.TryGetValue(name, out int v) && v >= 0
            ? v : throw new AssemblerException(line, $"unknown label '{name}'");
        var exceptions = tries.Select(t => new ExceptionInfo
        {
            From = Resolve(t.From, t.Line), To = Resolve(t.To, t.Line), Target = Resolve(t.Target, t.Line), ExcType = t.Type, VarName = t.Name,
        }).ToList();

        var bytes = code.ToArray();
        var (maxStack, maxScope, locals) = Analyze(abc, bytes, exceptions, method);
        body.Code = bytes;
        body.Exceptions = exceptions;
        // Never go below the original values: an underestimate would cause the SWF to be rejected
        body.MaxStack = Math.Max(Math.Max(maxStack, 1), body.MaxStack);
        body.MaxScopeDepth = Math.Max(body.MaxScopeDepth, body.InitScopeDepth + maxScope);
        body.LocalCount = Math.Max(body.LocalCount, locals);
    }

    static List<Line> Parse(string text)
    {
        var list = new List<Line>();
        var rows = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < rows.Length; i++)
        {
            string row = StripComment(rows[i]).Trim();
            if (row.Length == 0 || row.StartsWith("maxstack")) continue;
            string? label = null;
            int colon = LabelEnd(row);
            if (colon > 0)
            {
                label = row[..colon].Trim();
                row = row[(colon + 1)..].Trim();
            }
            if (row.Length == 0)
            {
                list.Add(new Line(i + 1, label, null, ""));
                continue;
            }
            int sp = row.IndexOf(' ');
            string op = sp < 0 ? row : row[..sp];
            string args = sp < 0 ? "" : row[(sp + 1)..].Trim();
            list.Add(new Line(i + 1, label, op, args));
        }
        return list;
    }

    /// <summary>"name:" at the start of a line (outside a string).</summary>
    static int LabelEnd(string row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (c == ':') return i > 0 ? i : -1;
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$' or '.')) return -1;
        }
        return -1;
    }

    static string StripComment(string row)
    {
        bool inStr = false;
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (inStr)
            {
                if (c == '\\') i++;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '/' && i + 1 < row.Length && row[i + 1] == '/') return row[..i];
        }
        return row;
    }

    static (int, string, string, string, int, int) ParseTry(AbcFile abc, Line l)
    {
        // try from L1 to L2 target L3 type <multiname> name <multiname>
        var c = new Cursor(l.Args, l.Number);
        c.Word("from");
        string from = c.Ident();
        c.Word("to");
        string to = c.Ident();
        c.Word("target");
        string target = c.Ident();
        c.Word("type");
        int type = c.Multiname(abc);
        c.Word("name");
        int name = c.Multiname(abc);
        return (l.Number, from, to, target, type, name);
    }

    static Func<Dictionary<string, int>, int, byte[]> Encode(AbcFile abc, Line l, int op)
    {
        var c = new Cursor(l.Args, l.Number);
        var w = new MemoryStream();
        w.WriteByte((byte)op);
        void U30(long v) => WriteU30(w, (uint)v);

        if (Opcodes.IsBranch(op))
        {
            string target = c.Ident();
            c.End();
            var head = w.ToArray();
            return (labels, at) =>
            {
                var o = new MemoryStream();
                o.Write(head);
                int rel = labels.TryGetValue(target, out int t) && t >= 0 ? t - (at + 4)
                    : RawOffset(target) is { } raw ? raw - (at + 4) : 0;
                if (labels.Count > 0 && !labels.ContainsKey(target) && RawOffset(target) == null)
                    throw new AssemblerException(l.Number, $"unknown label '{target}'");
                WriteS24(o, rel);
                return o.ToArray();
            };
        }
        if (op == Opcodes.LookupSwitch)
        {
            string dflt = c.Ident();
            c.Expect(',');
            c.Expect('[');
            var cases = new List<string>();
            while (!c.TryExpect(']'))
            {
                cases.Add(c.Ident());
                c.TryExpect(',');
            }
            c.End();
            if (cases.Count == 0) throw new AssemblerException(l.Number, "lookupswitch has no cases");
            return (labels, at) =>
            {
                var o = new MemoryStream();
                o.WriteByte((byte)op);
                int Rel(string name)
                {
                    if (labels.TryGetValue(name, out int t) && t >= 0) return t - at;
                    if (RawOffset(name) is { } raw) return raw - at;
                    if (labels.Count > 0) throw new AssemblerException(l.Number, $"unknown label '{name}'");
                    return 0;
                }
                WriteS24(o, Rel(dflt));
                WriteU30(o, (uint)(cases.Count - 1));
                foreach (var cs in cases) WriteS24(o, Rel(cs));
                return o.ToArray();
            };
        }

        switch (op)
        {
            case Opcodes.PushString:
            case Opcodes.DebugFile:
            case Opcodes.Dxns:
                U30(abc.InternString(c.String()));
                break;
            case Opcodes.PushInt:
                U30(abc.InternInt(c.Long()));
                break;
            case Opcodes.PushUInt:
                U30(abc.InternUInt((ulong)c.Long()));
                break;
            case Opcodes.PushDouble:
                U30(abc.InternDouble(c.Double()));
                break;
            case Opcodes.PushNamespace:
                U30(c.Namespace(abc));
                break;
            case Opcodes.PushByte:
                w.WriteByte((byte)(sbyte)c.Long());
                break;
            case Opcodes.PushShort:
                U30((ushort)(short)c.Long());
                break;
            case Opcodes.GetScopeObject:
                w.WriteByte((byte)c.Long());
                break;
            case Opcodes.GetSuper: case Opcodes.SetSuper: case Opcodes.GetDescendants: case Opcodes.FindPropStrict:
            case Opcodes.FindProperty: case Opcodes.FindDef: case Opcodes.GetLex: case Opcodes.SetProperty:
            case Opcodes.GetProperty: case Opcodes.InitProperty: case Opcodes.DeleteProperty: case Opcodes.Coerce:
            case Opcodes.AsType: case Opcodes.IsType:
                U30(c.Multiname(abc));
                break;
            case Opcodes.CallProperty: case Opcodes.CallPropLex: case Opcodes.CallPropVoid: case Opcodes.CallSuper:
            case Opcodes.CallSuperVoid: case Opcodes.ConstructProp:
                U30(c.Multiname(abc));
                c.Expect(',');
                U30(c.Long());
                break;
            case Opcodes.Debug:
            {
                w.WriteByte((byte)c.Long());
                c.Expect(',');
                U30(abc.InternString(c.String()));
                c.Expect(',');
                w.WriteByte((byte)c.Long());
                c.Expect(',');
                U30(c.Long());
                break;
            }
            default:
            {
                // Integer operands (registers, indices, argument counts)
                int expected = OperandCount(op);
                for (int k = 0; k < expected; k++)
                {
                    if (k > 0) c.Expect(',');
                    U30(c.Long());
                }
                break;
            }
        }
        c.End();
        var bytes = w.ToArray();
        return (_, _) => bytes;
    }

    /// <summary>"ofs00A4" with no matching label: the original address, used as is.</summary>
    static int? RawOffset(string label) =>
        label.StartsWith("ofs") && int.TryParse(label[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : null;

    static int OperandCount(int op) => op switch
    {
        Opcodes.HasNext2 or Opcodes.CallMethod or Opcodes.CallStatic => 2,
        Opcodes.Kill or Opcodes.GetLocal or Opcodes.SetLocal or Opcodes.IncLocal or Opcodes.DecLocal or Opcodes.IncLocalI
            or Opcodes.DecLocalI or Opcodes.NewFunction or Opcodes.Call or Opcodes.Construct or Opcodes.ConstructSuper
            or Opcodes.ApplyType or Opcodes.NewObject or Opcodes.NewArray or Opcodes.NewClass or Opcodes.NewCatch
            or Opcodes.GetOuterScope or Opcodes.GetSlot or Opcodes.SetSlot or Opcodes.GetGlobalSlot or Opcodes.SetGlobalSlot
            or Opcodes.DebugLine or Opcodes.BkptLine or 0x22 => 1,
        _ => 0,
    };

    static void WriteU30(Stream s, uint v)
    {
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            s.WriteByte(b);
        } while (v != 0);
    }

    static void WriteS24(Stream s, int v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
    }

    // ------------------------------------------------------------------ stack analysis

    /// <summary>Maximum stack and scope depth, and the number of registers used.</summary>
    static (int MaxStack, int MaxScope, int Locals) Analyze(AbcFile abc, byte[] code, List<ExceptionInfo> exceptions, MethodInfo m)
    {
        var instrs = Opcodes.Disassemble(code);
        var index = new Dictionary<int, int>();
        for (int i = 0; i < instrs.Count; i++) index[instrs[i].Offset] = i;
        var depth = new int[instrs.Count];
        var scope = new int[instrs.Count];
        Array.Fill(depth, -1);
        var work = new Stack<(int I, int D, int S)>();
        if (instrs.Count > 0) work.Push((0, 0, 0));
        foreach (var e in exceptions)
            if (index.TryGetValue(e.Target, out int t)) work.Push((t, 1, 0));
        int maxStack = 0, maxScope = 0;
        int locals = m.ParamTypes.Length + 1 + (m.NeedRest || m.NeedArguments ? 1 : 0);
        foreach (var ins in instrs)
        {
            int reg = ins.Op switch
            {
                >= Opcodes.GetLocal0 and <= Opcodes.GetLocal3 => ins.Op - Opcodes.GetLocal0,
                >= Opcodes.SetLocal0 and <= Opcodes.SetLocal3 => ins.Op - Opcodes.SetLocal0,
                Opcodes.GetLocal or Opcodes.SetLocal or Opcodes.Kill or Opcodes.IncLocal or Opcodes.DecLocal
                    or Opcodes.IncLocalI or Opcodes.DecLocalI => ins.Args[0],
                Opcodes.HasNext2 => Math.Max(ins.Args[0], ins.Args[1]),
                _ => -1,
            };
            locals = Math.Max(locals, reg + 1);
        }
        int guard = 0;
        while (work.Count > 0 && guard++ < 1_000_000)
        {
            var (i, d, s) = work.Pop();
            while (i < instrs.Count)
            {
                if (depth[i] >= 0) break;
                depth[i] = d;
                scope[i] = s;
                var ins = instrs[i];
                var (pop, push) = Effect(abc, ins);
                d = Math.Max(0, d - pop) + push;
                maxStack = Math.Max(maxStack, d);
                if (ins.Op is Opcodes.PushScope or Opcodes.PushWith) s++;
                else if (ins.Op == Opcodes.PopScope) s = Math.Max(0, s - 1);
                maxScope = Math.Max(maxScope, s);
                foreach (var t in ins.Targets)
                    if (index.TryGetValue(t, out int ti)) work.Push((ti, d, s));
                if (Opcodes.EndsFlow(ins.Op)) break;
                i++;
            }
        }
        return (maxStack, maxScope, locals);
    }

    static (int Pop, int Push) Effect(AbcFile abc, Instr ins)
    {
        int Rt(int mn)
        {
            var m = abc.Mn(mn);
            return (m.HasRuntimeName ? 1 : 0) + (m.HasRuntimeNs ? 1 : 0);
        }
        var a = ins.Args;
        return ins.Op switch
        {
            Opcodes.PushNull or Opcodes.PushUndefined or Opcodes.PushByte or Opcodes.PushShort or Opcodes.PushTrue
                or Opcodes.PushFalse or Opcodes.PushNaN or Opcodes.PushString or Opcodes.PushInt or Opcodes.PushUInt
                or Opcodes.PushDouble or Opcodes.PushNamespace or 0x22 or Opcodes.GetLocal or (>= Opcodes.GetLocal0 and <= Opcodes.GetLocal3)
                or Opcodes.GetGlobalScope or Opcodes.GetScopeObject or Opcodes.GetOuterScope or Opcodes.NewActivation
                or Opcodes.NewCatch or Opcodes.GetLex or Opcodes.NewFunction or Opcodes.GetGlobalSlot or Opcodes.HasNext2 => (0, 1),
            Opcodes.Dup => (1, 2),
            Opcodes.Swap => (2, 2),
            Opcodes.FindPropStrict or Opcodes.FindProperty or Opcodes.FindDef => (Rt(a[0]), 1),
            Opcodes.Pop or Opcodes.PushScope or Opcodes.PushWith or Opcodes.SetLocal or (>= Opcodes.SetLocal0 and <= Opcodes.SetLocal3)
                or Opcodes.IfTrue or Opcodes.IfFalse or Opcodes.LookupSwitch or Opcodes.Throw or Opcodes.ReturnValue
                or Opcodes.SetGlobalSlot or Opcodes.DxnsLate => (1, 0),
            >= Opcodes.IfNlt and <= Opcodes.IfNge or >= Opcodes.IfEq and <= Opcodes.IfStrictNe => (2, 0),
            Opcodes.SetSlot or (>= Opcodes.Si8 and <= Opcodes.Sf64) => (2, 0),
            Opcodes.GetProperty or Opcodes.DeleteProperty or Opcodes.GetSuper or Opcodes.GetDescendants => (1 + Rt(a[0]), 1),
            Opcodes.SetProperty or Opcodes.InitProperty or Opcodes.SetSuper => (2 + Rt(a[0]), 0),
            Opcodes.CallProperty or Opcodes.CallPropLex or Opcodes.CallSuper or Opcodes.ConstructProp => (a[1] + 1 + Rt(a[0]), 1),
            Opcodes.CallPropVoid or Opcodes.CallSuperVoid => (a[1] + 1 + Rt(a[0]), 0),
            Opcodes.Call => (a[0] + 2, 1),
            Opcodes.Construct or Opcodes.ApplyType => (a[0] + 1, 1),
            Opcodes.ConstructSuper => (a[0] + 1, 0),
            Opcodes.CallMethod or Opcodes.CallStatic => (a[1] + 1, 1),
            Opcodes.NewObject => (a[0] * 2, 1),
            Opcodes.NewArray => (a[0], 1),
            Opcodes.IsType or Opcodes.AsType or Opcodes.Coerce => (1, 1),
            >= Opcodes.Add and <= Opcodes.In or Opcodes.AddI or Opcodes.SubtractI or Opcodes.MultiplyI
                or Opcodes.NextName or Opcodes.NextValue or Opcodes.HasNext or Opcodes.AsTypeLate => (2, 1),
            _ when Opcodes.Name(ins.Op) is var n && (n.StartsWith("convert") || n.StartsWith("coerce") || n.StartsWith("negate")
                || n.StartsWith("increment") || n.StartsWith("decrement") || n is "typeof" or "not" or "bitnot" or "getslot"
                or "li8" or "li16" or "li32" or "lf32" or "lf64" or "sxi1" or "sxi8" or "sxi16" or "esc_xelem" or "esc_xattr"
                or "checkfilter" or "newclass") => (1, 1),
            _ => (0, 0),
        };
    }

    // ------------------------------------------------------------------ operand parsing

    sealed class Cursor(string text, int line)
    {
        int _p;

        void Skip()
        {
            while (_p < text.Length && char.IsWhiteSpace(text[_p])) _p++;
        }

        AssemblerException Error(string what) => new(line, $"expected {what} near {(_p < text.Length ? "'" + text[_p..] + "'" : "end of line")}");

        public void End()
        {
            Skip();
            if (_p < text.Length) throw new AssemblerException(line, $"unexpected extra operand: '{text[_p..]}'");
        }

        public void Expect(char c)
        {
            if (!TryExpect(c)) throw Error($"'{c}'");
        }

        public bool TryExpect(char c)
        {
            Skip();
            if (_p < text.Length && text[_p] == c)
            {
                _p++;
                return true;
            }
            return false;
        }

        public void Word(string w)
        {
            Skip();
            if (!text[_p..].StartsWith(w, StringComparison.Ordinal)) throw Error($"'{w}'");
            _p += w.Length;
        }

        public string Ident()
        {
            Skip();
            int start = _p;
            while (_p < text.Length && (char.IsLetterOrDigit(text[_p]) || text[_p] is '_' or '$' or '.')) _p++;
            if (_p == start) throw Error("a name");
            return text[start.._p];
        }

        public long Long()
        {
            Skip();
            int start = _p;
            if (_p < text.Length && text[_p] is '-' or '+') _p++;
            while (_p < text.Length && char.IsDigit(text[_p])) _p++;
            if (!long.TryParse(text[start.._p], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) throw Error("an integer");
            return v;
        }

        public double Double()
        {
            Skip();
            int start = _p;
            while (_p < text.Length && !char.IsWhiteSpace(text[_p]) && text[_p] != ',') _p++;
            string s = text[start.._p];
            return s switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                _ => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : throw Error("a number"),
            };
        }

        public string String()
        {
            Skip();
            if (_p >= text.Length || text[_p] != '"') throw Error("a quoted string");
            _p++;
            var sb = new StringBuilder();
            while (_p < text.Length && text[_p] != '"')
            {
                char c = text[_p++];
                if (c != '\\' || _p >= text.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char e = text[_p++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'x':
                        sb.Append((char)Convert.ToInt32(text.Substring(_p, 2), 16));
                        _p += 2;
                        break;
                    default: sb.Append(e); break;
                }
            }
            if (_p >= text.Length) throw Error("a closing quote");
            _p++;
            return sb.ToString();
        }

        public int Namespace(AbcFile abc)
        {
            string kind = Ident();
            Expect('(');
            if (kind == "AnyNamespace")
            {
                Expect(')');
                return 0;
            }
            string name = String();
            int? explicitIndex = null;
            if (TryExpect(','))
            {
                Expect('#');
                explicitIndex = (int)Long();
            }
            Expect(')');
            var k = kind switch
            {
                "PackageNamespace" => NsKind.Package, "PackageInternalNs" => NsKind.PackageInternal,
                "PrivateNamespace" => NsKind.Private, "ProtectedNamespace" => NsKind.Protected,
                "StaticProtectedNs" => NsKind.StaticProtected, "ExplicitNamespace" => NsKind.Explicit, "Namespace" => NsKind.Namespace,
                _ => throw new AssemblerException(line, $"unknown namespace kind '{kind}'"),
            };
            if (explicitIndex is { } ei)
            {
                if (ei <= 0 || ei >= abc.Namespaces.Length || abc.Namespaces[ei].Kind != k || abc.Namespaces[ei].Name != name)
                    throw new AssemblerException(line, $"namespace #{ei} not found");
                return ei;
            }
            return abc.InternNamespace(k, name);
        }

        int NsSet(AbcFile abc)
        {
            Expect('[');
            var list = new List<int>();
            while (!TryExpect(']'))
            {
                list.Add(Namespace(abc));
                TryExpect(',');
            }
            return abc.InternNsSet([.. list]);
        }

        public int Multiname(AbcFile abc)
        {
            Skip();
            if (TryExpect('*')) return 0;
            // "#mn12": multiname by pool index (used by the compiler to keep generated p-code compact)
            if (TryExpect('#'))
            {
                Word("mn");
                int index = (int)Long();
                if (index <= 0 || index >= abc.Multinames.Length) throw new AssemblerException(line, $"multiname #{index} does not exist");
                return index;
            }
            string kind = Ident();
            if (!Enum.TryParse<MnKind>(kind, out var mk)) throw new AssemblerException(line, $"unknown multiname kind '{kind}'");
            var m = new Multiname { Kind = mk };
            Expect('(');
            switch (mk)
            {
                case MnKind.QName:
                case MnKind.QNameA:
                    m.Ns = Namespace(abc);
                    Expect(',');
                    m.Name = abc.InternString(String());
                    break;
                case MnKind.RTQName:
                case MnKind.RTQNameA:
                    m.Name = abc.InternString(String());
                    break;
                case MnKind.RTQNameL:
                case MnKind.RTQNameLA:
                    break;
                case MnKind.Multiname:
                case MnKind.MultinameA:
                    m.Name = abc.InternString(String());
                    Expect(',');
                    m.NsSet = NsSet(abc);
                    break;
                case MnKind.MultinameL:
                case MnKind.MultinameLA:
                    m.NsSet = NsSet(abc);
                    break;
                case MnKind.TypeName:
                {
                    m.QName = Multiname(abc);
                    Expect('<');
                    var ps = new List<int>();
                    while (!TryExpect('>'))
                    {
                        ps.Add(Multiname(abc));
                        TryExpect(',');
                    }
                    m.Params = [.. ps];
                    break;
                }
            }
            Expect(')');
            return abc.InternMultiname(m);
        }
    }
}
