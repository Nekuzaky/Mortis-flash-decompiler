using MortisFlash.Decompiler;

namespace MortisFlash.Avm1;

/// <summary>
/// Rebuilds ActionScript 2 <c>class</c> syntax from the prototype-based code emitted by the Flash compiler
/// (DoInitAction of __Packages): constructor, extends/implements, methods, accessors and fields.
/// </summary>
public static class As2ClassBuilder
{
    sealed class Member
    {
        public bool Static;
        public required string Name;
        public FunctionExpr? Function;
        public Expr? Value;
    }

    sealed class ClassStmt : Stmt
    {
        public required string Name;
        public string? Extends;
        public List<string> Implements = [];
        public required FunctionExpr Constructor;
        public List<Member> Members = [];
        public bool IsInterface;

        public override void Write(CodeWriter w)
        {
            w.Indent();
            w.Keyword(IsInterface ? "interface" : "class");
            w.Text(" ");
            w.Type(Name);
            if (Extends != null) { w.Text(" "); w.Keyword("extends"); w.Text(" "); w.Type(Extends); }
            if (Implements.Count > 0)
            {
                w.Text(" ");
                w.Keyword(IsInterface ? "extends" : "implements");
                w.Text(" ");
                w.Type(string.Join(", ", Implements));
            }
            w.Line();
            w.Indent();
            w.Punct("{");
            w.Line();
            w.Push();
            bool any = false;
            foreach (var f in Members.Where(m => m.Function == null))
            {
                w.Indent();
                if (f.Static) { w.Keyword("static"); w.Text(" "); }
                w.Keyword("var");
                w.Text(" ");
                w.Identifier(f.Name);
                w.Punct(" = ");
                f.Value!.Write(w);
                w.Punct(";");
                w.Line();
                any = true;
            }
            if (!IsInterface)
            {
                if (any) w.Line();
                string shortName = Name[(Name.LastIndexOf('.') + 1)..];
                WriteFunction(w, false, null, shortName, Constructor);
                any = true;
            }
            foreach (var m in Members.Where(m => m.Function != null))
            {
                if (any) w.Line();
                any = true;
                string? accessor = m.Name.StartsWith("__get__") ? "get" : m.Name.StartsWith("__set__") ? "set" : null;
                var body = m.Function!.Body;
                // The compiler ends every setter with "return this.__get__x();"
                if (accessor == "set" && body is [.., ReturnStmt { Value: CallExpr { Args.Count: 0, Function: MemberExpr g } }]
                    && g.Name == "__get__" + m.Name[7..])
                    m.Function.Body = body.Take(body.Count - 1).ToList();
                WriteFunction(w, m.Static, accessor, accessor != null ? m.Name[7..] : m.Name, m.Function);
            }
            w.Pop();
            w.Indent();
            w.Punct("}");
            w.Line();
        }

        static void WriteFunction(CodeWriter w, bool isStatic, string? accessor, string name, FunctionExpr f)
        {
            w.Indent();
            if (isStatic) { w.Keyword("static"); w.Text(" "); }
            w.Keyword("function");
            w.Text(" ");
            if (accessor != null) { w.Keyword(accessor); w.Text(" "); }
            w.Identifier(name);
            w.Punct("(");
            w.Text(string.Join(", ", f.Params));
            w.Punct(")");
            w.Block(f.Body);
        }
    }

    public static List<Stmt>? TryBuild(List<Stmt> stmts)
    {
        // Flatten the "if (!_global.a.b) { … }" guards that create the packages and the class
        var flat = new List<Stmt>();
        foreach (var s in stmts)
        {
            if (s is IfStmt { Else: null } i && IsGlobalCheck(i.Cond)) flat.AddRange(i.Then);
            else flat.Add(s);
        }

        string? className = null;
        FunctionExpr? ctor = null;
        LocalVar? classReg = null, protoReg = null;
        string? ext = null;
        var impls = new List<string>();
        var members = new List<Member>();
        var other = new List<Stmt>();
        bool hidesPrototype = false;

        foreach (var s in flat)
        {
            switch (s)
            {
                case ExprStmt { Expr: AssignExpr { Op: "=" } a }:
                {
                    if (ctor == null && Unchain(a.Value, out var reg, out var fn))
                    {
                        className = Clean(a.Target.ToString());
                        ctor = fn;
                        classReg = reg;
                        break;
                    }
                    if (a.Value is NewExpr { Type: NameExpr { Name: "Object" }, Args.Count: 0 }) break;
                    if (className != null && a.Target is MemberExpr m)
                    {
                        string owner = Clean(m.Object.ToString());
                        bool isProto = IsReg(m.Object, protoReg) || owner == className + ".prototype";
                        bool isStatic = IsReg(m.Object, classReg) || owner == className;
                        if (isProto || isStatic)
                        {
                            members.Add(new Member { Static = isStatic, Name = m.Name, Function = a.Value as FunctionExpr, Value = a.Value });
                            break;
                        }
                    }
                    other.Add(s);
                    break;
                }
                case VarStmt { Init: MemberExpr { Name: "prototype" } pm } v
                    when className != null && (IsReg(pm.Object, classReg) || Clean(pm.Object.ToString()) == className):
                    protoReg = v.Var;
                    break;
                case RawStmt r when className != null && r.Text.Contains(" extends "):
                    ext = Clean(r.Text[(r.Text.IndexOf(" extends ", StringComparison.Ordinal) + 9)..].TrimEnd(';', ' '));
                    break;
                case RawStmt r when className != null && r.Text.Contains(" implements "):
                    impls.AddRange(r.Text[(r.Text.IndexOf(" implements ", StringComparison.Ordinal) + 12)..].TrimEnd(';', ' ')
                        .Split(',').Select(x => Clean(x.Trim())));
                    break;
                case ExprStmt { Expr: CallExpr { Function: NameExpr { Name: "ASSetPropFlags" } } }:
                    hidesPrototype = true;
                    break;
                case ExprStmt { Expr: CallExpr { Function: MemberExpr { Name: "addProperty" } } }:
                    break;
                default:
                    other.Add(s);
                    break;
            }
        }
        if (className == null || ctor == null) return null;

        // Interface: empty constructor, no members, and no ASSetPropFlags on the prototype (every class gets one)
        bool isInterface = ctor.Body.Count == 0 && protoReg == null && members.Count == 0 && ext == null && !hidesPrototype;
        var cls = new ClassStmt
        {
            Name = className, Extends = ext, Implements = impls, Constructor = ctor, Members = members, IsInterface = isInterface,
        };
        var result = new List<Stmt> { cls };
        if (other.Count > 0)
        {
            result.Add(new CommentStmt("Other statements from the initialization script:"));
            result.AddRange(other);
        }
        return result;
    }

    static bool IsGlobalCheck(Expr cond) =>
        cond is UnaryExpr { Op: "!" } u && u.Operand is MemberExpr or NameExpr && !u.Operand.HasSideEffects;

    static bool Unchain(Expr value, out LocalVar? reg, out FunctionExpr fn)
    {
        reg = null;
        fn = null!;
        switch (value)
        {
            case AssignExpr { Op: "=", Target: LocalExpr l, Value: FunctionExpr f }:
                reg = l.Var;
                fn = f;
                return true;
            case FunctionExpr f2:
                fn = f2;
                return true;
        }
        return false;
    }

    static bool IsReg(Expr e, LocalVar? v) => v != null && e is LocalExpr l && l.Var == v;

    static string Clean(string s) => s.StartsWith("_global.") ? s[8..] : s;
}
