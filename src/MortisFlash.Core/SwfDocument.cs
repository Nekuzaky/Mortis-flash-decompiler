using MortisFlash.Assets;
using MortisFlash.Avm1;
using MortisFlash.Avm2;
using MortisFlash.Decompiler;
using MortisFlash.Swf;

namespace MortisFlash;

public enum ScriptKind { As2, As3 }

/// <summary>A decompilable script: an AS3 class, or an AS2 action block (frame, clip, button, AS2 class).</summary>
public sealed class ScriptItem
{
    public required string Path { get; init; }
    public required ScriptKind Kind { get; init; }
    readonly Func<CodeWriter> _source;
    readonly Func<string> _pcode;
    CodeWriter? _code;
    string? _pcodeText;

    public ScriptItem(Func<CodeWriter> source, Func<string> pcode)
    {
        _source = source;
        _pcode = pcode;
    }

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>AS2: where the actions are located (used to replace them after the p-code is edited).</summary>
    public As2Location? As2 { get; init; }
    /// <summary>AS3: the decompiled unit (class).</summary>
    public As3Unit? As3 { get; init; }
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";

    public CodeWriter Code
    {
        get
        {
            if (_code != null) return _code;
            try { _code = _source(); }
            catch (Exception e)
            {
                _code = new CodeWriter();
                _code.Comment("// Decompilation failed: " + e.Message);
                _code.Line();
            }
            return _code;
        }
    }

    public string Source => Code.ToString();
    public string PCode => _pcodeText ??= SafePCode();

    string SafePCode()
    {
        try { return _pcode(); }
        catch (Exception e) { return "// Unreadable p-code: " + e.Message; }
    }

    public bool IsDecompiled => _code != null;
}

/// <summary>AS2 actions within a tag: the [Offset, Offset + Length) range and an optional size field.</summary>
public sealed record As2Location(SwfTag Tag, int Offset, int Length, int SizeField = -1, int SizeBytes = 0, int SizeExtra = 0);

public sealed class ShapeItem
{
    public required int Id { get; init; }
    public required SwfTag Tag { get; init; }
    SwfShape? _shape;
    public SwfShape Shape => _shape ??= TagCodes.IsMorph(Tag.Code) ? SwfMorphShape.Parse(Tag).Start : ShapeParser.ParseDefineShape(Tag);
    public SwfMorphShape? Morph => TagCodes.IsMorph(Tag.Code) ? _morph ??= SwfMorphShape.Parse(Tag) : null;
    SwfMorphShape? _morph;
}

public sealed class SpriteItem
{
    public required int Id { get; init; }
    public required SwfTag Tag { get; init; }
    public int FrameCount { get; init; }
    public List<SwfTag> Children => Tag.Children ?? [];
}

public sealed class BinaryItem
{
    public required int Id { get; init; }
    public required byte[] Data { get; init; }
    public required string Extension { get; init; }
    public SwfTag? Tag { get; init; }
    /// <summary>Nested SWF (loaders, packed games): can itself be opened.</summary>
    public bool IsSwf => Extension == ".swf";
}

