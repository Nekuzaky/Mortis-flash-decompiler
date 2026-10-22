using MortisFlash.Decompiler;

namespace MortisFlash.Compiler;

// Syntax nodes that only the compiler needs (the shared AST covers everything else).

/// <summary>Type application, e.g. Vector.&lt;int&gt;.</summary>
public sealed class TypeArgsExpr(Expr baseType, List<Expr> args) : Expr
{
    public Expr Base { get; } = baseType;
    public List<Expr> Args { get; } = args;
    public override int Precedence => Prec.Call;
    public override void Write(CodeWriter w)
    {
        Base.Write(w);
        w.Punct(".<");
        w.List(Args);
        w.Punct(">");
    }
}

/// <summary>AS2 prototype-style inheritance statement: "Sub extends Super;".</summary>
public sealed class ExtendsStmt(Expr sub, Expr super) : Stmt
{
    public Expr Sub { get; } = sub;
    public Expr Super { get; } = super;
    public override void Write(CodeWriter w) => w.Lines($"{Sub} extends {Super};");
}

/// <summary>AS2 prototype-style interface statement: "Cls implements A, B;".</summary>
public sealed class ImplementsStmt(Expr cls, List<Expr> interfaces) : Stmt
{
    public Expr Class { get; } = cls;
    public List<Expr> Interfaces { get; } = interfaces;
    public override void Write(CodeWriter w) => w.Lines($"{Class} implements {string.Join(", ", Interfaces)};");
}

public sealed class ClassMember
{
    public bool Static;
    public required string Name;
    /// <summary>"get", "set" or null.</summary>
    public string? Accessor;
    public FunctionExpr? Function;
    public Expr? Value;
}

/// <summary>AS2 class or interface declaration.</summary>
public sealed class ClassDeclStmt : Stmt
{
    public required string Name;
    public string? Extends;
    public List<string> Implements = [];
    public bool IsInterface;
    public List<ClassMember> Members = [];
    public override void Write(CodeWriter w) => w.Lines($"class {Name}");
}

/// <summary>Parsed AS2 script: statements plus the on(...) / onClipEvent(...) wrapper of event handlers.</summary>
public sealed record ParsedScript(List<Stmt> Body, string? EventHeader, Dictionary<object, int> Lines);

/// <summary>
/// Recursive-descent parser for ActionScript 1/2/3 statements and expressions. It produces the same AST the
/// decompiler prints, so decompiled code can be edited and compiled back.
/// </summary>
public sealed class Parser
{
    readonly List<Token> _t;
    int _p;
    readonly Dictionary<object, int> _lines = new(ReferenceEqualityComparer.Instance);

    Parser(string source) => _t = Lexer.Tokenize(source);

    /// <summary>Parses a full script; an outer on(...) { } / onClipEvent(...) { } wrapper is reported separately.</summary>
    public static ParsedScript ParseScript(string source)
    {
        var p = new Parser(source);
        string? header = null;
        if (p.Peek.Kind == TokKind.Ident && p.Peek.Text is "on" or "onClipEvent" && p.PeekAt(1).Is("("))
        {
            int start = p._p;
            string kind = p.Next().Text;
            p.Expect("(");
            int depth = 1;
            var parts = new List<string>();
            while (depth > 0 && p.Peek.Kind != TokKind.End)
            {
                var tok = p.Next();
                if (tok.Is("(")) depth++;
                if (tok.Is(")") && --depth == 0) break;
                parts.Add(tok.Kind == TokKind.String ? $"\"{tok.Value}\"" : tok.Text);
            }
            header = $"{kind}({string.Join(" ", parts).Replace(" ,", ",")})";
            if (!p.Peek.Is("{")) p._p = start;
            else
            {
                var block = p.Block();
                p.ExpectEnd();
                return new ParsedScript(block, header, p._lines);
            }
        }
        var body = new List<Stmt>();
        while (p.Peek.Kind != TokKind.End) p.AddStatement(body);
        return new ParsedScript(body, null, p._lines);
    }

    /// <summary>Parses a function body (the text between the braces).</summary>
    public static (List<Stmt> Body, Dictionary<object, int> Lines) ParseBody(string source)
    {
        var p = new Parser(source);
        var body = new List<Stmt>();
        while (p.Peek.Kind != TokKind.End) p.AddStatement(body);
        return (body, p._lines);
    }

