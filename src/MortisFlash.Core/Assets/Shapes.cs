using MortisFlash.Swf;

namespace MortisFlash.Assets;

public enum FillKind { Solid, Linear, Radial, Focal, Bitmap }

public sealed class FillStyle
{
    public FillKind Kind;
    public Rgba Color;
    public Matrix Matrix = Matrix.Identity;
    public List<(int Ratio, Rgba Color)> Stops = [];
    /// <summary>0 = pad, 1 = reflect, 2 = repeat.</summary>
    public int Spread;
    public double FocalPoint;
    public int BitmapId;
    public bool Repeat;
    public bool Smooth;
}

public sealed class LineStyle
{
    public int Width;
    public Rgba Color;
    public FillStyle? Fill;
    /// <summary>0 = round, 1 = none, 2 = square.</summary>
    public int StartCap, EndCap;
    /// <summary>0 = round, 1 = bevel, 2 = miter.</summary>
    public int Join;
    public double MiterLimit = 3;
}

public abstract record ShapeRecord;
public sealed record StyleChangeRecord(bool HasMove, int MoveX, int MoveY, int? Fill0, int? Fill1, int? Line,
    List<FillStyle>? NewFills, List<LineStyle>? NewLines) : ShapeRecord;
public sealed record StraightEdge(int Dx, int Dy) : ShapeRecord;
public sealed record CurvedEdge(int Cx, int Cy, int Ax, int Ay) : ShapeRecord;

/// <summary>Parsed SWF shape (DefineShape 1 to 4, font glyph, or one state of a morph shape).</summary>
public sealed class SwfShape
{
    public int Id;
    public Rect Bounds;
    public List<FillStyle> Fills = [];
    public List<LineStyle> Lines = [];
    public List<ShapeRecord> Records = [];
    public bool NonZeroWinding;
}

/// <summary>Absolute segment in twips; if <see cref="Curved"/>, (Cx, Cy) is the quadratic control point.</summary>
public readonly record struct Segment(int X0, int Y0, int X1, int Y1, bool Curved, int Cx, int Cy)
{
    public Segment Reverse() => new(X1, Y1, X0, Y0, Curved, Cx, Cy);
}

/// <summary>A path ready to draw: either a fill (closed contours) or a stroke.</summary>
public sealed class ShapePath
{
    public FillStyle? Fill;
    public LineStyle? Line;
    public List<List<Segment>> Contours = [];
}

public static class ShapeParser
{
    public static SwfShape ParseDefineShape(SwfTag tag)
    {
        var r = new SwfReader(tag.Data);
        int ver = tag.Code switch
        {
            TagCodes.DefineShape => 1, TagCodes.DefineShape2 => 2, TagCodes.DefineShape3 => 3, _ => 4,
        };
        var s = new SwfShape { Id = r.U16(), Bounds = r.Rect() };
        if (ver == 4)
        {
            r.Rect(); // EdgeBounds
            r.UB(5);
            s.NonZeroWinding = r.Bit();
            r.UB(2);
        }
        s.Fills = ReadFillStyles(r, ver);
        s.Lines = ReadLineStyles(r, ver);
        ReadRecords(r, ver, s.Records);
        return s;
    }

    /// <summary>Shape without styles (glyph): fill style 1 is implicit.</summary>
    public static SwfShape ParseGlyph(SwfReader r, int length)
    {
        int end = r.Position + length;
        var s = new SwfShape();
        s.Fills.Add(new FillStyle { Kind = FillKind.Solid, Color = new Rgba(0, 0, 0, 255) });
        try
        {
            ReadRecords(r, 1, s.Records);
        }
        catch (EndOfStreamException) { }
        r.Position = Math.Max(r.Position, Math.Min(end, r.End));
        return s;
    }

    public static List<FillStyle> ReadFillStyles(SwfReader r, int ver)
    {
        int count = r.U8();
        if (count == 0xFF && ver >= 2) count = r.U16();
        var list = new List<FillStyle>(count);
        for (int i = 0; i < count; i++) list.Add(ReadFillStyle(r, ver));
        return list;
    }

