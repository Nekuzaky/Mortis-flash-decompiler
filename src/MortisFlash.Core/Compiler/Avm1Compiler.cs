using System.Text;
using MortisFlash.Avm1;
using MortisFlash.Decompiler;

namespace MortisFlash.Compiler;

/// <summary>
/// ActionScript 1/2 compiler. Source code is parsed into the shared AST, translated to AVM1 p-code text and assembled
/// by <see cref="Avm1Assembler"/>, which resolves labels, nested blocks and the constant pool.
/// </summary>
public static class Avm1Compiler
{
    /// <summary>Compiles a frame script, event handler body or AS2 class definition to AVM1 bytecode.</summary>
    public static byte[] Compile(string source, int swfVersion) => Avm1Assembler.Assemble(CompileToPCode(source, swfVersion));

    public static string CompileToPCode(string source, int swfVersion)
    {
        var parsed = Parser.ParseScript(source);
        var gen = new Avm1Generator(swfVersion, parsed.Lines);
        gen.Statements(parsed.Body);
        return gen.Finish();
    }
}

sealed class Avm1Generator
{
    readonly int _version;
    readonly Dictionary<object, int> _lines;
    readonly StringBuilder _code = new();
    readonly List<string> _pool = [];
    readonly HashSet<string> _poolSet = [];
    readonly Counter _labels;
    readonly List<Frame> _frames = [];
    readonly bool _inFunction;
    readonly int _firstTemp, _maxTemps;
    int _tempsInUse;
    int _depth;

    sealed class Counter
    {
        public int Value;
    }

    /// <summary>Break / continue target of a loop or switch.</summary>
    sealed record Frame(string? Label, string Break, string? Continue, bool IsForIn);

    public int MaxTempUsed { get; private set; } = -1;

    public Avm1Generator(int version, Dictionary<object, int> lines) : this(version, lines, new Counter(), false, 0) { }

    Avm1Generator(int version, Dictionary<object, int> lines, Counter labels, bool inFunction, int depth)
    {
        _version = version;
        _lines = lines;
        _labels = labels;
        _inFunction = inFunction;
        _depth = depth;
        // DefineFunction2 has its own registers (1..255); elsewhere only the 4 global registers exist
        bool ownRegisters = inFunction && version >= 7;
        _firstTemp = ownRegisters ? 1 : 0;
        _maxTemps = ownRegisters ? 200 : 4;
    }

    // ------------------------------------------------------------------ output

    public string Finish()
    {
        var sb = new StringBuilder();
        int poolBytes = _pool.Sum(s => Encoding.UTF8.GetByteCount(s) + 1);
        if (_pool.Count > 0 && _pool.Count < 65535 && poolBytes < 60000)
            sb.Append("    ConstantPool ").Append(string.Join(", ", _pool.Select(LitExpr.Quote))).Append('\n');
        sb.Append(_code);
        sb.Append("    End\n");
        return sb.ToString();
    }

    string Body() => _code.ToString();

    void Op(string action, string args = "")
    {
        _code.Append("    ").Append(action);
        if (args.Length > 0) _code.Append(' ').Append(args);
        _code.Append('\n');
    }

    void Mark(string label) => _code.Append(label).Append(":\n");

    string NewLabel() => $"L{_labels.Value++}";

    string Str(string s)
    {
        if (_poolSet.Add(s)) _pool.Add(s);
        return LitExpr.Quote(s);
    }

    void PushStr(string s) => Op("Push", Str(s));

