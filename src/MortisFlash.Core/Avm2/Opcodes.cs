namespace MortisFlash.Avm2;

public sealed class Instr
{
    public int Offset;
    public int Length;
    public int Op;
    public int[] Args = [];
    /// <summary>Target addresses (jumps: 1 target; lookupswitch: default, then cases).</summary>
    public int[] Targets = [];
    public string Name => Opcodes.Name(Op);
    public bool IsBranch => Opcodes.IsBranch(Op);
    public override string ToString() => $"{Offset:X4} {Name}";
}

public static class Opcodes
{
    public const int Bkpt = 0x01, Nop = 0x02, Throw = 0x03, GetSuper = 0x04, SetSuper = 0x05, Dxns = 0x06, DxnsLate = 0x07,
        Kill = 0x08, Label = 0x09, IfNlt = 0x0C, IfNle = 0x0D, IfNgt = 0x0E, IfNge = 0x0F, Jump = 0x10, IfTrue = 0x11,
        IfFalse = 0x12, IfEq = 0x13, IfNe = 0x14, IfLt = 0x15, IfLe = 0x16, IfGt = 0x17, IfGe = 0x18, IfStrictEq = 0x19,
        IfStrictNe = 0x1A, LookupSwitch = 0x1B, PushWith = 0x1C, PopScope = 0x1D, NextName = 0x1E, HasNext = 0x1F,
        PushNull = 0x20, PushUndefined = 0x21, NextValue = 0x23, PushByte = 0x24, PushShort = 0x25, PushTrue = 0x26,
        PushFalse = 0x27, PushNaN = 0x28, Pop = 0x29, Dup = 0x2A, Swap = 0x2B, PushString = 0x2C, PushInt = 0x2D,
        PushUInt = 0x2E, PushDouble = 0x2F, PushScope = 0x30, PushNamespace = 0x31, HasNext2 = 0x32,
        Li8 = 0x35, Li16 = 0x36, Li32 = 0x37, Lf32 = 0x38, Lf64 = 0x39, Si8 = 0x3A, Si16 = 0x3B, Si32 = 0x3C, Sf32 = 0x3D, Sf64 = 0x3E,
        NewFunction = 0x40, Call = 0x41, Construct = 0x42, CallMethod = 0x43, CallStatic = 0x44, CallSuper = 0x45,
        CallProperty = 0x46, ReturnVoid = 0x47, ReturnValue = 0x48, ConstructSuper = 0x49, ConstructProp = 0x4A,
        CallSuperId = 0x4B, CallPropLex = 0x4C, CallInterface = 0x4D, CallSuperVoid = 0x4E, CallPropVoid = 0x4F,
        Sxi1 = 0x50, Sxi8 = 0x51, Sxi16 = 0x52, ApplyType = 0x53, NewObject = 0x55, NewArray = 0x56,
        NewActivation = 0x57, NewClass = 0x58, GetDescendants = 0x59, NewCatch = 0x5A, FindPropStrict = 0x5D,
        FindProperty = 0x5E, FindDef = 0x5F, GetLex = 0x60, SetProperty = 0x61, GetLocal = 0x62, SetLocal = 0x63,
        GetGlobalScope = 0x64, GetScopeObject = 0x65, GetProperty = 0x66, GetOuterScope = 0x67, InitProperty = 0x68,
        DeleteProperty = 0x6A, GetSlot = 0x6C, SetSlot = 0x6D, GetGlobalSlot = 0x6E, SetGlobalSlot = 0x6F,
        ConvertS = 0x70, EscXElem = 0x71, EscXAttr = 0x72, ConvertI = 0x73, ConvertU = 0x74, ConvertD = 0x75,
        ConvertB = 0x76, ConvertO = 0x77, CheckFilter = 0x78, Coerce = 0x80, CoerceB = 0x81, CoerceA = 0x82,
        CoerceI = 0x83, CoerceD = 0x84, CoerceS = 0x85, AsType = 0x86, AsTypeLate = 0x87, CoerceU = 0x88, CoerceO = 0x89,
        Negate = 0x90, Increment = 0x91, IncLocal = 0x92, Decrement = 0x93, DecLocal = 0x94, TypeOf = 0x95, Not = 0x96,
        BitNot = 0x97, Add = 0xA0, Subtract = 0xA1, Multiply = 0xA2, Divide = 0xA3, Modulo = 0xA4, LShift = 0xA5,
        RShift = 0xA6, URShift = 0xA7, BitAnd = 0xA8, BitOr = 0xA9, BitXor = 0xAA, EqualsOp = 0xAB, StrictEquals = 0xAC,
        LessThan = 0xAD, LessEquals = 0xAE, GreaterThan = 0xAF, GreaterEquals = 0xB0, InstanceOf = 0xB1, IsType = 0xB2,
        IsTypeLate = 0xB3, In = 0xB4, IncrementI = 0xC0, DecrementI = 0xC1, IncLocalI = 0xC2, DecLocalI = 0xC3,
        NegateI = 0xC4, AddI = 0xC5, SubtractI = 0xC6, MultiplyI = 0xC7, GetLocal0 = 0xD0, GetLocal1 = 0xD1,
        GetLocal2 = 0xD2, GetLocal3 = 0xD3, SetLocal0 = 0xD4, SetLocal1 = 0xD5, SetLocal2 = 0xD6, SetLocal3 = 0xD7,
        Debug = 0xEF, DebugLine = 0xF0, DebugFile = 0xF1, BkptLine = 0xF2, Timestamp = 0xF3;

