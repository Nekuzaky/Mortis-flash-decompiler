using MortisFlash;
using MortisFlash.Export;
using MortisFlash.Rendering;

// mflash: command-line SWF decompiler (same engine as the MortisFlash application).

static int Usage()
{
    Console.WriteLine("""
        mflash — SWF decompiler (MortisFlash)

          mflash info    <file.swf>                       show a summary of the file
          mflash scripts <file.swf> [filter] [--pcode]    print the decompiled scripts
          mflash export  <file.swf> [folder] [parts]      export everything (or only the given parts)
          mflash frame   <file.swf> <n> <output.png>      render frame n of the timeline to PNG
          mflash sheet   <file.swf> <output.png>          render a contact sheet of the frames

        parts: scripts,pcode,images,sounds,shapes,sprites,fonts,texts,binary,videos,frames
        Flash projectors (.exe) are also accepted.
        """);
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
if (args.Length < 2) return Usage();

SwfDocument doc;
try
{
    doc = SwfDocument.Load(args[1]);
}
catch (Exception e)
{
    Console.Error.WriteLine("Cannot open file: " + e.Message);
    return 2;
}

switch (args[0])
{
    case "info":
        Console.Write(SwfExporter.Info(doc));
        break;

    case "scripts":
    {
        string? filter = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
        bool pcode = args.Contains("--pcode");
        foreach (var s in doc.Scripts)
        {
            if (filter != null && !s.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine("==== " + s.Path);
            Console.WriteLine(pcode ? s.PCode : s.Source);
        }
        break;
    }

    case "export":
    {
        string folder = args.Length > 2 && !args[2].Contains(',') && !IsPart(args[2])
            ? args[2]
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, Path.GetFileNameWithoutExtension(args[1]) + "_export");
        var parts = ExportParts.None;
        foreach (var a in args.Skip(2).Where(a => a.Contains(',') || IsPart(a)))
            foreach (var p in a.Split(','))
                parts |= Part(p);
        if (parts == ExportParts.None) parts = ExportParts.All;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string last = "";
        var progress = new Progress<ExportProgress>(p =>
        {
            if (p.Step == last) return;
            last = p.Step;
            Console.WriteLine($"  {p.Step}…");
        });
        var report = SwfExporter.Export(doc, folder, parts, progress);
        Console.WriteLine($"{report.Files} files exported to {folder} ({sw.Elapsed.TotalSeconds:0.0} s)");
        foreach (var e in report.Errors.Take(20)) Console.WriteLine("  ! " + e);
        break;
    }

    case "frame":
    {
        if (args.Length < 4) return Usage();
        int n = int.Parse(args[2]);
        using var bmp = new SwfRenderer(doc).RenderFrame(Math.Max(0, n - 1));
        File.WriteAllBytes(args[3], SwfRenderer.EncodePng(bmp));
        Console.WriteLine($"Frame {n} → {args[3]} ({bmp.Width}×{bmp.Height})");
        break;
    }

    case "sheet":
    {
        // Contact sheet: every frame of the timeline as a thumbnail
        if (args.Length < 3) return Usage();
        var r = new SwfRenderer(doc);
        int count = Math.Min(doc.Swf.FrameCount, 120), cols = 6;
        double scale = 0.35;
        using var first = r.RenderFrame(0, scale);
        int cw = first.Width + 8, ch = first.Height + 22;
        using var sheet = new SkiaSharp.SKBitmap(cols * cw, (count + cols - 1) / cols * ch);
        using (var c = new SkiaSharp.SKCanvas(sheet))
        {
            c.Clear(new SkiaSharp.SKColor(40, 40, 40));
            using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 12);
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White };
            for (int i = 0; i < count; i++)
            {
                using var f = r.RenderFrame(i, scale);
                c.DrawBitmap(f, i % cols * cw + 4, i / cols * ch + 4);
                c.DrawText($"{i + 1}", i % cols * cw + 6, i / cols * ch + ch - 4, font, paint);
            }
        }
        File.WriteAllBytes(args[2], SwfRenderer.EncodePng(sheet));
        Console.WriteLine($"{count} frames → {args[2]}");
        break;
    }

    case "compile-as2":
    {
        // Hidden diagnostic: mflash compile-as2 <file.swf> <script path filter> — shows source, generated p-code and the result decompiled again
        var script = doc.Scripts.First(s => s.As2 != null && s.Path.Contains(args[2]));
        string pcode = MortisFlash.Compiler.Avm1Compiler.CompileToPCode(script.Source, doc.Swf.Version);
        Console.WriteLine("---- source\n" + script.Source + "\n---- p-code\n" + pcode);
        var bytes = MortisFlash.Avm1.Avm1Assembler.Assemble(pcode);
        foreach (var st in MortisFlash.Avm1.Avm1Decompiler.Decompile(bytes, doc.Swf.Version)) Console.Write(st.ToString());
        break;
    }

    case "selftest":
        SelfTest.Run(doc);
        SelfTest.SourceRoundTrip(doc);
        SelfTest.As3SourceRoundTrip(doc);
        break;

    default:
        return Usage();
}
foreach (var e in doc.Errors) Console.Error.WriteLine("! " + e);
return 0;

static bool IsPart(string s) => s.Split(',').All(p => Part(p) != ExportParts.None);

static ExportParts Part(string p) => p.Trim().ToLowerInvariant() switch
{
    "scripts" => ExportParts.Scripts,
    "pcode" => ExportParts.PCode | ExportParts.Scripts,
    "images" => ExportParts.Images,
    "sounds" => ExportParts.Sounds,
    "shapes" => ExportParts.Shapes,
    "sprites" => ExportParts.Sprites,
    "fonts" => ExportParts.Fonts,
    "texts" => ExportParts.Texts,
    "binary" => ExportParts.BinaryData,
    "videos" => ExportParts.Videos,
    "frames" => ExportParts.Frames,
    "all" => ExportParts.All,
    _ => ExportParts.None,
};
