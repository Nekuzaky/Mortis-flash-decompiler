using MortisFlash.Assets;
using MortisFlash.Swf;
using SkiaSharp;

namespace MortisFlash.Rendering;

/// <summary>Display list object at a given depth.</summary>
public sealed class DisplayObject
{
    public int CharacterId;
    public Matrix Matrix = Matrix.Identity;
    public ColorTransform ColorTransform = ColorTransform.Identity;
    public int Ratio;
    public int ClipDepth;
    public string? Name;
    public bool Visible = true;
}

/// <summary>
/// Software renderer (SkiaSharp) for SWF characters: shapes, morph shapes, static text, buttons,
/// sprites and timeline frames. Filters and blend modes are not applied.
/// </summary>
public sealed class SwfRenderer
{
    readonly SwfDocument _doc;
    readonly Dictionary<int, List<ShapePath>> _paths = [];
    readonly Dictionary<int, SwfImage> _images;
    readonly Dictionary<int, SwfText> _texts;
    readonly Dictionary<int, SwfButton> _buttons;
    const int MaxDepth = 24;

    public SwfRenderer(SwfDocument doc)
    {
        _doc = doc;
        _images = doc.Images.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        _texts = doc.Texts.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        _buttons = doc.Buttons.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
    }

    // ------------------------------------------------------------------ display list

