using MortisFlash.Decompiler;

namespace MortisFlash.Avm2;

// Expressions internal to the AVM2 decompiler (scope objects, iterators…).

sealed class FindPropExpr(string name) : Expr
{
    public string Name { get; } = name;
    public override void Write(CodeWriter w) => w.Identifier(Name);
}

sealed class GlobalScopeExpr : Expr
{
    public override void Write(CodeWriter w) => w.Text("§§global");
}

sealed class ActivationExpr(List<Trait> traits) : Expr
{
    public List<Trait> Traits { get; } = traits;
    public HashSet<int> Declared { get; } = [];
    public override void Write(CodeWriter w) => w.Text("§§activation");
}

sealed class CatchScopeExpr(HandlerInfo h) : Expr
{
    public HandlerInfo Handler { get; } = h;
    public override void Write(CodeWriter w) => w.Text("§§catchscope");
}

/// <summary>Exception received by a handler; written using the catch variable's name.</summary>
sealed class CaughtExpr(HandlerInfo h) : Expr
{
    public HandlerInfo Handler { get; } = h;
    public override void Write(CodeWriter w) => w.Identifier(Handler.Name ?? "e");
}

sealed class HasNextExpr(Expr obj, Expr index) : Expr
{
    public Expr Object { get; } = obj;
    public Expr Index { get; } = index;
    public override bool HasSideEffects => true;
    public override void Write(CodeWriter w)
    {
        w.Text("§§hasnext(");
        Object.Write(w);
        w.Text(", ");
        Index.Write(w);
        w.Text(")");
    }
}

sealed class NextNameExpr(Expr obj, Expr index, bool value) : Expr
{
    public Expr Object { get; } = obj;
    public Expr Index { get; } = index;
    public bool Value { get; } = value;
    public override void Write(CodeWriter w)
    {
        w.Text(Value ? "§§nextvalue(" : "§§nextname(");
        Object.Write(w);
        w.Text(", ");
        Index.Write(w);
        w.Text(")");
    }
}

sealed class NewClassExpr(int index) : Expr
{
    public int Index { get; } = index;
    public override bool HasSideEffects => true;
    public override void Write(CodeWriter w) => w.Text($"§§newclass({Index})");
}

/// <summary>Type conversion (coerce / convert): transparent when written, but useful for typing variables.</summary>
/// <summary>Variable stored in the activation object (a local captured by a closure).</summary>
sealed class SlotVarExpr(string name, string type) : Expr
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    public override void Write(CodeWriter w) => w.Identifier(Name);
}

sealed class TypedExpr(string type, Expr inner) : Expr
{
    public string Type { get; } = type;
    public Expr Inner { get; } = inner;
    public override int Precedence => Inner.Precedence;
    public override bool HasSideEffects => Inner.HasSideEffects;
    public override Expr Core => Inner.Core;
    public override void Write(CodeWriter w) => Inner.Write(w);
}

sealed class Avm2State : SymState, ICopyable
{
    public List<Expr> Scope = [];
    public Dictionary<int, Expr> RegObjects = [];

    public override SymState Clone() => new Avm2State
    {
        Stack = [.. Stack], Scope = [.. Scope], RegObjects = new Dictionary<int, Expr>(RegObjects),
    };

    public void CopyFrom(SymState other)
    {
        if (other is not Avm2State o) return;
        Scope = o.Scope;
        RegObjects = o.RegObjects;
    }
}

/// <summary>Method context: the class it belongs to (used to name slots).</summary>
public sealed class MethodOwner
{
    public InstanceInfo? Instance;
    public ClassInfo? Class;
    public ScriptInfo? Script;
    public bool IsStatic;
    public string ClassName = "";
}

/// <summary>Decompiles an AVM2 method body into ActionScript 3 statements.</summary>
public sealed class MethodDecompiler : IBlockTranslator
{
    readonly AbcFile _abc;
    readonly NameContext _names;
    readonly MethodInfo _method;
    readonly MethodBody _body;
    readonly MethodOwner _owner;
    readonly List<Instr> _code;
    readonly Dictionary<int, LocalVar> _locals = [];
    readonly Dictionary<int, string> _debugNames = [];
    /// <summary>Values pushed twice by dup.</summary>
    readonly HashSet<Expr> _duplicated = new(ReferenceEqualityComparer.Instance);
    readonly int _depth;
    List<HandlerInfo> _handlers = [];

    public MethodDecompiler(AbcFile abc, NameContext names, MethodInfo method, MethodOwner owner, int depth = 0)
    {
        _abc = abc;
        _names = names;
        _method = method;
        _body = method.Body ?? throw new InvalidOperationException("Method has no body");
        _owner = owner;
        _depth = depth;
        _code = Opcodes.Disassemble(_body.Code);
        foreach (var ins in _code)
            if (ins.Op == Opcodes.Debug && ins.Args.Length == 4 && ins.Args[0] == 1)
            {
                var n = _abc.Str(ins.Args[1]);
                if (n.Length > 0 && !_debugNames.ContainsKey(ins.Args[2] + 1)) _debugNames[ins.Args[2] + 1] = n;
            }
    }

    public IReadOnlyList<Instr> Code => _code;

    // ------------------------------------------------------------------ parameters and variables

    public List<string> ParamNames()
    {
        var list = new List<string>();
        int n = _method.ParamTypes.Length;
        for (int i = 0; i < n; i++) list.Add(Local(i + 1).Name);
        return list;
    }

    public string ParamName(int i) => Local(i + 1).Name;

    /// <summary>Name of the "...rest" or "arguments" register that follows the parameters, if the method has one.</summary>
    public string? ExtraParamName() => _method.NeedRest || _method.NeedArguments ? Local(_method.ParamTypes.Length + 1).Name : null;

