using System.Globalization;
using System.Text;
using MortisFlash.Avm2;
using MortisFlash.Decompiler;

namespace MortisFlash.Compiler;

/// <summary>
/// ActionScript 3 compiler for method bodies. The signature, class and traits of the method stay as they are; only its
/// code is rebuilt. Identifiers that are not locals are resolved at run time through a namespace set covering the
/// class (private, protected, internal) and every package of the ABC, as the Flex compiler does for untyped names.
/// The generated p-code is assembled by <see cref="Avm2Assembler"/>.
/// </summary>
public static class Avm2Compiler
{
    public static void CompileMethodBody(AbcFile abc, MethodInfo method, MethodOwner owner, string source)
    {
        string pcode = CompileToPCode(abc, method, owner, source);
        Avm2Assembler.Replace(abc, method, pcode);
    }

    public static string CompileToPCode(AbcFile abc, MethodInfo method, MethodOwner owner, string source)
    {
        var (body, lines) = Parser.ParseBody(source);
        var names = new NameContext(abc, []);
        var decompiler = new MethodDecompiler(abc, names, method, owner);
        var parameters = decompiler.ParamNames();
        if (decompiler.ExtraParamName() is { } extra) parameters.Add(extra);
        var gen = new Avm2Generator(abc, method, owner, lines, parameters, new Avm2Generator.Shared(abc, owner), outerLocals: null);
        return gen.Generate(body, isConstructor: owner.Instance != null && owner.Instance.IInit == method.Index && !owner.IsStatic);
    }
}

sealed class Avm2Generator
{
    /// <summary>State shared by a method and the closures it contains.</summary>
    public sealed class Shared
    {
        public readonly AbcFile Abc;
        public readonly int NsSet;
        public int Labels;
        readonly Dictionary<string, int> _names = [];
        readonly Dictionary<string, int> _types = [];

        public Shared(AbcFile abc, MethodOwner owner)
        {
            Abc = abc;
            NsSet = BuildNamespaceSet(abc, owner);
        }

        /// <summary>Run-time resolved name: Multiname("name", [all relevant namespaces]).</summary>
        public int Name(string name)
        {
            if (_names.TryGetValue(name, out int i)) return i;
            return _names[name] = Abc.InternMultiname(new Multiname { Kind = MnKind.Multiname, Name = Abc.InternString(name), NsSet = NsSet });
        }

        /// <summary>Run-time name taken from the stack: obj[expr].</summary>
        public int Late => Abc.InternMultiname(new Multiname { Kind = MnKind.MultinameL, NsSet = NsSet });

        /// <summary>Type reference for catch clauses: an existing QName with that name, otherwise a top-level QName.</summary>
        public int Type(string type)
        {
            if (type is "*" or "") return 0;
            if (_types.TryGetValue(type, out int i)) return i;
            // Vector.<T>: TypeName(Vector, [T])
            int generic = type.IndexOf(".<", StringComparison.Ordinal);
            if (generic > 0 && type.EndsWith('>'))
            {
                int vector = Type(type[..generic] == "Vector" ? "__AS3__.vec.Vector" : type[..generic]);
                var args = SplitTypeArgs(type[(generic + 2)..^1]).Select(t => t == "*" ? 0 : Type(t)).ToArray();
                return _types[type] = Abc.InternMultiname(new Multiname { Kind = MnKind.TypeName, QName = vector, Params = args });
            }
            string shortName = type[(type.LastIndexOf('.') + 1)..];
            string pkg = type.Contains('.') ? type[..type.LastIndexOf('.')] : "";
            for (int k = 1; k < Abc.Multinames.Length; k++)
            {
                var m = Abc.Multinames[k];
                if (m.Kind != MnKind.QName || Abc.Str(m.Name) != shortName) continue;
                var ns = Abc.Ns(m.Ns);
                if (ns.Kind == NsKind.Package && (pkg.Length == 0 || ns.Name == pkg)) return _types[type] = k;
            }
            return _types[type] = Abc.InternMultiname(new Multiname
            {
                Kind = MnKind.QName, Ns = Abc.InternNamespace(NsKind.Package, pkg), Name = Abc.InternString(shortName),
            });
        }

        static IEnumerable<string> SplitTypeArgs(string s)
        {
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') depth++;
                else if (s[i] == '>') depth--;
                else if (s[i] == ',' && depth == 0)
                {
                    yield return s[start..i].Trim();
                    start = i + 1;
                }
            }
            yield return s[start..].Trim();
        }

