using MortisFlash;
using MortisFlash.Avm1;
using MortisFlash.Avm2;
using MortisFlash.Swf;

/// <summary>Round-trip checks for SWF, ABC, AS3 p-code and AS2 p-code (hidden “selftest” command).</summary>
static class SelfTest
{
    public static void Run(SwfDocument doc)
    {
        // SWF: write, then re-read; tags must be identical
        var bytes = SwfWriter.Write(doc.Swf);
        var again = SwfFile.Parse(bytes);
        var a = doc.Swf.AllTags().ToList();
        var b = again.AllTags().ToList();
        // Sprites are compared through their child tags (a tag's short/long header form may change)
        static bool Same(SwfTag x, SwfTag y) => x.Code == y.Code && (x.Code == TagCodes.DefineSprite || x.Data.AsSpan().SequenceEqual(y.Data));
        int tagDiff = a.Count != b.Count ? b.Count - a.Count : a.Zip(b).Count(p => !Same(p.First, p.Second));
        Console.WriteLine($"SWF: {a.Count} tags, {tagDiff} differences");
        if (tagDiff > 0)
            foreach (var (x, y) in a.Zip(b).Where(p => !Same(p.First, p.Second)).Take(3))
                Console.WriteLine($"  ! {x.Name} {x.Data.Length} → {y.Data.Length} bytes, original tail: {Convert.ToHexString(x.Data[Math.Max(0, x.Data.Length - 6)..])}");

        foreach (var abc in doc.AbcFiles)
        {
            var orig = AbcPayload(abc.SourceTag!, doc.Swf.Version);
            var written = AbcWriter.Write(abc);
            int firstDiff = Enumerable.Range(0, Math.Min(orig.Length, written.Length)).FirstOrDefault(i => orig[i] != written[i], -1);
            Console.WriteLine($"ABC {abc.Name}: {orig.Length} → {written.Length} bytes{(orig.AsSpan().SequenceEqual(written) ? ", identical" : $", first difference at {firstDiff}")}");
            var reparsed = AbcFile.Parse(written);
            if (reparsed.Methods.Count != abc.Methods.Count || reparsed.Bodies.Count != abc.Bodies.Count) Console.WriteLine("  ! inconsistent result after re-parsing");

            var names = new NameContext(abc, []);
            int ok = 0, bad = 0;
            foreach (var m in abc.Methods.Where(m => m.Body != null))
            {
                var body = m.Body!;
                var (code, exc, ms, ls, sd) = (body.Code, body.Exceptions, body.MaxStack, body.LocalCount, body.MaxScopeDepth);
                string text = As3Printer.PCodeOf(abc, names, m);
                try
                {
                    Avm2Assembler.Replace(abc, m, text);
                    string text2 = As3Printer.PCodeOf(abc, names, m);
                    if (text2 == text && body.MaxStack >= ms) ok++;
                    else
                    {
                        if (bad++ < 3)
                        {
                            var l1 = text.Split('\n');
                            var l2 = text2.Split('\n');
                            int k = Enumerable.Range(0, Math.Min(l1.Length, l2.Length)).FirstOrDefault(i => l1[i] != l2[i], 0);
                            Console.WriteLine($"  ! method {m.Index}: “{l1.ElementAtOrDefault(k)?.Trim()}” ≠ “{l2.ElementAtOrDefault(k)?.Trim()}”");
                        }
                    }
                }
                catch (Exception e)
                {
                    if (bad++ < 3) Console.WriteLine($"  ! method {m.Index}: {e.Message}");
                }
                (body.Code, body.Exceptions, body.MaxStack, body.LocalCount, body.MaxScopeDepth) = (code, exc, ms, ls, sd);
            }
            Console.WriteLine($"  AS3 p-code: {ok} methods identical after reassembly, {bad} failed");
        }

        int ok1 = 0, bad1 = 0;
        foreach (var t in doc.Swf.AllTags().Where(t => t.Code is TagCodes.DoAction or TagCodes.DoInitAction))
        {
            var code = t.Code == TagCodes.DoInitAction ? t.Data[2..] : t.Data;
            string text = Avm1Parser.Disassemble(Avm1Parser.Parse(code, doc.Swf.Version));
            try
            {
                var re = Avm1Assembler.Assemble(text);
                string text2 = Avm1Parser.Disassemble(Avm1Parser.Parse(re, doc.Swf.Version));
                if (Normalize(text2) == Normalize(text)) ok1++;
                else if (bad1++ < 3)
                {
                    var l1 = Normalize(text).Split('\n');
                    var l2 = Normalize(text2).Split('\n');
                    int i = Enumerable.Range(0, Math.Min(l1.Length, l2.Length)).FirstOrDefault(k => l1[k] != l2[k], Math.Min(l1.Length, l2.Length));
                    Console.WriteLine($"  ! AS2: line {i} “{(i < l1.Length ? l1[i].Trim() : "")}” ≠ “{(i < l2.Length ? l2[i].Trim() : "")}”");
                }
            }
            catch (Exception e)
            {
                if (bad1++ < 3) Console.WriteLine("  ! AS2: " + e.Message);
            }
        }
        if (ok1 + bad1 > 0) Console.WriteLine($"AS2 p-code: {ok1} blocks identical after reassembly, {bad1} failed");
    }

