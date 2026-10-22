using System.Globalization;
using System.Text;

namespace MortisFlash.Decompiler;

// ActionScript syntax tree shared by both virtual machines (AVM1 and AVM2).
// Precedences follow ECMAScript: the higher the number, the tighter the operator binds.

public static class Prec
{
    public const int Comma = 0, Assign = 1, Ternary = 2, Or = 3, And = 4, BitOr = 5, BitXor = 6, BitAnd = 7,
        Equality = 8, Relational = 9, Shift = 10, Additive = 11, Multiplicative = 12, Unary = 13, Postfix = 14,
        Call = 15, Primary = 16;
}

public abstract class Expr
{
    public virtual int Precedence => Prec.Primary;
    /// <summary>The expression has a side effect (call, assignment…) and cannot be silently discarded.</summary>
    public virtual bool HasSideEffects => false;
    /// <summary>The expression stripped of its transparent wrappers (type conversions).</summary>
    public virtual Expr Core => this;
    public abstract void Write(CodeWriter w);

    public override string ToString()
    {
        var w = new CodeWriter();
        Write(w);
        return w.ToString();
    }
}

public sealed class LitExpr(object? value) : Expr
{
    public object? Value { get; } = value;
    public static readonly object Undefined = new();

    public static LitExpr Null => new(null);
    public static LitExpr Undef => new(Undefined);
    public static LitExpr Bool(bool b) => new(b);
    public static LitExpr Num(double d) => new(d);
    public static LitExpr Int(long i) => new(i);
    public static LitExpr Str(string s) => new(s);

    public bool IsNumber => Value is double or long or int or uint;
    public double AsNumber => Value switch { double d => d, long l => l, int i => i, uint u => u, _ => double.NaN };

    public override void Write(CodeWriter w)
    {
        switch (Value)
        {
            case null: w.Keyword("null"); break;
            case bool b: w.Keyword(b ? "true" : "false"); break;
            case string s: w.String(Quote(s)); break;
            case long l: w.Number(l.ToString(CultureInfo.InvariantCulture)); break;
            case int i: w.Number(i.ToString(CultureInfo.InvariantCulture)); break;
            case uint u: w.Number(u.ToString(CultureInfo.InvariantCulture)); break;
            case double d: w.Number(FormatNumber(d)); break;
            default:
                if (ReferenceEquals(Value, Undefined)) w.Keyword("undefined");
                else w.Text(Value.ToString() ?? "");
                break;
        }
    }