    LocalVar Local(int reg)
    {
        if (_locals.TryGetValue(reg, out var v)) return v;
        v = new LocalVar { Register = reg };
        int pc = _method.ParamTypes.Length;
        if (reg == 0) { v.Name = "this"; v.IsParam = true; }
        else if (reg <= pc)
        {
            v.IsParam = true;
            string? n = null;
            if (_method.HasParamNames && reg - 1 < _method.ParamNames.Length) n = _abc.Str(_method.ParamNames[reg - 1]);
            if (string.IsNullOrEmpty(n) && _debugNames.TryGetValue(reg, out var dn)) n = dn;
            v.Name = !string.IsNullOrEmpty(n) && CodeWriter.IsIdentifier(n) ? n : $"param{reg}";
        }
        else if (reg == pc + 1 && (_method.NeedRest || _method.NeedArguments))
        {
            v.IsParam = true;
            v.Name = _method.NeedRest ? (_debugNames.GetValueOrDefault(reg) ?? "rest") : "arguments";
        }
        else
        {
            v.Name = _debugNames.TryGetValue(reg, out var dn) && CodeWriter.IsIdentifier(dn) ? dn : $"_loc{reg}_";
        }
        _locals[reg] = v;
        return v;
    }

    // ------------------------------------------------------------------ graph

    public List<Stmt> Decompile()
    {
        var (cfg, tries) = BuildCfg();
        var st = new Avm2State();
        var s = new Structurer(cfg, this, tries);
        var stmts = Simplifier.Run(s.Run(st));
        stmts = PostProcess(stmts);
        stmts = Simplifier.Run(stmts);
        return Declarations.Apply(stmts, target => target switch
        {
            LocalExpr { Var.IsParam: false } l when l.Var.Register != 0 => (l.Var, l.Var.Name, l.Var.Type ?? "*"),
            SlotVarExpr sv => ("slot:" + sv.Name, sv.Name, sv.Type),
            _ => null,
        });
    }

    (Cfg, List<TryRegion>) BuildCfg()
    {
        var byOffset = new Dictionary<int, int>();
        for (int i = 0; i < _code.Count; i++) byOffset[_code[i].Offset] = i;
        int end = _body.Code.Length;
        var leaders = new SortedSet<int> { 0 };
        void Lead(int off)
        {
            if (byOffset.TryGetValue(off, out int idx)) leaders.Add(idx);
        }
        for (int i = 0; i < _code.Count; i++)
        {
            var ins = _code[i];
            if (ins.Targets.Length > 0) foreach (var t in ins.Targets) Lead(t);
            if ((Opcodes.IsBranch(ins.Op) || Opcodes.EndsFlow(ins.Op) || ins.Op == -1) && i + 1 < _code.Count) leaders.Add(i + 1);
        }
        foreach (var e in _body.Exceptions)
        {
            Lead(e.From);
            Lead(e.To);
            Lead(e.Target);
        }
        if (_code.Count == 0) leaders.Clear();

        var starts = leaders.ToList();
        var blocks = new List<CfgBlock>();
        var blockAt = new Dictionary<int, CfgBlock>();
        for (int k = 0; k < starts.Count; k++)
        {
            var b = new CfgBlock { Start = starts[k], End = k + 1 < starts.Count ? starts[k + 1] : _code.Count };
            b.Offset = _code[b.Start].Offset;
            blocks.Add(b);
            blockAt[b.Offset] = b;
        }
        CfgBlock? At(int off) => blockAt.GetValueOrDefault(off);

        for (int k = 0; k < blocks.Count; k++)
        {
            var b = blocks[k];
            var last = _code[b.End - 1];
            var next = k + 1 < blocks.Count ? blocks[k + 1] : null;
            if (last.Op == Opcodes.Jump)
            {
                b.Kind = BlockKind.Jump;
                if (At(last.Targets[0]) is { } t) b.Succs.Add(t);
                else b.Kind = BlockKind.Exit;
            }
            else if (Opcodes.IsConditional(last.Op))
            {
                var t = At(last.Targets[0]);
                if (t != null && next != null)
                {
                    b.Kind = BlockKind.Cond;
                    b.Succs.Add(t);
                    b.Succs.Add(next);
                }
                else if (next != null) { b.Kind = BlockKind.Fall; b.Succs.Add(next); }
                else b.Kind = BlockKind.Exit;
            }
            else if (last.Op == Opcodes.LookupSwitch)
            {
                b.Kind = BlockKind.Switch;
                foreach (var t in last.Targets) b.Succs.Add(At(t) ?? next ?? b);
            }
            else if (last.Op is Opcodes.ReturnValue or Opcodes.ReturnVoid or Opcodes.Throw || last.Op == -1 || next == null)
                b.Kind = BlockKind.Exit;
            else
            {
                b.Kind = BlockKind.Fall;
                b.Succs.Add(next);
            }
        }

        // Try regions: group handlers that cover the same range
        var tries = new List<TryRegion>();
        _handlers = [];
        for (int i = 0; i < _body.Exceptions.Count; i++)
        {
            var e = _body.Exceptions[i];
            var target = At(e.Target);
            if (target == null) continue;
            var h = new HandlerInfo
            {
                Target = target,
                Index = i,
                IsFinally = e.VarName == 0 && e.ExcType == 0,
                Name = e.VarName != 0 ? _names.Short(e.VarName) : null,
                Type = e.ExcType != 0 ? _names.TypeName(e.ExcType) : null,
            };
            _handlers.Add(h);
            var region = tries.FirstOrDefault(r => r.From == e.From && r.To == e.To);
            if (region == null) tries.Add(region = new TryRegion { From = e.From, To = e.To });
            region.Handlers.Add(h);
            foreach (var b in blocks)
                if (b.Offset >= e.From && b.Offset < e.To && !b.Handlers.Contains(target))
                    b.Handlers.Add(target);
        }
        if (blocks.Count == 0) blocks.Add(new CfgBlock { Kind = BlockKind.Exit });
        return (new Cfg(blocks), tries);
    }