    // ------------------------------------------------------------------ tokens

    Token Peek => _t[_p];
    Token PeekAt(int k) => _t[Math.Min(_t.Count - 1, _p + k)];
    Token Next() => _t[_p < _t.Count - 1 ? _p++ : _p];

    CompileException Error(string message, Token? at = null) => new((at ?? Peek).Line, message);

    void Expect(string text)
    {
        if (!Peek.Is(text)) throw Error($"expected '{text}' but found {Peek}");
        _p++;
    }

    bool Accept(string text)
    {
        if (!Peek.Is(text)) return false;
        _p++;
        return true;
    }

    void ExpectEnd()
    {
        if (Peek.Kind != TokKind.End) throw Error($"unexpected {Peek} after the end of the script");
    }

    /// <summary>Statement terminator: ';', or automatic insertion before '}', a new line or the end.</summary>
    void Semicolon()
    {
        if (Accept(";")) return;
        if (Peek.Is("}") || Peek.Kind == TokKind.End || Peek.NewlineBefore) return;
        throw Error($"expected ';' but found {Peek}");
    }

    string Identifier()
    {
        var t = Peek;
        // Contextual keywords and keywords are valid property / variable names in many places
        if (t.Kind is TokKind.Ident or TokKind.Keyword)
        {
            _p++;
            return t.Text;
        }
        throw Error($"expected a name but found {t}");
    }

    T At<T>(T node, Token tok) where T : notnull
    {
        _lines[node] = tok.Line;
        return node;
    }

    // ------------------------------------------------------------------ statements

    List<Stmt> Block()
    {
        Expect("{");
        var list = new List<Stmt>();
        while (!Peek.Is("}"))
        {
            if (Peek.Kind == TokKind.End) throw Error("missing '}'");
            AddStatement(list);
        }
        _p++;
        return list;
    }

    void AddStatement(List<Stmt> list)
    {
        var s = Statement();
        if (s == null) return;
        if (s is BlockStmt b && b.Body.All(x => x is VarStmt)) list.AddRange(b.Body);
        else list.Add(s);
    }

    List<Stmt> Body()
    {
        if (Peek.Is("{")) return Block();
        var list = new List<Stmt>();
        AddStatement(list);
        return list;
    }

    Stmt? Statement()
    {
        var tok = Peek;
        if (tok.Is(";"))
        {
            _p++;
            return null;
        }
        if (tok.Is("{")) return At(new BlockStmt(Block()), tok);
        if (tok.Kind == TokKind.Ident && PeekAt(1).Is(":") && !PeekAt(2).Is(":"))
        {
            // Labeled statement
            string label = Next().Text;
            _p++;
            var inner = Statement();
            switch (inner)
            {
                case LoopStmt loop: loop.Label = label; return inner;
                case SwitchStmt sw: sw.Label = label; return inner;
                default: throw Error("labels are only supported on loops and switch statements", tok);
            }
        }
        if (tok.Kind == TokKind.Ident)
        {
            switch (tok.Text)
            {
                case "import":
                    while (!Peek.Is(";") && Peek.Kind != TokKind.End && !(Peek.NewlineBefore && _p > 0 && Peek != tok)) _p++;
                    Accept(";");
                    return null;
                case "each":
                    break;
            }
        }
        if (tok.Kind == TokKind.Keyword)
        {
            switch (tok.Text)
            {
                case "var":
                case "const":
                {
                    _p++;
                    var decls = VarDeclarations(noIn: false);
                    Semicolon();
                    return decls.Count == 1 ? decls[0] : At(new BlockStmt(decls), tok);
                }
                case "if":
                {
                    _p++;
                    Expect("(");
                    var cond = Expression();
                    Expect(")");
                    var then = Body();
                    List<Stmt>? els = null;
                    if (Accept("else")) els = Body();
                    return At(new IfStmt(cond, then, els), tok);
                }
                case "while":
                {
                    _p++;
                    Expect("(");
                    var cond = Expression();
                    Expect(")");
                    return At(new WhileStmt { Cond = cond, Body = Body() }, tok);
                }
                case "do":
                {
                    _p++;
                    var body = Body();
                    Expect("while");
                    Expect("(");
                    var cond = Expression();
                    Expect(")");
                    Accept(";");
                    return At(new DoWhileStmt { Body = body, Cond = cond }, tok);
                }
                case "for":
                    return For();
                case "switch":
                    return Switch();
                case "break":
                case "continue":
                {
                    _p++;
                    string? label = null;
                    if (Peek.Kind == TokKind.Ident && !Peek.NewlineBefore) label = Next().Text;
                    Semicolon();
                    return At<Stmt>(tok.Text == "break" ? new BreakStmt(label) : new ContinueStmt(label), tok);
                }
                case "return":
                {
                    _p++;
                    Expr? value = null;
                    if (!Peek.Is(";") && !Peek.Is("}") && Peek.Kind != TokKind.End && !Peek.NewlineBefore) value = Expression();
                    Semicolon();
                    return At(new ReturnStmt(value), tok);
                }
                case "throw":
                {
                    _p++;
                    var value = Expression();
                    Semicolon();
                    return At(new ThrowStmt(value), tok);
                }
                case "try":
                    return Try();
                case "with":
                {
                    _p++;
                    Expect("(");
                    var obj = Expression();
                    Expect(")");
                    return At(new WithStmt(obj, Body()), tok);
                }
                case "function" when PeekAt(1).Kind is TokKind.Ident or TokKind.Keyword && !PeekAt(1).Is("("):
                {
                    var f = Function();
                    return At(new FunctionStmt(f), tok);
                }
                case "class":
                case "interface":
                    return Class();
            }
        }
        var expr = Expression();
        // AS2 prototype inheritance written by the decompiler: "A extends B;" / "A implements B, C;"
        if (Peek.Is("extends"))
        {
            _p++;
            var super = Expression();
            Semicolon();
            return At(new ExtendsStmt(expr, super), tok);
        }
        if (Peek.Is("implements"))
        {
            _p++;
            var list = new List<Expr> { Assignment(false) };
            while (Accept(",")) list.Add(Assignment(false));
            Semicolon();
            return At(new ImplementsStmt(expr, list), tok);
        }
        Semicolon();
        return At(new ExprStmt(expr), tok);
    }