    public static SortedDictionary<int, DisplayObject> DisplayList(IEnumerable<SwfTag> timeline, int frame, int version)
    {
        var list = new SortedDictionary<int, DisplayObject>();
        int current = 0;
        foreach (var t in timeline)
        {
            switch (t.Code)
            {
                case TagCodes.ShowFrame:
                    if (current++ >= frame) return list;
                    break;
                case TagCodes.PlaceObject:
                case TagCodes.PlaceObject2:
                case TagCodes.PlaceObject3:
                case TagCodes.PlaceObject4:
                {
                    PlaceObject p;
                    try { p = PlaceObject.Parse(t, version); }
                    catch { break; }
                    if (!p.Move || !list.TryGetValue(p.Depth, out var o))
                    {
                        if (p.CharacterId == null) break;
                        o = new DisplayObject();
                        list[p.Depth] = o;
                    }
                    else if (p.CharacterId != null && !p.Move)
                    {
                        o = new DisplayObject();
                        list[p.Depth] = o;
                    }
                    if (p.CharacterId is { } cid) o.CharacterId = cid;
                    if (p.Matrix is { } m) o.Matrix = m;
                    if (p.ColorTransform is { } ct) o.ColorTransform = ct;
                    if (p.Ratio is { } r) o.Ratio = r;
                    if (p.ClipDepth is { } cd) o.ClipDepth = cd;
                    if (p.Name != null) o.Name = p.Name;
                    if (p.Visible is { } vis) o.Visible = vis;
                    break;
                }
                case TagCodes.RemoveObject:
                    if (t.Data.Length >= 4) list.Remove(t.Data[2] | t.Data[3] << 8);
                    break;
                case TagCodes.RemoveObject2:
                    if (t.Data.Length >= 2) list.Remove(t.Data[0] | t.Data[1] << 8);
                    break;
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ bounds

    public Rect? Bounds(int id, int depth = 0)
    {
        if (depth > MaxDepth || !_doc.Characters.TryGetValue(id, out var tag)) return null;
        try
        {
            switch (tag.Code)
            {
                case var c when TagCodes.IsShape(c):
                    return Shape(id)?.Bounds;
                case var c when TagCodes.IsMorph(c):
                    return _doc.MorphShapes.FirstOrDefault(m => m.Id == id)?.Morph?.StartBounds;
                case TagCodes.DefineText:
                case TagCodes.DefineText2:
                case TagCodes.DefineEditText:
                    return _texts.TryGetValue(id, out var t) ? Transform(t.Bounds, t.IsEditText ? Matrix.Identity : t.Matrix) : null;
                case TagCodes.DefineButton:
                case TagCodes.DefineButton2:
                    if (!_buttons.TryGetValue(id, out var b)) return null;
                    return Union(b.Records.Where(r => r.Up).Select(r => Bounds(r.CharacterId, depth + 1) is { } bb ? Transform(bb, r.Matrix) : (Rect?)null));
                case TagCodes.DefineSprite:
                {
                    var dl = DisplayList(tag.Children ?? [], 0, _doc.Swf.Version);
                    return Union(dl.Values.Select(o => Bounds(o.CharacterId, depth + 1) is { } bb ? Transform(bb, o.Matrix) : (Rect?)null));
                }
                case var c when TagCodes.IsImage(c):
                    return _images.TryGetValue(id, out var img) ? new Rect(0, img.Width * 20, 0, img.Height * 20) : null;
            }
        }
        catch { }
        return null;
    }

    static Rect? Union(IEnumerable<Rect?> rects)
    {
        Rect? acc = null;
        foreach (var r in rects)
        {
            if (r is not { } x || x.Width <= 0 && x.Height <= 0) continue;
            acc = acc is { } a ? new Rect(Math.Min(a.XMin, x.XMin), Math.Max(a.XMax, x.XMax), Math.Min(a.YMin, x.YMin), Math.Max(a.YMax, x.YMax)) : x;
        }
        return acc;
    }

    static Rect Transform(Rect r, Matrix m)
    {
        var pts = new[] { m.Apply(r.XMin, r.YMin), m.Apply(r.XMax, r.YMin), m.Apply(r.XMin, r.YMax), m.Apply(r.XMax, r.YMax) };
        return new Rect((int)pts.Min(p => p.X), (int)pts.Max(p => p.X), (int)pts.Min(p => p.Y), (int)pts.Max(p => p.Y));
    }

    SwfShape? Shape(int id) => _doc.Shapes.FirstOrDefault(s => s.Id == id)?.Shape;

    List<ShapePath> Paths(int id)
    {
        if (_paths.TryGetValue(id, out var p)) return p;
        var s = Shape(id);
        p = s != null ? ShapeParser.BuildPaths(s) : [];
        _paths[id] = p;
        return p;
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Renders a single character, framed to its bounds (maximum size in pixels).</summary>
    public SKBitmap? RenderCharacter(int id, int maxSize = 1024, int frame = 0)
    {
        if (!_doc.Characters.TryGetValue(id, out var tag)) return null;
        Rect? bounds = tag.Code == TagCodes.DefineSprite && frame > 0
            ? Union(DisplayList(tag.Children ?? [], frame, _doc.Swf.Version).Values.Select(o => Bounds(o.CharacterId) is { } b ? Transform(b, o.Matrix) : (Rect?)null))
            : Bounds(id);
        if (bounds is not { } r || r.Width <= 0 || r.Height <= 0) r = new Rect(-200, 200, -200, 200);
        double w = r.Width / 20.0, h = r.Height / 20.0;
        double scale = Math.Min(4, Math.Min(maxSize / Math.Max(w, 1), maxSize / Math.Max(h, 1)));
        int pw = Math.Max(1, (int)Math.Ceiling(w * scale)), ph = Math.Max(1, (int)Math.Ceiling(h * scale));
        var bmp = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)(scale / 20), (float)(scale / 20));
        canvas.Translate(-r.XMin, -r.YMin);
        Draw(canvas, id, Matrix.Identity, ColorTransform.Identity, 0, frame, 0);
        return bmp;
    }

    /// <summary>Renders a frame of the main timeline at stage size.</summary>
    public SKBitmap RenderFrame(int frame, double scale = 1, bool background = true)
    {
        var size = _doc.Swf.FrameSize;
        int pw = Math.Max(1, (int)Math.Ceiling(size.Width / 20.0 * scale)), ph = Math.Max(1, (int)Math.Ceiling(size.Height / 20.0 * scale));
        var bmp = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        var bg = _doc.Background;
        canvas.Clear(background ? new SKColor(bg.R, bg.G, bg.B) : SKColors.Transparent);
        canvas.Scale((float)(scale / 20), (float)(scale / 20));
        canvas.Translate(-size.XMin, -size.YMin);
        DrawList(canvas, DisplayList(_doc.Swf.Tags, frame, _doc.Swf.Version), Matrix.Identity, ColorTransform.Identity, 0);
        return bmp;
    }

    void DrawList(SKCanvas c, SortedDictionary<int, DisplayObject> list, Matrix m, ColorTransform ct, int depth)
    {
        var items = list.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var (d, o) = items[i];
            if (!o.Visible) continue;
            if (o.ClipDepth > 0)
            {
                // Mask: draw the content into a layer, then the mask with DstIn
                c.SaveLayer();
                int j = i + 1;
                for (; j < items.Count && items[j].Key <= o.ClipDepth; j++)
                {
                    var inner = items[j].Value;
                    if (inner.ClipDepth > 0 || !inner.Visible) continue;
                    Draw(c, inner.CharacterId, m.Multiply(inner.Matrix), ct.Concat(inner.ColorTransform), inner.Ratio, 0, depth + 1);
                }
                using (var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn })
                {
                    c.SaveLayer(dstIn);
                    Draw(c, o.CharacterId, m.Multiply(o.Matrix), ColorTransform.Identity, o.Ratio, 0, depth + 1, maskMode: true);
                    c.Restore();
                }
                c.Restore();
                i = j - 1;
                continue;
            }
            Draw(c, o.CharacterId, m.Multiply(o.Matrix), ct.Concat(o.ColorTransform), o.Ratio, 0, depth + 1);
        }
    }