/// <summary>
/// Overview of a SWF: everything that can be displayed, decompiled or exported, along with symbol names.
/// Expensive decoding (images, scripts) is performed on demand.
/// </summary>
public sealed class SwfDocument
{
    public SwfFile Swf { get; }
    public string FilePath { get; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public Dictionary<int, string> SymbolNames { get; } = [];
    public Dictionary<int, SwfTag> Characters { get; } = [];
    public List<SwfImage> Images { get; } = [];
    public List<SwfSound> Sounds { get; } = [];
    public List<ShapeItem> Shapes { get; } = [];
    public List<ShapeItem> MorphShapes { get; } = [];
    public List<SpriteItem> Sprites { get; } = [];
    public List<SwfButton> Buttons { get; } = [];
    public Dictionary<int, SwfFont> Fonts { get; } = [];
    public List<SwfText> Texts { get; } = [];
    public List<BinaryItem> BinaryData { get; } = [];
    public List<SwfVideo> Videos { get; } = [];
    public List<ScriptItem> Scripts { get; } = [];
    public List<AbcFile> AbcFiles { get; } = [];
    public List<string> FrameLabels { get; } = [];
    public Rgba Background { get; private set; } = new(255, 255, 255, 255);
    public List<string> Errors { get; } = [];
    /// <summary>The document comes from a modified (unsaved) version of the file.</summary>
    public bool IsModified { get; init; }

    public SwfDocument(SwfFile swf, string path)
    {
        Swf = swf;
        FilePath = path;
        Errors.AddRange(swf.Warnings);
        Scan();
    }

    public static SwfDocument Load(string path) => new(SwfFile.Load(path), path);
    public static SwfDocument FromBytes(byte[] data, string name) => new(SwfFile.Parse(SwfFile.ExtractFromContainer(data)), name);

    public string NameOf(int id) => SymbolNames.TryGetValue(id, out var n) ? n : "";

    public string Label(string kind, int id)
    {
        var n = NameOf(id);
        return n.Length > 0 ? $"{kind} {id} — {n}" : $"{kind} {id}";
    }

    void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Errors.Add($"{what}: {e.Message}"); }
    }

    void Scan()
    {
        int v = Swf.Version;
        byte[]? jpegTables = null;

        // Symbol names (ExportAssets, SymbolClass) and definitions
        foreach (var t in Swf.AllTags())
        {
            if (t.Code is TagCodes.ExportAssets or TagCodes.SymbolClass)
                Try(t.Name, () =>
                {
                    var r = t.Reader(v);
                    int n = r.U16();
                    for (int i = 0; i < n && r.Remaining > 0; i++)
                    {
                        int id = r.U16();
                        string name = r.String();
                        if (id != 0 || t.Code == TagCodes.ExportAssets) SymbolNames.TryAdd(id, name);
                    }
                });
            if (t.Code == TagCodes.JPEGTables) jpegTables = t.Data;
            if (t.Code == TagCodes.SetBackgroundColor && t.Data.Length >= 3) Background = new Rgba(t.Data[0], t.Data[1], t.Data[2], 255);
            if (t.Code == TagCodes.FrameLabel && t.Parent == null) Try(t.Name, () => FrameLabels.Add(t.Reader(v).String()));
            if (TagCodes.DefinesCharacter(t.Code) && t.CharacterId >= 0) Characters.TryAdd(t.CharacterId, t);
        }

        foreach (var t in Swf.AllTags())
        {
            int id = t.CharacterId;
            switch (t.Code)
            {
                case var c when TagCodes.IsImage(c):
                    Try($"{t.Name} {id}", () => { if (ImageDecoder.Decode(t, jpegTables) is { } img) Images.Add(img); });
                    break;
                case TagCodes.DefineSound:
                    Try($"{t.Name} {id}", () => { if (SoundDecoder.DecodeDefineSound(t) is { } s) Sounds.Add(s); });
                    break;
                case var c when TagCodes.IsShape(c):
                    Shapes.Add(new ShapeItem { Id = id, Tag = t });
                    break;
                case var c when TagCodes.IsMorph(c):
                    MorphShapes.Add(new ShapeItem { Id = id, Tag = t });
                    break;
                case TagCodes.DefineSprite:
                    Try($"Sprite {id}", () =>
                    {
                        int frames = t.Data.Length >= 4 ? t.Data[2] | t.Data[3] << 8 : 0;
                        Sprites.Add(new SpriteItem { Id = id, Tag = t, FrameCount = frames });
                    });
                    break;
                case TagCodes.DefineButton:
                case TagCodes.DefineButton2:
                    Try($"Button {id}", () => Buttons.Add(SwfButton.Parse(t, v)));
                    break;
                case var c when TagCodes.IsFont(c):
                    Try($"Font {id}", () => Fonts[id] = FontParser.Parse(t, v));
                    break;
                case var c when TagCodes.IsText(c):
                    Try($"Text {id}", () => Texts.Add(TextParser.Parse(t, v)));
                    break;
                case TagCodes.DefineBinaryData:
                    Try($"Binary data {id}", () =>
                    {
                        var data = t.Data.Length > 6 ? t.Data[6..] : [];
                        BinaryData.Add(new BinaryItem { Id = id, Data = data, Extension = DetectExtension(data), Tag = t });
                    });
                    break;
                case TagCodes.DefineVideoStream:
                    Try($"Video {id}", () => { if (VideoExtractor.Extract(t, Swf.AllTags(), Swf.FrameRate) is { } vid) Videos.Add(vid); });
                    break;
            }
        }
        foreach (var t in Swf.AllTags())
            if (t.Code is TagCodes.DefineFontInfo or TagCodes.DefineFontInfo2 && t.Data.Length >= 2)
            {
                int fid = t.Data[0] | t.Data[1] << 8;
                if (Fonts.TryGetValue(fid, out var f)) Try("FontInfo", () => FontParser.ApplyInfo(f, t, v));
            }

        // Streaming sounds of the main timeline and of sprites
        Try("Stream sound", () => CollectStreams(Swf.Tags, "main timeline"));
        foreach (var sp in Sprites) Try($"Stream sound of sprite {sp.Id}", () => CollectStreams(sp.Children, Label("sprite", sp.Id)));

        ScanScripts();
    }

    void CollectStreams(List<SwfTag> tags, string owner)
    {
        var head = tags.FirstOrDefault(t => t.Code is TagCodes.SoundStreamHead or TagCodes.SoundStreamHead2);
        if (head == null) return;
        var blocks = tags.Where(t => t.Code == TagCodes.SoundStreamBlock).ToList();
        if (blocks.Count == 0) return;
        if (SoundDecoder.DecodeStream(head, blocks, $"stream — {owner}") is { } s) Sounds.Add(s);
    }

    public static string DetectExtension(byte[] d)
    {
        if (SwfFile.LooksLikeSwf(d)) return ".swf";
        if (ImageDecoder.IsPng(d)) return ".png";
        if (ImageDecoder.IsGif(d)) return ".gif";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8) return ".jpg";
        if (d.Length > 4 && d[0] == 'P' && d[1] == 'K' && d[2] == 3 && d[3] == 4) return ".zip";
        if (d.Length > 3 && (d[0] == 'I' && d[1] == 'D' && d[2] == '3' || d[0] == 0xFF && (d[1] & 0xE0) == 0xE0)) return ".mp3";
        if (d.Length > 4 && d[0] == 'O' && d[1] == 'g' && d[2] == 'g') return ".ogg";
        if (d.Length > 4 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F') return ".wav";
        if (d.Length > 3 && d[0] == 'F' && d[1] == 'L' && d[2] == 'V') return ".flv";
        if (d.Length > 2 && d[0] == 0x0A && d[1] <= 0x10) return ".amf";
        // Text (XML, JSON, CSV, etc.): printable characters only at the start
        int n = Math.Min(d.Length, 512);
        if (n > 0 && d.Take(n).All(b => b >= 0x20 || b is 9 or 10 or 13 || b >= 0x80))
        {
            var head = System.Text.Encoding.UTF8.GetString(d, 0, n).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (head.StartsWith('<')) return ".xml";
            if (head.StartsWith('{') || head.StartsWith('[')) return ".json";
            return ".txt";
        }
        return ".bin";
    }

    // ------------------------------------------------------------------ scripts

    void ScanScripts()
    {
        int v = Swf.Version;
        foreach (var t in Swf.AllTags().Where(t => t.Code is TagCodes.DoABC or TagCodes.DoABC2))
        {
            Try("DoABC", () =>
            {
                var r = t.Reader(v);
                string abcName = "";
                uint flags = 0;
                if (t.Code == TagCodes.DoABC2)
                {
                    flags = r.U32();
                    abcName = r.String();
                }
                var abc = AbcFile.Parse(r.Rest(), abcName);
                abc.SourceTag = t;
                abc.DoAbcFlags = flags;
                AbcFiles.Add(abc);
                foreach (var u in As3Printer.Units(abc))
                {
                    var unit = u;
                    string path = "scripts/" + unit.FilePath;
                    if (Scripts.Any(s => s.Path == path)) path = $"scripts/{abcName}_{Scripts.Count}/{unit.FilePath}";
                    Scripts.Add(new ScriptItem(() => unit.Code, () => unit.PCode) { Path = path, Kind = ScriptKind.As3, As3 = unit });
                }
            });
        }

        // AS1/AS2: frames of the main timeline, then of sprites
        AddFrameScripts(Swf.Tags, "scripts");
        foreach (var sp in Sprites)
        {
            string name = NameOf(sp.Id);
            string folder = $"scripts/DefineSprite_{sp.Id}" + (name.Length > 0 ? "_" + FileSafe(name) : "");
            AddFrameScripts(sp.Children, folder);
        }
        foreach (var t in Swf.AllTags().Where(t => t.Code == TagCodes.DoInitAction && t.Data.Length >= 2))
        {
            int spriteId = t.Data[0] | t.Data[1] << 8;
            string name = NameOf(spriteId);
            string path = name.StartsWith("__Packages.")
                ? "scripts/__Packages/" + string.Join('/', name[11..].Split('.').Select(FileSafe))
                : $"scripts/DefineSprite_{spriteId}{(name.Length > 0 ? "_" + FileSafe(name) : "")}/DoInitAction";
            AddAs2(path, t.Data[2..], location: new As2Location(t, 2, t.Data.Length - 2));
        }
        foreach (var b in Buttons)
        {
            for (int i = 0; i < b.Actions.Count; i++)
            {
                var ea = b.Actions[i];
                AddAs2($"scripts/DefineButton_{b.Id}/{FileSafe(ea.Header)}{(i > 0 ? "_" + i : "")}", ea.Actions, ea.Header,
                    b.Tag == null ? null : new As2Location(b.Tag, ea.Offset, ea.Actions.Length, ea.SizeField, ea.SizeBytes, ea.SizeExtra));
            }
        }
    }

    void AddFrameScripts(List<SwfTag> tags, string folder)
    {
        int frame = 1;
        int idxInFrame = 0;
        foreach (var t in tags)
        {
            switch (t.Code)
            {
                case TagCodes.ShowFrame:
                    frame++;
                    idxInFrame = 0;
                    break;
                case TagCodes.DoAction:
                    AddAs2($"{folder}/frame_{frame}/DoAction{(idxInFrame++ > 0 ? "_" + idxInFrame : "")}", t.Data,
                        location: new As2Location(t, 0, t.Data.Length));
                    break;
                case TagCodes.PlaceObject2:
                case TagCodes.PlaceObject3:
                    Try("PlaceObject", () =>
                    {
                        // Fast check: only PlaceObject tags with clip actions are relevant
                        if (t.Data.Length == 0 || (t.Data[0] & 0x80) == 0) return;
                        var po = PlaceObject.Parse(t, Swf.Version);
                        string obj = po.CharacterId is { } cid ? $"PlaceObject_{cid}_{FileSafe(NameOf(cid))}_depth{po.Depth}" : $"PlaceObject_depth{po.Depth}";
                        for (int i = 0; i < po.ClipActions.Count; i++)
                        {
                            var ea = po.ClipActions[i];
                            AddAs2($"{folder}/frame_{frame}/{obj}/{FileSafe(ea.Header)}{(i > 0 ? "_" + i : "")}", ea.Actions, ea.Header,
                                new As2Location(t, ea.Offset, ea.Actions.Length, ea.SizeField, ea.SizeBytes, ea.SizeExtra));
                        }
                    });
                    break;
            }
        }
    }

    void AddAs2(string path, byte[] code, string? header = null, As2Location? location = null)
    {
        int v = Swf.Version;
        while (Scripts.Any(s => s.Path == path)) path += "_";
        bool isClass = path.StartsWith("scripts/__Packages/");
        Scripts.Add(new ScriptItem(() =>
        {
            var stmts = Avm1Decompiler.Decompile(code, v);
            if (isClass && As2ClassBuilder.TryBuild(stmts) is { } cls) stmts = cls;
            var w = new CodeWriter();
            if (header != null)
            {
                w.Keyword(header.StartsWith("on(") ? "on" : "onClipEvent");
                w.Text(header[header.IndexOf('(')..]);
                w.Block(stmts);
            }
            else foreach (var s in stmts) s.Write(w);
            return w;
        }, () => Avm1Parser.Disassemble(Avm1Parser.Parse(code, v))) { Path = path, Kind = ScriptKind.As2, As2 = location });
    }

    public static string FileSafe(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '(' or ')' or ' ' or ',' ? c : '_').ToArray();
        var r = new string(chars).Trim();
        if (r.Length > 80) r = r[..80];
        return r.Length == 0 ? "_" : r;
    }
}