    // Addresses (loc…) shift when the encoding of pushed values changes, so they are stripped before comparing
    /// <summary>
    /// AS2 source round trip: decompile, compile, decompile again and compare. Scripts are rebuilt in place so
    /// the second decompilation sees the compiled bytes.
    /// </summary>
    public static void SourceRoundTrip(SwfDocument doc)
    {
        int ok = 0, differ = 0, failed = 0;
        foreach (var s in doc.Scripts.Where(s => s.As2 != null))
        {
            string src = s.Source;
            try
            {
                var code = MortisFlash.Compiler.Avm1Compiler.Compile(src, doc.Swf.Version);
                var stmts = Avm1Decompiler.Decompile(code, doc.Swf.Version);
                if (s.Path.StartsWith("scripts/__Packages/") && As2ClassBuilder.TryBuild(stmts) is { } cls) stmts = cls;
                var w = new MortisFlash.Decompiler.CodeWriter();
                string header = src.StartsWith("on(") || src.StartsWith("onClipEvent(") ? src[..src.IndexOf('\n')] : "";
                if (header.Length > 0)
                {
                    w.Text(header);
                    w.Block(stmts);
                }
                else foreach (var st in stmts) st.Write(w);
                string again = w.ToString();
                if (Squash(again) == Squash(src)) ok++;
                else
                {
                    differ++;
                    if (differ <= 4)
                    {
                        var a = src.Split('\n');
                        var b = again.Split('\n');
                        int k = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i].Trim() != b[i].Trim(), Math.Min(a.Length, b.Length));
                        Console.WriteLine($"  ~ {s.Path} line {k + 1}: “{a.ElementAtOrDefault(k)?.Trim()}” → “{b.ElementAtOrDefault(k)?.Trim()}”");
                    }
                }
            }
            catch (Exception e)
            {
                failed++;
                if (failed <= 6) Console.WriteLine($"  ! {s.Path}: {e.Message.Split('\n')[0]}");
            }
        }
        Console.WriteLine($"AS2 source round trip: {ok} identical, {differ} different, {failed} failed to compile");
    }

    /// <summary>AS3 source round trip, method by method: decompile, compile, decompile again and compare.</summary>
    public static void As3SourceRoundTrip(SwfDocument doc)
    {
        int ok = 0, differ = 0, failed = 0;
        foreach (var s in doc.Scripts.Where(s => s.As3 != null))
        {
            var unit = s.As3!;
            foreach (var (title, method, owner) in MortisFlash.Avm2.As3Printer.Methods(unit))
            {
                string src;
                try { src = MortisFlash.Avm2.As3Printer.MethodBodySource(unit, method, owner); }
                catch { continue; }
                var body = method.Body!;
                var saved = (body.Code, body.Exceptions, body.MaxStack, body.LocalCount, body.MaxScopeDepth);
                try
                {
                    MortisFlash.Compiler.Avm2Compiler.CompileMethodBody(unit.Abc, method, owner, src);
                    string again = MortisFlash.Avm2.As3Printer.MethodBodySource(unit, method, owner);
                    if (Squash(again) == Squash(src)) ok++;
                    else
                    {
                        differ++;
                        if (differ <= 5)
                        {
                            var a = src.Split('\n');
                            var b = again.Split('\n');
                            int k = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i].Trim() != b[i].Trim(), Math.Min(a.Length, b.Length));
                            Console.WriteLine($"  ~ {unit.FullName}.{title} line {k + 1}: “{a.ElementAtOrDefault(k)?.Trim()}” → “{b.ElementAtOrDefault(k)?.Trim()}”");
                        }
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    if (failed <= 8) Console.WriteLine($"  ! {unit.FullName}.{title}: {e.Message.Split('\n')[0]}");
                }
                (body.Code, body.Exceptions, body.MaxStack, body.LocalCount, body.MaxScopeDepth) = saved;
            }
        }
        Console.WriteLine($"AS3 source round trip: {ok} identical, {differ} different, {failed} failed to compile");
    }

    static string Squash(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    static string Normalize(string s) => System.Text.RegularExpressions.Regex.Replace(s, "loc[0-9A-F]+", "loc");

    static byte[] AbcPayload(SwfTag t, int version)
    {
        var r = t.Reader(version);
        if (t.Code == TagCodes.DoABC2)
        {
            r.U32();
            r.String();
        }
        return r.Rest();
    }
}
