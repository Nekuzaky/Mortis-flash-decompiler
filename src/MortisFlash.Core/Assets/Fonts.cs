using System.Text;
using MortisFlash.Swf;

namespace MortisFlash.Assets;

public sealed class SwfFont
{
    public int Id;
    public string Name = "";
    public bool Bold, Italic;
    public List<SwfShape> Glyphs = [];
    /// <summary>Unicode character of each glyph (0 if unknown).</summary>
    public List<int> Codes = [];
    public List<int> Advances = [];
    public int Ascent, Descent, Leading;
    /// <summary>Units per em: 1024 (DefineFont/2) or 20480 (DefineFont3).</summary>
    public int EmSquare = 1024;
    /// <summary>Raw OpenType/CFF data (DefineFont4).</summary>
    public byte[]? FontData;
    public SwfTag? Tag;

    public string Charset => new(Codes.Where(c => c > 0).Select(c => c <= 0xFFFF ? (char)c : '?').ToArray());
}

public static class FontParser
{
    public static SwfFont Parse(SwfTag tag, int version)
    {
        var r = new SwfReader(tag.Data) { Version = version };
        var f = new SwfFont { Tag = tag, Id = r.U16() };
        switch (tag.Code)
        {
            case TagCodes.DefineFont:
            {
                int first = tag.Data.Length > 3 ? r.U16() : 0;
                int n = first / 2;
                var offsets = new List<int> { first };
                for (int i = 1; i < n; i++) offsets.Add(r.U16());
                for (int i = 0; i < n; i++)
                {
                    int start = 2 + offsets[i];
                    int end = i + 1 < n ? 2 + offsets[i + 1] : tag.Data.Length;
                    var gr = new SwfReader(tag.Data, 0, tag.Data.Length) { Position = start };
                    f.Glyphs.Add(ShapeParser.ParseGlyph(gr, end - start));
                    f.Codes.Add(0);
                }
                break;
            }
            case TagCodes.DefineFont2:
            case TagCodes.DefineFont3:
            {
                bool hasLayout = r.Bit();
                r.Bit(); // ShiftJIS
                r.Bit(); // SmallText
                r.Bit(); // ANSI
                bool wideOffsets = r.Bit();
                bool wideCodes = r.Bit();
                f.Italic = r.Bit();
                f.Bold = r.Bit();
                r.U8(); // language
                int nameLen = r.U8();
                f.Name = CleanName(r.Bytes(nameLen), version);
                int n = r.U16();
                int tableStart = r.Position;
                var offsets = new int[n];
                for (int i = 0; i < n; i++) offsets[i] = wideOffsets ? (int)r.U32() : r.U16();
                int codeOffset = n > 0 || r.Remaining >= 2 ? (wideOffsets ? (int)r.U32() : r.U16()) : 0;
                for (int i = 0; i < n; i++)
                {
                    int start = tableStart + offsets[i];
                    int end = i + 1 < n ? tableStart + offsets[i + 1] : tableStart + codeOffset;
                    var gr = new SwfReader(tag.Data) { Position = start };
                    f.Glyphs.Add(ShapeParser.ParseGlyph(gr, end - start));
                }
                r.Position = tableStart + codeOffset;
                for (int i = 0; i < n; i++) f.Codes.Add(wideCodes ? r.U16() : r.U8());
                if (hasLayout && r.Remaining >= 6)
                {
                    f.Ascent = r.S16();
                    f.Descent = r.S16();
                    f.Leading = r.S16();
                    for (int i = 0; i < n; i++) f.Advances.Add(r.S16());
                }
                f.EmSquare = tag.Code == TagCodes.DefineFont3 ? 20480 : 1024;
                break;
            }
            case TagCodes.DefineFont4:
            {
                int flags = r.U8();
                f.Italic = (flags & 2) != 0;
                f.Bold = (flags & 1) != 0;
                f.Name = r.String();
                if ((flags & 4) != 0) f.FontData = r.Rest();
                break;
            }
        }
        return f;
    }

    /// <summary>Completes a DefineFont (v1) with DefineFontInfo/2: name and character table.</summary>
    public static void ApplyInfo(SwfFont f, SwfTag info, int version)
    {
        var r = new SwfReader(info.Data) { Version = version };
        r.U16();
        int nameLen = r.U8();
        f.Name = CleanName(r.Bytes(nameLen), version);
        r.UB(2);
        r.Bit(); r.Bit(); r.Bit();
        f.Italic = r.Bit();
        f.Bold = r.Bit();
        bool wide = r.Bit();
        if (info.Code == TagCodes.DefineFontInfo2) r.U8();
        for (int i = 0; i < f.Glyphs.Count && r.Remaining > 0; i++)
            f.Codes[i] = wide ? r.U16() : r.U8();
    }