    public override int Precedence => Value is double d && d < 0 || Value is long l && l < 0 || Value is int i && i < 0
        ? Prec.Unary : Prec.Primary;

    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append($"\\x{(int)c:X2}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}

/// <summary>Identifier (variable, global property, type…), already formatted.</summary>
public sealed class NameExpr(string name) : Expr
{
    public string Name { get; set; } = name;
    public override void Write(CodeWriter w) => w.Identifier(Name);
}

/// <summary>Local variable (register); its name is assigned after decompilation.</summary>
public sealed class LocalVar
{
    public int Register;
    public string Name = "";
    public string? Type;
    public bool Declared;
    public bool IsParam;
}

public sealed class LocalExpr(LocalVar v) : Expr
{
    public LocalVar Var { get; } = v;
    public override void Write(CodeWriter w)
    {
        if (Var.Name == "this") w.Keyword("this");
        else w.Identifier(Var.Name);
    }
}

public sealed class ThisExpr : Expr
{
    public override void Write(CodeWriter w) => w.Keyword("this");
}

public sealed class SuperExpr : Expr
{
    public override void Write(CodeWriter w) => w.Keyword("super");
}

public sealed class MemberExpr(Expr obj, string name) : Expr
{
    public Expr Object { get; } = obj;
    public string Name { get; } = name;
    public override int Precedence => Prec.Call;
    public override void Write(CodeWriter w)
    {
        // "5.toString()" is not valid syntax: a number literal needs parentheses before a member access
        w.Sub(Object, Object is LitExpr { IsNumber: true } ? Prec.Primary + 1 : Prec.Call);
        w.Punct(".");
        w.Identifier(Name);
    }
}

public sealed class IndexExpr(Expr obj, Expr index) : Expr
{
    public Expr Object { get; } = obj;
    public Expr Index { get; } = index;
    public override int Precedence => Prec.Call;
    public override void Write(CodeWriter w)
    {
        w.Sub(Object, Prec.Call);
        w.Punct("[");
        Index.Write(w);
        w.Punct("]");
    }
}

/// <summary>obj..name (XML descendants).</summary>
public sealed class DescendantsExpr(Expr obj, string name) : Expr
{
    public override int Precedence => Prec.Call;
    public override void Write(CodeWriter w)
    {
        w.Sub(obj, Prec.Call);
        w.Punct("..");
        w.Identifier(name);
    }
}

public sealed class CallExpr(Expr fn, List<Expr> args) : Expr
{
    public Expr Function { get; } = fn;
    public List<Expr> Args { get; } = args;
    public override int Precedence => Prec.Call;
    public override bool HasSideEffects => true;
    public override void Write(CodeWriter w)
    {
        w.Sub(Function, Prec.Call);
        w.Punct("(");
        w.List(Args);
        w.Punct(")");
    }
}

public sealed class NewExpr(Expr type, List<Expr> args) : Expr
{
    public Expr Type { get; } = type;
    public List<Expr> Args { get; } = args;
    public override int Precedence => Prec.Call;
    public override bool HasSideEffects => true;
    public override void Write(CodeWriter w)
    {
        w.Keyword("new");
        w.Text(" ");
        // "new a.b()"; a call inside the type must be parenthesized
        if (Type is CallExpr || Type.Precedence < Prec.Call) { w.Punct("("); Type.Write(w); w.Punct(")"); }
        else Type.Write(w);
        w.Punct("(");
        w.List(Args);
        w.Punct(")");
    }
}

public sealed class BinaryExpr(string op, Expr left, Expr right) : Expr
{
    public string Op { get; } = op;
    public Expr Left { get; } = left;
    public Expr Right { get; } = right;

    public static int PrecOf(string op) => op switch
    {
        "||" => Prec.Or, "&&" => Prec.And, "|" => Prec.BitOr, "^" => Prec.BitXor, "&" => Prec.BitAnd,
        "==" or "!=" or "===" or "!==" => Prec.Equality,
        "<" or ">" or "<=" or ">=" or "instanceof" or "is" or "as" or "in" => Prec.Relational,
        "<<" or ">>" or ">>>" => Prec.Shift,
        "+" or "-" => Prec.Additive,
        "*" or "/" or "%" => Prec.Multiplicative,
        "," => Prec.Comma,
        _ => Prec.Relational,
    };

    public override int Precedence => PrecOf(Op);
    public override bool HasSideEffects => Left.HasSideEffects || Right.HasSideEffects;

    public override void Write(CodeWriter w)
    {
        int p = Precedence;
        w.Sub(Left, p);
        if (Op == ",") w.Punct(", ");
        else if (char.IsLetter(Op[0])) { w.Text(" "); w.Keyword(Op); w.Text(" "); }
        else w.Punct($" {Op} ");
        // Left associativity: a right operand of equal precedence is parenthesized
        w.Sub(Right, p + 1);
    }
}

public sealed class UnaryExpr(string op, Expr operand) : Expr
{
    public string Op { get; } = op;
    public Expr Operand { get; } = operand;
    public override int Precedence => Prec.Unary;
    public override bool HasSideEffects => Op is "delete" or "++" or "--" || Operand.HasSideEffects;
    public override void Write(CodeWriter w)
    {
        if (char.IsLetter(Op[0])) { w.Keyword(Op); w.Text(" "); }
        else w.Punct(Op);
        // Keep "- -x" and "!!x" readable
        if (Operand is UnaryExpr u && (u.Op == Op || (Op == "-" && u.Op == "--") || (Op == "+" && u.Op == "++"))) w.Text(" ");
        w.Sub(Operand, Prec.Unary);
    }
}

public sealed class PostfixExpr(string op, Expr operand) : Expr
{
    public string Op { get; } = op;
    public Expr Operand { get; } = operand;
    public override int Precedence => Prec.Postfix;
    public override bool HasSideEffects => true;
    public override void Write(CodeWriter w)
    {
        w.Sub(Operand, Prec.Postfix);
        w.Punct(Op);
    }
}

public sealed class AssignExpr(Expr target, Expr value, string op = "=") : Expr
{
    public Expr Target { get; } = target;
    public Expr Value { get; } = value;
    public string Op { get; } = op;
    public override int Precedence => Prec.Assign;
    public override bool HasSideEffects => true;
    public override void Write(CodeWriter w)
    {
        w.Sub(Target, Prec.Postfix);
        w.Punct($" {Op} ");
        w.Sub(Value, Prec.Assign);
    }
}

public sealed class TernaryExpr(Expr cond, Expr a, Expr b) : Expr
{
    public Expr Cond { get; } = cond;
    public Expr Then { get; } = a;
    public Expr Else { get; } = b;
    public override int Precedence => Prec.Ternary;
    public override bool HasSideEffects => Cond.HasSideEffects || Then.HasSideEffects || Else.HasSideEffects;
    public override void Write(CodeWriter w)
    {
        w.Sub(Cond, Prec.Ternary + 1);
        w.Punct(" ? ");
        w.Sub(Then, Prec.Assign);
        w.Punct(" : ");
        w.Sub(Else, Prec.Assign);
    }
}

public sealed class ArrayLitExpr(List<Expr> items) : Expr
{
    public List<Expr> Items { get; } = items;
    public override bool HasSideEffects => Items.Any(i => i.HasSideEffects);
    public override void Write(CodeWriter w)
    {
        w.Punct("[");
        w.List(Items);
        w.Punct("]");
    }
}

public sealed class ObjectLitExpr(List<(Expr Key, Expr Value)> props) : Expr
{
    public List<(Expr Key, Expr Value)> Props { get; } = props;
    public override bool HasSideEffects => Props.Any(p => p.Value.HasSideEffects);
    public override void Write(CodeWriter w)
    {
        if (Props.Count == 0) { w.Punct("{}"); return; }
        w.Punct("{");
        for (int i = 0; i < Props.Count; i++)
        {
            if (i > 0) w.Punct(",");
            w.Text(" ");
            var (k, v) = Props[i];
            if (k is LitExpr { Value: string s } && CodeWriter.IsIdentifier(s)) w.Identifier(s);
            else k.Write(w);
            w.Punct(":");
            w.Sub(v, Prec.Assign);
        }
        w.Punct(" }");
    }
}

/// <summary>Anonymous or named function, with its body already decompiled.</summary>
public sealed class FunctionExpr : Expr
{
    public string? Name;
    public List<string> Params = [];
    /// <summary>Declared parameter types, when known (filled by the compiler's parser).</summary>
    public List<string?> ParamTypes = [];
    public string? ReturnType;
    public List<Stmt> Body = [];
    public override bool HasSideEffects => Name != null;
    public override void Write(CodeWriter w)
    {
        w.Keyword("function");
        if (Name != null) { w.Text(" "); w.Identifier(Name); }
        w.Punct("(");
        w.Text(string.Join(", ", Params));
        w.Punct(")");
        if (ReturnType != null) { w.Punct(" : "); w.Type(ReturnType); }
        w.Block(Body, trailing: true);
    }
}

/// <summary>Raw text (unrecognized cases).</summary>
public sealed class RawExpr(string text, int prec = Prec.Primary, bool sideEffects = false) : Expr
{
    public string Text { get; } = text;
    public override int Precedence => prec;
    public override bool HasSideEffects => sideEffects;
    public override void Write(CodeWriter w) => w.Text(Text);
}

// ---------------------------------------------------------------- Statements

public abstract class Stmt
{
    public abstract void Write(CodeWriter w);

    public override string ToString()
    {
        var w = new CodeWriter();
        Write(w);
        return w.ToString();
    }
}

public sealed class ExprStmt(Expr e) : Stmt
{
    public Expr Expr { get; set; } = e;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        // An object literal at the start of a statement would be parsed as a block
        if (Expr is ObjectLitExpr or FunctionExpr { Name: null }) { w.Punct("("); Expr.Write(w); w.Punct(")"); }
        else Expr.Write(w);
        w.Punct(";");
        w.Line();
    }
}

public sealed class VarStmt(string name, string? type, Expr? init) : Stmt
{
    public string Name { get; } = name;
    public string? Type { get; } = type;
    public Expr? Init { get; set; } = init;
    public LocalVar? Var { get; init; }
    public override void Write(CodeWriter w)
    {
        w.Indent();
        WriteInline(w);
        w.Punct(";");
        w.Line();
    }

