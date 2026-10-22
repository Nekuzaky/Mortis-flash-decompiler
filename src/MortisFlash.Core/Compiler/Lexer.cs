using System.Globalization;
using System.Text;

namespace MortisFlash.Compiler;

public sealed class CompileException(int line, string message) : Exception(line > 0 ? $"line {line}: {message}" : message)
{
    public int Line { get; } = line;
}

public enum TokKind { Ident, Number, String, Punct, Keyword, End }

public readonly record struct Token(TokKind Kind, string Text, int Line, object? Value = null, bool NewlineBefore = false)
{
    public bool Is(string text) => (Kind is TokKind.Punct or TokKind.Keyword) && Text == text;
    public override string ToString() => Kind == TokKind.End ? "end of input" : $"'{Text}'";
}

/// <summary>
/// ActionScript tokenizer (AS1/2/3 subset). Accepts the obfuscated-name syntax §name§ printed by the decompiler.
/// </summary>
public static class Lexer
{
    static readonly HashSet<string> Keywords =
    [
        "break", "case", "catch", "class", "continue", "default", "delete", "do", "else", "extends", "false", "finally",
        "for", "function", "if", "implements", "in", "instanceof", "interface", "new", "null", "return", "super", "switch",
        "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "is", "as", "const", "undefined",
    ];

    // Longest operators first
    static readonly string[] Puncts =
    [
        ">>>=", "...", "===", "!==", ">>>", "<<=", ">>=", "::", ".<", "..", "==", "!=", "<=", ">=", "&&", "||", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+",
        "-", "*", "/", "%", "&", "|", "^", "!", "~", "?", ":", "=", ".", "@",
    ];

    public static List<Token> Tokenize(string src)
    {
        var list = new List<Token>();
        int i = 0, line = 1;
        bool newline = false;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; newline = true; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new CompileException(line, "unterminated comment");
                line += src.AsSpan(i, end - i).Count('\n');
                i = end + 2;
                continue;
            }
            int start = i;
            Token tok;
            if (char.IsLetter(c) || c is '_' or '$')
            {
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] is '_' or '$')) i++;
                string word = src[start..i];
                tok = new Token(Keywords.Contains(word) ? TokKind.Keyword : TokKind.Ident, word, line, null, newline);
            }
            else if (c == '§')
            {
                // Obfuscated identifier: §any text§ (\§ escapes a section sign)
                var sb = new StringBuilder();
                i++;
                while (i < src.Length && src[i] != '§')
                {
                    if (src[i] == '\\' && i + 1 < src.Length && src[i + 1] == '§') i++;
                    sb.Append(src[i++]);
                }
                if (i >= src.Length) throw new CompileException(line, "unterminated §name§");
                i++;
                string name = sb.ToString();
                if (name.Length == 0)
                {
                    int w = i;
                    while (w < src.Length && char.IsLetter(src[w])) w++;
                    throw new CompileException(line, $"'§§{src[i..w]}' marks code the decompiler could not rebuild; edit this script as p-code instead");
                }
                tok = new Token(TokKind.Ident, name, line, null, newline);
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                tok = Number(src, ref i, line, newline);
            }
            else if (c is '"' or '\'')
            {
                tok = new Token(TokKind.String, src[start..Math.Min(src.Length, start + 1)], line, ReadString(src, ref i, ref line), newline);
            }
            else
            {
                string? p = Puncts.FirstOrDefault(x => string.CompareOrdinal(src, i, x, 0, x.Length) == 0);
                if (p == null) throw new CompileException(line, $"unexpected character '{c}'");
                i += p.Length;
                tok = new Token(TokKind.Punct, p, line, null, newline);
            }
            list.Add(tok);
            newline = false;
        }
        list.Add(new Token(TokKind.End, "", line));
        return list;
    }

    static Token Number(string s, ref int i, int line, bool newline)
    {
        int start = i;
        if (s[i] == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'X')
        {
            i += 2;
            while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
            return new Token(TokKind.Number, s[start..i], line, (double)Convert.ToInt64(s[(start + 2)..i], 16), newline);
        }
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i < s.Length && s[i] == '.' && !(i + 1 < s.Length && s[i + 1] == '.'))
        {
            i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            int save = i;
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            if (i < s.Length && char.IsDigit(s[i])) while (i < s.Length && char.IsDigit(s[i])) i++;
            else i = save;
        }
        string text = s[start..i];
        return new Token(TokKind.Number, text, line, double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), newline);
    }

    static string ReadString(string s, ref int i, ref int line)
    {
        char quote = s[i++];
        var sb = new StringBuilder();
        while (i < s.Length && s[i] != quote)
        {
            char c = s[i++];
            if (c == '\n') throw new CompileException(line, "unterminated string");
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '0': sb.Append('\0'); break;
                case 'x' when i + 2 <= s.Length:
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 2), 16));
                    i += 2;
                    break;
                case 'u' when i + 4 <= s.Length:
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                    i += 4;
                    break;
                case '\n':
                    line++;
                    break;
                default: sb.Append(e); break;
            }
        }
        if (i >= s.Length) throw new CompileException(line, "unterminated string");
        i++;
        return sb.ToString();
    }
}
