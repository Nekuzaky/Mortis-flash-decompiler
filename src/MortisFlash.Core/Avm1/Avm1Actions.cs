using System.Globalization;
using MortisFlash.Swf;

namespace MortisFlash.Avm1;

/// <summary>Value pushed by the Push action.</summary>
public readonly record struct PushValue(int Type, object? Value)
{
    // 0 string, 1 float, 2 null, 3 undefined, 4 register, 5 boolean, 6 double, 7 integer, 8/9 constant (resolved to a string)
    public override string ToString() => Type switch
    {
        0 or 8 or 9 => Decompiler.LitExpr.Quote((string)Value!),
        2 => "null",
        3 => "undefined",
        4 => $"register{Value}",
        5 => (bool)Value! ? "true" : "false",
        1 or 6 => Decompiler.LitExpr.FormatNumber(Convert.ToDouble(Value, CultureInfo.InvariantCulture)),
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "",
    };
}

public sealed class Avm1Param
{
    public int Register;
    public string Name = "";
}

public sealed class Avm1Action
{
    public int Offset;
    public int Code;
    /// <summary>Size of the action itself (header + payload), excluding nested bodies.</summary>
    public int Size;
    /// <summary>Size including nested bodies (function, with, try).</summary>
    public int TotalSize;
    public int Target = -1;               // Jump / If: absolute address
    public List<PushValue>? Values;       // Push
    public string? Str1, Str2;            // GetURL, GoToLabel, SetTarget, function name, etc.
    public int Int1, Int2;                // GotoFrame, WaitForFrame, StoreRegister, GetURL2…
    public List<string>? Pool;            // ConstantPool
    public List<Avm1Param>? Params;       // DefineFunction(2)
    public int Flags;                     // DefineFunction2, Try
    public int RegisterCount;
    public List<Avm1Action>? Body;        // function / with / try
    public List<Avm1Action>? Catch, Finally;
    public string? CatchName;
    public int CatchRegister = -1;

    public string Name => Avm1Names.Name(Code);
    public override string ToString() => $"{Offset:X4} {Name}";
}

public static class Avm1Names
{
    static readonly Dictionary<int, string> Names = new()
    {
        [0x00] = "End", [0x04] = "NextFrame", [0x05] = "PrevFrame", [0x06] = "Play", [0x07] = "Stop", [0x08] = "ToggleQuality",
        [0x09] = "StopSounds", [0x0A] = "Add", [0x0B] = "Subtract", [0x0C] = "Multiply", [0x0D] = "Divide", [0x0E] = "Equals",
        [0x0F] = "Less", [0x10] = "And", [0x11] = "Or", [0x12] = "Not", [0x13] = "StringEquals", [0x14] = "StringLength",
        [0x15] = "StringExtract", [0x17] = "Pop", [0x18] = "ToInteger", [0x1C] = "GetVariable", [0x1D] = "SetVariable",
        [0x20] = "SetTarget2", [0x21] = "StringAdd", [0x22] = "GetProperty", [0x23] = "SetProperty", [0x24] = "CloneSprite",
        [0x25] = "RemoveSprite", [0x26] = "Trace", [0x27] = "StartDrag", [0x28] = "EndDrag", [0x29] = "StringLess",
        [0x2A] = "Throw", [0x2B] = "CastOp", [0x2C] = "ImplementsOp", [0x30] = "RandomNumber", [0x31] = "MBStringLength",
        [0x32] = "CharToAscii", [0x33] = "AsciiToChar", [0x34] = "GetTime", [0x35] = "MBStringExtract", [0x36] = "MBCharToAscii",
        [0x37] = "MBAsciiToChar", [0x3A] = "Delete", [0x3B] = "Delete2", [0x3C] = "DefineLocal", [0x3D] = "CallFunction",
        [0x3E] = "Return", [0x3F] = "Modulo", [0x40] = "NewObject", [0x41] = "DefineLocal2", [0x42] = "InitArray",
        [0x43] = "InitObject", [0x44] = "TypeOf", [0x45] = "TargetPath", [0x46] = "Enumerate", [0x47] = "Add2",
        [0x48] = "Less2", [0x49] = "Equals2", [0x4A] = "ToNumber", [0x4B] = "ToString", [0x4C] = "PushDuplicate",
        [0x4D] = "StackSwap", [0x4E] = "GetMember", [0x4F] = "SetMember", [0x50] = "Increment", [0x51] = "Decrement",
        [0x52] = "CallMethod", [0x53] = "NewMethod", [0x54] = "InstanceOf", [0x55] = "Enumerate2", [0x60] = "BitAnd",
        [0x61] = "BitOr", [0x62] = "BitXor", [0x63] = "BitLShift", [0x64] = "BitRShift", [0x65] = "BitURShift",
        [0x66] = "StrictEquals", [0x67] = "Greater", [0x68] = "StringGreater", [0x69] = "Extends", [0x81] = "GotoFrame",
        [0x83] = "GetURL", [0x87] = "StoreRegister", [0x88] = "ConstantPool", [0x8A] = "WaitForFrame", [0x8B] = "SetTarget",
        [0x8C] = "GoToLabel", [0x8D] = "WaitForFrame2", [0x8E] = "DefineFunction2", [0x8F] = "Try", [0x94] = "With",
        [0x96] = "Push", [0x99] = "Jump", [0x9A] = "GetURL2", [0x9B] = "DefineFunction", [0x9D] = "If", [0x9E] = "Call",
        [0x9F] = "GotoFrame2",
    };