    void Draw(SKCanvas c, int id, Matrix m, ColorTransform ct, int ratio, int frame, int depth, bool maskMode = false)
    {
        if (depth > MaxDepth || !_doc.Characters.TryGetValue(id, out var tag)) return;
        try
        {
            switch (tag.Code)
            {
                case var code when TagCodes.IsShape(code):
                    DrawPaths(c, Paths(id), m, ct, maskMode);
                    break;
                case var code when TagCodes.IsMorph(code):
                {
                    var morph = _doc.MorphShapes.FirstOrDefault(s => s.Id == id)?.Morph;
                    if (morph != null) DrawPaths(c, ShapeParser.BuildPaths(morph.Interpolate(ratio / 65535.0)), m, ct, maskMode);
                    break;
                }
                case TagCodes.DefineSprite:
                    DrawList(c, DisplayList(tag.Children ?? [], frame, _doc.Swf.Version), m, ct, depth);
                    break;
                case TagCodes.DefineButton:
                case TagCodes.DefineButton2:
                    if (_buttons.TryGetValue(id, out var b))
                        foreach (var r in b.Records.Where(r => r.Up).OrderBy(r => r.Depth))
                            Draw(c, r.CharacterId, m.Multiply(r.Matrix), ct.Concat(r.ColorTransform), 0, 0, depth + 1, maskMode);
                    break;
                case TagCodes.DefineText:
                case TagCodes.DefineText2:
                    if (_texts.TryGetValue(id, out var t)) DrawText(c, t, m, ct, maskMode);
                    break;
                case TagCodes.DefineEditText:
                    if (_texts.TryGetValue(id, out var et)) DrawEditText(c, et, m, ct);
                    break;
                case var code when TagCodes.IsImage(code):
                    if (_images.TryGetValue(id, out var img) && img.Bitmap is { } bmp)
                    {
                        c.Save();
                        c.Concat(ToSk(m));
                        c.Scale(20, 20);
                        using var paint = new SKPaint { ColorFilter = Filter(ct) };
                        c.DrawBitmap(bmp, 0, 0, paint);
                        c.Restore();
                    }
                    break;
            }
        }
        catch
        {
            // A broken character must not prevent the rest of the frame from rendering
        }
    }

    void DrawText(SKCanvas c, SwfText t, Matrix m, ColorTransform ct, bool maskMode)
    {
        var tm = m.Multiply(t.Matrix);
        foreach (var run in t.Runs)
        {
            if (!_doc.Fonts.TryGetValue(run.FontId, out var font)) continue;
            double scale = run.Height / (double)font.EmSquare;
            int x = run.X;
            foreach (var (g, adv) in run.Glyphs)
            {
                if (g < font.Glyphs.Count)
                {
                    var gm = tm.Multiply(new Matrix(scale, 0, 0, scale, x, run.Y));
                    var glyph = font.Glyphs[g];
                    glyph.Fills[0].Color = run.Color;
                    DrawPaths(c, ShapeParser.BuildPaths(glyph), gm, ct, maskMode);
                }
                x += adv;
            }
        }
    }

    void DrawEditText(SKCanvas c, SwfText t, Matrix m, ColorTransform ct)
    {
        string text = t.Html ? TextParser.StripHtml(t.InitialText) : t.InitialText;
        if (string.IsNullOrEmpty(text)) return;
        c.Save();
        c.Concat(ToSk(m));
        var col = ct.Apply(t.Color);
        using var font = new SKFont(SKTypeface.Default, Math.Max(20, t.FontHeight > 0 ? t.FontHeight : 240));
        using var paint = new SKPaint { Color = new SKColor(col.R, col.G, col.B, col.A), IsAntialias = true };
        float y = t.Bounds.YMin + font.Size;
        c.ClipRect(new SKRect(t.Bounds.XMin, t.Bounds.YMin, t.Bounds.XMax, t.Bounds.YMax));
        foreach (var line in text.Split('\n', '\r'))
        {
            c.DrawText(line, t.Bounds.XMin + 40, y, font, paint);
            y += font.Size * 1.15f;
        }
        c.Restore();
    }