    public bool IsEmpty(CfgBlock b)
    {
        for (int i = b.Start; i < b.End; i++)
            if (_code[i].Op is not (Opcodes.Jump or Opcodes.Label or Opcodes.Nop or Opcodes.DebugLine or Opcodes.DebugFile
                or Opcodes.Debug or Opcodes.Kill or Opcodes.Timestamp or Opcodes.BkptLine)) return false;
        return true;
    }

    public SymState HandlerState(HandlerInfo h, SymState outer)
    {
        var o = (Avm2State)outer;
        var st = new Avm2State { RegObjects = new Dictionary<int, Expr>(o.RegObjects) };
        st.Push(new CaughtExpr(h));
        return st;
    }

    // ------------------------------------------------------------------ translation

    string Mn(int i) => _names.Use(i);

    /// <summary>Property name: static, or popped from the stack (MultinameL / RTQName).</summary>
    (string? Name, Expr? Runtime, bool Attr) PropName(int mnIndex, Avm2State st)
    {
        var mn = _abc.Mn(mnIndex);
        Expr? rt = null;
        if (mn.HasRuntimeName) rt = st.Pop();
        if (mn.HasRuntimeNs) st.Pop();
        if (rt != null) return (null, rt, mn.IsAttribute);
        return (Mn(mnIndex), null, mn.IsAttribute);
    }

    static Expr Unwrap(Expr e) => e is TypedExpr t ? Unwrap(t.Inner) : e;

    Expr Member(Expr obj, (string? Name, Expr? Runtime, bool Attr) p)
    {
        obj = Unwrap(obj);
        if (p.Runtime != null)
        {
            if (obj is FindPropExpr or GlobalScopeExpr) return p.Runtime;
            return p.Attr ? new RawExpr($"{obj}.@[{p.Runtime}]", Prec.Call) : new IndexExpr(obj, p.Runtime);
        }
        string name = p.Attr ? "@" + p.Name : p.Name!;
        if (obj is FindPropExpr or GlobalScopeExpr or ActivationExpr or CatchScopeExpr) return new NameExpr(name);
        return new MemberExpr(obj, name);
    }

    string? SlotName(Expr obj, int slot, out bool isActivation)
    {
        isActivation = false;
        obj = Unwrap(obj);
        List<Trait>? traits = null;
        switch (obj)
        {
            case ActivationExpr a:
                traits = a.Traits;
                isActivation = true;
                break;
            case CatchScopeExpr c:
                return c.Handler.Name ?? "e";
            case LocalExpr { Var.Register: 0 }:
            case ThisExpr:
                traits = _owner.IsStatic ? _owner.Class?.Traits : _owner.Instance?.Traits;
                break;
            case GlobalScopeExpr:
                traits = _owner.Script?.Traits;
                break;
        }
        var t = traits?.FirstOrDefault(x => x.SlotId == slot && x.Kind is TraitKind.Slot or TraitKind.Const);
        return t != null ? _names.Short(t.Name) : null;
    }

    Expr Slot(Expr obj, int slot)
    {
        var name = SlotName(obj, slot, out _);
        var u = Unwrap(obj);
        if (name == null) return new MemberExpr(u, $"§§slot[{slot}]");
        if (u is ActivationExpr or CatchScopeExpr or GlobalScopeExpr) return new NameExpr(name);
        return new MemberExpr(u, name);
    }

    static Expr Lit(object? v) => new LitExpr(v);

    Expr Binary(Avm2State st, string op)
    {
        var b = st.Pop();
        var a = st.Pop();
        return new BinaryExpr(op, a, b);
    }

    void EmitExpr(List<Stmt> output, Expr e)
    {
        var u = Unwrap(e);
        if (u.HasSideEffects) output.Add(new ExprStmt(Simplifier.SimplifyAssign(u)));
    }