    void PushNum(double d)
    {
        if (d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue && !(d == 0 && double.IsNegative(d)))
            Op("Push", ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture));
        else Op("Push", LitExpr.FormatNumber(d));
    }

    int AllocTemp(object? node)
    {
        if (_tempsInUse >= _maxTemps) throw Error(node, "too many nested for..in / switch statements for the available registers");
        int r = _firstTemp + _tempsInUse++;
        MaxTempUsed = Math.Max(MaxTempUsed, r);
        return r;
    }

    void FreeTemp() => _tempsInUse--;

    CompileException Error(object? node, string message) => new(node != null && _lines.TryGetValue(node, out int l) ? l : 0, message);

    // ------------------------------------------------------------------ statements

    public void Statements(List<Stmt> list)
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
                PushStr(v.Name);
                if (v.Init != null)
                {
                    Value(v.Init);
                    Op("DefineLocal");
                }
                else Op("DefineLocal2");
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
                    Op("Jump", end);
                    Mark(elseLabel);
                    Statements(i.Else);
                    Mark(end);
                }
                else Mark(elseLabel);
                break;
            }
            case WhileStmt w:
            {
                string top = NewLabel(), end = NewLabel();
                Mark(top);
                Condition(w.Cond, end, jumpIfTrue: false);
                Loop(w.Label, end, top, false, () => Statements(w.Body));
                Op("Jump", top);
                Mark(end);
                break;
            }
            case DoWhileStmt d:
            {
                string top = NewLabel(), cont = NewLabel(), end = NewLabel();
                Mark(top);
                Loop(d.Label, end, cont, false, () => Statements(d.Body));
                Mark(cont);
                Condition(d.Cond, top, jumpIfTrue: true);
                Mark(end);
                break;
            }
            case ForStmt f:
            {
                if (f.Init != null) Statement(f.Init);
                string top = NewLabel(), cont = NewLabel(), end = NewLabel();
                Mark(top);
                if (f.Cond != null) Condition(f.Cond, end, jumpIfTrue: false);
                Loop(f.Label, end, cont, false, () => Statements(f.Body));
                Mark(cont);
                foreach (var u in f.Update) Statement(u);
                Op("Jump", top);
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
                Op("Jump", Target(br, br.Label, isBreak: true));
                break;
            case ContinueStmt co:
                Op("Jump", Target(co, co.Label, isBreak: false));
                break;
            case ReturnStmt r:
                if (r.Value != null) Value(r.Value);
                else Op("Push", "undefined");
                Op("Return");
                break;
            case ThrowStmt t:
                Value(t.Value);
                Op("Throw");
                break;
            case TryStmt t:
                Try(t);
                break;
            case WithStmt w:
                Value(w.Object);
                Op("With");
                Block(w.Body);
                break;
            case FunctionStmt fs:
                Function(fs.Function);
                break;
            case ExtendsStmt ex:
                Value(ex.Sub);
                Value(ex.Super);
                Op("Extends");
                break;
            case ImplementsStmt im:
            {
                for (int k = im.Interfaces.Count - 1; k >= 0; k--) Value(im.Interfaces[k]);
                PushNum(im.Interfaces.Count);
                Value(im.Class);
                Op("ImplementsOp");
                break;
            }
            case ClassDeclStmt cls:
                Class(cls);
                break;
            case CommentStmt:
                break;
            default:
                throw Error(s, $"unsupported statement: {s.ToString().Trim()}");
        }
    }

    /// <summary>Nested block (with / try / function body) written as "{ … }" under its action.</summary>
    void Block(List<Stmt> body)
    {
        _code.Append("{\n");
        Statements(body);
        _code.Append("}\n");
    }

    void Loop(string? label, string breakLabel, string? continueLabel, bool forIn, Action body)
    {
        _frames.Add(new Frame(label, breakLabel, continueLabel, forIn));
        try { body(); }
        finally { _frames.RemoveAt(_frames.Count - 1); }
    }

    string Target(Stmt s, string? label, bool isBreak)
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (label != null && f.Label != label) continue;
            if (isBreak) return f.Break;
            if (f.Continue != null) return f.Continue;
            if (label != null) throw Error(s, $"'continue {label}' does not refer to a loop");
        }
        throw Error(s, label != null ? $"unknown label '{label}'" : isBreak ? "'break' outside of a loop or switch" : "'continue' outside of a loop");
    }

    void ForIn(ForInStmt fi)
    {
        if (fi.Each) throw Error(fi, "'for each' is ActionScript 3 only");
        int reg = AllocTemp(fi);
        string loop = NewLabel(), cleanup = NewLabel(), end = NewLabel();
        Value(fi.Object);
        Op("Enumerate2");
        Mark(loop);
        Op("StoreRegister", reg.ToString());
        Op("Push", "null");
        Op("Equals2");
        Op("If", end);
        // Loop variable
        var name = fi.Variable is NameExpr n ? n.Name : null;
        if (fi.Declare && name != null)
        {
            PushStr(name);
            Op("Push", $"register{reg}");
            Op("DefineLocal");
        }
        else Assign(fi.Variable, () => Op("Push", $"register{reg}"), needValue: false, fi);
        Loop(fi.Label, cleanup, loop, true, () => Statements(fi.Body));
        Op("Jump", loop);
        // break: the remaining enumerated names are still on the stack, pop them up to the null marker
        Mark(cleanup);
        Op("Push", "null");
        Op("Equals2");
        Op("Not");
        Op("If", cleanup);
        Mark(end);
        FreeTemp();
    }

    /// <summary>Same layout as the Flash compiler: the first test reuses the stored value, the others reload it.</summary>
    void Switch(SwitchStmt sw)
    {
        int reg = AllocTemp(sw);
        string end = NewLabel();
        Value(sw.Discriminant);
        Op("StoreRegister", reg.ToString());
        var bodies = sw.Cases.Select(_ => NewLabel()).ToList();
        string? defaultLabel = null;
        bool first = true;
        for (int i = 0; i < sw.Cases.Count; i++)
        {
            foreach (var label in sw.Cases[i].Labels)
            {
                if (label == null)
                {
                    defaultLabel = bodies[i];
                    continue;
                }
                if (!first) Op("Push", $"register{reg}");
                first = false;
                Value(label);
                Op("StrictEquals");
                Op("If", bodies[i]);
            }
        }
        if (first) Op("Pop");
        Op("Jump", defaultLabel ?? end);
        Loop(sw.Label, end, null, false, () =>
        {
            for (int i = 0; i < sw.Cases.Count; i++)
            {
                Mark(bodies[i]);
                Statements(sw.Cases[i].Body);
            }
        });
        Mark(end);
        FreeTemp();
    }

    void Try(TryStmt t)
    {
        if (t.Catches.Count > 1) throw Error(t, "ActionScript 2 supports a single catch clause per try");
        var c = t.Catches.FirstOrDefault();
        Op("Try", c != null ? Str(c.Name ?? "e") : "");
        Block(t.Body);
        if (c != null)
        {
            _code.Append("catch {\n");
            Statements(c.Body);
            _code.Append("}\n");
        }
        if (t.Finally != null)
        {
            _code.Append("finally {\n");
            Statements(t.Finally);
            _code.Append("}\n");
        }
    }

    /// <summary>Defines a function: a statement for named functions, a value on the stack for anonymous ones.</summary>
    void Function(FunctionExpr f)
    {
        if (_depth > 48) throw Error(f, "functions are nested too deeply");
        var inner = new Avm1Generator(_version, _lines, _labels, true, _depth + 1);
        inner.Statements(f.Body);
        var names = f.Params.Select(p => p.StartsWith("...") ? throw Error(f, "rest parameters (...args) are ActionScript 3 only") : p).ToList();
        string name = Str(f.Name ?? "");
        string args = $"({string.Join(", ", names)})";
        if (_version >= 7)
            Op("DefineFunction2", $"{name} {args} registers {Math.Max(1, inner.MaxTempUsed + 1)}, flags 0x0000");
        else Op("DefineFunction", $"{name} {args}");
        // The nested body shares this script's constant pool
        foreach (var s in inner._pool) Str(s);
        _code.Append("{\n").Append(inner.Body()).Append("}\n");
    }

    // ------------------------------------------------------------------ AS2 classes

    /// <summary>Rebuilds the prototype code the Flash compiler emits for an AS2 class.</summary>
    void Class(ClassDeclStmt c)
    {
        var parts = c.Name.Split('.');
        Expr Path(int count)
        {
            Expr e = new NameExpr("_global");
            for (int i = 0; i < count; i++) e = new MemberExpr(e, parts[i]);
            return e;
        }
        // Package objects: if (!_global.a) _global.a = new Object();
        for (int i = 1; i < parts.Length; i++)
            Statement(new IfStmt(new UnaryExpr("!", Path(i)), [new ExprStmt(new AssignExpr(Path(i), new NewExpr(new NameExpr("Object"), [])))], null));
        var cls = Path(parts.Length);
        var proto = new MemberExpr(cls, "prototype");
        var body = new List<Stmt>();
        var ctor = c.Members.FirstOrDefault(m => m.Function != null && m.Name == parts[^1] && !m.Static && m.Accessor == null)?.Function
                   ?? new FunctionExpr();
        body.Add(new ExprStmt(new AssignExpr(cls, new FunctionExpr { Params = ctor.Params, Body = ctor.Body })));
        if (c.Extends != null) body.Add(new ExtendsStmt(cls, new NameExpr(c.Extends)));
        var accessors = new Dictionary<(bool, string), (bool Get, bool Set)>();
        foreach (var m in c.Members)
        {
            if (m.Function != null && m.Name == parts[^1] && !m.Static && m.Accessor == null) continue;
            Expr owner = m.Static ? cls : proto;
            if (m.Function != null)
            {
                string name = m.Accessor == null ? m.Name : $"__{m.Accessor}__{m.Name}";
                var fn = new FunctionExpr { Params = m.Function.Params, Body = m.Function.Body };
                // As in Flash, a setter returns the getter's value
                if (m.Accessor == "set" && c.Members.Any(x => x.Accessor == "get" && x.Name == m.Name && x.Static == m.Static))
                    fn.Body = [.. fn.Body, new ReturnStmt(new CallExpr(new MemberExpr(new ThisExpr(), $"__get__{m.Name}"), []))];
                body.Add(new ExprStmt(new AssignExpr(new MemberExpr(owner, name), fn)));
                if (m.Accessor != null)
                {
                    accessors.TryGetValue((m.Static, m.Name), out var a);
                    accessors[(m.Static, m.Name)] = m.Accessor == "get" ? (true, a.Set) : (a.Get, true);
                }
            }
            else if (m.Value != null) body.Add(new ExprStmt(new AssignExpr(new MemberExpr(owner, m.Name), m.Value)));
        }
        foreach (var ((isStatic, name), (get, set)) in accessors)
        {
            Expr owner = isStatic ? cls : proto;
            body.Add(new ExprStmt(new CallExpr(new MemberExpr(owner, "addProperty"),
            [
                LitExpr.Str(name),
                get ? new MemberExpr(owner, $"__get__{name}") : LitExpr.Null,
                set ? new MemberExpr(owner, $"__set__{name}") : LitExpr.Null,
            ])));
        }
        if (c.Implements.Count > 0) body.Add(new ImplementsStmt(cls, c.Implements.Select(i => (Expr)new NameExpr(i)).ToList()));
        if (!c.IsInterface)
            body.Add(new ExprStmt(new CallExpr(new NameExpr("ASSetPropFlags"), [proto, LitExpr.Null, LitExpr.Int(1)])));
        Statement(new IfStmt(new UnaryExpr("!", cls), body, null));
    }

    // ------------------------------------------------------------------ expressions

    static readonly string[] Properties =
    [
        "_x", "_y", "_xscale", "_yscale", "_currentframe", "_totalframes", "_alpha", "_visible", "_width", "_height",
        "_rotation", "_target", "_framesloaded", "_name", "_droptarget", "_url", "_highquality", "_focusrect",
        "_soundbuftime", "_quality", "_xmouse", "_ymouse",
    ];

    /// <summary>Jumps to <paramref name="label"/> when the condition has the given truth value.</summary>
    void Condition(Expr cond, string label, bool jumpIfTrue)
    {
        if (cond is UnaryExpr { Op: "!" } u)
        {
            Condition(u.Operand, label, !jumpIfTrue);
            return;
        }
        Value(cond);
        if (!jumpIfTrue) Op("Not");
        Op("If", label);
    }

    /// <summary>Evaluates an expression only for its side effects.</summary>
    void Effect(Expr e)
    {
        switch (e)
        {
            case AssignExpr a:
                Assignment(a, needValue: false);
                return;
            case PostfixExpr p:
                Increment(p.Operand, p.Op == "++", needValue: false, before: false, p);
                return;
            case UnaryExpr { Op: "++" or "--" } u:
                Increment(u.Operand, u.Op == "++", needValue: false, before: true, u);
                return;
            case CallExpr c:
                if (Call(c, needValue: false)) Op("Pop");
                return;
            case BinaryExpr { Op: "," } comma:
                Effect(comma.Left);
                Effect(comma.Right);
                return;
            default:
                Value(e);
                Op("Pop");
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
                Variable(n.Name);
                break;
            case ThisExpr:
                PushStr("this");
                Op("GetVariable");
                break;
            case SuperExpr:
                PushStr("super");
                Op("GetVariable");
                break;
            case MemberExpr m:
                Value(m.Object);
                PushStr(m.Name);
                Op("GetMember");
                break;
            case IndexExpr ix:
                Value(ix.Object);
                Value(ix.Index);
                Op("GetMember");
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
                Increment(p.Operand, p.Op == "++", needValue: true, before: false, p);
                break;
            case UnaryExpr u:
                Unary(u);
                break;
            case BinaryExpr b:
                Binary(b);
                break;
            case TernaryExpr t:
            {
                string then = NewLabel(), end = NewLabel();
                Value(t.Cond);
                Op("If", then);
                Value(t.Else);
                Op("Jump", end);
                Mark(then);
                Value(t.Then);
                Mark(end);
                break;
            }
            case ArrayLitExpr arr:
                for (int i = arr.Items.Count - 1; i >= 0; i--) Value(arr.Items[i]);
                PushNum(arr.Items.Count);
                Op("InitArray");
                break;
            case ObjectLitExpr obj:
                foreach (var (k, v) in obj.Props)
                {
                    Value(k);
                    Value(v);
                }
                PushNum(obj.Props.Count);
                Op("InitObject");
                break;
            case FunctionExpr f:
                if (f.Name != null)
                {
                    // Named function expression: define it, then read it back
                    Function(f);
                    Variable(f.Name);
                }
                else Function(f);
                break;
            default:
                throw Error(e, $"cannot compile the expression '{e}'");
        }
    }

    void Literal(LitExpr l)
    {
        switch (l.Value)
        {
            case null: Op("Push", "null"); break;
            case bool b: Op("Push", b ? "true" : "false"); break;
            case string s: PushStr(s); break;
            case long or int or uint or double: PushNum(l.AsNumber); break;
            default: Op("Push", "undefined"); break;
        }
    }

    void Variable(string name)
    {
        int prop = Array.IndexOf(Properties, name);
        if (prop >= 0)
        {
            // Timeline property of the current clip (_x, _alpha…)
            PushStr("");
            PushNum(prop);
            Op("GetProperty");
            return;
        }
        PushStr(name);
        Op("GetVariable");
    }

    void Assignment(AssignExpr a, bool needValue)
    {
        var value = a.Op == "=" ? a.Value : new BinaryExpr(a.Op[..^1], a.Target, a.Value);
        Assign(a.Target, () => Value(value), needValue, a);
    }

    /// <summary>Stores a value produced by <paramref name="value"/> into a variable, member or element.</summary>
    void Assign(Expr target, Action value, bool needValue, object node)
    {
        switch (target)
        {
            case NameExpr n:
            {
                int prop = Array.IndexOf(Properties, n.Name);
                if (prop >= 0)
                {
                    PushStr("");
                    PushNum(prop);
                    value();
                    Op("SetProperty");
                }
                else
                {
                    PushStr(n.Name);
                    value();
                    Op("SetVariable");
                }
                if (needValue) Variable(n.Name);
                break;
            }
            case MemberExpr m:
                Value(m.Object);
                PushStr(m.Name);
                value();
                Op("SetMember");
                if (needValue) Value(m);
                break;
            case IndexExpr ix:
                Value(ix.Object);
                Value(ix.Index);
                value();
                Op("SetMember");
                if (needValue) Value(ix);
                break;
            default:
                throw Error(node, "invalid assignment target");
        }
    }

    void Increment(Expr target, bool up, bool needValue, bool before, object node)
    {
        if (needValue && !before) Value(target);
        Assign(target, () =>
        {
            Value(target);
            Op(up ? "Increment" : "Decrement");
        }, needValue: needValue && before, node);
    }

    void Unary(UnaryExpr u)
    {
        switch (u.Op)
        {
            case "!":
                Value(u.Operand);
                Op("Not");
                break;
            case "-":
                Op("Push", "0");
                Value(u.Operand);
                Op("Subtract");
                break;
            case "+":
                Value(u.Operand);
                Op("ToNumber");
                break;
            case "~":
                Value(u.Operand);
                Op("Push", "-1");
                Op("BitXor");
                break;
            case "typeof":
                Value(u.Operand);
                Op("TypeOf");
                break;
            case "void":
                Value(u.Operand);
                Op("Pop");
                Op("Push", "undefined");
                break;
            case "delete":
                switch (u.Operand)
                {
                    case MemberExpr m:
                        Value(m.Object);
                        PushStr(m.Name);
                        Op("Delete");
                        break;
                    case IndexExpr ix:
                        Value(ix.Object);
                        Value(ix.Index);
                        Op("Delete");
                        break;
                    case NameExpr n:
                        PushStr(n.Name);
                        Op("Delete2");
                        break;
                    case CallExpr { Function: NameExpr { Name: "eval" }, Args.Count: 1 } ev:
                        Value(ev.Args[0]);
                        Op("Delete2");
                        break;
                    default:
                        throw Error(u, "invalid operand for 'delete'");
                }
                break;
            case "++":
            case "--":
                Increment(u.Operand, u.Op == "++", needValue: true, before: true, u);
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
                Op("PushDuplicate");
                if (b.Op == "&&") Op("Not");
                Op("If", end);
                Op("Pop");
                Value(b.Right);
                Mark(end);
                return;
            }
            case ",":
                Effect(b.Left);
                Value(b.Right);
                return;
            case "is":
            case "as":
            case "in":
                throw Error(b, $"the '{b.Op}' operator is ActionScript 3 only");
        }
        Value(b.Left);
        Value(b.Right);
        switch (b.Op)
        {
            case "+": Op("Add2"); break;
            case "-": Op("Subtract"); break;
            case "*": Op("Multiply"); break;
            case "/": Op("Divide"); break;
            case "%": Op("Modulo"); break;
            case "&": Op("BitAnd"); break;
            case "|": Op("BitOr"); break;
            case "^": Op("BitXor"); break;
            case "<<": Op("BitLShift"); break;
            case ">>": Op("BitRShift"); break;
            case ">>>": Op("BitURShift"); break;
            case "==": Op("Equals2"); break;
            case "!=": Op("Equals2"); Op("Not"); break;
            case "===": Op("StrictEquals"); break;
            case "!==": Op("StrictEquals"); Op("Not"); break;
            case "<": Op("Less2"); break;
            case ">": Op("Greater"); break;
            case "<=": Op("Greater"); Op("Not"); break;
            case ">=": Op("Less2"); Op("Not"); break;
            case "instanceof": Op("InstanceOf"); break;
            default: throw Error(b, $"unsupported operator '{b.Op}'");
        }
    }

    void Args(List<Expr> args)
    {
        for (int i = args.Count - 1; i >= 0; i--) Value(args[i]);
        PushNum(args.Count);
    }

    void New(NewExpr n)
    {
        switch (n.Type)
        {
            case NameExpr name:
                Args(n.Args);
                PushStr(name.Name);
                Op("NewObject");
                break;
            case MemberExpr m:
                Args(n.Args);
                Value(m.Object);
                PushStr(m.Name);
                Op("NewMethod");
                break;
            default:
                Args(n.Args);
                Value(n.Type);
                Op("Push", "undefined");
                Op("NewMethod");
                break;
        }
    }

    /// <summary>Compiles a call. Returns false when nothing is left on the stack (actions such as trace or stop).</summary>
    bool Call(CallExpr c, bool needValue)
    {
        switch (c.Function)
        {
            case NameExpr n when Builtin(n.Name, c.Args, c):
                if (needValue) Op("Push", "undefined");
                return needValue;
            case NameExpr n when BuiltinValue(n.Name, c.Args):
                return true;
            case NameExpr n:
                Args(c.Args);
                PushStr(n.Name);
                Op("CallFunction");
                return true;
            case SuperExpr:
                Args(c.Args);
                PushStr("super");
                Op("GetVariable");
                Op("Push", "undefined");
                Op("CallMethod");
                return true;
            case MemberExpr m:
                Args(c.Args);
                Value(m.Object);
                PushStr(m.Name);
                Op("CallMethod");
                return true;
            case IndexExpr ix:
                Args(c.Args);
                Value(ix.Object);
                Value(ix.Index);
                Op("CallMethod");
                return true;
            default:
                Args(c.Args);
                Value(c.Function);
                Op("Push", "undefined");
                Op("CallMethod");
                return true;
        }
    }

    static int? IntArg(Expr e) => e is LitExpr { IsNumber: true } l && l.AsNumber == Math.Floor(l.AsNumber) ? (int)l.AsNumber : null;

    void Level(Expr level)
    {
        if (IntArg(level) is { } lv) PushStr("_level" + lv);
        else
        {
            PushStr("_level");
            Value(level);
            Op("Add2");
        }
    }

    static int Method(List<Expr> a, int index) => a.Count > index && a[index] is LitExpr { Value: string m }
        ? m.Equals("POST", StringComparison.OrdinalIgnoreCase) ? 2 : m.Equals("GET", StringComparison.OrdinalIgnoreCase) ? 1 : 0
        : 0;

    /// <summary>Global functions that are dedicated actions and leave nothing on the stack.</summary>
    bool Builtin(string name, List<Expr> a, object node)
    {
        void Need(int min, int max)
        {
            if (a.Count < min || a.Count > max) throw Error(node, $"{name}() takes {(min == max ? min.ToString() : $"{min} to {max}")} argument(s)");
        }
        switch (name)
        {
            case "trace": Need(1, 1); Value(a[0]); Op("Trace"); return true;
            case "play": Need(0, 0); Op("Play"); return true;
            case "stop": Need(0, 0); Op("Stop"); return true;
            case "nextFrame": Need(0, 0); Op("NextFrame"); return true;
            case "prevFrame": Need(0, 0); Op("PrevFrame"); return true;
            case "stopAllSounds": Need(0, 0); Op("StopSounds"); return true;
            case "toggleHighQuality": Need(0, 0); Op("ToggleQuality"); return true;
            case "stopDrag": Need(0, 0); Op("EndDrag"); return true;
            case "gotoAndPlay":
            case "gotoAndStop":
            {
                Need(1, 1);
                bool play = name == "gotoAndPlay";
                if (IntArg(a[0]) is { } frame && frame >= 1)
                {
                    Op("GotoFrame", (frame - 1).ToString());
                    if (play) Op("Play");
                }
                else if (a[0] is LitExpr { Value: string label })
                {
                    Op("GoToLabel", Str(label));
                    if (play) Op("Play");
                }
                else
                {
                    Value(a[0]);
                    Op("GotoFrame2", play ? "1" : "0");
                }
                return true;
            }
            case "getURL":
                Need(1, 3);
                if (a.Count <= 2 && a[0] is LitExpr { Value: string url } && (a.Count == 1 || a[1] is LitExpr { Value: string }))
                {
                    Op("GetURL", $"{Str(url)}, {Str(a.Count > 1 ? (string)((LitExpr)a[1]).Value! : "")}");
                    return true;
                }
                Value(a[0]);
                if (a.Count > 1) Value(a[1]);
                else PushStr("");
                Op("GetURL2", Method(a, 2).ToString());
                return true;
            case "fscommand":
                Need(1, 2);
                if (a[0] is LitExpr { Value: string cmd } && (a.Count == 1 || a[1] is LitExpr { Value: string }))
                {
                    Op("GetURL", $"{Str("FSCommand:" + cmd)}, {Str(a.Count > 1 ? (string)((LitExpr)a[1]).Value! : "")}");
                    return true;
                }
                PushStr("FSCommand:");
                Value(a[0]);
                Op("Add2");
                if (a.Count > 1) Value(a[1]);
                else PushStr("");
                Op("GetURL2", "0");
                return true;
            case "loadMovie":
            case "loadVariables":
                Need(2, 3);
                Value(a[0]);
                Value(a[1]);
                Op("GetURL2", ((name == "loadMovie" ? 0x40 : 0xC0) | Method(a, 2)).ToString());
                return true;
            case "loadMovieNum":
            case "loadVariablesNum":
                Need(2, 3);
                Value(a[0]);
                Level(a[1]);
                Op("GetURL2", ((name == "loadMovieNum" ? 0 : 0x80) | Method(a, 2)).ToString());
                return true;
            case "unloadMovie":
                Need(1, 1);
                PushStr("");
                Value(a[0]);
                Op("GetURL2", "64");
                return true;
            case "unloadMovieNum":
                Need(1, 1);
                PushStr("");
                Level(a[0]);
                Op("GetURL2", "0");
                return true;
            case "duplicateMovieClip":
                Need(3, 3);
                Value(a[0]);
                Value(a[1]);
                Value(a[2]);
                Op("Push", "16384");
                Op("Add2");
                Op("CloneSprite");
                return true;
            case "removeMovieClip":
                Need(1, 1);
                Value(a[0]);
                Op("RemoveSprite");
                return true;
            case "startDrag":
            {
                if (a.Count is not (1 or 2 or 6)) throw Error(node, "startDrag() takes 1, 2 or 6 arguments");
                if (a.Count == 6)
                {
                    for (int i = 2; i < 6; i++) Value(a[i]);
                    Op("Push", "1");
                }
                else Op("Push", "0");
                if (a.Count > 1) Value(a[1]);
                else Op("Push", "0");
                Value(a[0]);
                Op("StartDrag");
                return true;
            }
            case "setProperty":
            {
                Need(3, 3);
                Value(a[0]);
                int prop = a[1] is NameExpr pn ? Array.IndexOf(Properties, pn.Name) : -1;
                if (prop >= 0) PushNum(prop);
                else Value(a[1]);
                Value(a[2]);
                Op("SetProperty");
                return true;
            }
            case "set":
                Need(2, 2);
                Value(a[0]);
                Value(a[1]);
                Op("SetVariable");
                return true;
            case "call":
                Need(1, 1);
                Value(a[0]);
                Op("Call");
                return true;
            case "tellTarget":
                Need(1, 1);
                Value(a[0]);
                Op("SetTarget2");
                return true;
        }
        return false;
    }

    /// <summary>Global functions that are dedicated actions returning a value.</summary>
    bool BuiltinValue(string name, List<Expr> a)
    {
        string? action = name switch
        {
            "getTimer" when a.Count == 0 => "GetTime",
            "random" when a.Count == 1 => "RandomNumber",
            "int" when a.Count == 1 => "ToInteger",
            "ord" when a.Count == 1 => "CharToAscii",
            "chr" when a.Count == 1 => "AsciiToChar",
            "mbord" when a.Count == 1 => "MBCharToAscii",
            "mbchr" when a.Count == 1 => "MBAsciiToChar",
            "length" when a.Count == 1 => "StringLength",
            "mblength" when a.Count == 1 => "MBStringLength",
            "substring" when a.Count == 3 => "StringExtract",
            "mbsubstring" when a.Count == 3 => "MBStringExtract",
            "targetPath" when a.Count == 1 => "TargetPath",
            "eval" when a.Count == 1 => "GetVariable",
            "getProperty" when a.Count == 2 => "GetProperty",
            _ => null,
        };
        if (action == null) return false;
        if (action == "GetProperty")
        {
            Value(a[0]);
            int prop = a[1] is NameExpr pn ? Array.IndexOf(Properties, pn.Name) : -1;
            if (prop >= 0) PushNum(prop);
            else Value(a[1]);
        }
        else foreach (var x in a) Value(x);
        Op(action);
        return true;
    }
}
