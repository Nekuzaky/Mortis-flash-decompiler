using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MortisFlash.Decompiler;

namespace MortisFlash.Controls;

/// <summary>Selectable, syntax-highlighted ActionScript view (falls back to plain text above a size limit).</summary>
public static class CodeView
{
    static readonly IBrush Keyword = Brush("#FF8A65");
    static readonly IBrush Str = Brush("#A5D6A7");
    static readonly IBrush Number = Brush("#90CAF9");
    static readonly IBrush Comment = Brush("#6B6B6B");
    static readonly IBrush Type = Brush("#CE93D8");
    static readonly IBrush Punct = Brush("#9A9A9A");
    const int MaxColored = 300_000;

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    public static Control Create(CodeWriter code) => Create(code.ToString(), code.Tokens);

    public static Control Create(string text, IReadOnlyList<Token>? tokens = null)
    {
        var tb = new SelectableTextBlock { Classes = { "code" }, Margin = new Avalonia.Thickness(16, 12), TextWrapping = TextWrapping.NoWrap };
        if (tokens == null || tokens.Count == 0 || text.Length > MaxColored)
        {
            tb.Text = text;
        }
        else
        {
            var inlines = new InlineCollection();
            int pos = 0;
            foreach (var t in tokens)
            {
                if (t.Start > pos) inlines.Add(new Run(text[pos..t.Start]));
                var run = new Run(text.Substring(t.Start, t.Length));
                switch (t.Kind)
                {
                    case TokenKind.Keyword: run.Foreground = Keyword; break;
                    case TokenKind.String: run.Foreground = Str; break;
                    case TokenKind.Number: run.Foreground = Number; break;
                    case TokenKind.Comment: run.Foreground = Comment; run.FontStyle = FontStyle.Italic; break;
                    case TokenKind.Type: run.Foreground = Type; break;
                    case TokenKind.Punct: run.Foreground = Punct; break;
                }
                inlines.Add(run);
                pos = t.Start + t.Length;
            }
            if (pos < text.Length) inlines.Add(new Run(text[pos..]));
            tb.Inlines = inlines;
        }
        return new ScrollViewer
        {
            Content = tb,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    /// <summary>Hex dump (offset, bytes, ASCII) of the first bytes.</summary>
    public static string Hex(byte[] data, int max = 64 * 1024)
    {
        var sb = new System.Text.StringBuilder();
        int n = Math.Min(data.Length, max);
        for (int i = 0; i < n; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int k = 0; k < 16; k++)
            {
                sb.Append(i + k < n ? data[i + k].ToString("X2") + " " : "   ");
                if (k == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int k = 0; k < 16 && i + k < n; k++)
            {
                byte b = data[i + k];
                sb.Append(b is >= 32 and < 127 ? (char)b : '·');
            }
            sb.Append('\n');
        }
        if (data.Length > n) sb.Append($"… {data.Length - n:N0} more bytes\n");
        return sb.ToString();
    }
}