    public Expr? Translate(CfgBlock block, SymState state, List<Stmt> output)
    {
        var st = (Avm2State)state;
        for (int i = block.Start; i < block.End; i++)
        {
            var ins = _code[i];
            var a = ins.Args;
            switch (ins.Op)
            {
                case -1:
                    output.Add(new CommentStmt($"Undecodable bytes at 0x{ins.Offset:X}"));
                    return null;
                case Opcodes.Nop:
                case Opcodes.Label:
                case Opcodes.Bkpt:
                case Opcodes.BkptLine:
                case Opcodes.Timestamp:
                case Opcodes.DebugLine:
                case Opcodes.DebugFile:
                case Opcodes.Debug:
                case Opcodes.Kill:
                case Opcodes.Jump:
                    break;

                case Opcodes.Throw:
                    output.Add(new ThrowStmt(Unwrap(st.Pop())));
                    return null;
                case Opcodes.ReturnVoid:
                    output.Add(new ReturnStmt(null));
                    return null;
                case Opcodes.ReturnValue:
                    output.Add(new ReturnStmt(Unwrap(st.Pop())));
                    return null;

                // --- constants
                case Opcodes.PushNull: st.Push(LitExpr.Null); break;
                case Opcodes.PushUndefined: st.Push(LitExpr.Undef); break;
                case Opcodes.PushTrue: st.Push(LitExpr.Bool(true)); break;
                case Opcodes.PushFalse: st.Push(LitExpr.Bool(false)); break;
                case Opcodes.PushNaN: st.Push(LitExpr.Num(double.NaN)); break;
                case Opcodes.PushByte: st.Push(LitExpr.Int(a[0])); break;
                case Opcodes.PushShort: st.Push(LitExpr.Int((short)a[0])); break;
                case Opcodes.PushInt: st.Push(LitExpr.Int(a[0] < _abc.Ints.Length ? _abc.Ints[a[0]] : 0)); break;
                case Opcodes.PushUInt: st.Push(LitExpr.Int(a[0] < _abc.UInts.Length ? (long)_abc.UInts[a[0]] : 0)); break;
                case Opcodes.PushDouble: st.Push(LitExpr.Num(a[0] < _abc.Doubles.Length ? _abc.Doubles[a[0]] : double.NaN)); break;
                case Opcodes.PushString: st.Push(LitExpr.Str(_abc.Str(a[0]))); break;
                case 0x22: st.Push(new RawExpr($"§§float({a[0]})")); break;
                case Opcodes.PushNamespace: st.Push(new NameExpr(_names.NamespaceIdentifier(_abc.Ns(a[0]).Name))); break;

                // --- stack
                case Opcodes.Pop:
                {
                    var v = st.Pop();
                    // Discarding one copy of a duplicated value (a && b, a || b): the other copy is still used
                    if (!_duplicated.Contains(v)) EmitExpr(output, v);
                    break;
                }
                case Opcodes.Dup:
                    _duplicated.Add(st.Peek());
                    st.Push(st.Peek());
                    break;
                case Opcodes.Swap:
                {
                    var x = st.Pop();
                    var y = st.Pop();
                    st.Push(x);
                    st.Push(y);
                    break;
                }

                // --- registers
                case Opcodes.GetLocal0:
                case Opcodes.GetLocal1:
                case Opcodes.GetLocal2:
                case Opcodes.GetLocal3:
                    PushLocal(st, ins.Op - Opcodes.GetLocal0);
                    break;
                case Opcodes.GetLocal:
                    PushLocal(st, a[0]);
                    break;
                case Opcodes.SetLocal0:
                case Opcodes.SetLocal1:
                case Opcodes.SetLocal2:
                case Opcodes.SetLocal3:
                    SetLocal(st, ins.Op - Opcodes.SetLocal0, output);
                    break;
                case Opcodes.SetLocal:
                    SetLocal(st, a[0], output);
                    break;
                case Opcodes.IncLocal:
                case Opcodes.IncLocalI:
                    output.Add(new ExprStmt(new PostfixExpr("++", new LocalExpr(Local(a[0])))));
                    break;
                case Opcodes.DecLocal:
                case Opcodes.DecLocalI:
                    output.Add(new ExprStmt(new PostfixExpr("--", new LocalExpr(Local(a[0])))));
                    break;

                // --- scope
                case Opcodes.PushScope:
                case Opcodes.PushWith:
                    st.Scope.Add(Unwrap(st.Pop()));
                    break;
                case Opcodes.PopScope:
                    if (st.Scope.Count > 0) st.Scope.RemoveAt(st.Scope.Count - 1);
                    break;
                case Opcodes.GetGlobalScope:
                    st.Push(new GlobalScopeExpr());
                    break;
                case Opcodes.GetScopeObject:
                    st.Push(a[0] < st.Scope.Count ? st.Scope[a[0]] : new RawExpr($"§§scope({a[0]})"));
                    break;
                case Opcodes.GetOuterScope:
                    st.Push(new GlobalScopeExpr());
                    break;
                case Opcodes.NewActivation:
                    st.Push(new ActivationExpr(_body.Traits));
                    break;
                case Opcodes.NewCatch:
                    st.Push(new CatchScopeExpr(_handlers.FirstOrDefault(h => h.Index == a[0]) ?? new HandlerInfo { Target = new CfgBlock(), Name = "e" }));
                    break;

                // --- properties
                case Opcodes.FindPropStrict:
                case Opcodes.FindProperty:
                case Opcodes.FindDef:
                {
                    var mn = _abc.Mn(a[0]);
                    if (mn.HasRuntimeName) st.Pop();
                    if (mn.HasRuntimeNs) st.Pop();
                    st.Push(new FindPropExpr(mn.HasRuntimeName ? "§§runtime" : Mn(a[0])));
                    break;
                }
                case Opcodes.GetLex:
                    st.Push(new NameExpr(Mn(a[0])));
                    break;
                case Opcodes.GetProperty:
                {
                    var p = PropName(a[0], st);
                    var obj = st.Pop();
                    st.Push(Member(obj, p));
                    break;
                }
                case Opcodes.SetProperty:
                case Opcodes.InitProperty:
                {
                    var v = Unwrap(st.Pop());
                    var p = PropName(a[0], st);
                    var obj = st.Pop();
                    output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(Member(obj, p), v))));
                    break;
                }
                case Opcodes.DeleteProperty:
                {
                    var p = PropName(a[0], st);
                    var obj = st.Pop();
                    st.Push(new UnaryExpr("delete", Member(obj, p)));
                    break;
                }
                case Opcodes.GetSuper:
                {
                    var p = PropName(a[0], st);
                    st.Pop();
                    st.Push(Member(new SuperExpr(), p));
                    break;
                }
                case Opcodes.SetSuper:
                {
                    var v = Unwrap(st.Pop());
                    var p = PropName(a[0], st);
                    st.Pop();
                    output.Add(new ExprStmt(new AssignExpr(Member(new SuperExpr(), p), v)));
                    break;
                }
                case Opcodes.GetDescendants:
                {
                    var p = PropName(a[0], st);
                    var obj = st.Pop();
                    st.Push(new DescendantsExpr(obj, p.Name ?? $"[{p.Runtime}]"));
                    break;
                }
                case Opcodes.GetSlot:
                {
                    var obj = st.Pop();
                    st.Push(Slot(obj, a[0]));
                    break;
                }
                case Opcodes.SetSlot:
                {
                    var v = Unwrap(st.Pop());
                    var obj = Unwrap(st.Pop());
                    SetSlot(obj, a[0], v, output);
                    break;
                }
                case Opcodes.GetGlobalSlot:
                    st.Push(Slot(new GlobalScopeExpr(), a[0]));
                    break;
                case Opcodes.SetGlobalSlot:
                    output.Add(new ExprStmt(new AssignExpr(Slot(new GlobalScopeExpr(), a[0]), Unwrap(st.Pop()))));
                    break;