    List<Stmt> VarDeclarations(bool noIn)
    {
        var list = new List<Stmt>();
        do
        {
            var tok = Peek;
            string name = Identifier();
            string? type = Accept(":") ? TypeName() : null;
            Expr? init = Accept("=") ? Assignment(noIn) : null;
            list.Add(At(new VarStmt(name, type, init), tok));
        } while (Accept(","));
        return list;
    }

    string TypeName()
    {
        if (Accept("*")) return "*";
        var sb = new System.Text.StringBuilder(Identifier());
        while (Peek.Is(".") || Peek.Is(".<"))
        {
            if (Accept(".")) sb.Append('.').Append(Identifier());
            else
            {
                _p++;
                sb.Append(".<").Append(TypeName());
                while (Accept(",")) sb.Append(", ").Append(TypeName());
                CloseAngle();
                sb.Append('>');
            }
        }
        return sb.ToString();
    }

    /// <summary>Closing '>' of a type argument list (the lexer may have produced '>>' or '>=').</summary>
    void CloseAngle()
    {
        var t = Peek;
        if (t.Is(">")) { _p++; return; }
        if (t.Kind == TokKind.Punct && t.Text.StartsWith('>'))
        {
            _t[_p] = t with { Text = t.Text[1..] };
            return;
        }
        throw Error("expected '>'");
    }

    Stmt For()
    {
        var tok = Next();
        bool each = false;
        if (Peek.Kind == TokKind.Ident && Peek.Text == "each")
        {
            _p++;
            each = true;
        }
        Expect("(");
        Stmt? init = null;
        if (Peek.Is("var") || Peek.Is("const"))
        {
            _p++;
            var decls = VarDeclarations(noIn: true);
            if (decls.Count == 1 && Accept("in"))
            {
                var v = (VarStmt)decls[0];
                var obj = Expression();
                Expect(")");
                return At(new ForInStmt { Variable = new NameExpr(v.Name), Object = obj, Each = each, Declare = true, DeclareType = v.Type, Body = Body() }, tok);
            }
            init = decls.Count == 1 ? decls[0] : new BlockStmt(decls);
        }
        else if (!Peek.Is(";"))
        {
            var e = Expression(noIn: true);
            if (Accept("in"))
            {
                var obj = Expression();
                Expect(")");
                return At(new ForInStmt { Variable = e, Object = obj, Each = each, Body = Body() }, tok);
            }
            init = new ExprStmt(e);
        }
        if (each) throw Error("'for each' needs the form 'for each (x in object)'", tok);
        Expect(";");
        Expr? cond = Peek.Is(";") ? null : Expression();
        Expect(";");
        var update = new List<Stmt>();
        if (!Peek.Is(")"))
        {
            update.Add(new ExprStmt(Assignment(false)));
            while (Accept(",")) update.Add(new ExprStmt(Assignment(false)));
        }
        Expect(")");
        return At(new ForStmt { Init = init, Cond = cond, Update = update, Body = Body() }, tok);
    }

