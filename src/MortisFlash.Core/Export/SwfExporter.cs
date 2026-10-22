using System.Text;
using MortisFlash.Assets;
using MortisFlash.Rendering;

namespace MortisFlash.Export;

[Flags]
public enum ExportParts
{
    None = 0,
    Scripts = 1, PCode = 2, Images = 4, Sounds = 8, Shapes = 16, Sprites = 32, Fonts = 64, Texts = 128,
    BinaryData = 256, Videos = 512, Frames = 1024,
    All = Scripts | Images | Sounds | Shapes | Sprites | Fonts | Texts | BinaryData | Videos | Frames,
}

public sealed record ExportProgress(string Step, int Done, int Total);

public sealed class ExportReport
{
    public int Files;
    public List<string> Errors = [];
}

/// <summary>Exports the contents of a SWF to a folder, organized by category.</summary>
public static class SwfExporter
{
    public static ExportReport Export(SwfDocument doc, string folder, ExportParts parts = ExportParts.All,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default, int maxFrames = 500)
    {
        var report = new ExportReport();
        Directory.CreateDirectory(folder);
        var renderer = new SwfRenderer(doc);
        var images = doc.Images.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());

        void Save(string relPath, byte[] data)
        {
            var full = Path.Combine(folder, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, data);
            report.Files++;
        }
        void SaveText(string relPath, string text) => Save(relPath, Encoding.UTF8.GetBytes(text));
        void Step<T>(string name, IReadOnlyList<T> items, Action<T> action)
        {
            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ExportProgress(name, i, items.Count));
                try { action(items[i]); }
                catch (Exception e) { report.Errors.Add($"{name}: {e.Message}"); }
            }
        }
        string Named(int id, string ext) => doc.NameOf(id) is { Length: > 0 } n ? $"{id}_{SwfDocument.FileSafe(n)}{ext}" : $"{id}{ext}";

        SaveText("info.txt", Info(doc));

        if (parts.HasFlag(ExportParts.Scripts))
            Step("Scripts", doc.Scripts, s =>
            {
                string rel = s.Path.EndsWith(".as") ? s.Path : s.Path + ".as";
                SaveText(rel, s.Source);
                if (parts.HasFlag(ExportParts.PCode)) SaveText("pcode/" + rel[..^3] + ".pcode", s.PCode);
            });
        if (parts.HasFlag(ExportParts.Images))
            Step("Images", doc.Images, img => Save("images/" + Named(img.Id, img.Extension), img.FileBytes));
        if (parts.HasFlag(ExportParts.Sounds))
            Step("Sounds", doc.Sounds, s => Save("sounds/" + (s.Id >= 0 ? Named(s.Id, s.Extension) : SwfDocument.FileSafe(s.Name) + s.Extension), s.FileBytes));
        if (parts.HasFlag(ExportParts.Shapes))
        {
            Step("Shapes", doc.Shapes, s => SaveText("shapes/" + Named(s.Id, ".svg"), SvgExporter.Export(s.Shape, images)));
            Step("Morph shapes", doc.MorphShapes, s =>
            {
                SaveText("morphshapes/" + Named(s.Id, "_start.svg"), SvgExporter.Export(s.Morph!.Start, images));
                SaveText("morphshapes/" + Named(s.Id, "_end.svg"), SvgExporter.Export(s.Morph!.Interpolate(1), images));
            });
        }
        if (parts.HasFlag(ExportParts.Sprites))
            Step("Sprites", doc.Sprites, s =>
            {
                using var bmp = renderer.RenderCharacter(s.Id, 1024);
                if (bmp != null) Save("sprites/" + Named(s.Id, ".png"), SwfRenderer.EncodePng(bmp));
            });
        if (parts.HasFlag(ExportParts.Fonts))
            Step("Fonts", doc.Fonts.Values.ToList(), f =>
            {
                string baseName = Named(f.Id, "") + (f.Name.Length > 0 ? "_" + SwfDocument.FileSafe(f.Name) : "");
                if (f.FontData != null) Save($"fonts/{baseName}.otf", f.FontData);
                if (f.Glyphs.Count > 0) SaveText($"fonts/{baseName}.svg", FontSheet(f));
                SaveText($"fonts/{baseName}.txt", $"Name: {f.Name}\nBold: {f.Bold}\nItalic: {f.Italic}\nGlyphs: {f.Glyphs.Count}\nCharacters: {f.Charset}\n");
            });
        if (parts.HasFlag(ExportParts.Texts))
            Step("Texts", doc.Texts, t =>
            {
                var text = t.ToPlainText(doc.Fonts);
                if (t.IsEditText && t.Html) SaveText("texts/" + Named(t.Id, ".html"), t.InitialText);
                SaveText("texts/" + Named(t.Id, ".txt"), (t.IsEditText && t.Html ? TextParser.StripHtml(text) : text)
                    + (t.VariableName.Length > 0 ? $"\n\n[variable: {t.VariableName}]" : ""));
            });
        if (parts.HasFlag(ExportParts.BinaryData))
            Step("Binary data", doc.BinaryData, b => Save("binaryData/" + Named(b.Id, b.Extension), b.Data));
        if (parts.HasFlag(ExportParts.Videos))
            Step("Videos", doc.Videos, v => Save("videos/" + Named(v.Id, ".flv"), v.Flv));
        if (parts.HasFlag(ExportParts.Frames))
        {
            var frames = Enumerable.Range(0, Math.Min(doc.Swf.FrameCount, maxFrames)).ToList();
            Step("Timeline frames", frames, f =>
            {
                using var bmp = renderer.RenderFrame(f);
                Save($"frames/{f + 1:D4}.png", SwfRenderer.EncodePng(bmp));
            });
        }
        if (report.Errors.Count > 0 || doc.Errors.Count > 0)
            SaveText("errors.txt", string.Join('\n', doc.Errors.Concat(report.Errors)) + "\n");
        progress?.Report(new ExportProgress("Done", 1, 1));
        return report;
    }

    public static string Info(SwfDocument doc)
    {
        var s = doc.Swf;
        var sb = new StringBuilder();
        sb.AppendLine($"File: {doc.FileName}");
        sb.AppendLine($"SWF version: {s.Version}");
        sb.AppendLine($"Compression: {s.Compression}");
        sb.AppendLine($"Uncompressed size: {s.Uncompressed.Length:N0} bytes");
        sb.AppendLine($"Stage: {s.FrameSize.Width / 20.0} × {s.FrameSize.Height / 20.0} px");
        sb.AppendLine($"Frame rate: {s.FrameRate:0.##} fps");
        sb.AppendLine($"Frames: {s.FrameCount}");
        sb.AppendLine($"ActionScript: {(s.IsAs3 ? "3 (AVM2)" : "1/2 (AVM1)")}");
        sb.AppendLine($"Background: {doc.Background}");
        sb.AppendLine();
        sb.AppendLine($"Scripts: {doc.Scripts.Count}");
        sb.AppendLine($"Images: {doc.Images.Count}");
        sb.AppendLine($"Sounds: {doc.Sounds.Count}");
        sb.AppendLine($"Shapes: {doc.Shapes.Count} (+ {doc.MorphShapes.Count} morph)");
        sb.AppendLine($"Sprites: {doc.Sprites.Count}");
        sb.AppendLine($"Buttons: {doc.Buttons.Count}");
        sb.AppendLine($"Fonts: {doc.Fonts.Count}");
        sb.AppendLine($"Texts: {doc.Texts.Count}");
        sb.AppendLine($"Binary data: {doc.BinaryData.Count}");
        sb.AppendLine($"Videos: {doc.Videos.Count}");
        if (doc.FrameLabels.Count > 0) sb.AppendLine($"Frame labels: {string.Join(", ", doc.FrameLabels)}");
        sb.AppendLine();
        sb.AppendLine("Tags:");
        foreach (var g in s.AllTags().GroupBy(t => t.Name).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Key,-28} {g.Count(),6}");
        return sb.ToString();
    }

    /// <summary>SVG sheet of all glyphs in a font, each labeled with its character.</summary>
    static string FontSheet(SwfFont f)
    {
        const int cell = 64, cols = 16;
        int rows = (f.Glyphs.Count + cols - 1) / cols;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{cols * cell}\" height=\"{rows * (cell + 16)}\">\n");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");
        double scale = 40.0 / f.EmSquare;
        for (int i = 0; i < f.Glyphs.Count; i++)
        {
            int cx = i % cols * cell, cy = i / cols * (cell + 16);
            var paths = ShapeParser.BuildPaths(f.Glyphs[i]);
            var d = new StringBuilder();
            foreach (var p in paths)
                foreach (var c in p.Contours)
                {
                    int lx = int.MinValue, ly = int.MinValue;
                    foreach (var s in c)
                    {
                        if (s.X0 != lx || s.Y0 != ly) d.Append(System.FormattableString.Invariant($"M{s.X0 * scale:0.##} {s.Y0 * scale:0.##}"));
                        d.Append(s.Curved
                            ? System.FormattableString.Invariant($"Q{s.Cx * scale:0.##} {s.Cy * scale:0.##} {s.X1 * scale:0.##} {s.Y1 * scale:0.##}")
                            : System.FormattableString.Invariant($"L{s.X1 * scale:0.##} {s.Y1 * scale:0.##}"));
                        lx = s.X1;
                        ly = s.Y1;
                    }
                }
            sb.Append($"<g transform=\"translate({cx + 12},{cy + 50})\"><path fill-rule=\"evenodd\" d=\"{d}\"/></g>\n");
            int code = i < f.Codes.Count ? f.Codes[i] : 0;
            string label = code > 32 ? System.Security.SecurityElement.Escape(char.ConvertFromUtf32(Math.Min(code, 0x10FFFF))) : $"#{code}";
            sb.Append($"<text x=\"{cx + 4}\" y=\"{cy + cell + 12}\" font-size=\"10\" fill=\"#888\">{label}</text>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}
