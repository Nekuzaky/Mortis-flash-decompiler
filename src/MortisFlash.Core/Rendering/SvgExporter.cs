using System.Globalization;
using System.Text;
using MortisFlash.Assets;
using MortisFlash.Swf;

namespace MortisFlash.Rendering;

/// <summary>Vector export of a shape to SVG (coordinates in pixels, gradients and bitmap patterns included).</summary>
public static class SvgExporter
{
    static string N(double v) => (v / 20).ToString("0.##", CultureInfo.InvariantCulture);
    static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    public static string Export(SwfShape shape, IReadOnlyDictionary<int, SwfImage> images)
    {
        var b = shape.Bounds;
        var sb = new StringBuilder();
        var defs = new StringBuilder();
        int defId = 0;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{N(Math.Max(1, b.Width))}\" height=\"{N(Math.Max(1, b.Height))}\" viewBox=\"{N(b.XMin)} {N(b.YMin)} {N(Math.Max(1, b.Width))} {N(Math.Max(1, b.Height))}\">\n");
        var body = new StringBuilder();
        foreach (var p in ShapeParser.BuildPaths(shape))
        {
            string d = PathData(p.Contours);
            if (d.Length == 0) continue;
            if (p.Fill != null)
            {
                string fill = Paint(p.Fill, images, defs, ref defId, out string opacity);
                body.Append($"  <path fill-rule=\"{(shape.NonZeroWinding ? "nonzero" : "evenodd")}\" fill=\"{fill}\"{opacity} stroke=\"none\" d=\"{d}\"/>\n");
            }
            else if (p.Line != null)
            {
                string stroke = p.Line.Fill != null ? Paint(p.Line.Fill, images, defs, ref defId, out string op) : Color(p.Line.Color, out op);
                string cap = p.Line.StartCap switch { 1 => "butt", 2 => "square", _ => "round" };
                string join = p.Line.Join switch { 1 => "bevel", 2 => "miter", _ => "round" };
                string opAttr = op.Replace("fill-opacity", "stroke-opacity");
                body.Append($"  <path fill=\"none\" stroke=\"{stroke}\"{opAttr} stroke-width=\"{N(Math.Max(20, p.Line.Width))}\" stroke-linecap=\"{cap}\" stroke-linejoin=\"{join}\" d=\"{d}\"/>\n");
            }
        }
        if (defs.Length > 0) sb.Append("  <defs>\n").Append(defs).Append("  </defs>\n");
        sb.Append(body);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    static string PathData(List<List<Segment>> contours)
    {
        var sb = new StringBuilder();
        foreach (var c in contours)
        {
            if (c.Count == 0) continue;
            int lx = int.MinValue, ly = int.MinValue;
            foreach (var s in c)
            {
                if (s.X0 != lx || s.Y0 != ly) sb.Append($"M{N(s.X0)} {N(s.Y0)}");
                if (s.Curved) sb.Append($"Q{N(s.Cx)} {N(s.Cy)} {N(s.X1)} {N(s.Y1)}");
                else sb.Append($"L{N(s.X1)} {N(s.Y1)}");
                lx = s.X1;
                ly = s.Y1;
            }
        }
        return sb.ToString();
    }

    static string Color(Rgba c, out string opacity)
    {
        opacity = c.A < 255 ? $" fill-opacity=\"{F(c.A / 255.0)}\"" : "";
        return c.Hex;
    }

    static string Matrix(Matrix m) =>
        $"matrix({F(m.A / 20)} {F(m.B / 20)} {F(m.C / 20)} {F(m.D / 20)} {F(m.Tx / 20)} {F(m.Ty / 20)})";

    static string Paint(FillStyle f, IReadOnlyDictionary<int, SwfImage> images, StringBuilder defs, ref int defId, out string opacity)
    {
        opacity = "";
        switch (f.Kind)
        {
            case FillKind.Solid:
                return Color(f.Color, out opacity);
            case FillKind.Linear:
            case FillKind.Radial:
            case FillKind.Focal:
            {
                string id = $"g{defId++}";
                string spread = f.Spread switch { 1 => "reflect", 2 => "repeat", _ => "pad" };
                if (f.Kind == FillKind.Linear)
                    defs.Append($"    <linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"-16384\" y1=\"0\" x2=\"16384\" y2=\"0\" spreadMethod=\"{spread}\" gradientTransform=\"{Matrix(f.Matrix)}\">\n");
                else
                    defs.Append($"    <radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" cx=\"0\" cy=\"0\" r=\"16384\" fx=\"{F(f.FocalPoint * 16384)}\" fy=\"0\" spreadMethod=\"{spread}\" gradientTransform=\"{Matrix(f.Matrix)}\">\n");
                foreach (var (ratio, c) in f.Stops)
                    defs.Append($"      <stop offset=\"{F(ratio / 255.0)}\" stop-color=\"{c.Hex}\"{(c.A < 255 ? $" stop-opacity=\"{F(c.A / 255.0)}\"" : "")}/>\n");
                defs.Append(f.Kind == FillKind.Linear ? "    </linearGradient>\n" : "    </radialGradient>\n");
                return $"url(#{id})";
            }
            case FillKind.Bitmap:
            {
                if (!images.TryGetValue(f.BitmapId, out var img) || img.Bitmap == null) return "none";
                string id = $"p{defId++}";
                string mime = img.Extension == ".jpg" ? "image/jpeg" : img.Extension == ".gif" ? "image/gif" : "image/png";
                defs.Append($"    <pattern id=\"{id}\" patternUnits=\"userSpaceOnUse\" width=\"{img.Width}\" height=\"{img.Height}\" patternTransform=\"{Matrix(f.Matrix)}\">\n");
                defs.Append($"      <image width=\"{img.Width}\" height=\"{img.Height}\" xlink:href=\"data:{mime};base64,{Convert.ToBase64String(img.FileBytes)}\"/>\n");
                defs.Append("    </pattern>\n");
                return $"url(#{id})";
            }
        }
        return "none";
    }
}