    Stmt Switch()
    {
        var tok = Next();
        Expect("(");
        var disc = Expression();
        Expect(")");
        Expect("{");
        var sw = new SwitchStmt { Discriminant = disc };
        SwitchCase? current = null;
        while (!Accept("}"))
        {
            if (Peek.Is("case") || Peek.Is("default"))
            {
                Expr? label = null;
                if (Accept("case")) label = Expression();
                else _p++;
                Expect(":");
                // Consecutive labels share one body
                if (current == null || current.Body.Count > 0)
                {
                    current = new SwitchCase();
                    sw.Cases.Add(current);
                }
                current.Labels.Add(label);
                continue;
            }
            if (current == null) throw Error("statement before the first 'case' of a switch");
            if (Peek.Kind == TokKind.End) throw Error("missing '}' at the end of the switch");
            AddStatement(current.Body);
        }
        return At(sw, tok);
    }

    Stmt Try()
    {
        var tok = Next();
        var t = new TryStmt { Body = Block() };
        while (Accept("catch"))
        {
            Expect("(");
            var c = new CatchClause { Name = Identifier() };
            if (Accept(":")) c.Type = TypeName();
            Expect(")");
            c.Body = Block();
            t.Catches.Add(c);
        }
        if (Accept("finally")) t.Finally = Block();
        if (t.Catches.Count == 0 && t.Finally == null) throw Error("'try' needs a 'catch' or a 'finally' block", tok);
        return At(t, tok);
    }

    FunctionExpr Function()
    {
        var tok = Next(); // function
        var f = new FunctionExpr();
        if (Peek.Kind is TokKind.Ident or TokKind.Keyword && !Peek.Is("(")) f.Name = Identifier();
        Expect("(");
        while (!Accept(")"))
        {
            if (Accept("..."))
            {
                f.Params.Add("..." + Identifier());
                f.ParamTypes.Add(Accept(":") ? TypeName() : null);
            }
            else
            {
                string name = Identifier();
                f.ParamTypes.Add(Accept(":") ? TypeName() : null);
                if (Accept("=")) Assignment(false); // default values are kept by the original method signature
                f.Params.Add(name);
            }
            if (!Peek.Is(")")) Expect(",");
        }
        if (Accept(":")) f.ReturnType = TypeName();
        f.Body = Block();
        _lines[f] = tok.Line;
        return f;
    }

    Stmt Class()
    {
        var tok = Next();
        var c = new ClassDeclStmt { Name = TypeName(), IsInterface = tok.Text == "interface" };
        if (Accept("extends"))
        {
            if (c.IsInterface)
            {
                c.Implements.Add(TypeName());
                while (Accept(",")) c.Implements.Add(TypeName());
            }
            else c.Extends = TypeName();
        }
        if (Accept("implements"))
        {
            c.Implements.Add(TypeName());
            while (Accept(",")) c.Implements.Add(TypeName());
        }
        Expect("{");
        while (!Accept("}"))
        {
            if (Peek.Kind == TokKind.End) throw Error("missing '}' at the end of the class");
            bool isStatic = false;
            // Modifiers
            while (Peek.Kind is TokKind.Ident && Peek.Text is "public" or "private" or "static" or "dynamic" or "intrinsic")
            {
                if (Next().Text == "static") isStatic = true;
            }
            if (Peek.Is("var"))
            {
                _p++;
                foreach (VarStmt v in VarDeclarations(noIn: false).Cast<VarStmt>())
                    c.Members.Add(new ClassMember { Static = isStatic, Name = v.Name, Value = v.Init });
                Semicolon();
                continue;
            }
            if (Peek.Is("function"))
            {
                // function get name() / function set name(v)
                string? accessor = null;
                if (PeekAt(1).Kind == TokKind.Ident && PeekAt(1).Text is "get" or "set" && PeekAt(2).Kind is TokKind.Ident or TokKind.Keyword && !PeekAt(2).Is("("))
                {
                    accessor = PeekAt(1).Text;
                    _t.RemoveAt(_p + 1);
                }
                var f = c.IsInterface ? InterfaceMethod() : Function();
                c.Members.Add(new ClassMember { Static = isStatic, Name = f.Name ?? "", Accessor = accessor, Function = f });
                continue;
            }
            if (Accept(";")) continue;
            throw Error($"unexpected {Peek} in class body");
        }
        return At(c, tok);
    }