    public void WriteInline(CodeWriter w)
    {
        w.Keyword("var");
        w.Text(" ");
        w.Identifier(Name);
        if (Type != null) { w.Punct(":"); w.Type(Type); }
        if (Init != null) { w.Punct(" = "); w.Sub(Init, Prec.Assign); }
    }
}

public sealed class ReturnStmt(Expr? value) : Stmt
{
    public Expr? Value { get; } = value;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Keyword("return");
        if (Value != null) { w.Text(" "); Value.Write(w); }
        w.Punct(";");
        w.Line();
    }
}

public sealed class ThrowStmt(Expr value) : Stmt
{
    public Expr Value { get; } = value;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Keyword("throw");
        w.Text(" ");
        Value.Write(w);
        w.Punct(";");
        w.Line();
    }
}

public sealed class IfStmt(Expr cond, List<Stmt> then, List<Stmt>? els) : Stmt
{
    public Expr Cond { get; set; } = cond;
    public List<Stmt> Then { get; set; } = then;
    public List<Stmt>? Else { get; set; } = els;

    public override void Write(CodeWriter w)
    {
        w.Indent();
        WriteNoIndent(w);
    }

    void WriteNoIndent(CodeWriter w)
    {
        w.Keyword("if");
        w.Punct(" (");
        Cond.Write(w);
        w.Punct(")");
        w.Block(Then);
        if (Else is { Count: > 0 })
        {
            w.Indent();
            w.Keyword("else");
            if (Else is [IfStmt nested])
            {
                w.Text(" ");
                nested.WriteNoIndent(w);
            }
            else w.Block(Else);
        }
    }
}