                // --- calls
                case Opcodes.CallProperty:
                case Opcodes.CallPropLex:
                case Opcodes.CallPropVoid:
                {
                    var args = st.PopN(a[1]).Select(Unwrap).ToList();
                    var p = PropName(a[0], st);
                    var obj = st.Pop();
                    var call = new CallExpr(Member(obj, p), args);
                    if (ins.Op == Opcodes.CallPropVoid) output.Add(new ExprStmt(call));
                    else st.Push(call);
                    break;
                }
                case Opcodes.CallSuper:
                case Opcodes.CallSuperVoid:
                {
                    var args = st.PopN(a[1]).Select(Unwrap).ToList();
                    var p = PropName(a[0], st);
                    st.Pop();
                    var call = new CallExpr(Member(new SuperExpr(), p), args);
                    if (ins.Op == Opcodes.CallSuperVoid) output.Add(new ExprStmt(call));
                    else st.Push(call);
                    break;
                }
                case Opcodes.Call:
                {
                    var args = st.PopN(a[0]).Select(Unwrap).ToList();
                    var recv = Unwrap(st.Pop());
                    var fn = Unwrap(st.Pop());
                    if (recv is LitExpr { Value: null } or GlobalScopeExpr or FindPropExpr || recv.ToString() == "this")
                        st.Push(new CallExpr(fn, args));
                    else st.Push(new CallExpr(new MemberExpr(fn, "call"), [recv, .. args]));
                    break;
                }
                case Opcodes.CallMethod:
                case Opcodes.CallStatic:
                {
                    var args = st.PopN(a[1]).Select(Unwrap).ToList();
                    var recv = st.Pop();
                    st.Push(new CallExpr(new RawExpr(ins.Op == Opcodes.CallMethod ? $"{recv}.§§method{a[0]}" : $"§§static{a[0]}"), args));
                    break;
                }
                case Opcodes.Construct:
                {
                    var args = st.PopN(a[0]).Select(Unwrap).ToList();
                    var obj = Unwrap(st.Pop());
                    st.Push(new NewExpr(obj, args));
                    break;
                }
                case Opcodes.ConstructProp:
                {
                    var args = st.PopN(a[1]).Select(Unwrap).ToList();
                    var p = PropName(a[0], st);
                    var obj = st.Pop();
                    st.Push(new NewExpr(Member(obj, p), args));
                    break;
                }
                case Opcodes.ConstructSuper:
                {
                    var args = st.PopN(a[0]).Select(Unwrap).ToList();
                    st.Pop();
                    output.Add(new ExprStmt(new CallExpr(new SuperExpr(), args)));
                    break;
                }
                case Opcodes.ApplyType:
                {
                    var ps = st.PopN(a[0]).Select(Unwrap).ToList();
                    var b = Unwrap(st.Pop());
                    st.Push(new RawExpr($"{b}.<{string.Join(", ", ps)}>"));
                    break;
                }
                case Opcodes.NewObject:
                {
                    var items = st.PopN(a[0] * 2).Select(Unwrap).ToList();
                    var props = new List<(Expr, Expr)>();
                    for (int k = 0; k + 1 < items.Count; k += 2) props.Add((items[k], items[k + 1]));
                    st.Push(new ObjectLitExpr(props));
                    break;
                }
                case Opcodes.NewArray:
                    st.Push(new ArrayLitExpr(st.PopN(a[0]).Select(Unwrap).ToList()));
                    break;
                case Opcodes.NewFunction:
                    st.Push(NestedFunction(a[0]));
                    break;
                case Opcodes.NewClass:
                    st.Pop();
                    st.Push(new NewClassExpr(a[0]));
                    break;

                // --- conversions (transparent)
                case Opcodes.ConvertS: case Opcodes.CoerceS: st.Push(new TypedExpr("String", st.Pop())); break;
                case Opcodes.ConvertI: case Opcodes.CoerceI: st.Push(new TypedExpr("int", st.Pop())); break;
                case Opcodes.ConvertU: case Opcodes.CoerceU: st.Push(new TypedExpr("uint", st.Pop())); break;
                case Opcodes.ConvertD: case Opcodes.CoerceD: st.Push(new TypedExpr("Number", st.Pop())); break;
                case Opcodes.ConvertB: case Opcodes.CoerceB: st.Push(new TypedExpr("Boolean", st.Pop())); break;
                case Opcodes.ConvertO: case Opcodes.CoerceO: st.Push(new TypedExpr("Object", st.Pop())); break;
                case Opcodes.CoerceA: st.Push(new TypedExpr("*", st.Pop())); break;
                case Opcodes.Coerce: st.Push(new TypedExpr(_names.TypeName(a[0]), st.Pop())); break;
                case Opcodes.CheckFilter:
                case Opcodes.EscXElem:
                case Opcodes.EscXAttr:
                    break;
                case Opcodes.AsType:
                    st.Push(new BinaryExpr("as", Unwrap(st.Pop()), new NameExpr(_names.TypeName(a[0]))));
                    break;
                case Opcodes.AsTypeLate: st.Push(Binary(st, "as")); break;
                case Opcodes.IsType:
                    st.Push(new BinaryExpr("is", Unwrap(st.Pop()), new NameExpr(_names.TypeName(a[0]))));
                    break;
                case Opcodes.IsTypeLate: st.Push(Binary(st, "is")); break;
                case Opcodes.InstanceOf: st.Push(Binary(st, "instanceof")); break;
                case Opcodes.In: st.Push(Binary(st, "in")); break;
                case Opcodes.TypeOf: st.Push(new UnaryExpr("typeof", Unwrap(st.Pop()))); break;