    FunctionExpr InterfaceMethod()
    {
        _p++;
        var f = new FunctionExpr { Name = Identifier() };
        Expect("(");
        while (!Accept(")")) Next();
        if (Accept(":")) TypeName();
        Semicolon();
        return f;
    }

    // ------------------------------------------------------------------ expressions

    Expr Expression(bool noIn = false)
    {
        var e = Assignment(noIn);
        while (Peek.Is(","))
        {
            _p++;
            e = new BinaryExpr(",", e, Assignment(noIn));
        }
        return e;
    }

    static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", ">>>=", "&=", "|=", "^="];

    Expr Assignment(bool noIn)
    {
        var left = Conditional(noIn);
        if (Peek.Kind == TokKind.Punct && AssignOps.Contains(Peek.Text))
        {
            var op = Next();
            if (left is not (NameExpr or MemberExpr or IndexExpr)) throw Error("invalid assignment target", op);
            return new AssignExpr(left, Assignment(noIn), op.Text);
        }
        return left;
    }

    Expr Conditional(bool noIn)
    {
        var cond = Binary(Prec.Or, noIn);
        if (!Accept("?")) return cond;
        var a = Assignment(false);
        Expect(":");
        var b = Assignment(noIn);
        return new TernaryExpr(cond, a, b);
    }

    static int BinaryPrec(Token t) => t.Kind is TokKind.Punct or TokKind.Keyword ? t.Text switch
    {
        "||" => Prec.Or, "&&" => Prec.And, "|" => Prec.BitOr, "^" => Prec.BitXor, "&" => Prec.BitAnd,
        "==" or "!=" or "===" or "!==" => Prec.Equality,
        "<" or ">" or "<=" or ">=" or "instanceof" or "is" or "as" or "in" => Prec.Relational,
        "<<" or ">>" or ">>>" => Prec.Shift,
        "+" or "-" => Prec.Additive,
        "*" or "/" or "%" => Prec.Multiplicative,
        _ => -1,
    } : -1;

    Expr Binary(int minPrec, bool noIn)
    {
        var left = Unary();
        while (true)
        {
            var op = Peek;
            int prec = BinaryPrec(op);
            if (prec < minPrec || (noIn && op.Is("in"))) return left;
            _p++;
            var right = Binary(prec + 1, noIn);
            left = new BinaryExpr(op.Text, left, right);
        }
    }

    Expr Unary()
    {
        var t = Peek;
        if (t.Kind is TokKind.Punct or TokKind.Keyword && t.Text is "!" or "~" or "-" or "+" or "typeof" or "delete" or "void" or "++" or "--")
        {
            _p++;
            var operand = Unary();
            // Literal negative numbers stay literals
            if (t.Text == "-" && operand is LitExpr { IsNumber: true } n)
                return n.Value is long l ? LitExpr.Int(-l) : LitExpr.Num(-n.AsNumber);
            if (t.Text is "++" or "--" && operand is not (NameExpr or MemberExpr or IndexExpr)) throw Error($"invalid operand for '{t.Text}'", t);
            return new UnaryExpr(t.Text, operand);
        }
        return Postfix();
    }

    Expr Postfix()
    {
        var e = CallMember(allowCall: true);
        if ((Peek.Is("++") || Peek.Is("--")) && !Peek.NewlineBefore)
        {
            var op = Next();
            if (e is not (NameExpr or MemberExpr or IndexExpr)) throw Error($"invalid operand for '{op.Text}'", op);
            return new PostfixExpr(op.Text, e);
        }
        return e;
    }

