using System.Text;

namespace MortisFlash.Decompiler;

public enum TokenKind : byte { Plain, Keyword, Identifier, String, Number, Comment, Type, Punct }

public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Writes indented ActionScript code (braces on their own line, Flex style) and records the kind of
/// each text fragment, which enables syntax highlighting in the UI.
/// </summary>
public sealed class CodeWriter
{
    readonly StringBuilder _sb = new();
    readonly List<Token> _tokens = [];
    int _level;
    public string IndentUnit { get; init; } = "    ";

    public IReadOnlyList<Token> Tokens => _tokens;
    public int Level => _level;

    void Add(string s, TokenKind k)
    {
        if (s.Length == 0) return;
        if (k != TokenKind.Plain) _tokens.Add(new Token(_sb.Length, s.Length, k));
        _sb.Append(s);
    }

    public void Text(string s) => Add(s, TokenKind.Plain);
    public void Keyword(string s) => Add(s, TokenKind.Keyword);
    public void Identifier(string s) => Add(s, TokenKind.Identifier);
    public void String(string s) => Add(s, TokenKind.String);
    public void Number(string s) => Add(s, TokenKind.Number);
    public void Comment(string s) => Add(s, TokenKind.Comment);
    public void Type(string s) => Add(s, TokenKind.Type);
    public void Punct(string s) => Add(s, TokenKind.Punct);

    public void Indent()
    {
        for (int i = 0; i < _level; i++) _sb.Append(IndentUnit);
    }

    public void Line() => _sb.Append('\n');
    public void Push() => _level++;
    public void Pop() => _level = Math.Max(0, _level - 1);

    /// <summary>Writes a subexpression, parenthesized if its precedence is too low.</summary>
    public void Sub(Expr e, int minPrec)
    {
        if (e.Precedence < minPrec)
        {
            Punct("(");
            e.Write(this);
            Punct(")");
        }
        else e.Write(this);
    }

    public void List(IReadOnlyList<Expr> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) Punct(", ");
            Sub(items[i], Prec.Assign);
        }
    }

    /// <summary>Block { … }, with the opening brace on its own line.</summary>
    public void Block(List<Stmt> body, bool trailing = false)
    {
        Line();
        Indent();
        Punct("{");
        Line();
        Push();
        foreach (var s in body) s.Write(this);
        Pop();
        Indent();
        Punct("}");
        if (!trailing) Line();
    }

    public void Lines(string text)
    {
        foreach (var l in text.Split('\n'))
        {
            Indent();
            Text(l);
            Line();
        }
    }

    public override string ToString() => _sb.ToString();

    public static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsLetter(s[0]) || s[0] is '_' or '$')) return false;
        for (int i = 1; i < s.Length; i++)
            if (!(char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) return false;
        return !Keywords.Contains(s);
    }

    public static readonly HashSet<string> Keywords =
    [
        "as", "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else", "extends",
        "false", "finally", "for", "function", "if", "implements", "import", "in", "instanceof", "interface",
        "internal", "is", "new", "null", "package", "private", "protected", "public", "return", "super", "switch",
        "this", "throw", "true", "try", "typeof", "use", "var", "void", "while", "with", "each", "get", "set",
        "namespace", "include", "dynamic", "final", "native", "override", "static", "undefined",
    ];

    static readonly HashSet<string> ContextualKeywords = ["each", "get", "set", "namespace", "include", "dynamic", "final", "native", "override", "static"];

    /// <summary>Returns the name as is when usable, or wrapped in § § if obfuscated or invalid (JPEXS convention).</summary>
    public static string SafeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "§§";
        if (IsIdentifier(s) || ContextualKeywords.Contains(s)) return s;
        return "§" + s.Replace("§", "\\§") + "§";
    }
}