                // --- arithmetic
                case Opcodes.Not: st.Push(Structurer.Negate(Unwrap(st.Pop()))); break;
                case Opcodes.BitNot: st.Push(new UnaryExpr("~", Unwrap(st.Pop()))); break;
                case Opcodes.Negate:
                case Opcodes.NegateI:
                {
                    var v = Unwrap(st.Pop());
                    st.Push(v is LitExpr { IsNumber: true } l ? LitExpr.Num(-l.AsNumber) : new UnaryExpr("-", v));
                    break;
                }
                case Opcodes.Increment:
                case Opcodes.IncrementI:
                    st.Push(new BinaryExpr("+", Unwrap(st.Pop()), LitExpr.Int(1)));
                    break;
                case Opcodes.Decrement:
                case Opcodes.DecrementI:
                    st.Push(new BinaryExpr("-", Unwrap(st.Pop()), LitExpr.Int(1)));
                    break;
                case Opcodes.Add: case Opcodes.AddI: st.Push(Binary(st, "+")); break;
                case Opcodes.Subtract: case Opcodes.SubtractI: st.Push(Binary(st, "-")); break;
                case Opcodes.Multiply: case Opcodes.MultiplyI: st.Push(Binary(st, "*")); break;
                case Opcodes.Divide: st.Push(Binary(st, "/")); break;
                case Opcodes.Modulo: st.Push(Binary(st, "%")); break;
                case Opcodes.LShift: st.Push(Binary(st, "<<")); break;
                case Opcodes.RShift: st.Push(Binary(st, ">>")); break;
                case Opcodes.URShift: st.Push(Binary(st, ">>>")); break;
                case Opcodes.BitAnd: st.Push(Binary(st, "&")); break;
                case Opcodes.BitOr: st.Push(Binary(st, "|")); break;
                case Opcodes.BitXor: st.Push(Binary(st, "^")); break;
                case Opcodes.EqualsOp: st.Push(Binary(st, "==")); break;
                case Opcodes.StrictEquals: st.Push(Binary(st, "===")); break;
                case Opcodes.LessThan: st.Push(Binary(st, "<")); break;
                case Opcodes.LessEquals: st.Push(Binary(st, "<=")); break;
                case Opcodes.GreaterThan: st.Push(Binary(st, ">")); break;
                case Opcodes.GreaterEquals: st.Push(Binary(st, ">=")); break;

                // --- iteration
                case Opcodes.HasNext2:
                    st.Push(new HasNextExpr(new LocalExpr(Local(a[0])), new LocalExpr(Local(a[1]))));
                    break;
                case Opcodes.HasNext:
                {
                    var idx = st.Pop();
                    var obj = st.Pop();
                    st.Push(new HasNextExpr(obj, idx));
                    break;
                }
                case Opcodes.NextName:
                case Opcodes.NextValue:
                {
                    var idx = Unwrap(st.Pop());
                    var obj = Unwrap(st.Pop());
                    st.Push(new NextNameExpr(obj, idx, ins.Op == Opcodes.NextValue));
                    break;
                }

                // --- domain memory (Alchemy)
                case >= Opcodes.Li8 and <= Opcodes.Lf64:
                    st.Push(new CallExpr(new NameExpr(ins.Name), [Unwrap(st.Pop())]));
                    break;
                case >= Opcodes.Si8 and <= Opcodes.Sf64:
                {
                    var addr = Unwrap(st.Pop());
                    var v = Unwrap(st.Pop());
                    output.Add(new ExprStmt(new CallExpr(new NameExpr(ins.Name), [v, addr])));
                    break;
                }
                case Opcodes.Sxi1:
                case Opcodes.Sxi8:
                case Opcodes.Sxi16:
                    st.Push(new CallExpr(new NameExpr(ins.Name), [Unwrap(st.Pop())]));
                    break;

                case Opcodes.Dxns:
                    output.Add(new RawStmt($"default xml namespace = {LitExpr.Quote(_abc.Str(a[0]))};"));
                    break;
                case Opcodes.DxnsLate:
                    output.Add(new RawStmt($"default xml namespace = {st.Pop()};"));
                    break;

                // --- jumps (last instruction of the block)
                case Opcodes.IfTrue: return Unwrap(st.Pop());
                case Opcodes.IfFalse: return new UnaryExpr("!", Unwrap(st.Pop()));
                case Opcodes.IfEq: return Cmp(st, "==");
                case Opcodes.IfNe: return Cmp(st, "!=");
                case Opcodes.IfLt: return Cmp(st, "<");
                case Opcodes.IfLe: return Cmp(st, "<=");
                case Opcodes.IfGt: return Cmp(st, ">");
                case Opcodes.IfGe: return Cmp(st, ">=");
                case Opcodes.IfStrictEq: return Cmp(st, "===");
                case Opcodes.IfStrictNe: return Cmp(st, "!==");
                case Opcodes.IfNlt: return new UnaryExpr("!", Cmp(st, "<"));
                case Opcodes.IfNle: return new UnaryExpr("!", Cmp(st, "<="));
                case Opcodes.IfNgt: return new UnaryExpr("!", Cmp(st, ">"));
                case Opcodes.IfNge: return new UnaryExpr("!", Cmp(st, ">="));
                case Opcodes.LookupSwitch: return Unwrap(st.Pop());