    void DrawPaths(SKCanvas c, List<ShapePath> paths, Matrix m, ColorTransform ct, bool maskMode)
    {
        c.Save();
        c.Concat(ToSk(m));
        foreach (var p in paths)
        {
            using var path = ToPath(p.Contours);
            if (p.Fill != null)
            {
                path.FillType = SKPathFillType.EvenOdd;
                using var paint = FillPaint(p.Fill, ct, maskMode);
                c.DrawPath(path, paint);
            }
            else if (p.Line != null && !maskMode)
            {
                using var paint = p.Line.Fill != null ? FillPaint(p.Line.Fill, ct, false) : SolidPaint(p.Line.Color, ct);
                paint.Style = SKPaintStyle.Stroke;
                // Width in twips, at least one pixel on screen
                float scale = (float)Math.Sqrt(Math.Abs(c.TotalMatrix.ScaleX * c.TotalMatrix.ScaleY - c.TotalMatrix.SkewX * c.TotalMatrix.SkewY));
                paint.StrokeWidth = Math.Max(p.Line.Width, scale > 0 ? 1 / scale : 20);
                paint.StrokeCap = p.Line.StartCap switch { 1 => SKStrokeCap.Butt, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Round };
                paint.StrokeJoin = p.Line.Join switch { 1 => SKStrokeJoin.Bevel, 2 => SKStrokeJoin.Miter, _ => SKStrokeJoin.Round };
                paint.StrokeMiter = (float)p.Line.MiterLimit;
                c.DrawPath(path, paint);
            }
        }
        c.Restore();
    }

    static SKPath ToPath(List<List<Segment>> contours)
    {
        var path = new SKPath();
        foreach (var contour in contours)
        {
            if (contour.Count == 0) continue;
            path.MoveTo(contour[0].X0, contour[0].Y0);
            int lx = contour[0].X0, ly = contour[0].Y0;
            foreach (var s in contour)
            {
                if (s.X0 != lx || s.Y0 != ly) path.MoveTo(s.X0, s.Y0);
                if (s.Curved) path.QuadTo(s.Cx, s.Cy, s.X1, s.Y1);
                else path.LineTo(s.X1, s.Y1);
                lx = s.X1;
                ly = s.Y1;
            }
        }
        return path;
    }

    SKPaint SolidPaint(Rgba color, ColorTransform ct)
    {
        var c = ct.Apply(color);
        return new SKPaint { Color = new SKColor(c.R, c.G, c.B, c.A), IsAntialias = true };
    }

    SKPaint FillPaint(FillStyle f, ColorTransform ct, bool maskMode)
    {
        if (maskMode) return new SKPaint { Color = SKColors.Black, IsAntialias = true };
        switch (f.Kind)
        {
            case FillKind.Solid:
                return SolidPaint(f.Color, ct);
            case FillKind.Linear:
            case FillKind.Radial:
            case FillKind.Focal:
            {
                if (f.Stops.Count == 0) return SolidPaint(new Rgba(0, 0, 0, 0), ct);
                var colors = f.Stops.Select(s => { var c = ct.Apply(s.Color); return new SKColor(c.R, c.G, c.B, c.A); }).ToArray();
                var pos = f.Stops.Select(s => s.Ratio / 255f).ToArray();
                var tile = f.Spread switch { 1 => SKShaderTileMode.Mirror, 2 => SKShaderTileMode.Repeat, _ => SKShaderTileMode.Clamp };
                var local = ToSk(f.Matrix);
                SKShader shader = f.Kind switch
                {
                    FillKind.Linear => SKShader.CreateLinearGradient(new SKPoint(-16384, 0), new SKPoint(16384, 0), colors, pos, tile, local),
                    FillKind.Radial => SKShader.CreateRadialGradient(new SKPoint(0, 0), 16384, colors, pos, tile, local),
                    _ => SKShader.CreateTwoPointConicalGradient(new SKPoint((float)(f.FocalPoint * 16384), 0), 0, new SKPoint(0, 0), 16384, colors, pos, tile, local),
                };
                return new SKPaint { Shader = shader, IsAntialias = true };
            }
            case FillKind.Bitmap:
            {
                if (!_images.TryGetValue(f.BitmapId, out var img) || img.Bitmap is not { } bmp)
                    return new SKPaint { Color = new SKColor(255, 0, 0, 0) };
                var tile = f.Repeat ? SKShaderTileMode.Repeat : SKShaderTileMode.Clamp;
                var shader = SKShader.CreateBitmap(bmp, tile, tile, ToSk(f.Matrix));
                return new SKPaint { Shader = shader, IsAntialias = true, ColorFilter = Filter(ct) };
            }
        }
        return new SKPaint();
    }

    static SKColorFilter? Filter(ColorTransform ct)
    {
        if (ct.IsIdentity) return null;
        return SKColorFilter.CreateColorMatrix(
        [
            ct.RMul / 256f, 0, 0, 0, ct.RAdd / 255f,
            0, ct.GMul / 256f, 0, 0, ct.GAdd / 255f,
            0, 0, ct.BMul / 256f, 0, ct.BAdd / 255f,
            0, 0, 0, ct.AMul / 256f, ct.AAdd / 255f,
        ]);
    }

    static SKMatrix ToSk(Matrix m) => new((float)m.A, (float)m.C, (float)m.Tx, (float)m.B, (float)m.D, (float)m.Ty, 0, 0, 1);

    public static byte[] EncodePng(SKBitmap bmp) => ImageDecoder.EncodePng(bmp);
}
