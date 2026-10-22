using System.Globalization;
using System.Text;
using MortisFlash.Avm2;

namespace MortisFlash.Avm1;

/// <summary>
/// AVM1 p-code assembler: reads back a listing produced by <see cref="Avm1Parser.Disassemble"/> (possibly edited) and
/// emits the bytecode. Function, with and try bodies are “{ … }” blocks placed under their owning action.
/// </summary>
public static class Avm1Assembler
{
    static readonly Dictionary<string, int> Codes = BuildCodes();

    static Dictionary<string, int> BuildCodes()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < 256; c++)
        {
            string n = Avm1Names.Name(c);
            if (!n.StartsWith("Unknown_")) d.TryAdd(n, c);
        }
        return d;
    }

    sealed class Node
    {
        public int Line;
        public string? Label;
        public string? Name;
        public string Args = "";
        public List<Node>? Body, Catch, Finally;
    }

    public static byte[] Assemble(string text)
    {
        var rows = text.Replace("\r", "").Split('\n');
        int pos = 0;
        var nodes = ParseBlock(rows, ref pos, topLevel: true);
        var ctx = new Emitter();
        ctx.Emit(nodes);
        return ctx.Finish();
    }

    /// <summary>
    /// Single-stream emission: nested bodies follow their action, block sizes are back-patched afterwards,
    /// and jumps are resolved against absolute addresses (a jump may exit a with or try block).
    /// </summary>
    sealed class Emitter
    {
        readonly MemoryStream _out = new();
        readonly List<string> _pool = [];
        readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        readonly List<(int Pos, int End, string Target, int Line)> _jumps = [];

        public void Emit(List<Node> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Label != null && !_labels.TryAdd(n.Label, (int)_out.Position))
                    throw new AssemblerException(n.Line, $"duplicate label '{n.Label}'");
                if (n.Name == null) continue;
                var (header, slots, blocks) = Encode(n, _pool);
                int at = (int)_out.Position;
                _out.Write(header);
                if (n.Name.Equals("Jump", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("If", StringComparison.OrdinalIgnoreCase))
                    _jumps.Add((at + 3, at + header.Length, n.Args.Trim(), n.Line));
                for (int k = 0; k < blocks.Count; k++)
                {
                    int start = (int)_out.Position;
                    if (blocks[k] != null) Emit(blocks[k]!);
                    int size = (int)_out.Position - start;
                    if (size > 0xFFFF) throw new AssemblerException(n.Line, "block too long (64 KB maximum)");
                    long save = _out.Position;
                    _out.Position = at + slots[k];
                    _out.WriteByte((byte)size);
                    _out.WriteByte((byte)(size >> 8));
                    _out.Position = save;
                }
            }
        }

        public byte[] Finish()
        {
            var bytes = _out.ToArray();
            foreach (var (pos, end, target, line) in _jumps)
            {
                int abs;
                if (_labels.TryGetValue(target, out int t)) abs = t;
                else if (target.StartsWith("loc") && int.TryParse(target[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int raw)) abs = raw;
                else throw new AssemblerException(line, $"unknown label '{target}'");
                int rel = abs - end;
                if (rel is < short.MinValue or > short.MaxValue) throw new AssemblerException(line, "jump out of range");
                bytes[pos] = (byte)rel;
                bytes[pos + 1] = (byte)(rel >> 8);
            }
            return bytes;
        }
    }

    static List<Node> ParseBlock(string[] rows, ref int pos, bool topLevel)
    {
        var list = new List<Node>();
        string? pendingLabel = null;
        while (pos < rows.Length)
        {
            int lineNo = pos + 1;
            string row = StripComment(rows[pos]).Trim();
            pos++;
            if (row.Length == 0) continue;
            if (row == "}")
            {
                if (topLevel) throw new AssemblerException(lineNo, "'}' without an open block");
                if (pendingLabel != null) list.Add(new Node { Line = lineNo, Label = pendingLabel });
                return list;
            }
            if (row == "{" || row is "catch {" or "finally {")
            {
                var owner = list.LastOrDefault(n => n.Name != null) ?? throw new AssemblerException(lineNo, "block without an owning action");
                var block = ParseBlock(rows, ref pos, topLevel: false);
                if (row == "catch {") owner.Catch = block;
                else if (row == "finally {") owner.Finally = block;
                else owner.Body = block;
                continue;
            }
            int colon = LabelEnd(row);
            if (colon > 0)
            {
                // Several labels in a row all mark the same position
                if (pendingLabel != null) list.Add(new Node { Line = lineNo, Label = pendingLabel });
                pendingLabel = row[..colon];
                row = row[(colon + 1)..].Trim();
                if (row.Length == 0) continue;
            }
            int sp = row.IndexOf(' ');
            var node = new Node
            {
                Line = lineNo, Label = pendingLabel, Name = sp < 0 ? row : row[..sp], Args = sp < 0 ? "" : row[(sp + 1)..].Trim(),
            };
            pendingLabel = null;
            list.Add(node);
        }
        if (!topLevel) throw new AssemblerException(rows.Length, "missing '}'");
        if (pendingLabel != null) list.Add(new Node { Line = rows.Length, Label = pendingLabel });
        return list;
    }

    static int LabelEnd(string row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            char c = row[i];
            if (c == ':') return i > 0 ? i : -1;
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$')) return -1;
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

    /// <summary>
    /// Action header, offsets (within the header) of the block sizes to back-patch, and the nested blocks
    /// to emit right after it, in order.
    /// </summary>
    static (byte[] Header, List<int> Slots, List<List<Node>?> Blocks) Encode(Node n, List<string> pool)
    {
        if (!Codes.TryGetValue(n.Name!, out int code)) throw new AssemblerException(n.Line, $"unknown action '{n.Name}'");
        var c = new Cursor(n.Args, n.Line);
        var p = new MemoryStream();
        var slots = new List<int>();
        var blocks = new List<List<Node>?>();
        void Slot(List<Node>? block)
        {
            slots.Add(3 + (int)p.Position);
            blocks.Add(block);
            U16(p, 0);
        }
        switch (code)
        {
            case 0x81: // GotoFrame
                U16(p, (int)c.Long());
                break;
            case 0x83: // GetURL
                Str(p, c.String());
                c.Expect(',');
                Str(p, c.String());
                break;
            case 0x87: // StoreRegister
            case 0x8D: // WaitForFrame2
            case 0x9A: // GetURL2
                p.WriteByte((byte)c.Long());
                break;
            case 0x88: // ConstantPool
            {
                var items = new List<string>();
                while (!c.AtEnd)
                {
                    items.Add(c.String());
                    c.TryExpect(',');
                }
                U16(p, items.Count);
                foreach (var s in items) Str(p, s);
                pool.Clear();
                pool.AddRange(items);
                break;
            }
            case 0x8A: // WaitForFrame
                U16(p, (int)c.Long());
                c.Expect(',');
                p.WriteByte((byte)c.Long());
                break;
            case 0x8B: // SetTarget
            case 0x8C: // GoToLabel
                Str(p, c.String());
                break;
            case 0x96: // Push
                while (!c.AtEnd)
                {
                    PushValue(p, c, pool);
                    c.TryExpect(',');
                }
                break;
            case 0x99:
            case 0x9D:
                c.Token(); // label, resolved at the end
                U16(p, 0);
                break;
            case 0x9F: // GotoFrame2
            {
                int flags = (int)c.Long();
                p.WriteByte((byte)flags);
                if (c.TryExpect(',')) U16(p, (int)c.Long());
                else if ((flags & 2) != 0) U16(p, 0);
                break;
            }
            case 0x9B: // DefineFunction "name" (a, b)
            {
                Str(p, c.String());
                var ps = c.Params();
                U16(p, ps.Count);
                foreach (var (_, name) in ps) Str(p, name);
                Slot(n.Body ?? []);
                break;
            }
            case 0x8E: // DefineFunction2 "name" (r1:a, b) registers N, flags 0xXXXX
            {
                Str(p, c.String());
                var ps = c.Params();
                c.Word("registers");
                int regs = (int)c.Long();
                c.Expect(',');
                c.Word("flags");
                int flags = (int)c.Long();
                U16(p, ps.Count);
                p.WriteByte((byte)regs);
                U16(p, flags);
                foreach (var (reg, name) in ps)
                {
                    p.WriteByte((byte)reg);
                    Str(p, name);
                }
                Slot(n.Body ?? []);
                break;
            }
            case 0x94: // With
                Slot(n.Body ?? []);
                break;
            case 0x8F: // Try ["name" | registerN]
            {
                int flags = (n.Catch != null ? 1 : 0) | (n.Finally != null ? 2 : 0);
                string? catchName = null;
                int catchReg = -1;
                if (!c.AtEnd)
                {
                    if (c.Peek('"')) catchName = c.String();
                    else catchReg = c.Register();
                }
                if (catchReg >= 0) flags |= 4;
                p.WriteByte((byte)flags);
                Slot(n.Body ?? []);
                Slot(n.Catch);
                Slot(n.Finally);
                if (catchReg >= 0) p.WriteByte((byte)catchReg);
                else Str(p, catchName ?? "");
                break;
            }
        }
        c.End();
        if (code < 0x80) return ([(byte)code], slots, blocks);
        var payload = p.ToArray();
        var header = new byte[3 + payload.Length];
        header[0] = (byte)code;
        header[1] = (byte)payload.Length;
        header[2] = (byte)(payload.Length >> 8);
        payload.CopyTo(header, 3);
        return (header, slots, blocks);
    }

    static void PushValue(Stream p, Cursor c, List<string> pool)
    {
        if (c.Peek('"'))
        {
            string s = c.String();
            int idx = pool.IndexOf(s);
            if (idx is >= 0 and < 256)
            {
                p.WriteByte(8);
                p.WriteByte((byte)idx);
            }
            else if (idx >= 256)
            {
                p.WriteByte(9);
                U16(p, idx);
            }
            else
            {
                p.WriteByte(0);
                Str(p, s);
            }
            return;
        }
        string word = c.Token();
        switch (word)
        {
            case "null": p.WriteByte(2); return;
            case "undefined": p.WriteByte(3); return;
            case "true": p.WriteByte(5); p.WriteByte(1); return;
            case "false": p.WriteByte(5); p.WriteByte(0); return;
        }
        if (word.StartsWith("register", StringComparison.Ordinal) && int.TryParse(word[8..], out int reg))
        {
            p.WriteByte(4);
            p.WriteByte((byte)reg);
            return;
        }
        if (long.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l) && l is >= int.MinValue and <= int.MaxValue)
        {
            p.WriteByte(7);
            p.Write(BitConverter.GetBytes((int)l));
            return;
        }
        double d = word switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ => double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw c.Error("a value"),
        };
        // AVM1 double: high-order 32-bit word first
        long bits = BitConverter.DoubleToInt64Bits(d);
        p.WriteByte(6);
        p.Write(BitConverter.GetBytes((int)(bits >> 32)));
        p.Write(BitConverter.GetBytes((int)bits));
    }

    static void U16(Stream s, int v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
    }

    static void Str(Stream s, string v)
    {
        s.Write(Encoding.UTF8.GetBytes(v));
        s.WriteByte(0);
    }

    sealed class Cursor(string text, int line)
    {
        int _p;

        void Skip()
        {
            while (_p < text.Length && char.IsWhiteSpace(text[_p])) _p++;
        }

        public bool AtEnd
        {
            get
            {
                Skip();
                return _p >= text.Length;
            }
        }

        public AssemblerException Error(string what) => new(line, $"expected {what} near '{(_p < text.Length ? text[_p..] : "end of line")}'");

        public void End()
        {
            if (!AtEnd) throw new AssemblerException(line, $"unexpected extra operand: '{text[_p..]}'");
        }

        public bool Peek(char c)
        {
            Skip();
            return _p < text.Length && text[_p] == c;
        }

        public void Expect(char c)
        {
            if (!TryExpect(c)) throw Error($"'{c}'");
        }

        public bool TryExpect(char c)
        {
            if (!Peek(c)) return false;
            _p++;
            return true;
        }

        public void Word(string w)
        {
            Skip();
            if (!text[_p..].StartsWith(w, StringComparison.Ordinal)) throw Error($"'{w}'");
            _p += w.Length;
        }

        public string Token()
        {
            Skip();
            int start = _p;
            while (_p < text.Length && !char.IsWhiteSpace(text[_p]) && text[_p] is not (',' or ')')) _p++;
            if (_p == start) throw Error("a value");
            return text[start.._p];
        }

        public long Long()
        {
            string t = Token();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) return h;
            return long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) ? v : throw Error("an integer");
        }

        public int Register()
        {
            string t = Token();
            return t.StartsWith("register") && int.TryParse(t[8..], out int r) ? r : throw Error("registerN");
        }

        public string String()
        {
            Skip();
            if (_p >= text.Length || text[_p] != '"') throw Error("a quoted string");
            _p++;
            var sb = new StringBuilder();
            while (_p < text.Length && text[_p] != '"')
            {
                char ch = text[_p++];
                if (ch != '\\' || _p >= text.Length)
                {
                    sb.Append(ch);
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

        /// <summary>(a, b) or (r1:a, b): register (0 = none) and name of each parameter.</summary>
        public List<(int Reg, string Name)> Params()
        {
            Expect('(');
            var list = new List<(int, string)>();
            while (!TryExpect(')'))
            {
                string t = Token();
                int reg = 0;
                string name = t;
                int colon = t.IndexOf(':');
                if (colon > 0 && t[0] == 'r' && int.TryParse(t[1..colon], out int r))
                {
                    reg = r;
                    name = t[(colon + 1)..];
                }
                list.Add((reg, name));
                TryExpect(',');
            }
            return list;
        }
    }
}