    // Operand format: u = u30, b = byte, B = signed byte, j = s24 jump, S = lookupswitch, d = debug
    static readonly (string Name, string Args)?[] Table = Build();

    static (string, string)?[] Build()
    {
        var t = new (string, string)?[256];
        void D(int op, string name, string args = "") => t[op] = (name, args);
        D(0x01, "bkpt"); D(0x02, "nop"); D(0x03, "throw"); D(0x04, "getsuper", "u"); D(0x05, "setsuper", "u");
        D(0x06, "dxns", "u"); D(0x07, "dxnslate"); D(0x08, "kill", "u"); D(0x09, "label");
        D(0x0C, "ifnlt", "j"); D(0x0D, "ifnle", "j"); D(0x0E, "ifngt", "j"); D(0x0F, "ifnge", "j");
        D(0x10, "jump", "j"); D(0x11, "iftrue", "j"); D(0x12, "iffalse", "j"); D(0x13, "ifeq", "j"); D(0x14, "ifne", "j");
        D(0x15, "iflt", "j"); D(0x16, "ifle", "j"); D(0x17, "ifgt", "j"); D(0x18, "ifge", "j");
        D(0x19, "ifstricteq", "j"); D(0x1A, "ifstrictne", "j"); D(0x1B, "lookupswitch", "S");
        D(0x1C, "pushwith"); D(0x1D, "popscope"); D(0x1E, "nextname"); D(0x1F, "hasnext"); D(0x20, "pushnull");
        D(0x21, "pushundefined"); D(0x22, "pushfloat", "u"); D(0x23, "nextvalue"); D(0x24, "pushbyte", "B"); D(0x25, "pushshort", "u");
        D(0x26, "pushtrue"); D(0x27, "pushfalse"); D(0x28, "pushnan"); D(0x29, "pop"); D(0x2A, "dup"); D(0x2B, "swap");
        D(0x2C, "pushstring", "u"); D(0x2D, "pushint", "u"); D(0x2E, "pushuint", "u"); D(0x2F, "pushdouble", "u");
        D(0x30, "pushscope"); D(0x31, "pushnamespace", "u"); D(0x32, "hasnext2", "uu");
        D(0x35, "li8"); D(0x36, "li16"); D(0x37, "li32"); D(0x38, "lf32"); D(0x39, "lf64");
        D(0x3A, "si8"); D(0x3B, "si16"); D(0x3C, "si32"); D(0x3D, "sf32"); D(0x3E, "sf64");
        D(0x40, "newfunction", "u"); D(0x41, "call", "u"); D(0x42, "construct", "u"); D(0x43, "callmethod", "uu");
        D(0x44, "callstatic", "uu"); D(0x45, "callsuper", "uu"); D(0x46, "callproperty", "uu"); D(0x47, "returnvoid");
        D(0x48, "returnvalue"); D(0x49, "constructsuper", "u"); D(0x4A, "constructprop", "uu"); D(0x4B, "callsuperid");
        D(0x4C, "callproplex", "uu"); D(0x4D, "callinterface"); D(0x4E, "callsupervoid", "uu"); D(0x4F, "callpropvoid", "uu");
        D(0x50, "sxi1"); D(0x51, "sxi8"); D(0x52, "sxi16"); D(0x53, "applytype", "u"); D(0x55, "newobject", "u");
        D(0x56, "newarray", "u"); D(0x57, "newactivation"); D(0x58, "newclass", "u"); D(0x59, "getdescendants", "u");
        D(0x5A, "newcatch", "u"); D(0x5D, "findpropstrict", "u"); D(0x5E, "findproperty", "u"); D(0x5F, "finddef", "u");
        D(0x60, "getlex", "u"); D(0x61, "setproperty", "u"); D(0x62, "getlocal", "u"); D(0x63, "setlocal", "u");
        D(0x64, "getglobalscope"); D(0x65, "getscopeobject", "b"); D(0x66, "getproperty", "u"); D(0x67, "getouterscope", "u");
        D(0x68, "initproperty", "u"); D(0x6A, "deleteproperty", "u"); D(0x6C, "getslot", "u"); D(0x6D, "setslot", "u");
        D(0x6E, "getglobalslot", "u"); D(0x6F, "setglobalslot", "u"); D(0x70, "convert_s"); D(0x71, "esc_xelem");
        D(0x72, "esc_xattr"); D(0x73, "convert_i"); D(0x74, "convert_u"); D(0x75, "convert_d"); D(0x76, "convert_b");
        D(0x77, "convert_o"); D(0x78, "checkfilter"); D(0x80, "coerce", "u"); D(0x81, "coerce_b"); D(0x82, "coerce_a");
        D(0x83, "coerce_i"); D(0x84, "coerce_d"); D(0x85, "coerce_s"); D(0x86, "astype", "u"); D(0x87, "astypelate");
        D(0x88, "coerce_u"); D(0x89, "coerce_o"); D(0x90, "negate"); D(0x91, "increment"); D(0x92, "inclocal", "u");
        D(0x93, "decrement"); D(0x94, "declocal", "u"); D(0x95, "typeof"); D(0x96, "not"); D(0x97, "bitnot");
        D(0xA0, "add"); D(0xA1, "subtract"); D(0xA2, "multiply"); D(0xA3, "divide"); D(0xA4, "modulo"); D(0xA5, "lshift");
        D(0xA6, "rshift"); D(0xA7, "urshift"); D(0xA8, "bitand"); D(0xA9, "bitor"); D(0xAA, "bitxor"); D(0xAB, "equals");
        D(0xAC, "strictequals"); D(0xAD, "lessthan"); D(0xAE, "lessequals"); D(0xAF, "greaterthan"); D(0xB0, "greaterequals");
        D(0xB1, "instanceof"); D(0xB2, "istype", "u"); D(0xB3, "istypelate"); D(0xB4, "in");
        D(0xC0, "increment_i"); D(0xC1, "decrement_i"); D(0xC2, "inclocal_i", "u"); D(0xC3, "declocal_i", "u");
        D(0xC4, "negate_i"); D(0xC5, "add_i"); D(0xC6, "subtract_i"); D(0xC7, "multiply_i");
        D(0xD0, "getlocal0"); D(0xD1, "getlocal1"); D(0xD2, "getlocal2"); D(0xD3, "getlocal3");
        D(0xD4, "setlocal0"); D(0xD5, "setlocal1"); D(0xD6, "setlocal2"); D(0xD7, "setlocal3");
        D(0xEF, "debug", "d"); D(0xF0, "debugline", "u"); D(0xF1, "debugfile", "u"); D(0xF2, "bkptline", "u"); D(0xF3, "timestamp");
        return t;
    }