                default:
                    output.Add(new CommentStmt($"unsupported instruction: {ins.Name}"));
                    break;
            }
        }
        return null;
    }

    Expr Cmp(Avm2State st, string op)
    {
        var b = Unwrap(st.Pop());
        var a = Unwrap(st.Pop());
        return new BinaryExpr(op, a, b);
    }

    void PushLocal(Avm2State st, int reg)
    {
        if (st.RegObjects.TryGetValue(reg, out var obj)) st.Push(obj);
        else if (reg == 0) st.Push(new ThisExpr());
        else st.Push(new LocalExpr(Local(reg)));
    }

    void SetLocal(Avm2State st, int reg, List<Stmt> output)
    {
        var raw = st.Pop();
        var value = Unwrap(raw);
        // Scope objects, and a caught exception kept in a register (compilers that skip newcatch): no statement,
        // later reads of the register print the catch variable
        if (value is ActivationExpr or CatchScopeExpr or CaughtExpr)
        {
            st.RegObjects[reg] = value;
            return;
        }
        st.RegObjects.Remove(reg);
        var v = Local(reg);
        if (raw is TypedExpr te && v.Type == null && !v.IsParam && te.Type != "*") v.Type = te.Type;
        var target = new LocalExpr(v);

        // x++ inside an expression: getlocal r; dup; increment; setlocal r
        if (st.Stack.Count > 0 && Unwrap(st.Peek()) is LocalExpr top && top.Var == v
            && value is BinaryExpr { Op: "+" or "-", Right: LitExpr { IsNumber: true } one } inc && one.AsNumber == 1
            && Unwrap(inc.Left) is LocalExpr l && l.Var == v)
        {
            st.Pop();
            st.Push(new PostfixExpr(inc.Op == "+" ? "++" : "--", target));
            return;
        }
        // a = b = c, while ((x = f()) != null): dup; setlocal
        if (st.Stack.Count > 0 && ReferenceEquals(st.Peek(), raw))
        {
            st.Pop();
            var asg = new AssignExpr(target, value);
            st.Push(value is BinaryExpr { Op: "+" or "-", Right: LitExpr { IsNumber: true } o } bi && o.AsNumber == 1
                    && Unwrap(bi.Left) is LocalExpr ll && ll.Var == v
                ? new UnaryExpr(bi.Op == "+" ? "++" : "--", target)
                : asg);
            return;
        }
        // The first assignment becomes "var …" later (Declarations), once the method is structured
        output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(target, value))));
    }

    void SetSlot(Expr obj, int slot, Expr value, List<Stmt> output)
    {
        if (obj is CatchScopeExpr) return; // stores the exception in the catch scope
        var name = SlotName(obj, slot, out bool act);
        if (act && obj is ActivationExpr a && name != null)
        {
            // Copy of a parameter into the activation object: not visible in source
            if (value is LocalExpr { Var.IsParam: true } p && p.Var.Name == name) return;
            var t = a.Traits.First(x => x.SlotId == slot && x.Kind is TraitKind.Slot or TraitKind.Const);
            output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(new SlotVarExpr(name, _names.TypeName(t.TypeName)), value))));
            return;
        }
        output.Add(new ExprStmt(Simplifier.SimplifyAssign(new AssignExpr(Slot(obj, slot), value))));
    }

    Expr NestedFunction(int methodIndex)
    {
        if (methodIndex >= _abc.Methods.Count) return new RawExpr($"§§function({methodIndex})");
        var m = _abc.Methods[methodIndex];
        var f = new FunctionExpr();
        if (m.Body == null || _depth > 16)
        {
            f.Body = [new CommentStmt("no method body")];
            return f;
        }
        try
        {
            var d = new MethodDecompiler(_abc, _names, m, new MethodOwner { Script = _owner.Script }, _depth + 1);
            f.Body = d.Decompile();
            f.Params = d.Signature();
            f.ReturnType = m.ReturnType == 0 ? "*" : _names.TypeName(m.ReturnType);
        }
        catch (Exception e)
        {
            f.Body = [new CommentStmt("Decompilation failed: " + e.Message)];
        }
        return f;
    }

    /// <summary>Parameters as "name:Type = default", plus "...rest".</summary>
    public List<string> Signature()
    {
        var list = new List<string>();
        int n = _method.ParamTypes.Length;
        int firstOptional = n - _method.Options.Count;
        for (int i = 0; i < n; i++)
        {
            string s = $"{ParamName(i)}:{(_method.ParamTypes[i] == 0 ? "*" : _names.TypeName(_method.ParamTypes[i]))}";
            if (i >= firstOptional)
            {
                var (val, kind) = _method.Options[i - firstOptional];
                s += " = " + DefaultValue(_abc, _names, val, kind);
            }
            list.Add(s);
        }
        if (_method.NeedRest) list.Add("..." + Local(n + 1).Name);
        return list;
    }

    public static string DefaultValue(AbcFile abc, NameContext names, int index, byte kind) => kind switch
    {
        0x03 => (index < abc.Ints.Length ? abc.Ints[index] : 0).ToString(),
        0x04 => (index < abc.UInts.Length ? abc.UInts[index] : 0).ToString(),
        0x06 => LitExpr.FormatNumber(index < abc.Doubles.Length ? abc.Doubles[index] : double.NaN),
        0x01 => LitExpr.Quote(abc.Str(index)),
        0x0B => "true",
        0x0A => "false",
        0x0C => "null",
        0x00 => "undefined",
        0x08 or 0x16 or 0x17 or 0x18 or 0x19 or 0x1A or 0x05 => LitExpr.Quote(abc.Ns(index).Name),
        _ => "undefined",
    };

    // ------------------------------------------------------------------ post-structuring patterns

    List<Stmt> PostProcess(List<Stmt> stmts)
    {
        var res = new List<Stmt>();
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = PostStmt(stmts[i]);
            // for-in / for each: while (hasnext2(o, i)) { x = nextname(o, i); … }
            if (s is WhileStmt { Cond: HasNextExpr hn } w && w.Body.Count > 0)
            {
                var fi = ForIn(hn, w.Body, w.Label, res);
                if (fi != null) { res.Add(fi); continue; }
            }
            // for (; hasnext; update): the "update" moves back to the end of the body if there is no continue
            if (s is ForStmt { Cond: HasNextExpr hn2 } f && f.Body.Count > 0 && !ContainsContinue(f.Body))
            {
                var fi = ForIn(hn2, [.. f.Body, .. f.Update], f.Label, res);
                if (fi != null) { res.Add(fi); continue; }
            }
            if (s is SwitchStmt sw) RewriteSwitch(sw, res);
            res.Add(s);
        }
        return res;
    }

    Stmt PostStmt(Stmt s)
    {
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
        return s;
    }

    static bool ContainsContinue(List<Stmt> body) => body.Any(s => s.ToString().Contains("continue"));

    ForInStmt? ForIn(HasNextExpr hn, List<Stmt> body, string? label, List<Stmt> before)
    {
        if (hn.Object is not LocalExpr objReg || hn.Index is not LocalExpr idxReg) return null;
        // First statement of the body: assignment from nextname / nextvalue
        Expr? variable = null;
        bool each = false;
        bool declare = false;
        string? declType = null;
        var first = body[0];
        Expr? value = null;
        switch (first)
        {
            case VarStmt { Init: not null } v:
                value = v.Init;
                variable = new NameExpr(v.Name);
                declare = true;
                declType = v.Type;
                break;
            case ExprStmt { Expr: AssignExpr { Op: "=" } asg }:
                value = asg.Value;
                variable = asg.Target;
                break;
        }
        if (Unwrap(value ?? new RawExpr("")) is not NextNameExpr nn) return null;
        each = nn.Value;
        // The iterated object: the last assignment to the register before the loop
        Expr obj = objReg;
        for (int k = before.Count - 1; k >= 0 && k >= before.Count - 4; k--)
        {
            if (before[k] is VarStmt vs && vs.Var == objReg.Var && vs.Init != null) { obj = vs.Init; before.RemoveAt(k); break; }
            if (before[k] is ExprStmt { Expr: AssignExpr { Target: LocalExpr t } a2 } && t.Var == objReg.Var) { obj = a2.Value; before.RemoveAt(k); break; }
        }
        for (int k = before.Count - 1; k >= 0 && k >= before.Count - 4; k--)
        {
            if (before[k] is VarStmt vs && vs.Var == idxReg.Var) { before.RemoveAt(k); break; }
            if (before[k] is ExprStmt { Expr: AssignExpr { Target: LocalExpr t } } && t.Var == idxReg.Var) { before.RemoveAt(k); break; }
        }
        return new ForInStmt
        {
            Variable = variable!, Object = obj, Each = each, Declare = declare, DeclareType = declType,
            Body = body.Skip(1).ToList(), Label = label,
        };
    }

    /// <summary>
    /// ASC switch: the lookupswitch index is a cascade (v0 === x ? 0 : v1 === x ? 1 : …);
    /// rebuild it as "switch (x) { case v0: … }".
    /// </summary>
    static void RewriteSwitch(SwitchStmt sw, List<Stmt> before)
    {
        var map = new Dictionary<long, Expr>();
        Expr? subject = null;
        Expr cur = sw.Discriminant;
        long? defaultIndex = null;
        while (cur is TernaryExpr t)
        {
            if (t.Cond is LitExpr { Value: bool bv })
            {
                cur = bv ? t.Then : t.Else;
                continue;
            }
            if (t.Cond is not BinaryExpr { Op: "===" or "==" } eq || t.Then is not LitExpr { IsNumber: true } idx) break;
            Expr value, subj;
            if (subject == null)
            {
                // The subject is the operand shared by all comparisons: take the "variable" side
                bool rightIsVar = eq.Right is LocalExpr or NameExpr or MemberExpr;
                subj = rightIsVar ? eq.Right : eq.Left;
                value = rightIsVar ? eq.Left : eq.Right;
                subject = subj;
            }
            else
            {
                if (eq.Right.ToString() == subject.ToString()) value = eq.Left;
                else if (eq.Left.ToString() == subject.ToString()) value = eq.Right;
                else return;
            }
            map[(long)idx.AsNumber] = value;
            cur = t.Else;
        }
        if (subject == null || cur is not LitExpr { IsNumber: true } d) return;
        defaultIndex = (long)d.AsNumber;
        foreach (var c in sw.Cases)
        {
            var labels = new List<Expr?>();
            foreach (var l in c.Labels)
            {
                if (l is LitExpr { IsNumber: true } n)
                {
                    long k = (long)n.AsNumber;
                    if (map.TryGetValue(k, out var v)) labels.Add(v);
                    else if (k == defaultIndex) labels.Add(null);
                }
                else labels.Add(null);
            }
            c.Labels = labels.Distinct().ToList();
        }
        sw.Cases.RemoveAll(c => c.Labels.Count == 0 && c.Body is [] or [BreakStmt]);
        // Temporary switch variable: "var _locN_ = expr; switch (_locN_)"
        if (subject is LocalExpr temp && before.Count > 0)
        {
            var last = before[^1];
            if (last is VarStmt vs && vs.Var == temp.Var && vs.Init != null && temp.Var.Name.StartsWith("_loc"))
            {
                subject = vs.Init;
                before.RemoveAt(before.Count - 1);
            }
            else if (last is ExprStmt { Expr: AssignExpr { Target: LocalExpr t2 } a } && t2.Var == temp.Var && temp.Var.Name.StartsWith("_loc"))
            {
                subject = a.Value;
                before.RemoveAt(before.Count - 1);
            }
        }
        sw.Discriminant = subject;
    }
}
