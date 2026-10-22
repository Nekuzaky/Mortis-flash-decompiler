using MortisFlash.Decompiler;

namespace MortisFlash.Avm1;

/// <summary>Stack marker pushed by Enumerate; the enumerated names are read through a register.</summary>
sealed class EnumMarker(Expr obj) : Expr
{
    public Expr Object { get; } = obj;
    public LocalVar? Register;
    public override void Write(CodeWriter w) => w.Text("§§enumerate(" + Object + ")");
}

/// <summary>Termination condition of a for..in loop (the register is null).</summary>
sealed class EnumDoneExpr(EnumMarker m) : Expr
{
    public EnumMarker Marker { get; } = m;
    public override void Write(CodeWriter w) => w.Text("§§enumdone(" + Marker.Object + ")");
}

sealed class Avm1State : SymState
{
    public override SymState Clone() => new Avm1State { Stack = [.. Stack] };
}

/// <summary>ActionScript 1/2 decompiler for a single action list (frame, function or handler).</summary>
public sealed class Avm1Decompiler : IBlockTranslator
{
    readonly List<Avm1Action> _actions;
    readonly Dictionary<int, LocalVar> _registers;
    readonly int _depth;
    readonly Dictionary<int, int> _indexByOffset = [];
    /// <summary>Values pushed twice by PushDuplicate.</summary>
    readonly HashSet<Expr> _duplicated = new(ReferenceEqualityComparer.Instance);
    /// <summary>Reconstructed switch blocks: index of the StoreRegister action that holds the subject.</summary>
    readonly Dictionary<CfgBlock, int> _switchStore = [];

    static readonly string[] Properties =
    [
        "_x", "_y", "_xscale", "_yscale", "_currentframe", "_totalframes", "_alpha", "_visible", "_width", "_height",
        "_rotation", "_target", "_framesloaded", "_name", "_droptarget", "_url", "_highquality", "_focusrect",
        "_soundbuftime", "_quality", "_xmouse", "_ymouse",
    ];

    Avm1Decompiler(List<Avm1Action> actions, Dictionary<int, LocalVar> registers, int depth)
    {
        _actions = actions;
        _registers = registers;
        _depth = depth;
        for (int i = 0; i < actions.Count; i++) _indexByOffset[actions[i].Offset] = i;
    }

    /// <summary>Decompiles a top-level action block (frame, event handler, etc.).</summary>
    public static List<Stmt> Decompile(byte[] code, int version)
    {
        var actions = Avm1Parser.Parse(code, version);
        return Decompile(actions, [], 0);
    }

    static List<Stmt> Decompile(List<Avm1Action> actions, Dictionary<int, LocalVar> registers, int depth)
    {
        var d = new Avm1Decompiler(actions, registers, depth);
        try
        {
            var stmts = d.Run();
            stmts = Simplifier.Run(stmts, isFunctionBody: true);
            stmts = PostProcess(stmts);
            stmts = Simplifier.Run(stmts, isFunctionBody: true);
            return Declarations.Apply(stmts, t => t is LocalExpr { Var.IsParam: false } l ? (l.Var, l.Var.Name, null) : null);
        }
        catch (Exception e)
        {
            return [new CommentStmt("Decompilation failed: " + e.Message), new CommentStmt(Avm1Parser.Disassemble(actions).TrimEnd())];
        }
    }

    List<Stmt> Run()
    {
        if (_actions.Count == 0) return [];
        var cfg = BuildCfg();
        var s = new Structurer(cfg, this);
        return s.Run(new Avm1State());
    }