    public static string Name(int op) => Table[op & 0xFF]?.Name ?? $"op_{op:X2}";

    public static bool IsBranch(int op) => op is >= 0x0C and <= 0x1A;
    public static bool IsConditional(int op) => op is >= 0x0C and <= 0x1A and not 0x10;
    public static bool EndsFlow(int op) => op is Jump or Throw or ReturnVoid or ReturnValue or LookupSwitch;

    public static List<Instr> Disassemble(byte[] code)
    {
        var list = new List<Instr>();
        var r = new AbcReader(code);
        while (!r.Eof)
        {
            int start = r.Position;
            int op = r.U8();
            var ins = new Instr { Offset = start, Op = op };
            var def = Table[op];
            try
            {
                if (def == null) throw new InvalidDataException($"Unknown opcode 0x{op:X2}");
                switch (def.Value.Args)
                {
                    case "":
                        break;
                    case "j":
                    {
                        int rel = r.S24();
                        ins.Targets = [r.Position + rel];
                        ins.Args = [rel];
                        break;
                    }
                    case "S":
                    {
                        int dflt = r.S24();
                        int count = r.U30();
                        var targets = new int[count + 2];
                        targets[0] = start + dflt;
                        for (int i = 0; i <= count; i++) targets[i + 1] = start + r.S24();
                        ins.Targets = targets;
                        ins.Args = [count];
                        break;
                    }
                    case "d":
                        ins.Args = [r.U8(), r.U30(), r.U8(), r.U30()];
                        break;
                    case "b":
                        ins.Args = [r.U8()];
                        break;
                    case "B":
                        ins.Args = [(sbyte)r.U8()];
                        break;
                    default:
                    {
                        var a = new int[def.Value.Args.Length];
                        for (int i = 0; i < a.Length; i++) a[i] = r.U30();
                        ins.Args = a;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
            {
                // Undecodable bytes (obfuscation): stop cleanly
                ins.Op = -1;
                ins.Length = code.Length - start;
                list.Add(ins);
                break;
            }
            ins.Length = r.Position - start;
            list.Add(ins);
        }
        return list;
    }
}