    static FillStyle ReadFillStyle(SwfReader r, int ver)
    {
        int type = r.U8();
        var f = new FillStyle();
        switch (type)
        {
            case 0x00:
                f.Kind = FillKind.Solid;
                f.Color = ver >= 3 ? r.Rgba() : r.Rgb();
                break;
            case 0x10:
            case 0x12:
            case 0x13:
                f.Kind = type == 0x10 ? FillKind.Linear : type == 0x12 ? FillKind.Radial : FillKind.Focal;
                f.Matrix = r.Matrix();
                ReadGradient(r, ver, f);
                break;
            case >= 0x40 and <= 0x43:
                f.Kind = FillKind.Bitmap;
                f.BitmapId = r.U16();
                f.Matrix = r.Matrix();
                f.Repeat = type is 0x40 or 0x42;
                f.Smooth = type is 0x40 or 0x41;
                break;
            default:
                throw new InvalidDataException($"Unknown fill style 0x{type:X2}");
        }
        return f;
    }

    static void ReadGradient(SwfReader r, int ver, FillStyle f)
    {
        f.Spread = (int)r.UB(2);
        r.UB(2); // interpolation
        int n = (int)r.UB(4);
        for (int i = 0; i < n; i++)
        {
            int ratio = r.U8();
            f.Stops.Add((ratio, ver >= 3 ? r.Rgba() : r.Rgb()));
        }
        if (f.Kind == FillKind.Focal) f.FocalPoint = r.Fixed8();
    }

    public static List<LineStyle> ReadLineStyles(SwfReader r, int ver)
    {
        int count = r.U8();
        if (count == 0xFF && ver >= 2) count = r.U16();
        var list = new List<LineStyle>(count);
        for (int i = 0; i < count; i++)
        {
            var l = new LineStyle { Width = r.U16() };
            if (ver == 4)
            {
                l.StartCap = (int)r.UB(2);
                l.Join = (int)r.UB(2);
                bool hasFill = r.Bit();
                r.UB(3); // NoHScale, NoVScale, PixelHinting
                r.UB(5);
                r.Bit(); // NoClose
                l.EndCap = (int)r.UB(2);
                if (l.Join == 2) l.MiterLimit = r.Fixed8();
                if (hasFill)
                {
                    l.Fill = ReadFillStyle(r, ver);
                    l.Color = l.Fill.Kind == FillKind.Solid ? l.Fill.Color : l.Fill.Stops.FirstOrDefault().Color;
                }
                else l.Color = r.Rgba();
            }
            else l.Color = ver >= 3 ? r.Rgba() : r.Rgb();
            list.Add(l);
        }
        return list;
    }

    public static void ReadRecords(SwfReader r, int ver, List<ShapeRecord> records)
    {
        r.Align();
        int fillBits = (int)r.UB(4);
        int lineBits = (int)r.UB(4);
        while (true)
        {
            bool isEdge = r.Bit();
            if (!isEdge)
            {
                bool newStyles = r.Bit(), lineChange = r.Bit(), fill1Change = r.Bit(), fill0Change = r.Bit(), move = r.Bit();
                if (!newStyles && !lineChange && !fill1Change && !fill0Change && !move) break;
                int mx = 0, my = 0;
                if (move)
                {
                    int n = (int)r.UB(5);
                    mx = r.SB(n);
                    my = r.SB(n);
                }
                int? f0 = fill0Change ? (int)r.UB(fillBits) : null;
                int? f1 = fill1Change ? (int)r.UB(fillBits) : null;
                int? ln = lineChange ? (int)r.UB(lineBits) : null;
                List<FillStyle>? nf = null;
                List<LineStyle>? nl = null;
                if (newStyles && ver >= 2)
                {
                    nf = ReadFillStyles(r, ver);
                    nl = ReadLineStyles(r, ver);
                    r.Align();
                    fillBits = (int)r.UB(4);
                    lineBits = (int)r.UB(4);
                }
                records.Add(new StyleChangeRecord(move, mx, my, f0, f1, ln, nf, nl));
            }
            else if (r.Bit())
            {
                int n = (int)r.UB(4) + 2;
                if (r.Bit()) records.Add(new StraightEdge(r.SB(n), r.SB(n)));
                else if (r.Bit()) records.Add(new StraightEdge(0, r.SB(n)));
                else records.Add(new StraightEdge(r.SB(n), 0));
            }
            else
            {
                int n = (int)r.UB(4) + 2;
                records.Add(new CurvedEdge(r.SB(n), r.SB(n), r.SB(n), r.SB(n)));
            }
        }
        r.Align();
    }