    Cfg BuildCfg()
    {
        var leaders = new SortedSet<int> { 0 };
        for (int i = 0; i < _actions.Count; i++)
        {
            var a = _actions[i];
            if (a.Code is 0x99 or 0x9D)
            {
                if (_indexByOffset.TryGetValue(a.Target, out int t)) leaders.Add(t);
                if (i + 1 < _actions.Count) leaders.Add(i + 1);
            }
            else if (a.Code is 0x3E or 0x2A or 0x00 && i + 1 < _actions.Count) leaders.Add(i + 1);
        }
        var starts = leaders.ToList();
        var blocks = new List<CfgBlock>();
        var byIndex = new Dictionary<int, CfgBlock>();
        for (int k = 0; k < starts.Count; k++)
        {
            var b = new CfgBlock { Start = starts[k], End = k + 1 < starts.Count ? starts[k + 1] : _actions.Count };
            b.Offset = _actions[b.Start].Offset;
            blocks.Add(b);
            byIndex[b.Start] = b;
        }
        // Empty final block: target of jumps to the end of the code (function exit)
        var lastAction = _actions[^1];
        int endOffset = lastAction.Offset + Math.Max(1, lastAction.Size);
        var endBlock = new CfgBlock { Start = _actions.Count, End = _actions.Count, Offset = endOffset, Kind = BlockKind.Exit };
        bool endUsed = false;
        CfgBlock? At(int offset)
        {
            if (_indexByOffset.TryGetValue(offset, out int i)) return byIndex.GetValueOrDefault(i);
            if (offset != endOffset) return null;
            endUsed = true;
            return endBlock;
        }
        for (int k = 0; k < blocks.Count; k++)
        {
            var b = blocks[k];
            var last = _actions[b.End - 1];
            var next = k + 1 < blocks.Count ? blocks[k + 1] : null;
            switch (last.Code)
            {
                case 0x99:
                    if (At(last.Target) is { } t) { b.Kind = BlockKind.Jump; b.Succs.Add(t); }
                    else b.Kind = BlockKind.Exit; // jump outside the list (end of code)
                    break;
                case 0x9D:
                {
                    var tt = At(last.Target);
                    if (tt != null && next != null)
                    {
                        b.Kind = BlockKind.Cond;
                        b.Succs.Add(tt);
                        b.Succs.Add(next);
                    }
                    else if (next != null) { b.Kind = BlockKind.Fall; b.Succs.Add(next); }
                    else b.Kind = BlockKind.Exit;
                    break;
                }
                case 0x3E:
                case 0x2A:
                case 0x00:
                    b.Kind = BlockKind.Exit;
                    break;
                default:
                    if (next != null) { b.Kind = BlockKind.Fall; b.Succs.Add(next); }
                    else b.Kind = BlockKind.Exit;
                    break;
            }
        }
        if (endUsed)
        {
            // The last block falls through to the end of the code
            var lastBlock = blocks[^1];
            if (lastBlock.Kind == BlockKind.Exit && _actions[lastBlock.End - 1].Code is not (0x3E or 0x2A or 0x99))
            {
                lastBlock.Kind = BlockKind.Fall;
                lastBlock.Succs.Add(endBlock);
            }
            blocks.Add(endBlock);
        }
        DetectSwitches(blocks);
        return new Cfg(blocks);
    }

    /// <summary>
    /// AS2 switch: “subject; StoreRegister r; v0; StrictEquals; If case0”, then
    /// “register r; v1; StrictEquals; If case1” … and finally “Jump default”.
    /// </summary>
    void DetectSwitches(List<CfgBlock> blocks)
    {
        var predCount = new Dictionary<CfgBlock, int>();
        foreach (var b in blocks)
            foreach (var s in b.Succs)
                predCount[s] = predCount.GetValueOrDefault(s) + 1;
        foreach (var b0 in blocks.ToList())
        {
            if (b0.Kind != BlockKind.Cond || b0.End - b0.Start < 4) continue;
            int ifIdx = b0.End - 1;
            if (_actions[ifIdx - 1].Code != 0x66) continue;
            int store = -1;
            for (int k = ifIdx - 2; k >= b0.Start; k--)
                if (_actions[k].Code == 0x87) { store = k; break; }
            if (store < 0) continue;
            int reg = _actions[store].Int1;
            var marker = new RawExpr("§§subject");
            var first = CaseValue(store + 1, ifIdx, marker);
            if (first == null) continue;
            var labels = new List<Expr> { first };
            var targets = new List<CfgBlock> { b0.Succs[0] };
            var chain = new List<CfgBlock>();
            var cur = b0.Succs[1];
            while (cur.Kind == BlockKind.Cond && predCount.GetValueOrDefault(cur) == 1 && cur.End - cur.Start >= 3 && _actions[cur.End - 2].Code == 0x66)
            {
                var v = CaseValue(cur.Start, cur.End - 1, null, reg);
                if (v == null) break;
                labels.Add(v);
                targets.Add(cur.Succs[0]);
                chain.Add(cur);
                cur = cur.Succs[1];
            }
            if (labels.Count < 2) continue;
            // Last link of the chain: jump to default (or to the end of the switch)
            CfgBlock dflt = cur;
            if (cur.Kind == BlockKind.Jump && cur.End - cur.Start == 1 && predCount.GetValueOrDefault(cur) == 1)
            {
                dflt = cur.Succs[0];
                chain.Add(cur);
            }
            foreach (var c in chain) c.Succs.Clear();
            b0.Kind = BlockKind.Switch;
            b0.Succs = [dflt, .. targets];
            b0.CaseLabels = labels;
            _switchStore[b0] = store;
        }
    }