    public static string Name(int code) => Names.TryGetValue(code, out var n) ? n : $"Unknown_{code:X2}";
}

/// <summary>AVM1 bytecode reader; function, with and try bodies are nested inside their owning action.</summary>
public static class Avm1Parser
{
    public static List<Avm1Action> Parse(byte[] data, int version)
    {
        var pool = new List<string>();
        return ParseRange(data, 0, data.Length, version, ref pool, 0);
    }

    static List<Avm1Action> ParseRange(byte[] data, int start, int end, int version, ref List<string> pool, int depth)
    {
        var list = new List<Avm1Action>();
        var r = new SwfReader(data, 0, Math.Min(end, data.Length)) { Version = version, Position = start };
        while (r.Remaining > 0)
        {
            int offset = r.Position;
            int code = r.U8();
            var a = new Avm1Action { Offset = offset, Code = code };
            if (code == 0)
            {
                a.Size = a.TotalSize = 1;
                list.Add(a);
                break;
            }
            int len = 0;
            if (code >= 0x80)
            {
                if (r.Remaining < 2) break;
                len = r.U16();
            }
            int payloadStart = r.Position;
            int payloadEnd = Math.Min(payloadStart + len, r.End);
            a.Size = payloadEnd - offset;
            var p = new SwfReader(data, 0, payloadEnd) { Version = version, Position = payloadStart };
            try
            {
                switch (code)
                {
                    case 0x81: a.Int1 = p.U16(); break;
                    case 0x83: a.Str1 = p.String(); a.Str2 = p.String(); break;
                    case 0x87: a.Int1 = p.U8(); break;
                    case 0x88:
                    {
                        int n = p.U16();
                        var newPool = new List<string>(n);
                        for (int i = 0; i < n && p.Remaining > 0; i++) newPool.Add(p.String());
                        pool = newPool;
                        a.Pool = newPool;
                        break;
                    }
                    case 0x8A: a.Int1 = p.U16(); a.Int2 = p.U8(); break;
                    case 0x8B: a.Str1 = p.String(); break;
                    case 0x8C: a.Str1 = p.String(); break;
                    case 0x8D: a.Int1 = p.U8(); break;
                    case 0x96: a.Values = ReadPush(p, pool); break;
                    case 0x99:
                    case 0x9D:
                        a.Int1 = p.S16();
                        a.Target = payloadEnd + a.Int1;
                        break;
                    case 0x9A: a.Int1 = p.U8(); break;
                    case 0x9F:
                        a.Int1 = p.U8();
                        if ((a.Int1 & 2) != 0) a.Int2 = p.U16();
                        break;
                    case 0x9B:
                    {
                        a.Str1 = p.String();
                        int n = p.U16();
                        a.Params = [];
                        for (int i = 0; i < n; i++) a.Params.Add(new Avm1Param { Name = p.String() });
                        int codeSize = p.U16();
                        a.Body = depth < 64 ? ParseRange(data, payloadEnd, payloadEnd + codeSize, version, ref pool, depth + 1) : [];
                        payloadEnd += codeSize;
                        break;
                    }
                    case 0x8E:
                    {
                        a.Str1 = p.String();
                        int n = p.U16();
                        a.RegisterCount = p.U8();
                        a.Flags = p.U16();
                        a.Params = [];
                        for (int i = 0; i < n; i++) a.Params.Add(new Avm1Param { Register = p.U8(), Name = p.String() });
                        int codeSize = p.U16();
                        a.Body = depth < 64 ? ParseRange(data, payloadEnd, payloadEnd + codeSize, version, ref pool, depth + 1) : [];
                        payloadEnd += codeSize;
                        break;
                    }
                    case 0x94:
                    {
                        int size = p.U16();
                        a.Body = depth < 64 ? ParseRange(data, payloadEnd, payloadEnd + size, version, ref pool, depth + 1) : [];
                        payloadEnd += size;
                        break;
                    }
                    case 0x8F:
                    {
                        a.Flags = p.U8();
                        int trySize = p.U16(), catchSize = p.U16(), finallySize = p.U16();
                        if ((a.Flags & 4) != 0) a.CatchRegister = p.U8();
                        else a.CatchName = p.String();
                        int t0 = payloadEnd;
                        a.Body = ParseRange(data, t0, t0 + trySize, version, ref pool, depth + 1);
                        if ((a.Flags & 1) != 0) a.Catch = ParseRange(data, t0 + trySize, t0 + trySize + catchSize, version, ref pool, depth + 1);
                        if ((a.Flags & 2) != 0)
                            a.Finally = ParseRange(data, t0 + trySize + catchSize, t0 + trySize + catchSize + finallySize, version, ref pool, depth + 1);
                        payloadEnd += trySize + catchSize + finallySize;
                        break;
                    }
                }
            }
            catch (EndOfStreamException) { }
            a.TotalSize = Math.Min(payloadEnd, r.End) - offset;
            list.Add(a);
            r.Position = Math.Min(payloadEnd, r.End);
        }
        return list;
    }