public abstract class LoopStmt : Stmt
{
    public string? Label;
    public List<Stmt> Body = [];

    protected void WriteLabel(CodeWriter w)
    {
        if (Label == null) return;
        w.Indent();
        w.Identifier(Label);
        w.Punct(":");
        w.Line();
    }
}

public sealed class WhileStmt : LoopStmt
{
    public Expr Cond = LitExpr.Bool(true);
    public override void Write(CodeWriter w)
    {
        WriteLabel(w);
        w.Indent();
        w.Keyword("while");
        w.Punct(" (");
        Cond.Write(w);
        w.Punct(")");
        w.Block(Body);
    }
}

public sealed class DoWhileStmt : LoopStmt
{
    public Expr Cond = LitExpr.Bool(true);
    public override void Write(CodeWriter w)
    {
        WriteLabel(w);
        w.Indent();
        w.Keyword("do");
        w.Block(Body, trailing: true);
        w.Text(" ");
        w.Keyword("while");
        w.Punct(" (");
        Cond.Write(w);
        w.Punct(");");
        w.Line();
    }
}

public sealed class ForStmt : LoopStmt
{
    public Stmt? Init;
    public Expr? Cond;
    public List<Stmt> Update = [];
    public override void Write(CodeWriter w)
    {
        WriteLabel(w);
        w.Indent();
        w.Keyword("for");
        w.Punct(" (");
        if (Init != null) WriteInline(Init, w);
        w.Punct(";");
        if (Cond != null) { w.Text(" "); Cond.Write(w); }
        w.Punct(";");
        for (int i = 0; i < Update.Count; i++)
        {
            w.Text(i == 0 ? " " : ", ");
            WriteInline(Update[i], w);
        }
        w.Punct(")");
        w.Block(Body);
    }

    static void WriteInline(Stmt s, CodeWriter w)
    {
        switch (s)
        {
            case VarStmt v: v.WriteInline(w); break;
            case ExprStmt e: e.Expr.Write(w); break;
            default: w.Text(s.ToString().Trim().TrimEnd(';')); break;
        }
    }
}