    /// <summary>Value compared by one link of the cascade (subject === value).</summary>
    Expr? CaseValue(int start, int end, Expr? subject, int reg = -1)
    {
        var st = new Avm1State();
        if (subject != null) st.Push(subject);
        var output = new List<Stmt>();
        try { TranslateRange(start, end, st, output); }
        catch { return null; }
        if (output.Count > 0 || st.Stack.Count != 1 || st.Stack[0] is not BinaryExpr { Op: "===" } eq) return null;
        bool IsSubject(Expr e) => subject != null ? ReferenceEquals(e, subject) : e is LocalExpr l && l.Var.Register == reg;
        if (IsSubject(eq.Left)) return eq.Right;
        if (IsSubject(eq.Right)) return eq.Left;
        return null;
    }

    public bool IsEmpty(CfgBlock b)
    {
        for (int i = b.Start; i < b.End; i++)
            if (_actions[i].Code is not (0x99 or 0x00)) return false;
        return true;
    }

    public SymState HandlerState(HandlerInfo h, SymState outer) => new Avm1State();

    LocalVar Reg(int r)
    {
        if (_registers.TryGetValue(r, out var v)) return v;
        v = new LocalVar { Register = r, Name = $"_loc{r}_" };
        _registers[r] = v;
        return v;
    }

    // ------------------------------------------------------------------ translation

    static Expr Lit(PushValue v) => v.Type switch
    {
        0 or 8 or 9 => LitExpr.Str((string)v.Value!),
        1 or 6 => LitExpr.Num(Convert.ToDouble(v.Value)),
        2 => LitExpr.Null,
        3 => LitExpr.Undef,
        5 => LitExpr.Bool((bool)v.Value!),
        7 => LitExpr.Int((long)v.Value!),
        _ => new RawExpr(v.ToString()),
    };

    /// <summary>Variable name: a plain identifier when valid, otherwise eval("…") (/a:b paths, dynamic names).</summary>
    static Expr VarName(Expr name)
    {
        if (name is LitExpr { Value: string s })
        {
            if (CodeWriter.IsIdentifier(s) || IsDottedPath(s)) return new NameExpr(s);
            return new CallExpr(new NameExpr("eval"), [name]);
        }
        return new CallExpr(new NameExpr("eval"), [name]);
    }

    static bool IsDottedPath(string s) => s.Length > 0 && s.Split('.').All(p => CodeWriter.IsIdentifier(p) || p is "this" or "super");

    static Expr Member(Expr obj, Expr name)
    {
        if (name is LitExpr { Value: string s } && CodeWriter.IsIdentifier(s)) return new MemberExpr(obj, s);
        return new IndexExpr(obj, name);
    }

    static int ToInt(Expr e) => e is LitExpr { IsNumber: true } l ? (int)l.AsNumber : e is LitExpr { Value: string s } && int.TryParse(s, out int v) ? v : -1;

    static Expr Call(string fn, params Expr[] args) => new CallExpr(new NameExpr(fn), [.. args]);

    public Expr? Translate(CfgBlock block, SymState state, List<Stmt> output)
    {
        if (_switchStore.TryGetValue(block, out int store))
        {
            TranslateRange(block.Start, store, (Avm1State)state, output);
            return state.Pop();
        }
        return TranslateRange(block.Start, block.End, (Avm1State)state, output);
    }