    /// <summary>
    /// Converts the records into paths: each edge belongs to the left fill (fill0, reversed) and the
    /// right fill (fill1); the edges of each style are then stitched back into closed contours.
    /// </summary>
    public static List<ShapePath> BuildPaths(SwfShape shape)
    {
        var result = new List<ShapePath>();
        var fills = shape.Fills;
        var lines = shape.Lines;
        var fillEdges = new Dictionary<int, List<Segment>>();
        var linePaths = new List<(int Style, List<Segment> Segs)>();
        int x = 0, y = 0, f0 = 0, f1 = 0, ln = 0;
        List<Segment>? currentLine = null;

        void Flush()
        {
            foreach (var (idx, edges) in fillEdges.OrderBy(k => k.Key))
            {
                if (idx < 1 || idx > fills.Count) continue;
                result.Add(new ShapePath { Fill = fills[idx - 1], Contours = LinkContours(edges) });
            }
            foreach (var (idx, segs) in linePaths)
            {
                if (idx < 1 || idx > lines.Count || segs.Count == 0) continue;
                var last = result.LastOrDefault();
                if (last is { Line: not null } && ReferenceEquals(last.Line, lines[idx - 1])) last.Contours.Add(segs);
                else result.Add(new ShapePath { Line = lines[idx - 1], Contours = [segs] });
            }
            fillEdges.Clear();
            linePaths.Clear();
            currentLine = null;
        }

        void AddEdge(Segment s)
        {
            if (f1 != 0) Get(fillEdges, f1).Add(s);
            if (f0 != 0) Get(fillEdges, f0).Add(s.Reverse());
            if (ln != 0)
            {
                if (currentLine == null)
                {
                    currentLine = [];
                    linePaths.Add((ln, currentLine));
                }
                currentLine.Add(s);
            }
        }

        foreach (var rec in shape.Records)
        {
            switch (rec)
            {
                case StyleChangeRecord sc:
                    if (sc.NewFills != null)
                    {
                        Flush();
                        fills = sc.NewFills;
                        lines = sc.NewLines ?? [];
                        f0 = f1 = ln = 0;
                    }
                    if (sc.HasMove)
                    {
                        x = sc.MoveX;
                        y = sc.MoveY;
                        currentLine = null;
                    }
                    if (sc.Fill0 is { } a) f0 = a;
                    if (sc.Fill1 is { } b) f1 = b;
                    if (sc.Line is { } c)
                    {
                        ln = c;
                        currentLine = null;
                    }
                    break;
                case StraightEdge e:
                    AddEdge(new Segment(x, y, x + e.Dx, y + e.Dy, false, 0, 0));
                    x += e.Dx;
                    y += e.Dy;
                    break;
                case CurvedEdge ce:
                {
                    int cx = x + ce.Cx, cy = y + ce.Cy;
                    int ax = cx + ce.Ax, ay = cy + ce.Ay;
                    AddEdge(new Segment(x, y, ax, ay, true, cx, cy));
                    x = ax;
                    y = ay;
                    break;
                }
            }
        }
        Flush();
        return result;
    }

    static List<Segment> Get(Dictionary<int, List<Segment>> d, int k)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        return l;
    }

    static List<List<Segment>> LinkContours(List<Segment> edges)
    {
        var byStart = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            var key = (edges[i].X0, edges[i].Y0);
            if (!byStart.TryGetValue(key, out var l)) byStart[key] = l = [];
            l.Add(i);
        }
        var used = new bool[edges.Count];
        var contours = new List<List<Segment>>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (used[i]) continue;
            var contour = new List<Segment>();
            int cur = i;
            while (cur >= 0)
            {
                used[cur] = true;
                var s = edges[cur];
                contour.Add(s);
                if (s.X1 == edges[i].X0 && s.Y1 == edges[i].Y0) break;
                cur = -1;
                if (byStart.TryGetValue((s.X1, s.Y1), out var cands))
                    foreach (var c in cands)
                        if (!used[c]) { cur = c; break; }
            }
            contours.Add(contour);
        }
        return contours;
    }
}