public sealed class ForInStmt : LoopStmt
{
    public required Expr Variable;
    public required Expr Object;
    public bool Each;
    public string? DeclareType;
    public bool Declare;
    public override void Write(CodeWriter w)
    {
        WriteLabel(w);
        w.Indent();
        w.Keyword("for");
        if (Each) { w.Text(" "); w.Keyword("each"); }
        w.Punct(" (");
        if (Declare) { w.Keyword("var"); w.Text(" "); }
        Variable.Write(w);
        if (Declare && DeclareType != null) { w.Punct(":"); w.Type(DeclareType); }
        w.Text(" ");
        w.Keyword("in");
        w.Text(" ");
        Object.Write(w);
        w.Punct(")");
        w.Block(Body);
    }
}

public sealed class SwitchCase
{
    /// <summary>Case labels; null = default.</summary>
    public List<Expr?> Labels = [];
    public List<Stmt> Body = [];
}

public sealed class SwitchStmt : Stmt
{
    public string? Label;
    public required Expr Discriminant;
    public List<SwitchCase> Cases = [];
    public override void Write(CodeWriter w)
    {
        if (Label != null)
        {
            w.Indent();
            w.Identifier(Label);
            w.Punct(":");
            w.Line();
        }
        w.Indent();
        w.Keyword("switch");
        w.Punct(" (");
        Discriminant.Write(w);
        w.Punct(")");
        w.Line();
        w.Indent();
        w.Punct("{");
        w.Line();
        w.Push();
        foreach (var c in Cases)
        {
            foreach (var l in c.Labels)
            {
                w.Indent();
                if (l == null) w.Keyword("default");
                else { w.Keyword("case"); w.Text(" "); l.Write(w); }
                w.Punct(":");
                w.Line();
            }
            w.Push();
            foreach (var s in c.Body) s.Write(w);
            w.Pop();
        }
        w.Pop();
        w.Indent();
        w.Punct("}");
        w.Line();
    }
}

public sealed class CatchClause
{
    public string? Name;
    public string? Type;
    public List<Stmt> Body = [];
}

public sealed class TryStmt : Stmt
{
    public List<Stmt> Body = [];
    public List<CatchClause> Catches = [];
    public List<Stmt>? Finally;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Keyword("try");
        w.Block(Body);
        foreach (var c in Catches)
        {
            w.Indent();
            w.Keyword("catch");
            w.Punct(" (");
            w.Identifier(c.Name ?? "e");
            if (c.Type != null) { w.Punct(":"); w.Type(c.Type); }
            w.Punct(")");
            w.Block(c.Body);
        }
        if (Finally != null)
        {
            w.Indent();
            w.Keyword("finally");
            w.Block(Finally);
        }
    }
}

public sealed class BreakStmt(string? label = null) : Stmt
{
    public string? Label { get; set; } = label;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Keyword("break");
        if (Label != null) { w.Text(" "); w.Identifier(Label); }
        w.Punct(";");
        w.Line();
    }
}

public sealed class ContinueStmt(string? label = null) : Stmt
{
    public string? Label { get; set; } = label;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Keyword("continue");
        if (Label != null) { w.Text(" "); w.Identifier(Label); }
        w.Punct(";");
        w.Line();
    }
}

public sealed class WithStmt(Expr obj, List<Stmt> body) : Stmt
{
    public Expr Object { get; } = obj;
    public List<Stmt> Body { get; } = body;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Keyword("with");
        w.Punct(" (");
        Object.Write(w);
        w.Punct(")");
        w.Block(Body);
    }
}

/// <summary>Named function declaration (AVM1).</summary>
public sealed class FunctionStmt(FunctionExpr f) : Stmt
{
    public FunctionExpr Function { get; } = f;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        Function.Write(w);
        w.Line();
    }
}

public sealed class CommentStmt(string text) : Stmt
{
    public string Text { get; } = text;
    public override void Write(CodeWriter w)
    {
        foreach (var line in Text.Split('\n'))
        {
            w.Indent();
            w.Comment("// " + line);
            w.Line();
        }
    }
}

/// <summary>Raw line (unstructurable jump, unknown instruction).</summary>
public sealed class RawStmt(string text) : Stmt
{
    public string Text { get; } = text;
    public override void Write(CodeWriter w)
    {
        w.Indent();
        w.Text(Text);
        w.Line();
    }
}

public sealed class BlockStmt(List<Stmt> body) : Stmt
{
    public List<Stmt> Body { get; } = body;
    public override void Write(CodeWriter w)
    {
        foreach (var s in Body) s.Write(w);
    }
}