    static string CleanName(byte[] b, int version)
    {
        int n = Array.IndexOf(b, (byte)0);
        return SwfReader.Decode(b, 0, n < 0 ? b.Length : n, version);
    }
}

public sealed class TextGlyphRun
{
    public int FontId;
    public Rgba Color;
    public int X, Y;
    public int Height;
    public List<(int Glyph, int Advance)> Glyphs = [];
}

public sealed class SwfText
{
    public int Id;
    public Rect Bounds;
    public Matrix Matrix = Matrix.Identity;
    public List<TextGlyphRun> Runs = [];
    /// <summary>For DefineEditText.</summary>
    public bool IsEditText;
    public string VariableName = "";
    public string InitialText = "";
    public bool Html;
    public int FontId;
    public int FontHeight;
    public Rgba Color = new(0, 0, 0, 255);
    public SwfTag? Tag;
    /// <summary>DefineEditText: offset of the first flags byte and of the variable name within the tag.</summary>
    public int FlagsOffset = -1, VariableOffset = -1;
    /// <summary>DefineText: position of GlyphBits, right after the bounds and the matrix (the records follow).</summary>
    public int RecordsOffset = -1;

    /// <summary>Readable text: glyphs resolved through the fonts' character tables.</summary>
    public string ToPlainText(IReadOnlyDictionary<int, SwfFont> fonts)
    {
        if (IsEditText) return InitialText;
        var sb = new StringBuilder();
        int? lastY = null;
        foreach (var run in Runs)
        {
            if (lastY != null && run.Y != lastY) sb.Append('\n');
            lastY = run.Y;
            fonts.TryGetValue(run.FontId, out var font);
            foreach (var (g, _) in run.Glyphs)
            {
                int code = font != null && g < font.Codes.Count ? font.Codes[g] : 0;
                sb.Append(code > 0 ? char.ConvertFromUtf32(Math.Min(code, 0x10FFFF)) : "?");
            }
        }
        return sb.ToString();
    }
}

public static class TextParser
{
    public static SwfText Parse(SwfTag tag, int version)
    {
        var r = new SwfReader(tag.Data) { Version = version };
        var t = new SwfText { Tag = tag, Id = r.U16(), Bounds = r.Rect() };
        if (tag.Code == TagCodes.DefineEditText)
        {
            t.IsEditText = true;
            t.FlagsOffset = r.Position;
            bool hasText = r.Bit(), wordWrap = r.Bit(), multiline = r.Bit(), password = r.Bit(), readOnly = r.Bit(),
                hasColor = r.Bit(), hasMaxLength = r.Bit(), hasFont = r.Bit();
            bool hasFontClass = r.Bit(), autoSize = r.Bit(), hasLayout = r.Bit(), noSelect = r.Bit(), border = r.Bit(),
                wasStatic = r.Bit(), html = r.Bit(), useOutlines = r.Bit();
            _ = (wordWrap, multiline, password, readOnly, autoSize, noSelect, border, wasStatic, useOutlines);
            t.Html = html;
            if (hasFont) t.FontId = r.U16();
            if (hasFontClass) r.String();
            if (hasFont || hasFontClass) t.FontHeight = r.U16();
            if (hasColor) t.Color = r.Rgba();
            if (hasMaxLength) r.U16();
            if (hasLayout)
            {
                r.U8(); r.U16(); r.U16(); r.U16(); r.S16();
            }
            t.VariableOffset = r.Position;
            t.VariableName = r.String();
            if (hasText) t.InitialText = r.String();
            return t;
        }

        t.Matrix = r.Matrix();
        t.RecordsOffset = r.Position;
        int glyphBits = r.U8(), advanceBits = r.U8();
        bool v2 = tag.Code == TagCodes.DefineText2;
        int font = 0, height = 0, x = 0, y = 0;
        var color = new Rgba(0, 0, 0, 255);
        while (r.Remaining > 0)
        {
            int flags = r.U8();
            if (flags == 0) break;
            if ((flags & 0x08) != 0) font = r.U16();
            if ((flags & 0x04) != 0) color = v2 ? r.Rgba() : r.Rgb();
            if ((flags & 0x01) != 0) x = r.S16();
            if ((flags & 0x02) != 0) y = r.S16();
            if ((flags & 0x08) != 0) height = r.U16();
            int count = r.U8();
            var run = new TextGlyphRun { FontId = font, Color = color, X = x, Y = y, Height = height };
            for (int i = 0; i < count; i++)
            {
                int g = (int)r.UB(glyphBits);
                int adv = r.SB(advanceBits);
                run.Glyphs.Add((g, adv));
                x += adv;
            }
            r.Align();
            t.Runs.Add(run);
        }
        return t;
    }

    /// <summary>Strips the tags from Flash HTML text to produce a readable preview.</summary>
    public static string StripHtml(string html)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(html, @"<(br|/p)\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(s);
    }
}