/// <summary>Morph shape: both states (start / end) are kept so they can be interpolated.</summary>
public sealed class SwfMorphShape
{
    public int Id;
    public Rect StartBounds, EndBounds;
    public SwfShape Start = new(), End = new();

    public static SwfMorphShape Parse(SwfTag tag)
    {
        var r = new SwfReader(tag.Data);
        bool v2 = tag.Code == TagCodes.DefineMorphShape2;
        var m = new SwfMorphShape { Id = r.U16(), StartBounds = r.Rect(), EndBounds = r.Rect() };
        if (v2)
        {
            r.Rect();
            r.Rect();
            r.U8();
        }
        uint offset = r.U32();
        int afterOffset = r.Position;
        int fillCount = r.U8();
        if (fillCount == 0xFF) fillCount = r.U16();
        for (int i = 0; i < fillCount; i++)
        {
            var (a, b) = ReadMorphFill(r);
            m.Start.Fills.Add(a);
            m.End.Fills.Add(b);
        }
        int lineCount = r.U8();
        if (lineCount == 0xFF) lineCount = r.U16();
        for (int i = 0; i < lineCount; i++)
        {
            var a = new LineStyle { Width = r.U16() };
            var b = new LineStyle { Width = r.U16() };
            if (v2)
            {
                a.StartCap = b.StartCap = (int)r.UB(2);
                a.Join = b.Join = (int)r.UB(2);
                bool hasFill = r.Bit();
                r.UB(3);
                r.UB(5);
                r.Bit();
                a.EndCap = b.EndCap = (int)r.UB(2);
                if (a.Join == 2) a.MiterLimit = b.MiterLimit = r.Fixed8();
                if (hasFill)
                {
                    var (fa, fb) = ReadMorphFill(r);
                    a.Fill = fa; b.Fill = fb;
                    a.Color = fa.Color; b.Color = fb.Color;
                }
                else
                {
                    a.Color = r.Rgba();
                    b.Color = r.Rgba();
                }
            }
            else
            {
                a.Color = r.Rgba();
                b.Color = r.Rgba();
            }
            m.Start.Lines.Add(a);
            m.End.Lines.Add(b);
        }
        ShapeParser.ReadRecords(r, 3, m.Start.Records);
        r.Position = afterOffset + (int)offset;
        try { ShapeParser.ReadRecords(r, 3, m.End.Records); } catch (EndOfStreamException) { }
        m.Start.Bounds = m.StartBounds;
        m.End.Bounds = m.EndBounds;
        return m;
    }

    static (FillStyle, FillStyle) ReadMorphFill(SwfReader r)
    {
        int type = r.U8();
        var a = new FillStyle();
        var b = new FillStyle();
        switch (type)
        {
            case 0x00:
                a.Kind = b.Kind = FillKind.Solid;
                a.Color = r.Rgba();
                b.Color = r.Rgba();
                break;
            case 0x10:
            case 0x12:
            case 0x13:
                a.Kind = b.Kind = type == 0x10 ? FillKind.Linear : type == 0x12 ? FillKind.Radial : FillKind.Focal;
                a.Matrix = r.Matrix();
                b.Matrix = r.Matrix();
                int n = r.U8() & 0x0F;
                for (int i = 0; i < n; i++)
                {
                    a.Stops.Add((r.U8(), r.Rgba()));
                    b.Stops.Add((r.U8(), r.Rgba()));
                }
                if (type == 0x13)
                {
                    a.FocalPoint = r.Fixed8();
                    b.FocalPoint = r.Fixed8();
                }
                break;
            case >= 0x40 and <= 0x43:
                a.Kind = b.Kind = FillKind.Bitmap;
                a.BitmapId = b.BitmapId = r.U16();
                a.Matrix = r.Matrix();
                b.Matrix = r.Matrix();
                a.Repeat = b.Repeat = type is 0x40 or 0x42;
                a.Smooth = b.Smooth = type is 0x40 or 0x41;
                break;
            default:
                throw new InvalidDataException($"Unknown morph style 0x{type:X2}");
        }
        return (a, b);
    }