        static int BuildNamespaceSet(AbcFile abc, MethodOwner owner)
        {
            var set = new List<int>();
            void Add(int ns)
            {
                if (ns > 0 && !set.Contains(ns)) set.Add(ns);
            }
            // Private namespaces: only the ones of this class / script (another class's private names must stay hidden)
            IEnumerable<Trait> own = owner.Instance?.Traits ?? [];
            if (owner.Class != null) own = own.Concat(owner.Class.Traits);
            if (owner.Script != null) own = own.Concat(owner.Script.Traits);
            foreach (var t in own)
            {
                var m = abc.Mn(t.Name);
                if (m.Kind is MnKind.QName or MnKind.QNameA && abc.Ns(m.Ns).Kind == NsKind.Private) Add(m.Ns);
            }
            if (owner.Instance is { } inst)
            {
                var m = abc.Mn(inst.Name);
                if (m.Kind == MnKind.QName) Add(m.Ns);
                if ((inst.Flags & 8) != 0) Add(inst.ProtectedNs);
            }
            Add(abc.InternNamespace(NsKind.Package, ""));
            for (int i = 1; i < abc.Namespaces.Length; i++)
                if (abc.Namespaces[i].Kind is NsKind.Package or NsKind.PackageInternal or NsKind.Protected or NsKind.StaticProtected
                    or NsKind.Namespace or NsKind.Explicit)
                    Add(i);
            return abc.InternNsSet([.. set]);
        }
    }

    readonly AbcFile _abc;
    readonly MethodInfo _method;
    readonly MethodOwner _owner;
    readonly Dictionary<object, int> _lines;
    readonly Shared _shared;
    readonly StringBuilder _code = new();
    readonly Dictionary<string, (int Reg, string? Type)> _locals = new(StringComparer.Ordinal);
    readonly HashSet<string>? _outerLocals;
    readonly List<string> _tries = [];
    readonly List<Frame> _frames = [];
    readonly int _paramCount;
    int _nextReg;
    readonly Stack<int> _freeTemps = new();

    /// <summary>Break / continue target. Finally blocks between a jump and its target are replayed before jumping.</summary>
    sealed record Frame(string? Label, string? Break, string? Continue, List<Stmt>? Finally);

    public Avm2Generator(AbcFile abc, MethodInfo method, MethodOwner owner, Dictionary<object, int> lines, List<string> parameters,
        Shared shared, HashSet<string>? outerLocals)
    {
        _abc = abc;
        _method = method;
        _owner = owner;
        _lines = lines;
        _shared = shared;
        _outerLocals = outerLocals;
        _paramCount = parameters.Count;
        for (int i = 0; i < parameters.Count; i++)
        {
            string p = parameters[i].StartsWith("...") ? parameters[i][3..] : parameters[i];
            _locals[p] = (i + 1, null);
        }
        _nextReg = parameters.Count + 1;
    }

    CompileException Error(object? node, string message) => new(node != null && _lines.TryGetValue(node, out int l) ? l : 0, message);

    // ------------------------------------------------------------------ output

    void Op(string op, string args = "")
    {
        _code.Append("      ").Append(op);
        if (args.Length > 0) _code.Append(' ').Append(args);
        _code.Append('\n');
    }

    void Mark(string label) => _code.Append(label).Append(":\n");

    string NewLabel() => $"L{_shared.Labels++}";

    string Mn(string name) => $"#mn{_shared.Name(name)}";

    string Late => $"#mn{_shared.Late}";

    int Temp()
    {
        if (_freeTemps.Count > 0) return _freeTemps.Pop();
        return _nextReg++;
    }

    void Free(int reg)
    {
        Op("kill", reg.ToString());
        _freeTemps.Push(reg);
    }

    void GetLocal(int r) => Op(r <= 3 ? $"getlocal{r}" : "getlocal", r <= 3 ? "" : r.ToString());
    void SetLocal(int r) => Op(r <= 3 ? $"setlocal{r}" : "setlocal", r <= 3 ? "" : r.ToString());

    public string Generate(List<Stmt> body, bool isConstructor)
    {
        // Every "var" is function-scoped: registers are reserved up front and initialized with their default value
        DeclareLocals(body);
        Op("getlocal0");
        Op("pushscope");
        // Debug records keep parameter and variable names (players ignore them; the decompiler reads them back)
        foreach (var (name, (reg, _)) in _locals.OrderBy(l => l.Value.Reg))
            Op("debug", $"1, {LitExpr.Quote(name)}, {reg - 1}, 0");
        // Registers start as undefined; typed locals that may be read before their declaration runs get their default
        var topLevelInit = body.OfType<VarStmt>().Where(v => v.Init != null).Select(v => v.Name).ToHashSet();
        foreach (var (name, (reg, type)) in _locals.Where(l => l.Value.Reg > _paramCount).OrderBy(l => l.Value.Reg))
        {
            if (type is null or "*" || topLevelInit.Contains(name)) continue;
            DefaultValue(type);
            SetLocal(reg);
        }
        Statements(body);
        Op("returnvoid");
        var sb = new StringBuilder();
        foreach (var t in _tries) sb.Append("    ").Append(t).Append('\n');
        sb.Append(_code);
        _ = isConstructor;
        return sb.ToString();
    }

    void DeclareLocals(List<Stmt> body)
    {
        void Declare(string name, string? type)
        {
            if (!_locals.ContainsKey(name)) _locals[name] = (_nextReg++, type);
        }
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                switch (s)
                {
                    case VarStmt v: Declare(v.Name, v.Type); break;
                    case BlockStmt b: Walk(b.Body); break;
                    case IfStmt i:
                        Walk(i.Then);
                        if (i.Else != null) Walk(i.Else);
                        break;
                    case ForStmt f:
                        if (f.Init != null) Walk([f.Init]);
                        Walk(f.Body);
                        break;
                    case ForInStmt fi:
                        if (fi.Declare && fi.Variable is NameExpr n) Declare(n.Name, fi.DeclareType);
                        Walk(fi.Body);
                        break;
                    case LoopStmt l: Walk(l.Body); break;
                    case SwitchStmt sw:
                        foreach (var c in sw.Cases) Walk(c.Body);
                        break;
                    case TryStmt t:
                        Walk(t.Body);
                        foreach (var c in t.Catches)
                        {
                            Declare(c.Name ?? "e", null);
                            Walk(c.Body);
                        }
                        if (t.Finally != null) Walk(t.Finally);
                        break;
                    case WithStmt w: Walk(w.Body); break;
                    case FunctionStmt fs when fs.Function.Name != null: Declare(fs.Function.Name, "Function"); break;
                }
            }
        }
        Walk(body);
    }

    void DefaultValue(string? type)
    {
        switch (type)
        {
            case "int":
            case "uint": Op("pushbyte", "0"); break;
            case "Number": Op("pushnan"); break;
            case "Boolean": Op("pushfalse"); break;
            case null or "*": Op("pushundefined"); break;
            default: Op("pushnull"); break;
        }
    }

    /// <summary>Converts the value on the stack to the declared type of a local.</summary>
    void Coerce(string? type)
    {
        switch (type)
        {
            case null or "*": break;
            case "int": Op("convert_i"); break;
            case "uint": Op("convert_u"); break;
            case "Number": Op("convert_d"); break;
            case "Boolean": Op("convert_b"); break;
            case "String": Op("coerce_s"); break;
            default:
                Op("coerce", $"#mn{_shared.Type(type)}");
                break;
        }
    }

    bool TryLocal(string name, out (int Reg, string? Type) local)
    {
        if (_locals.TryGetValue(name, out local)) return true;
        if (_outerLocals != null && _outerLocals.Contains(name))
            throw new CompileException(0, $"the nested function uses the local variable '{name}' of the enclosing method; closures over locals are not supported yet");
        return false;
    }

    // ------------------------------------------------------------------ statements

    void Statements(List<Stmt> list)
    {
        foreach (var s in list) Statement(s);
    }

    void Statement(Stmt s)
    {
        switch (s)
        {
            case ExprStmt e:
                Effect(e.Expr);
                break;
            case VarStmt v:
                if (v.Init != null)
                {
                    var (reg, type) = _locals[v.Name];
                    Value(v.Init);
                    Coerce(type);
                    SetLocal(reg);
                }
                break;
            case BlockStmt b:
                Statements(b.Body);
                break;
            case IfStmt i:
            {
                string elseLabel = NewLabel();
                Condition(i.Cond, elseLabel, jumpIfTrue: false);
                Statements(i.Then);
                if (i.Else is { Count: > 0 })
                {
                    string end = NewLabel();
                    Op("jump", end);
                    Mark(elseLabel);
                    Statements(i.Else);
                    Mark(end);
                }
                else Mark(elseLabel);
                break;
            }
            case WhileStmt w:
            {
                // Same layout as the Flex compiler: the condition sits after the body
                string body = NewLabel(), cond = NewLabel(), end = NewLabel();
                Op("jump", cond);
                Mark(body);
                Op("label");
                Loop(w.Label, end, cond, () => Statements(w.Body));
                Mark(cond);
                Condition(w.Cond, body, jumpIfTrue: true);
                Mark(end);
                break;
            }
            case DoWhileStmt d:
            {
                string body = NewLabel(), cond = NewLabel(), end = NewLabel();
                Mark(body);
                Op("label");
                Loop(d.Label, end, cond, () => Statements(d.Body));
                Mark(cond);
                Condition(d.Cond, body, jumpIfTrue: true);
                Mark(end);
                break;
            }
            case ForStmt f:
            {
                if (f.Init != null) Statement(f.Init);
                string body = NewLabel(), cont = NewLabel(), cond = NewLabel(), end = NewLabel();
                Op("jump", cond);
                Mark(body);
                Op("label");
                Loop(f.Label, end, cont, () => Statements(f.Body));
                Mark(cont);
                foreach (var u in f.Update) Statement(u);
                Mark(cond);
                if (f.Cond != null) Condition(f.Cond, body, jumpIfTrue: true);
                else Op("jump", body);
                Mark(end);
                break;
            }
            case ForInStmt fi:
                ForIn(fi);
                break;
            case SwitchStmt sw:
                Switch(sw);
                break;
            case BreakStmt br:
                Jump(br, br.Label, isBreak: true);
                break;
            case ContinueStmt co:
                Jump(co, co.Label, isBreak: false);
                break;
            case ReturnStmt r:
                Return(r);
                break;
            case ThrowStmt t:
                Value(t.Value);
                Op("throw");
                break;
            case TryStmt t:
                Try(t);
                break;
            case FunctionStmt fs:
            {
                Closure(fs.Function);
                var (reg, _) = _locals[fs.Function.Name!];
                SetLocal(reg);
                break;
            }
            case CommentStmt:
                break;
            case WithStmt:
                throw Error(s, "'with' is not supported by the ActionScript 3 compiler");
            default:
                throw Error(s, $"unsupported statement: {s.ToString().Trim()}");
        }
    }

    void Loop(string? label, string breakLabel, string? continueLabel, Action body)
    {
        _frames.Add(new Frame(label, breakLabel, continueLabel, null));
        try { body(); }
        finally { _frames.RemoveAt(_frames.Count - 1); }
    }

    /// <summary>break / continue: runs the finally blocks that are left on the way, then jumps.</summary>
    void Jump(Stmt s, string? label, bool isBreak)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (f.Finally != null || f.Break == null) continue;
            if (label != null && f.Label != label) continue;
            string? target = isBreak ? f.Break : f.Continue;
            if (target == null)
            {
                if (label != null) throw Error(s, $"'continue {label}' does not refer to a loop");
                continue;
            }
            ReplayFinally(i + 1);
            Op("jump", target);
            return;
        }
        throw Error(s, label != null ? $"unknown label '{label}'" : isBreak ? "'break' outside of a loop or switch" : "'continue' outside of a loop");
    }

    /// <summary>Emits the finally blocks of the try statements entered after frame index <paramref name="from"/>.</summary>
    void ReplayFinally(int from)
    {
        var saved = _frames.ToList();
        for (int i = saved.Count - 1; i >= from; i--)
        {
            if (saved[i].Finally is not { } fin) continue;
            // While replaying, the finally block itself runs outside its own try
            _frames.RemoveRange(i, _frames.Count - i);
            Statements(fin);
        }
        _frames.Clear();
        _frames.AddRange(saved);
    }

    void Return(ReturnStmt r)
    {
        bool hasFinally = _frames.Any(f => f.Finally != null);
        if (r.Value == null)
        {
            ReplayFinally(0);
            Op("returnvoid");
            return;
        }
        Value(r.Value);
        if (!hasFinally)
        {
            Op("returnvalue");
            return;
        }
        int t = Temp();
        SetLocal(t);
        ReplayFinally(0);
        GetLocal(t);
        Op("returnvalue");
    }

    void ForIn(ForInStmt fi)
    {
        int obj = Temp(), idx = Temp();
        string body = NewLabel(), cond = NewLabel(), end = NewLabel();
        Op("pushbyte", "0");
        SetLocal(idx);
        Value(fi.Object);
        Op("coerce_a");
        SetLocal(obj);
        Op("jump", cond);
        Mark(body);
        Op("label");
        string? declaredType = fi.Variable is NameExpr n && _locals.TryGetValue(n.Name, out var l) ? l.Type : null;
        AssignFrom(fi.Variable, () =>
        {
            GetLocal(obj);
            GetLocal(idx);
            Op(fi.Each ? "nextvalue" : "nextname");
        }, needValue: false, fi);
        _ = declaredType;
        Loop(fi.Label, end, cond, () => Statements(fi.Body));
        Mark(cond);
        Op("hasnext2", $"{obj}, {idx}");
        Op("iftrue", body);
        Mark(end);
        Free(obj);
        Free(idx);
    }

    /// <summary>
    /// Same layout as the Flex compiler: case bodies first, then a dispatch that turns the matching case into an
    /// index for lookupswitch.
    /// </summary>
    void Switch(SwitchStmt sw)
    {
        int reg = Temp();
        string end = NewLabel(), dispatch = NewLabel(), select = NewLabel();
        Value(sw.Discriminant);
        SetLocal(reg);
        Op("jump", dispatch);
        var bodies = sw.Cases.Select(_ => NewLabel()).ToList();
        _frames.Add(new Frame(sw.Label, end, null, null));
        try
        {
            for (int i = 0; i < sw.Cases.Count; i++)
            {
                Mark(bodies[i]);
                Op("label");
                Statements(sw.Cases[i].Body);
            }
        }
        finally { _frames.RemoveAt(_frames.Count - 1); }
        Op("jump", end);

        Mark(dispatch);
        var targets = new List<string>();
        string defaultTarget = end;
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            foreach (var label in sw.Cases[i].Labels)
            {
                if (label == null)
                {
                    defaultTarget = bodies[i];
                    continue;
                }
                string next = NewLabel();
                Value(label);
                GetLocal(reg);
                Op("ifstrictne", next);
                Literal(LitExpr.Int(targets.Count));
                Op("jump", select);
                Mark(next);
                targets.Add(bodies[i]);
            }
        }
        Literal(LitExpr.Int(targets.Count));
        Mark(select);
        Op("lookupswitch", $"{defaultTarget}, [{string.Join(", ", targets.Append(defaultTarget))}]");
        Mark(end);
        Free(reg);
    }

    /// <summary>
    /// try / catch / finally. Catch variables live in registers; finally blocks are emitted on every exit path
    /// (normal end, return, break, continue) plus a catch-all handler that runs them and rethrows.
    /// </summary>
    void Try(TryStmt t)
    {
        string start = NewLabel(), bodyEnd = NewLabel(), catchesEnd = NewLabel(), normalExit = NewLabel(), after = NewLabel();
        var fin = t.Finally;
        // Without finally, every path jumps straight past the statement (Flex layout)
        if (fin == null) normalExit = after;
        Mark(start);
        _frames.Add(new Frame(null, null, null, fin));
        Statements(t.Body);
        _frames.RemoveAt(_frames.Count - 1);
        Mark(bodyEnd);
        Op("jump", normalExit);

        var handlers = new List<(string Label, CatchClause Clause)>();
        if (fin != null) _frames.Add(new Frame(null, null, null, fin));
        foreach (var c in t.Catches)
        {
            string h = NewLabel();
            handlers.Add((h, c));
            Mark(h);
            // The scope stack is reset when an exception is caught
            Op("getlocal0");
            Op("pushscope");
            SetLocal(_locals[c.Name ?? "e"].Reg);
            Statements(c.Body);
            Op("jump", normalExit);
        }
        if (fin != null) _frames.RemoveAt(_frames.Count - 1);
        Mark(catchesEnd);
        if (fin != null)
        {
            Mark(normalExit);
            Statements(fin);
            Op("jump", after);
        }

        foreach (var (h, c) in handlers)
            _tries.Add($"try from {start} to {bodyEnd} target {h} type {(c.Type == null ? "*" : "#mn" + _shared.Type(c.Type))} name #mn{_shared.Name(c.Name ?? "e")}");
        if (fin != null)
        {
            string handler = NewLabel();
            int saved = Temp();
            Mark(handler);
            Op("getlocal0");
            Op("pushscope");
            SetLocal(saved);
            Statements(fin);
            GetLocal(saved);
            Op("throw");
            _tries.Add($"try from {start} to {catchesEnd} target {handler} type * name *");
            Free(saved);
        }
        Mark(after);
    }

    // ------------------------------------------------------------------ conditions

    static readonly Dictionary<string, (string IfTrue, string IfFalse)> Comparisons = new()
    {
        ["<"] = ("iflt", "ifnlt"), ["<="] = ("ifle", "ifnle"), [">"] = ("ifgt", "ifngt"), [">="] = ("ifge", "ifnge"),
        ["=="] = ("ifeq", "ifne"), ["!="] = ("ifne", "ifeq"), ["==="] = ("ifstricteq", "ifstrictne"), ["!=="] = ("ifstrictne", "ifstricteq"),
    };

    void Condition(Expr cond, string label, bool jumpIfTrue)
    {
        switch (cond)
        {
            case UnaryExpr { Op: "!" } u:
                Condition(u.Operand, label, !jumpIfTrue);
                return;
            // && and || are evaluated as values (dup / iffalse / pop), like the Flex compiler, then tested once
            case BinaryExpr b when Comparisons.TryGetValue(b.Op, out var ops):
                Value(b.Left);
                Value(b.Right);
                Op(jumpIfTrue ? ops.IfTrue : ops.IfFalse, label);
                return;
            default:
                Value(cond);
                Op(jumpIfTrue ? "iftrue" : "iffalse", label);
                return;
        }
    }

    // ------------------------------------------------------------------ expressions

    void Effect(Expr e)
    {
        switch (e)
        {
            case AssignExpr a:
                Assignment(a, needValue: false);
                return;
            case PostfixExpr p:
                Increment(p.Operand, p.Op == "++", needValue: false, prefix: false, p);
                return;
            case UnaryExpr { Op: "++" or "--" } u:
                Increment(u.Operand, u.Op == "++", needValue: false, prefix: true, u);
                return;
            case CallExpr c:
                Call(c, needValue: false);
                return;
            case BinaryExpr { Op: "," } comma:
                Effect(comma.Left);
                Effect(comma.Right);
                return;
            default:
                Value(e);
                Op("pop");
                return;
        }
    }

    void Value(Expr e)
    {
        switch (e)
        {
            case LitExpr l:
                Literal(l);
                break;
            case NameExpr n:
                if (TryLocal(n.Name, out var local)) GetLocal(local.Reg);
                else Op("getlex", Mn(n.Name));
                break;
            case ThisExpr:
                Op("getlocal0");
                break;
            case MemberExpr { Object: SuperExpr } sm:
                Op("getlocal0");
                Op("getsuper", Mn(sm.Name));
                break;
            case MemberExpr m:
                Value(m.Object);
                Op("getproperty", Mn(m.Name));
                break;
            case IndexExpr ix:
                Value(ix.Object);
                Value(ix.Index);
                Op("getproperty", Late);
                break;
            case TypeArgsExpr ta:
                Value(ta.Base);
                foreach (var a in ta.Args) Value(a is NameExpr { Name: "*" } ? LitExpr.Null : a);
                Op("applytype", ta.Args.Count.ToString());
                break;
            case CallExpr c:
                Call(c, needValue: true);
                break;
            case NewExpr n:
                New(n);
                break;
            case AssignExpr a:
                Assignment(a, needValue: true);
                break;
            case PostfixExpr p:
                Increment(p.Operand, p.Op == "++", needValue: true, prefix: false, p);
                break;
            case UnaryExpr u:
                Unary(u);
                break;
            case BinaryExpr b:
                Binary(b);
                break;
            case TernaryExpr t:
            {
                string elseLabel = NewLabel(), end = NewLabel();
                Condition(t.Cond, elseLabel, jumpIfTrue: false);
                Value(t.Then);
                Op("coerce_a");
                Op("jump", end);
                Mark(elseLabel);
                Value(t.Else);
                Op("coerce_a");
                Mark(end);
                break;
            }
            case ArrayLitExpr arr:
                foreach (var x in arr.Items) Value(x);
                Op("newarray", arr.Items.Count.ToString());
                break;
            case ObjectLitExpr obj:
                foreach (var (k, v) in obj.Props)
                {
                    Op("pushstring", LitExpr.Quote(k is LitExpr { Value: string ks } ? ks : k.ToString()));
                    Value(v);
                }
                Op("newobject", obj.Props.Count.ToString());
                break;
            case FunctionExpr f:
                Closure(f);
                break;
            case SuperExpr:
                throw Error(e, "'super' must be followed by a call or a property access");
            default:
                throw Error(e, $"cannot compile the expression '{e}'");
        }
    }

    void Literal(LitExpr l)
    {
        switch (l.Value)
        {
            case null: Op("pushnull"); break;
            case bool b: Op(b ? "pushtrue" : "pushfalse"); break;
            case string s: Op("pushstring", LitExpr.Quote(s)); break;
            case long or int or uint or double:
            {
                double d = l.AsNumber;
                if (double.IsNaN(d)) Op("pushnan");
                else if (d == Math.Floor(d) && !(d == 0 && double.IsNegative(d)) && d is >= int.MinValue and <= int.MaxValue)
                {
                    long v = (long)d;
                    if (v is >= sbyte.MinValue and <= sbyte.MaxValue) Op("pushbyte", v.ToString(CultureInfo.InvariantCulture));
                    else if (v is >= short.MinValue and <= short.MaxValue) Op("pushshort", v.ToString(CultureInfo.InvariantCulture));
                    else Op("pushint", v.ToString(CultureInfo.InvariantCulture));
                }
                else Op("pushdouble", LitExpr.FormatNumber(d));
                break;
            }
            default: Op("pushundefined"); break;
        }
    }

    void Unary(UnaryExpr u)
    {
        switch (u.Op)
        {
            case "!": Value(u.Operand); Op("not"); break;
            case "-": Value(u.Operand); Op("negate"); break;
            case "+": Value(u.Operand); Op("convert_d"); break;
            case "~": Value(u.Operand); Op("bitnot"); break;
            case "typeof": Value(u.Operand); Op("typeof"); break;
            case "void": Value(u.Operand); Op("pop"); Op("pushundefined"); break;
            case "++":
            case "--":
                Increment(u.Operand, u.Op == "++", needValue: true, prefix: true, u);
                break;
            case "delete":
                switch (u.Operand)
                {
                    case MemberExpr m:
                        Value(m.Object);
                        Op("deleteproperty", Mn(m.Name));
                        break;
                    case IndexExpr ix:
                        Value(ix.Object);
                        Value(ix.Index);
                        Op("deleteproperty", Late);
                        break;
                    case NameExpr n:
                        Op("findproperty", Mn(n.Name));
                        Op("deleteproperty", Mn(n.Name));
                        break;
                    default:
                        throw Error(u, "invalid operand for 'delete'");
                }
                break;
            default:
                throw Error(u, $"unsupported operator '{u.Op}'");
        }
    }

    void Binary(BinaryExpr b)
    {
        switch (b.Op)
        {
            case "&&":
            case "||":
            {
                string end = NewLabel();
                Value(b.Left);
                Op("coerce_a");
                Op("dup");
                Op(b.Op == "&&" ? "iffalse" : "iftrue", end);
                Op("pop");
                Value(b.Right);
                Op("coerce_a");
                Mark(end);
                return;
            }
            case ",":
                Effect(b.Left);
                Value(b.Right);
                return;
        }
        Value(b.Left);
        Value(b.Right);
        switch (b.Op)
        {
            case "+": Op("add"); break;
            case "-": Op("subtract"); break;
            case "*": Op("multiply"); break;
            case "/": Op("divide"); break;
            case "%": Op("modulo"); break;
            case "<<": Op("lshift"); break;
            case ">>": Op("rshift"); break;
            case ">>>": Op("urshift"); break;
            case "&": Op("bitand"); break;
            case "|": Op("bitor"); break;
            case "^": Op("bitxor"); break;
            case "==": Op("equals"); break;
            case "!=": Op("equals"); Op("not"); break;
            case "===": Op("strictequals"); break;
            case "!==": Op("strictequals"); Op("not"); break;
            case "<": Op("lessthan"); break;
            case "<=": Op("lessequals"); break;
            case ">": Op("greaterthan"); break;
            case ">=": Op("greaterequals"); break;
            case "instanceof": Op("instanceof"); break;
            case "is": Op("istypelate"); break;
            case "as": Op("astypelate"); break;
            case "in": Op("in"); break;
            default: throw Error(b, $"unsupported operator '{b.Op}'");
        }
    }

    void Args(List<Expr> args)
    {
        foreach (var a in args) Value(a);
    }

    void Call(CallExpr c, bool needValue)
    {
        string n = c.Args.Count.ToString();
        switch (c.Function)
        {
            case SuperExpr:
                // super(…) in a constructor
                Op("getlocal0");
                Args(c.Args);
                Op("constructsuper", n);
                if (needValue) Op("pushundefined");
                return;
            case MemberExpr { Object: SuperExpr } sm:
                Op("getlocal0");
                Args(c.Args);
                Op(needValue ? "callsuper" : "callsupervoid", $"{Mn(sm.Name)}, {n}");
                return;
            case NameExpr name when !TryLocal(name.Name, out _):
                Op("findpropstrict", Mn(name.Name));
                Args(c.Args);
                Op(needValue ? "callproperty" : "callpropvoid", $"{Mn(name.Name)}, {n}");
                return;
            case MemberExpr m:
                Value(m.Object);
                Args(c.Args);
                Op(needValue ? "callproperty" : "callpropvoid", $"{Mn(m.Name)}, {n}");
                return;
            case IndexExpr ix:
                Value(ix.Object);
                Value(ix.Index);
                Args(c.Args);
                Op("callproperty", $"{Late}, {n}");
                if (!needValue) Op("pop");
                return;
            default:
                // Function value (local variable, call result…): the receiver is null (the global object)
                Value(c.Function);
                Op("pushnull");
                Args(c.Args);
                Op("call", n);
                if (!needValue) Op("pop");
                return;
        }
    }

    void New(NewExpr n)
    {
        string count = n.Args.Count.ToString();
        switch (n.Type)
        {
            case NameExpr name when !TryLocal(name.Name, out _):
                Op("findpropstrict", Mn(name.Name));
                Args(n.Args);
                Op("constructprop", $"{Mn(name.Name)}, {count}");
                break;
            case MemberExpr m:
                Value(m.Object);
                Args(n.Args);
                Op("constructprop", $"{Mn(m.Name)}, {count}");
                break;
            default:
                Value(n.Type);
                Args(n.Args);
                Op("construct", count);
                break;
        }
    }

    void Assignment(AssignExpr a, bool needValue)
    {
        if (a.Op == "=")
        {
            AssignFrom(a.Target, () => Value(a.Value), needValue, a);
            return;
        }
        string op = a.Op[..^1];
        // Compound assignment: the object of a member is evaluated once
        switch (a.Target)
        {
            case MemberExpr { Object: not SuperExpr } m:
            {
                Value(m.Object);
                Op("dup");
                Op("getproperty", Mn(m.Name));
                Value(a.Value);
                BinaryOp(op, a);
                int t = -1;
                if (needValue)
                {
                    t = Temp();
                    Op("dup");
                    SetLocal(t);
                }
                Op("setproperty", Mn(m.Name));
                if (needValue)
                {
                    GetLocal(t);
                    Free(t);
                }
                return;
            }
            default:
                AssignFrom(a.Target, () => Value(new BinaryExpr(op, a.Target, a.Value)), needValue, a);
                return;
        }
    }

    void BinaryOp(string op, object node)
    {
        switch (op)
        {
            case "+": Op("add"); break;
            case "-": Op("subtract"); break;
            case "*": Op("multiply"); break;
            case "/": Op("divide"); break;
            case "%": Op("modulo"); break;
            case "<<": Op("lshift"); break;
            case ">>": Op("rshift"); break;
            case ">>>": Op("urshift"); break;
            case "&": Op("bitand"); break;
            case "|": Op("bitor"); break;
            case "^": Op("bitxor"); break;
            default: throw Error(node, $"unsupported operator '{op}='");
        }
    }

    /// <summary>Stores the value produced by <paramref name="value"/> into a local, a property or an element.</summary>
    void AssignFrom(Expr target, Action value, bool needValue, object node)
    {
        switch (target)
        {
            case NameExpr n when TryLocal(n.Name, out var local):
                value();
                Coerce(local.Type);
                if (needValue) Op("dup");
                SetLocal(local.Reg);
                return;
            case NameExpr n:
            {
                Op("findproperty", Mn(n.Name));
                value();
                int t = StoreCopy(needValue);
                Op("setproperty", Mn(n.Name));
                LoadCopy(t);
                return;
            }
            case MemberExpr { Object: SuperExpr } sm:
            {
                Op("getlocal0");
                value();
                int t = StoreCopy(needValue);
                Op("setsuper", Mn(sm.Name));
                LoadCopy(t);
                return;
            }
            case MemberExpr m:
            {
                Value(m.Object);
                value();
                int t = StoreCopy(needValue);
                Op("setproperty", Mn(m.Name));
                LoadCopy(t);
                return;
            }
            case IndexExpr ix:
            {
                Value(ix.Object);
                Value(ix.Index);
                value();
                int t = StoreCopy(needValue);
                Op("setproperty", Late);
                LoadCopy(t);
                return;
            }
            default:
                throw Error(node, "invalid assignment target");
        }
    }

    int StoreCopy(bool needValue)
    {
        if (!needValue) return -1;
        int t = Temp();
        Op("dup");
        SetLocal(t);
        return t;
    }

    void LoadCopy(int t)
    {
        if (t < 0) return;
        GetLocal(t);
        Free(t);
    }

    void Increment(Expr target, bool up, bool needValue, bool prefix, object node)
    {
        if (target is NameExpr n && TryLocal(n.Name, out var local))
        {
            bool isInt = local.Type is "int" or "uint";
            if (!needValue)
            {
                Op((up ? "inclocal" : "declocal") + (isInt ? "_i" : ""), local.Reg.ToString());
                return;
            }
            GetLocal(local.Reg);
            if (!prefix) Op("dup");
            Op(up ? "increment" : "decrement");
            Coerce(local.Type);
            if (prefix) Op("dup");
            SetLocal(local.Reg);
            return;
        }
        if (target is MemberExpr { Object: not SuperExpr } m)
        {
            Value(m.Object);
            Op("dup");
            Op("getproperty", Mn(m.Name));
            int t = -1;
            if (needValue && !prefix)
            {
                t = Temp();
                Op("convert_d");
                Op("dup");
                SetLocal(t);
            }
            Op(up ? "increment" : "decrement");
            if (needValue && prefix)
            {
                t = Temp();
                Op("dup");
                SetLocal(t);
            }
            Op("setproperty", Mn(m.Name));
            LoadCopy(t);
            return;
        }
        // Other targets: read, then write target = target ± 1
        if (needValue && !prefix) Value(target);
        AssignFrom(target, () =>
        {
            Value(target);
            Op(up ? "increment" : "decrement");
        }, needValue && prefix, node);
    }

    // ------------------------------------------------------------------ closures

    /// <summary>Compiles a function expression into a new method of the ABC and pushes it (newfunction).</summary>
    void Closure(FunctionExpr f)
    {
        var info = new MethodInfo
        {
            Index = _abc.Methods.Count,
            ParamTypes = f.Params.Select((p, i) => (p, i)).Where(x => !x.p.StartsWith("..."))
                .Select(x => x.i < f.ParamTypes.Count && f.ParamTypes[x.i] is { } t ? _shared.Type(t) : 0).ToArray(),
            Flags = (byte)(f.Params.Any(p => p.StartsWith("...")) ? 0x04 : 0),
            ReturnType = f.ReturnType is null or "*" ? 0 : _shared.Type(f.ReturnType),
        };
        int scopeDepth = (_method.Body?.InitScopeDepth ?? 0) + 1;
        var body = new MethodBody
        {
            Method = info.Index, MaxStack = 1, LocalCount = f.Params.Count + 1, InitScopeDepth = scopeDepth, MaxScopeDepth = scopeDepth + 1,
        };
        info.Body = body;
        _abc.Methods.Add(info);
        _abc.Bodies.Add(body);
        var outer = new HashSet<string>(_locals.Keys.Where(k => k != f.Name), StringComparer.Ordinal);
        if (_outerLocals != null) outer.UnionWith(_outerLocals);
        var inner = new Avm2Generator(_abc, info, new MethodOwner { Instance = _owner.Instance, Class = _owner.Class, Script = _owner.Script, IsStatic = _owner.IsStatic },
            _lines, f.Params, _shared, outer);
        var pcode = inner.Generate(f.Body, isConstructor: false);
        Avm2Assembler.Replace(_abc, info, pcode);
        Op("newfunction", info.Index.ToString());
    }
}