    Expr CallMember(bool allowCall)
    {
        var e = Primary();
        while (true)
        {
            if (Peek.Is("."))
            {
                _p++;
                if (Peek.Is("@")) throw Error("E4X attribute access (@) is not supported by the compiler");
                string name = Identifier();
                // ns::name — the namespace is resolved at runtime through the namespace set
                if (Accept("::")) name = Identifier();
                e = new MemberExpr(e, name);
            }
            else if (Peek.Is(".<"))
            {
                _p++;
                var args = new List<Expr> { TypeExpr() };
                while (Accept(",")) args.Add(TypeExpr());
                CloseAngle();
                e = new TypeArgsExpr(e, args);
            }
            else if (Peek.Is("::") && e is NameExpr)
            {
                _p++;
                e = new NameExpr(Identifier());
            }
            else if (Peek.Is("["))
            {
                _p++;
                var index = Expression();
                Expect("]");
                e = new IndexExpr(e, index);
            }
            else if (allowCall && Peek.Is("("))
            {
                e = new CallExpr(e, Arguments());
            }
            else if (Peek.Is(".."))
                throw Error("E4X descendant access (..) is not supported by the compiler");
            else return e;
        }
    }

    Expr TypeExpr()
    {
        if (Accept("*")) return new NameExpr("*");
        Expr e = new NameExpr(Identifier());
        while (Peek.Is(".") || Peek.Is(".<"))
        {
            if (Accept(".")) e = new MemberExpr(e, Identifier());
            else
            {
                _p++;
                var args = new List<Expr> { TypeExpr() };
                while (Accept(",")) args.Add(TypeExpr());
                CloseAngle();
                e = new TypeArgsExpr(e, args);
            }
        }
        return e;
    }

    List<Expr> Arguments()
    {
        Expect("(");
        var args = new List<Expr>();
        while (!Accept(")"))
        {
            args.Add(Assignment(false));
            if (!Peek.Is(")")) Expect(",");
        }
        return args;
    }

    Expr Primary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case TokKind.Number:
            {
                double d = (double)t.Value!;
                return d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue && !t.Text.Contains('.') && !t.Text.Contains('e') && !t.Text.Contains('E')
                    ? LitExpr.Int((long)d) : LitExpr.Num(d);
            }
            case TokKind.String:
                return LitExpr.Str((string)t.Value!);
            case TokKind.Ident:
                return t.Text switch
                {
                    "NaN" => LitExpr.Num(double.NaN),
                    "Infinity" => LitExpr.Num(double.PositiveInfinity),
                    _ => new NameExpr(t.Text),
                };
            case TokKind.Keyword:
                switch (t.Text)
                {
                    case "true": return LitExpr.Bool(true);
                    case "false": return LitExpr.Bool(false);
                    case "null": return LitExpr.Null;
                    case "undefined": return LitExpr.Undef;
                    case "this": return new ThisExpr();
                    case "super": return new SuperExpr();
                    case "function":
                        _p--;
                        return Function();
                    case "new":
                    {
                        if (Peek.Is("<")) throw Error("Vector literals (new <T>[…]) are not supported by the compiler", t);
                        var target = CallMember(allowCall: false);
                        var args = Peek.Is("(") ? Arguments() : [];
                        return new NewExpr(target, args);
                    }
                }
                break;
            case TokKind.Punct:
                switch (t.Text)
                {
                    case "(":
                    {
                        var e = Expression();
                        Expect(")");
                        return e;
                    }
                    case "[":
                    {
                        var items = new List<Expr>();
                        while (!Accept("]"))
                        {
                            items.Add(Assignment(false));
                            if (!Peek.Is("]")) Expect(",");
                        }
                        return new ArrayLitExpr(items);
                    }
                    case "{":
                    {
                        var props = new List<(Expr, Expr)>();
                        while (!Accept("}"))
                        {
                            var k = Next();
                            Expr key = k.Kind switch
                            {
                                TokKind.String => LitExpr.Str((string)k.Value!),
                                TokKind.Number => LitExpr.Str(k.Text),
                                TokKind.Ident or TokKind.Keyword => LitExpr.Str(k.Text),
                                _ => throw Error($"invalid object key {k}", k),
                            };
                            Expect(":");
                            props.Add((key, Assignment(false)));
                            if (!Peek.Is("}")) Expect(",");
                        }
                        return new ObjectLitExpr(props);
                    }
                }
                break;
        }
        throw Error($"unexpected {t}", t);
    }
}