    /// <summary>Intermediate shape at ratio t (0 = start, 1 = end).</summary>
    public SwfShape Interpolate(double t)
    {
        if (t <= 0) return Start;
        var s = new SwfShape { Id = Id, Bounds = StartBounds };
        for (int i = 0; i < Start.Fills.Count; i++)
        {
            var a = Start.Fills[i];
            var b = i < End.Fills.Count ? End.Fills[i] : a;
            var f = new FillStyle
            {
                Kind = a.Kind, Color = Lerp(a.Color, b.Color, t), Matrix = Lerp(a.Matrix, b.Matrix, t),
                BitmapId = a.BitmapId, Repeat = a.Repeat, Smooth = a.Smooth, Spread = a.Spread,
                FocalPoint = a.FocalPoint + (b.FocalPoint - a.FocalPoint) * t,
            };
            for (int k = 0; k < a.Stops.Count; k++)
            {
                var sb = k < b.Stops.Count ? b.Stops[k] : a.Stops[k];
                f.Stops.Add(((int)(a.Stops[k].Ratio + (sb.Ratio - a.Stops[k].Ratio) * t), Lerp(a.Stops[k].Color, sb.Color, t)));
            }
            s.Fills.Add(f);
        }
        for (int i = 0; i < Start.Lines.Count; i++)
        {
            var a = Start.Lines[i];
            var b = i < End.Lines.Count ? End.Lines[i] : a;
            s.Lines.Add(new LineStyle
            {
                Width = (int)(a.Width + (b.Width - a.Width) * t), Color = Lerp(a.Color, b.Color, t),
                StartCap = a.StartCap, EndCap = a.EndCap, Join = a.Join,
            });
        }
        // End edges have no style changes: pair them with the start edges
        var endEdges = End.Records.Where(e => e is not StyleChangeRecord).ToList();
        var endMoves = End.Records.OfType<StyleChangeRecord>().Where(m => m.HasMove).ToList();
        int ei = 0, mi = 0;
        foreach (var rec in Start.Records)
        {
            switch (rec)
            {
                case StyleChangeRecord sc:
                    if (sc.HasMove && mi < endMoves.Count)
                    {
                        var em = endMoves[mi++];
                        s.Records.Add(sc with { MoveX = L(sc.MoveX, em.MoveX, t), MoveY = L(sc.MoveY, em.MoveY, t) });
                    }
                    else s.Records.Add(sc);
                    break;
                default:
                {
                    var e = ei < endEdges.Count ? endEdges[ei++] : rec;
                    if (rec is StraightEdge sa && e is StraightEdge sb)
                    {
                        s.Records.Add(new StraightEdge(L(sa.Dx, sb.Dx, t), L(sa.Dy, sb.Dy, t)));
                        break;
                    }
                    var (acx, acy, aax, aay) = Quad(rec);
                    var (bcx, bcy, bax, bay) = Quad(e);
                    s.Records.Add(new CurvedEdge(L(acx, bcx, t), L(acy, bcy, t), L(aax, bax, t), L(aay, bay, t)));
                    break;
                }
            }
        }
        return s;
    }

    /// <summary>Edge as (relative control point, anchor relative to the control point).</summary>
    static (int, int, int, int) Quad(ShapeRecord r) => r switch
    {
        StraightEdge s => (s.Dx / 2, s.Dy / 2, s.Dx - s.Dx / 2, s.Dy - s.Dy / 2),
        CurvedEdge c => (c.Cx, c.Cy, c.Ax, c.Ay),
        _ => (0, 0, 0, 0),
    };

    static int L(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);
    static Rgba Lerp(Rgba a, Rgba b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t), (byte)(a.A + (b.A - a.A) * t));
    static Matrix Lerp(Matrix a, Matrix b, double t) => new(
        a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t, a.C + (b.C - a.C) * t, a.D + (b.D - a.D) * t,
        a.Tx + (b.Tx - a.Tx) * t, a.Ty + (b.Ty - a.Ty) * t);
}