    static List<PushValue> ReadPush(SwfReader p, List<string> pool)
    {
        var list = new List<PushValue>();
        while (p.Remaining > 0)
        {
            int t = p.U8();
            switch (t)
            {
                case 0: list.Add(new(0, p.String())); break;
                case 1: list.Add(new(1, (double)p.Float())); break;
                case 2: list.Add(new(2, null)); break;
                case 3: list.Add(new(3, null)); break;
                case 4: list.Add(new(4, (int)p.U8())); break;
                case 5: list.Add(new(5, p.U8() != 0)); break;
                case 6: list.Add(new(6, p.DoubleAvm1())); break;
                case 7: list.Add(new(7, (long)p.S32())); break;
                case 8:
                {
                    int i = p.U8();
                    list.Add(new(8, i < pool.Count ? pool[i] : $"§§constant{i}"));
                    break;
                }
                case 9:
                {
                    int i = p.U16();
                    list.Add(new(9, i < pool.Count ? pool[i] : $"§§constant{i}"));
                    break;
                }
                default:
                    return list;
            }
        }
        return list;
    }

    /// <summary>P-code listing (nested bodies are indented).</summary>
    public static string Disassemble(List<Avm1Action> actions, int indent = 0)
    {
        var sb = new System.Text.StringBuilder();
        var targets = actions.Where(a => a.Target >= 0).Select(a => a.Target).ToHashSet();
        string pad = new(' ', indent * 4);
        int listEnd = actions.Count > 0 ? actions[^1].Offset + actions[^1].TotalSize : 0;
        foreach (var a in actions)
        {
            if (targets.Contains(a.Offset)) sb.Append(pad).Append($"loc{a.Offset:X4}:\n");
            sb.Append(pad).Append("    ").Append(a.Name);
            switch (a.Code)
            {
                case 0x96: sb.Append(' ').Append(string.Join(", ", a.Values!)); break;
                case 0x99:
                case 0x9D: sb.Append($" loc{a.Target:X4}"); break;
                case 0x88: sb.Append(' ').Append(string.Join(", ", a.Pool!.Select(Decompiler.LitExpr.Quote))); break;
                case 0x83: sb.Append($" {Decompiler.LitExpr.Quote(a.Str1 ?? "")}, {Decompiler.LitExpr.Quote(a.Str2 ?? "")}"); break;
                case 0x8B:
                case 0x8C: sb.Append($" {Decompiler.LitExpr.Quote(a.Str1 ?? "")}"); break;
                case 0x81:
                case 0x87:
                case 0x9A:
                case 0x8D: sb.Append(' ').Append(a.Int1); break;
                case 0x9F: sb.Append(' ').Append(a.Int1).Append(a.Int2 != 0 ? $", {a.Int2}" : ""); break;
                case 0x8A: sb.Append($" {a.Int1}, {a.Int2}"); break;
                case 0x9B:
                case 0x8E:
                    sb.Append($" {Decompiler.LitExpr.Quote(a.Str1 ?? "")} ({string.Join(", ", a.Params!.Select(p => p.Register > 0 ? $"r{p.Register}:{p.Name}" : p.Name))})");
                    if (a.Code == 0x8E) sb.Append($" registers {a.RegisterCount}, flags 0x{a.Flags:X4}");
                    break;
                case 0x8F:
                    if (a.Catch != null) sb.Append(a.CatchRegister >= 0 ? $" register{a.CatchRegister}" : $" {Decompiler.LitExpr.Quote(a.CatchName ?? "")}");
                    break;
            }
            sb.Append('\n');
            if (a.Body != null)
            {
                sb.Append(pad).Append("    {\n").Append(Disassemble(a.Body, indent + 1)).Append(pad).Append("    }\n");
                if (a.Catch != null) sb.Append(pad).Append("    catch {\n").Append(Disassemble(a.Catch, indent + 1)).Append(pad).Append("    }\n");
                if (a.Finally != null) sb.Append(pad).Append("    finally {\n").Append(Disassemble(a.Finally, indent + 1)).Append(pad).Append("    }\n");
            }
        }
        // Jump to the end of the block (function exit)
        if (targets.Contains(listEnd)) sb.Append(pad).Append($"loc{listEnd:X4}:\n");
        return sb.ToString();
    }
}