    Expr? TranslateRange(int start, int end, Avm1State st, List<Stmt> output)
    {
        var block = new { Start = start, End = end };
        for (int i = block.Start; i < block.End; i++)
        {
            var a = _actions[i];
            bool nextIs(int code) => i + 1 < block.End && _actions[i + 1].Code == code;
            switch (a.Code)
            {
                case 0x00:
                    return null;
                case 0x04: output.Add(new ExprStmt(Call("nextFrame"))); break;
                case 0x05: output.Add(new ExprStmt(Call("prevFrame"))); break;
                case 0x06: output.Add(new ExprStmt(Call("play"))); break;
                case 0x07: output.Add(new ExprStmt(Call("stop"))); break;
                case 0x08: output.Add(new ExprStmt(Call("toggleHighQuality"))); break;
                case 0x09: output.Add(new ExprStmt(Call("stopAllSounds"))); break;
                case 0x81:
                    if (nextIs(0x06)) { output.Add(new ExprStmt(Call("gotoAndPlay", LitExpr.Int(a.Int1 + 1)))); i++; }
                    else output.Add(new ExprStmt(Call("gotoAndStop", LitExpr.Int(a.Int1 + 1))));
                    break;
                case 0x8C:
                    if (nextIs(0x06)) { output.Add(new ExprStmt(Call("gotoAndPlay", LitExpr.Str(a.Str1!)))); i++; }
                    else output.Add(new ExprStmt(Call("gotoAndStop", LitExpr.Str(a.Str1!))));
                    break;
                case 0x83:
                    if (a.Str1!.StartsWith("FSCommand:", StringComparison.OrdinalIgnoreCase))
                        output.Add(new ExprStmt(Call("fscommand", LitExpr.Str(a.Str1[10..]), LitExpr.Str(a.Str2!))));
                    else output.Add(new ExprStmt(Call("getURL", LitExpr.Str(a.Str1), LitExpr.Str(a.Str2!))));
                    break;
                case 0x8B:
                    output.Add(new RawStmt(a.Str1 == "" ? "// end of tellTarget" : $"tellTarget({LitExpr.Quote(a.Str1!)})"));
                    break;
                case 0x20:
                    output.Add(new RawStmt($"tellTarget({st.Pop()})"));
                    break;
                case 0x8A:
                    output.Add(new CommentStmt($"ifFrameLoaded({a.Int1 + 1}): skips {a.Int2} actions if the frame is not loaded"));
                    break;
                case 0x8D:
                    output.Add(new CommentStmt($"ifFrameLoaded({st.Pop()}): skips {a.Int1} actions"));
                    break;
                case 0x88:
                    break;

                case 0x96:
                    foreach (var v in a.Values!)
                    {
                        if (v.Type == 4) st.Push(new LocalExpr(Reg((int)v.Value!)));
                        else st.Push(Lit(v));
                    }
                    break;
                case 0x17:
                {
                    var v = st.Pop();
                    if (v is EnumMarker) break;
                    // Discarding one copy of a duplicated value (a && b, a || b): the other copy is still used
                    if (_duplicated.Contains(v)) break;
                    if (v.HasSideEffects) output.Add(new ExprStmt(Simplifier.SimplifyAssign(v)));
                    break;
                }
                case 0x4C:
                    _duplicated.Add(st.Peek());
                    st.Push(st.Peek());
                    break;
                case 0x4D:
                {
                    var x = st.Pop();
                    var y = st.Pop();
                    st.Push(x);
                    st.Push(y);
                    break;
                }

                // --- operators
                case 0x0A:
                case 0x47:
                case 0x21: Bin(st, "+"); break;
                case 0x0B: Bin(st, "-"); break;
                case 0x0C: Bin(st, "*"); break;
                case 0x0D: Bin(st, "/"); break;
                case 0x3F: Bin(st, "%"); break;
                case 0x0E:
                case 0x13:
                case 0x49:
                {
                    var b = st.Pop();
                    var x = st.Pop();
                    // End of enumeration: register == null
                    if (x is EnumMarker m && b is LitExpr { Value: null }) st.Push(new EnumDoneExpr(m));
                    else if (b is EnumMarker m2 && x is LitExpr { Value: null }) st.Push(new EnumDoneExpr(m2));
                    else st.Push(new BinaryExpr("==", x, b));
                    break;
                }
                case 0x66: Bin(st, "==="); break;
                case 0x0F:
                case 0x29:
                case 0x48: Bin(st, "<"); break;
                case 0x67:
                case 0x68: Bin(st, ">"); break;
                case 0x10: Bin(st, "&&"); break;
                case 0x11: Bin(st, "||"); break;
                case 0x12:
                {
                    var v = st.Pop();
                    st.Push(v is UnaryExpr { Op: "!" } u ? u.Operand : new UnaryExpr("!", v));
                    break;
                }
                case 0x60: Bin(st, "&"); break;
                case 0x61: Bin(st, "|"); break;
                case 0x62: Bin(st, "^"); break;
                case 0x63: Bin(st, "<<"); break;
                case 0x64: Bin(st, ">>"); break;
                case 0x65: Bin(st, ">>>"); break;
                case 0x50: st.Push(new BinaryExpr("+", st.Pop(), LitExpr.Int(1))); break;
                case 0x51: st.Push(new BinaryExpr("-", st.Pop(), LitExpr.Int(1))); break;
                case 0x54:
                {
                    var c = st.Pop();
                    var o = st.Pop();
                    st.Push(new BinaryExpr("instanceof", o, c));
                    break;
                }
                case 0x44: st.Push(new UnaryExpr("typeof", st.Pop())); break;
                case 0x14: st.Push(Call("length", st.Pop())); break;
                case 0x31: st.Push(Call("mblength", st.Pop())); break;
                case 0x15:
                case 0x35:
                {
                    var count = st.Pop();
                    var index = st.Pop();
                    var s = st.Pop();
                    st.Push(Call(a.Code == 0x15 ? "substring" : "mbsubstring", s, index, count));
                    break;
                }
                case 0x18: st.Push(Call("int", st.Pop())); break;
                case 0x4A: st.Push(Call("Number", st.Pop())); break;
                case 0x4B: st.Push(Call("String", st.Pop())); break;
                case 0x32: st.Push(Call("ord", st.Pop())); break;
                case 0x33: st.Push(Call("chr", st.Pop())); break;
                case 0x36: st.Push(Call("mbord", st.Pop())); break;
                case 0x37: st.Push(Call("mbchr", st.Pop())); break;
                case 0x30: st.Push(Call("random", st.Pop())); break;
                case 0x34: st.Push(Call("getTimer")); break;
                case 0x45: st.Push(Call("targetPath", st.Pop())); break;
                case 0x2B:
                {
                    var obj = st.Pop();
                    var ctor = st.Pop();
                    st.Push(new CallExpr(ctor, [obj]));
                    break;
                }

                // --- variables
                case 0x1C:
                    st.Push(VarName(st.Pop()));
                    break;
                case 0x1D:
                {
                    var v = st.Pop();
                    var n = st.Pop();
                    var target = VarName(n);
                    if (target is CallExpr)
                        output.Add(new ExprStmt(Call("set", n, v)));
                    else output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(target, v))));
                    break;
                }
                case 0x3C:
                {
                    var v = st.Pop();
                    var n = st.Pop();
                    output.Add(n is LitExpr { Value: string s } ? new VarStmt(s, null, v) : new ExprStmt(Call("set", n, v)));
                    break;
                }
                case 0x41:
                {
                    var n = st.Pop();
                    if (n is LitExpr { Value: string s }) output.Add(new VarStmt(s, null, null));
                    break;
                }
                case 0x87:
                {
                    var v = st.Pop();
                    var reg = Reg(a.Int1);
                    if (v is EnumMarker em)
                    {
                        em.Register = reg;
                        st.Push(em);
                        break;
                    }
                    if (nextIs(0x17))
                    {
                        // StoreRegister + Pop: plain assignment
                        i++;
                        // Declarations are placed after structuring (Declarations.Apply)
                        output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(new LocalExpr(reg), v))));
                    }
                    else st.Push(new AssignExpr(new LocalExpr(reg), v));
                    break;
                }
                case 0x4E:
                {
                    var n = st.Pop();
                    var o = st.Pop();
                    st.Push(Member(o, n));
                    break;
                }
                case 0x4F:
                {
                    var v = st.Pop();
                    var n = st.Pop();
                    var o = st.Pop();
                    output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(Member(o, n), v))));
                    break;
                }
                case 0x22:
                {
                    int idx = ToInt(st.Pop());
                    var target = st.Pop();
                    string prop = idx >= 0 && idx < Properties.Length ? Properties[idx] : $"§§property{idx}";
                    st.Push(target is LitExpr { Value: "" } ? new NameExpr(prop) : new MemberExpr(TargetExpr(target), prop));
                    break;
                }
                case 0x23:
                {
                    var v = st.Pop();
                    int idx = ToInt(st.Pop());
                    var target = st.Pop();
                    string prop = idx >= 0 && idx < Properties.Length ? Properties[idx] : $"§§property{idx}";
                    Expr t = target is LitExpr { Value: "" } ? new NameExpr(prop) : new MemberExpr(TargetExpr(target), prop);
                    output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(t, v))));
                    break;
                }
                case 0x3A:
                {
                    var n = st.Pop();
                    var o = st.Pop();
                    st.Push(new UnaryExpr("delete", Member(o, n)));
                    break;
                }
                case 0x3B:
                    st.Push(new UnaryExpr("delete", VarName(st.Pop())));
                    break;

                // --- movie clips
                case 0x24:
                {
                    var depth = st.Pop();
                    var target = st.Pop();
                    var source = st.Pop();
                    output.Add(new ExprStmt(Call("duplicateMovieClip", source, target, new BinaryExpr("-", depth, LitExpr.Int(16384)))));
                    break;
                }
                case 0x25: output.Add(new ExprStmt(Call("removeMovieClip", st.Pop()))); break;
                case 0x26: output.Add(new ExprStmt(Call("trace", st.Pop()))); break;
                case 0x27:
                {
                    var target = st.Pop();
                    var lockCenter = st.Pop();
                    var constrain = st.Pop();
                    var args = new List<Expr> { target, lockCenter };
                    if (constrain is LitExpr l && l.AsNumber != 0 || constrain is LitExpr { Value: true })
                    {
                        var y2 = st.Pop();
                        var x2 = st.Pop();
                        var y1 = st.Pop();
                        var x1 = st.Pop();
                        args.AddRange([x1, y1, x2, y2]);
                    }
                    output.Add(new ExprStmt(new CallExpr(new NameExpr("startDrag"), args)));
                    break;
                }
                case 0x28: output.Add(new ExprStmt(Call("stopDrag"))); break;
                case 0x9E: output.Add(new ExprStmt(Call("call", st.Pop()))); break;
                case 0x9F:
                {
                    var frame = st.Pop();
                    if (a.Int2 != 0) frame = new BinaryExpr("+", frame, LitExpr.Int(a.Int2));
                    output.Add(new ExprStmt(Call((a.Int1 & 1) != 0 ? "gotoAndPlay" : "gotoAndStop", frame)));
                    break;
                }
                case 0x9A:
                {
                    var target = st.Pop();
                    var url = st.Pop();
                    int method = a.Int1 & 3;
                    bool loadTarget = (a.Int1 & 0x40) != 0, loadVars = (a.Int1 & 0x80) != 0;
                    var args = new List<Expr> { url, target };
                    if (method != 0) args.Add(LitExpr.Str(method == 1 ? "GET" : "POST"));
                    string fn = loadVars ? (loadTarget ? "loadVariables" : "loadVariablesNum")
                        : loadTarget ? "loadMovie"
                        : target is LitExpr { Value: string ts } && ts.StartsWith("_level") ? "loadMovieNum" : "getURL";
                    if (fn is "loadMovieNum" or "loadVariablesNum" && target is LitExpr { Value: string lv } && lv.StartsWith("_level")
                        && int.TryParse(lv[6..], out int level))
                        args[1] = LitExpr.Int(level);
                    if (fn == "loadMovie" && url is LitExpr { Value: "" }) { fn = "unloadMovie"; args = [target]; }
                    output.Add(new ExprStmt(new CallExpr(new NameExpr(fn), args)));
                    break;
                }

                // --- calls and objects
                case 0x3D:
                {
                    var name = st.Pop();
                    int n = ToInt(st.Pop());
                    var args = PopArgs(st, n);
                    st.Push(new CallExpr(VarName(name), args));
                    break;
                }
                case 0x52:
                {
                    var name = st.Pop();
                    var obj = st.Pop();
                    int n = ToInt(st.Pop());
                    var args = PopArgs(st, n);
                    bool noName = name is LitExpr { Value: null } || name is LitExpr { Value: "" } || name is LitExpr l2 && ReferenceEquals(l2.Value, LitExpr.Undefined);
                    st.Push(new CallExpr(noName ? obj : Member(obj, name), args));
                    break;
                }
                case 0x40:
                {
                    var name = st.Pop();
                    int n = ToInt(st.Pop());
                    var args = PopArgs(st, n);
                    if (name is LitExpr { Value: "Array" } && args.Count != 1) st.Push(new ArrayLitExpr(args));
                    else st.Push(new NewExpr(VarName(name), args));
                    break;
                }
                case 0x53:
                {
                    var name = st.Pop();
                    var obj = st.Pop();
                    int n = ToInt(st.Pop());
                    var args = PopArgs(st, n);
                    bool noName = name is LitExpr { Value: null or "" } || name is LitExpr l3 && ReferenceEquals(l3.Value, LitExpr.Undefined);
                    st.Push(new NewExpr(noName ? obj : Member(obj, name), args));
                    break;
                }
                case 0x42:
                {
                    int n = ToInt(st.Pop());
                    st.Push(new ArrayLitExpr(PopArgs(st, n)));
                    break;
                }
                case 0x43:
                {
                    int n = ToInt(st.Pop());
                    var props = new List<(Expr, Expr)>();
                    for (int k = 0; k < n; k++)
                    {
                        var v = st.Pop();
                        var key = st.Pop();
                        props.Add((key, v));
                    }
                    props.Reverse();
                    st.Push(new ObjectLitExpr(props));
                    break;
                }
                case 0x46:
                    st.Push(new EnumMarker(VarName(st.Pop())));
                    break;
                case 0x55:
                    st.Push(new EnumMarker(st.Pop()));
                    break;
                case 0x69:
                {
                    var super = st.Pop();
                    var sub = st.Pop();
                    output.Add(new RawStmt($"{sub} extends {super};"));
                    break;
                }
                case 0x2C:
                {
                    var ctor = st.Pop();
                    int n = ToInt(st.Pop());
                    var ifs = PopArgs(st, n);
                    output.Add(new RawStmt($"{ctor} implements {string.Join(", ", ifs)};"));
                    break;
                }

                // --- functions and nested blocks
                case 0x9B:
                case 0x8E:
                {
                    var f = Function(a);
                    if (!string.IsNullOrEmpty(a.Str1)) output.Add(new FunctionStmt(f));
                    else st.Push(f);
                    break;
                }
                case 0x94:
                {
                    var obj = st.Pop();
                    output.Add(new WithStmt(obj, Decompile(a.Body ?? [], _registers, _depth + 1)));
                    break;
                }
                case 0x8F:
                {
                    var t = new TryStmt { Body = Decompile(a.Body ?? [], _registers, _depth + 1) };
                    if (a.Catch != null)
                    {
                        string name = a.CatchName ?? Reg(a.CatchRegister).Name;
                        t.Catches.Add(new CatchClause { Name = name, Body = Decompile(a.Catch, _registers, _depth + 1) });
                    }
                    if (a.Finally != null) t.Finally = Decompile(a.Finally, _registers, _depth + 1);
                    output.Add(t);
                    break;
                }

                // --- block terminators
                case 0x3E:
                    output.Add(new ReturnStmt(st.Pop()));
                    return null;
                case 0x2A:
                    output.Add(new ThrowStmt(st.Pop()));
                    return null;
                case 0x99:
                    break;
                case 0x9D:
                    return st.Pop();

                default:
                    output.Add(new CommentStmt($"unknown action 0x{a.Code:X2}"));
                    break;
            }
        }
        return null;
    }

    static Expr TargetExpr(Expr target) => target is LitExpr { Value: string s } ? (IsDottedPath(s) ? new NameExpr(s) : Call("eval", target)) : target;

    static void Bin(Avm1State st, string op)
    {
        var b = st.Pop();
        var a = st.Pop();
        st.Push(new BinaryExpr(op, a, b));
    }

    static List<Expr> PopArgs(Avm1State st, int n)
    {
        var args = new List<Expr>();
        for (int k = 0; k < Math.Max(0, Math.Min(n, 255)); k++) args.Add(st.Pop());
        return args;
    }

    FunctionExpr Function(Avm1Action a)
    {
        var f = new FunctionExpr { Name = string.IsNullOrEmpty(a.Str1) ? null : a.Str1 };
        var regs = new Dictionary<int, LocalVar>();
        if (a.Code == 0x8E)
        {
            // Preloading: this, arguments, super, _root, _parent, _global, starting at register 1
            int r = 1;
            int flags = a.Flags;
            void Pre(bool cond, string name)
            {
                if (!cond) return;
                regs[r] = new LocalVar { Register = r, Name = name, IsParam = true };
                r++;
            }
            // Bit fields read as little-endian: byte 0 = preload flags, byte 1 = _global
            Pre((flags & 0x0001) != 0, "this");
            Pre((flags & 0x0004) != 0, "arguments");
            Pre((flags & 0x0010) != 0, "super");
            Pre((flags & 0x0040) != 0, "_root");
            Pre((flags & 0x0080) != 0, "_parent");
            Pre((flags & 0x0100) != 0, "_global");
            foreach (var p in a.Params!)
                if (p.Register > 0) regs[p.Register] = new LocalVar { Register = p.Register, Name = p.Name, IsParam = true };
        }
        else
        {
            // DefineFunction (SWF 5): registers are shared with the enclosing code
            foreach (var (k, v) in _registers) regs[k] = v;
        }
        f.Params = a.Params!.Select(p => p.Name).ToList();
        f.Body = _depth > 32 ? [new CommentStmt("nesting too deep")] : Decompile(a.Body ?? [], regs, _depth + 1);
        return f;
    }

    // ------------------------------------------------------------------ patterns

    static List<Stmt> PostProcess(List<Stmt> stmts)
    {
        var res = new List<Stmt>();
        foreach (var s0 in stmts)
        {
            var s = s0;
            switch (s)
            {
                case IfStmt i:
                    i.Then = PostProcess(i.Then);
                    if (i.Else != null) i.Else = PostProcess(i.Else);
                    break;
                case LoopStmt l:
                    l.Body = PostProcess(l.Body);
                    break;
                case SwitchStmt sw:
                    foreach (var c in sw.Cases) c.Body = PostProcess(c.Body);
                    break;
                case TryStmt t:
                    t.Body = PostProcess(t.Body);
                    foreach (var c in t.Catches) c.Body = PostProcess(c.Body);
                    if (t.Finally != null) t.Finally = PostProcess(t.Finally);
                    break;
            }
            // Stack cleanup after a break inside a for..in: do {} while (pop() != null)
            if (s is DoWhileStmt { Body.Count: 0 } dw && dw.Cond.ToString().Contains("§§pop()")) continue;
            // for (k in o): while (!enumdone) { k = register; … }
            if (s is WhileStmt w && Structurer.Negate(w.Cond) is EnumDoneExpr done && w.Body.Count > 0)
            {
                var reg = done.Marker.Register;
                Expr? variable = null;
                bool declare = false;
                switch (w.Body[0])
                {
                    case VarStmt { Init: LocalExpr l } v when l.Var == reg:
                        variable = new NameExpr(v.Name);
                        declare = true;
                        break;
                    case ExprStmt { Expr: AssignExpr { Op: "=", Value: LocalExpr l2 } asg } when l2.Var == reg:
                        variable = asg.Target;
                        break;
                }
                if (variable != null)
                {
                    s = new ForInStmt { Variable = variable, Object = done.Marker.Object, Declare = declare, Body = w.Body.Skip(1).ToList(), Label = w.Label };
                }
                else if (reg != null)
                {
                    s = new ForInStmt { Variable = new LocalExpr(reg), Object = done.Marker.Object, Body = w.Body, Label = w.Label };
                }
            }
            res.Add(s);
        }
        return res;
    }
}
