using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MortisFlash.Assets;
using MortisFlash.Controls;
using MortisFlash.Export;
using MortisFlash.Rendering;
using MortisFlash.Swf;
using SkiaSharp;

namespace MortisFlash;

public partial class MainWindow : Window
{
    SwfDocument? _doc;
    SwfRenderer? _renderer;
    readonly Stack<(SwfDocument Doc, byte[] Bytes)> _parents = new();
    /// <summary>Bytes of the displayed SWF (original or modified); played and saved as-is.</summary>
    byte[]? _currentBytes;
    /// <summary>Folder of the opened file; the game can load sibling files from it.</summary>
    string? _baseFolder;
    int _selectionToken;
    DispatcherTimer? _playTimer;
    readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    HashSet<ScriptItem>? _codeMatches;
    CancellationTokenSource? _searchCts;
    string? _lastExportFolder;

    sealed record InfoNode(string Kind);
    sealed record TimelineNode;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyFilter();
        };
        Filter.TextChanged += (_, _) =>
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        };
        SearchCode.IsCheckedChanged += (_, _) => ApplyFilter();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control)) OnOpen(null, new RoutedEventArgs());
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) Filter.Focus();
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && SaveSwfButton.IsVisible) OnSaveSwf(null, new RoutedEventArgs());
            if (e.Key == Key.F5) OnReloadPlayer(null, new RoutedEventArgs());
        };
        ShowWelcome();
        Opened += async (_, _) =>
        {
            if (Program.StartupFile != null) await OpenPath(Program.StartupFile);
        };
    }

    // ------------------------------------------------------------------ opening files

    void ShowWelcome()
    {
        PreviewHeader.IsVisible = false;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 14, MaxWidth = 560 };
        panel.Children.Add(new Image { Source = new Bitmap(AssetLoader("Icon.png")), Width = 72, Height = 72 });
        panel.Children.Add(new TextBlock { Classes = { "title" }, Text = "Decompile a Flash game", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "Drop a .swf file (or a Flash projector .exe) onto this window, or click “Open…”.\n" +
                   "Supports ActionScript 1, 2 and 3, images, sounds, shapes, fonts, texts, videos and timeline frames.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = MortisTheme.TextMute,
        });
        var open = new Button { Classes = { "accent" }, Content = "Open file…", HorizontalAlignment = HorizontalAlignment.Center };
        open.Click += (s, e) => OnOpen(s, e);
        panel.Children.Add(open);
        panel.Children.Add(new TextBlock
        {
            Classes = { "faint" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 18, 0, 0),
            Text = "Use only on files you own, for preservation or learning. " +
                   "Extracted code and assets remain the property of their authors: do not redistribute them.",
        });
        PreviewHost.Content = new Border
        {
            Margin = new Thickness(40), CornerRadius = new CornerRadius(12), BorderBrush = MortisTheme.LineStrong,
            BorderThickness = new Thickness(1), Background = MortisTheme.Elevated, Child = panel, Padding = new Thickness(32),
        };
    }

    static Stream AssetLoader(string name) => Avalonia.Platform.AssetLoader.Open(new Uri($"avares://MortisFlash/Assets/{name}"));

    async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a Flash file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Flash files") { Patterns = ["*.swf", "*.spl", "*.exe"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await OpenPath(path);
    }

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        var path = files?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);
        if (path != null) await OpenPath(path);
    }

    async Task OpenPath(string path)
    {
        SetStatus($"Opening {Path.GetFileName(path)}…", busy: true);
        try
        {
            var (doc, bytes) = await Task.Run(() =>
            {
                var raw = SwfFile.ExtractFromContainer(File.ReadAllBytes(path));
                return (new SwfDocument(SwfFile.Parse(raw), path), raw);
            });
            _parents.Clear();
            _baseFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            ShowDocument(doc, bytes);
        }
        catch (Exception ex)
        {
            SetStatus("Could not open this file: " + ex.Message);
        }
    }

    void ShowDocument(SwfDocument doc, byte[] bytes, string? reselect = null)
    {
        _doc = doc;
        _currentBytes = bytes;
        _renderer = new SwfRenderer(doc);
        _codeMatches = null;
        StopPlayback();
        Title = $"MortisFlash — {doc.FileName}{(doc.IsModified ? " • modified" : "")}";
        var s = doc.Swf;
        FileInfo.Text = $"{(doc.IsModified ? "• " : "")}{doc.FileName}  ·  SWF {s.Version}  ·  {(s.IsAs3 ? "ActionScript 3" : "ActionScript 1/2")}  ·  " +
                        $"{s.FrameSize.Width / 20.0:0.#}×{s.FrameSize.Height / 20.0:0.#}  ·  {s.FrameRate:0.##} fps  ·  {Frames(s.FrameCount)}";
        ExportAllButton.IsEnabled = true;
        PlayButton.IsEnabled = FlashPlayerView.IsAvailable;
        SaveSwfButton.IsVisible = doc.IsModified;
        BackButton.IsVisible = _parents.Count > 0;
        OpenFolderButton.IsVisible = false;
        BuildTree();
        if (reselect != null && Reselect(reselect)) { }
        else
        {
            SetStatus($"{Count(doc.Scripts.Count, "script")}, {Count(doc.Images.Count, "image")}, {Count(doc.Sounds.Count, "sound")}, "
                      + $"{Count(doc.Shapes.Count, "shape")}, {Count(doc.Sprites.Count, "sprite")}"
                      + (doc.Errors.Count > 0 ? $" — {doc.Errors.Count} warning(s)" : ""));
            ShowInfo("info");
        }
        if (PlayButton.IsChecked == true) ReloadPlayer();
    }

    void OnBack(object? sender, RoutedEventArgs e)
    {
        if (_parents.Count == 0) return;
        var (parent, bytes) = _parents.Pop();
        ShowDocument(parent, bytes);
    }

    // ------------------------------------------------------------------ tree

    static TreeViewItem Node(string header, object? tag, IEnumerable<TreeViewItem>? children = null, bool expanded = false)
    {
        var item = new TreeViewItem { Header = header, Tag = tag, IsExpanded = expanded };
        if (children != null)
            foreach (var c in children) item.Items.Add(c);
        return item;
    }

    void ApplyFilter()
    {
        if (_doc == null) return;
        string f = Filter.Text?.Trim() ?? "";
        if (SearchCode.IsChecked == true && f.Length >= 2)
        {
            _ = SearchInCode(f);
            return;
        }
        _searchCts?.Cancel();
        _codeMatches = null;
        BuildTree();
    }

    async Task SearchInCode(string text)
    {
        if (_doc == null) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var doc = _doc;
        var scripts = doc.Scripts.ToList();
        var matches = new HashSet<ScriptItem>();
        SetStatus("Searching code…", busy: true);
        try
        {
            await Task.Run(() =>
            {
                int done = 0;
                Parallel.ForEach(scripts, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, s =>
                {
                    bool hit = s.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
                               || s.Source.Contains(text, StringComparison.OrdinalIgnoreCase);
                    lock (matches)
                    {
                        if (hit) matches.Add(s);
                        done++;
                    }
                    if (done % 20 == 0)
                        Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) SetProgress(done, scripts.Count); });
                });
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || _doc != doc) return;
        _codeMatches = matches;
        BuildTree();
        SetStatus($"{matches.Count} script(s) contain “{text}”");
    }

    void BuildTree()
    {
        var doc = _doc;
        if (doc == null) return;
        string filter = _codeMatches != null ? "" : Filter.Text?.Trim() ?? "";
        bool filtering = filter.Length > 0 || _codeMatches != null;
        bool Match(string s) => filter.Length == 0 || s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var roots = new List<TreeViewItem>();

        if (!filtering)
        {
            roots.Add(Node("Overview", new InfoNode("info")));
            roots.Add(Node($"Timeline ({Frames(doc.Swf.FrameCount)})", new TimelineNode()));
        }

        // Scripts: folder hierarchy
        var scripts = doc.Scripts.Where(s => (_codeMatches == null || _codeMatches.Contains(s)) && Match(s.Path)).ToList();
        if (scripts.Count > 0)
        {
            var root = Node($"Scripts ({scripts.Count})", null, null, expanded: filtering || scripts.Count < 60);
            var folders = new Dictionary<string, TreeViewItem>();
            TreeViewItem Folder(string path)
            {
                if (path.Length == 0) return root;
                if (folders.TryGetValue(path, out var f)) return f;
                int slash = path.LastIndexOf('/');
                var parent = Folder(slash < 0 ? "" : path[..slash]);
                f = Node(slash < 0 ? path : path[(slash + 1)..], null, null, expanded: filtering);
                parent.Items.Add(f);
                folders[path] = f;
                return f;
            }
            foreach (var s in scripts.OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                string rel = s.Path.StartsWith("scripts/") ? s.Path[8..] : s.Path;
                string folder = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
                Folder(folder).Items.Add(Node(s.Name, s));
            }
            roots.Add(root);
        }
        if (_codeMatches == null)
        {
            void Category<T>(string title, IEnumerable<T> items, Func<T, string> label)
            {
                var list = items.Select(i => (Item: i, Label: label(i))).Where(x => Match(x.Label)).ToList();
                if (list.Count == 0) return;
                roots.Add(Node($"{title} ({list.Count})", null, list.Select(x => Node(x.Label, x.Item!)), expanded: filtering));
            }
            Category("Images", doc.Images, i => $"{doc.Label("image", i.Id)}  ·  {i.Format}");
            Category("Sounds", doc.Sounds, s => s.Id >= 0 ? $"{doc.Label("sound", s.Id)}  ·  {s.Codec}" : $"{s.Name}  ·  {s.Codec}");
            Category("Shapes", doc.Shapes, s => doc.Label("shape", s.Id));
            Category("Morph shapes", doc.MorphShapes, s => doc.Label("morph", s.Id));
            Category("Sprites", doc.Sprites, s => $"{doc.Label("sprite", s.Id)}  ·  {Frames(s.FrameCount)}");
            Category("Buttons", doc.Buttons, b => doc.Label("button", b.Id));
            Category("Fonts", doc.Fonts.Values, f => $"{doc.Label("font", f.Id)}  ·  {f.Name}");
            Category("Texts", doc.Texts, t => $"{doc.Label("text", t.Id)}  ·  {Preview(t)}");
            Category("Binary data", doc.BinaryData, b => $"{doc.Label("data", b.Id)}  ·  {b.Extension}  ·  {b.Data.Length:N0} B");
            Category("Videos", doc.Videos, v => $"{doc.Label("video", v.Id)}  ·  {v.CodecName}");
        }
        if (!filtering)
        {
            roots.Add(Node("Tags", new InfoNode("tags")));
            if (doc.Errors.Count > 0) roots.Add(Node($"Warnings ({doc.Errors.Count})", new InfoNode("errors")));
        }
        Tree.ItemsSource = roots;
    }

    string Preview(SwfText t)
    {
        var s = t.ToPlainText(_doc!.Fonts);
        if (t.Html) s = TextParser.StripHtml(s);
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 40 ? s[..40] + "…" : s.Length == 0 ? (t.VariableName.Length > 0 ? t.VariableName : "(empty)") : s;
    }

    void OnTreeSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (Tree.SelectedItem is not TreeViewItem { Tag: { } tag }) return;
        StopPlayback();
        _selectionToken++;
        try { Show(tag); }
        catch (Exception ex) { ShowText("Error", ex.ToString()); }
    }

    // ------------------------------------------------------------------ previews

    void Header(string title, string subtitle, params Control[] actions)
    {
        PreviewHeader.IsVisible = true;
        PreviewTitle.Text = title;
        PreviewSubtitle.Text = subtitle;
        PreviewActions.Children.Clear();
        foreach (var a in actions) PreviewActions.Children.Add(a);
    }

    Button Action(string text, Func<Task> run, string? tip = null, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.Classes.Add("accent");
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += async (_, _) =>
        {
            try { await run(); }
            catch (Exception ex) { SetStatus("Error: " + ex.Message); }
        };
        return b;
    }

    Button SaveAction(string suggested, Func<byte[]> data, string text = "Save…") =>
        Action(text, () => SaveFile(suggested, data()));

    void Show(object tag)
    {
        var doc = _doc!;
        switch (tag)
        {
            case InfoNode i:
                ShowInfo(i.Kind);
                break;
            case TimelineNode:
                ShowTimeline();
                break;
            case ScriptItem s:
                ShowScript(s);
                break;
            case SwfImage img:
                Header(doc.Label("Image", img.Id), $"{img.Format}  ·  {img.Width}×{img.Height} px  ·  {img.FileBytes.Length:N0} bytes",
                    Action("Replace…", () => ReplaceImage(img), "Replace the image with a PNG, JPEG or GIF file; the game reloads"),
                    SaveAction($"{img.Id}{img.Extension}", () => img.FileBytes));
                ShowBitmap(img.Bitmap, pixelArt: true);
                break;
            case SwfSound snd:
                ShowSound(snd);
                break;
            case ShapeItem sh:
            {
                var images = doc.Images.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
                bool morph = sh.Morph != null;
                Header(doc.Label(morph ? "Morph shape" : "Shape", sh.Id), $"{sh.Tag.Name}  ·  {sh.Shape.Bounds.Width / 20.0:0.#}×{sh.Shape.Bounds.Height / 20.0:0.#} px",
                    SaveAction($"{sh.Id}.svg", () => System.Text.Encoding.UTF8.GetBytes(SvgExporter.Export(sh.Shape, images)), "Export as SVG…"),
                    Action("Export as PNG…", () => SavePng($"{sh.Id}.png", () => _renderer!.RenderCharacter(sh.Id, 2048))));
                ShowCharacter(sh.Id);
                break;
            }
            case SpriteItem sp:
                Header(doc.Label("Sprite", sp.Id), $"{Frames(sp.FrameCount)}  ·  {sp.Children.Count} tags",
                    Action("Export as PNG…", () => SavePng($"sprite_{sp.Id}.png", () => _renderer!.RenderCharacter(sp.Id, 2048))));
                ShowFrames(sp.FrameCount, f => _renderer!.RenderCharacter(sp.Id, 1200, f));
                break;
            case SwfButton b:
                Header(doc.Label("Button", b.Id), $"{b.Tag?.Name}  ·  {b.Records.Count} layers  ·  {b.Actions.Count} handler(s)");
                ShowCharacter(b.Id);
                break;
            case SwfFont f:
                Header(doc.Label("Font", f.Id), $"{f.Name}{(f.Bold ? ", bold" : "")}{(f.Italic ? ", italic" : "")}  ·  {f.Glyphs.Count} glyphs",
                    f.FontData != null ? SaveAction($"{f.Name}.otf", () => f.FontData!) : new Control());
                ShowFont(f);
                break;
            case SwfText t:
            {
                string plain = t.ToPlainText(doc.Fonts);
                Header(doc.Label("Text", t.Id), t.IsEditText ? $"Text field{(t.VariableName.Length > 0 ? $"  ·  variable “{t.VariableName}”" : "")}{(t.Html ? "  ·  HTML" : "")}" : "Static text",
                    SaveAction($"text_{t.Id}.txt", () => System.Text.Encoding.UTF8.GetBytes(plain)));
                if (t.IsEditText || t.Runs.Count > 0)
                {
                    ShowTextEditor(t);
                    break;
                }
                var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                if (!t.IsEditText || t.InitialText.Length > 0)
                {
                    var bmp = _renderer!.RenderCharacter(t.Id, 900);
                    if (bmp != null) grid.Children.Add(new Border { Background = Checker(), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = ToAvalonia(bmp), Stretch = Stretch.None } });
                }
                var code = CodeView.Create(plain.Length > 0 ? plain : "(empty text)");
                Grid.SetRow(code, 1);
                grid.Children.Add(code);
                PreviewHost.Content = grid;
                break;
            }
            case BinaryItem bin:
            {
                var actions = new List<Control>
                {
                    Action("Replace…", () => ReplaceBinary(bin), "Replace this data with the contents of a file"),
                    SaveAction($"{bin.Id}{bin.Extension}", () => bin.Data),
                };
                if (bin.IsSwf) actions.Insert(0, Action("Open this SWF", () => OpenNested(bin), "This data contains another SWF (loader or packaged game)", accent: true));
                Header(doc.Label("Binary data", bin.Id), $"{bin.Extension}  ·  {bin.Data.Length:N0} bytes", [.. actions]);
                if (bin.Extension is ".png" or ".jpg" or ".gif")
                    ShowBitmap(SKBitmap.Decode(bin.Data), pixelArt: true);
                else if (bin.Extension is ".xml" or ".json" or ".txt")
                    PreviewHost.Content = CodeView.Create(System.Text.Encoding.UTF8.GetString(bin.Data));
                else PreviewHost.Content = CodeView.Create(CodeView.Hex(bin.Data));
                break;
            }
            case SwfVideo v:
                Header(doc.Label("Video", v.Id), $"{v.CodecName}  ·  {v.Width}×{v.Height}  ·  {Frames(v.FrameCount)}",
                    Action("Play", () => OpenTemp($"video_{v.Id}.flv", v.Flv), "Open the video in the default player"),
                    SaveAction($"video_{v.Id}.flv", () => v.Flv));
                PreviewHost.Content = Message("The video is rebuilt as an FLV file (playable in VLC, ffmpeg, etc.).");
                break;
        }
    }

    void ShowInfo(string kind)
    {
        var doc = _doc!;
        switch (kind)
        {
            case "info":
                Header(doc.FileName, doc.FilePath, Action("Export all…", ExportAll, accent: true));
                PreviewHost.Content = CodeView.Create(SwfExporter.Info(doc));
                break;
            case "tags":
            {
                var sb = new System.Text.StringBuilder();
                void Dump(IEnumerable<Swf.SwfTag> tags, string indent)
                {
                    foreach (var t in tags)
                    {
                        sb.Append($"{indent}0x{t.Offset:X8}  {t.Name,-28} {t.Data.Length,9:N0} B");
                        if (t.CharacterId >= 0) sb.Append($"   id {t.CharacterId}{(doc.NameOf(t.CharacterId) is { Length: > 0 } n ? $" ({n})" : "")}");
                        sb.Append('\n');
                        if (t.Children != null) Dump(t.Children, indent + "    ");
                    }
                }
                Dump(doc.Swf.Tags, "");
                Header("Tags", $"{doc.Swf.AllTags().Count()} tags");
                PreviewHost.Content = CodeView.Create(sb.ToString());
                break;
            }
            case "errors":
                Header("Warnings", "Unreadable or incomplete items (the rest of the file remains usable)");
                PreviewHost.Content = CodeView.Create(string.Join('\n', doc.Errors));
                break;
        }
    }

    void ShowScript(ScriptItem s)
    {
        bool pcode = false;
        int token = _selectionToken;
        var toggle = new Button { Content = "P-code" };
        ToolTip.SetTip(toggle, "Toggle between the reconstructed ActionScript and the disassembled bytecode");
        var edit = new Button { Content = "Edit", IsVisible = s.As2 != null || s.As3 != null };
        ToolTip.SetTip(edit, "Edit this script in ActionScript (or p-code), recompile it and test the game");
        edit.Click += (_, _) => ShowScriptEditor(s);
        Header(s.Name, $"{(s.Kind == ScriptKind.As3 ? "ActionScript 3" : "ActionScript 1/2")}  ·  {s.Folder}",
            edit,
            toggle,
            Action("Copy", async () =>
            {
                if (Clipboard is { } cb) await cb.SetTextAsync(pcode ? s.PCode : s.Source);
                SetStatus("Code copied to the clipboard");
            }),
            SaveAction(s.Name.EndsWith(".as") ? s.Name : s.Name + ".as", () => System.Text.Encoding.UTF8.GetBytes(pcode ? s.PCode : s.Source)));
        async void Render()
        {
            toggle.Content = pcode ? "ActionScript" : "P-code";
            if (!s.IsDecompiled && !pcode) PreviewHost.Content = Message("Decompiling…");
            var result = await Task.Run(() => pcode ? (object)s.PCode : s.Code);
            if (token != _selectionToken) return;
            PreviewHost.Content = result is string text ? CodeView.Create(text) : CodeView.Create((Decompiler.CodeWriter)result);
        }
        toggle.Click += (_, _) =>
        {
            pcode = !pcode;
            Render();
        };
        Render();
    }

    void ShowSound(SwfSound snd)
    {
        var doc = _doc!;
        string name = snd.Id >= 0 ? $"sound_{snd.Id}{snd.Extension}" : SwfDocument.FileSafe(snd.Name) + snd.Extension;
        bool playable = snd.Extension is ".mp3" or ".wav";
        Header(snd.Id >= 0 ? doc.Label("Sound", snd.Id) : snd.Name,
            $"{snd.Codec}  ·  {snd.Rate} Hz  ·  {(snd.Stereo ? "stereo" : "mono")}  ·  {snd.Duration:0.0} s  ·  {snd.FileBytes.Length:N0} bytes",
            playable ? Action("Play", () => OpenTemp(name, snd.FileBytes), "Open the sound in the default player", accent: true) : new Control(),
            snd.Tag?.Code is Swf.TagCodes.DefineSound or Swf.TagCodes.SoundStreamHead or Swf.TagCodes.SoundStreamHead2
                ? Action("Replace…", () => ReplaceSound(snd), "Replace the sound with an MP3 or WAV file; the game reloads") : new Control(),
            SaveAction(name, () => snd.FileBytes));
        PreviewHost.Content = Message(playable
            ? $"Sound exported as {snd.Extension[1..].ToUpperInvariant()} with no loss of quality."
            : "This codec (Nellymoser / Speex) is exported as-is; ffmpeg can convert it.");
    }

    void ShowFont(SwfFont f)
    {
        if (f.Glyphs.Count == 0)
        {
            PreviewHost.Content = Message(f.FontData != null ? "Embedded OpenType font (DefineFont4): use “Save…” to extract it." : "This font has no glyphs (name only).");
            return;
        }
        const int cell = 72, cols = 12;
        int rows = (f.Glyphs.Count + cols - 1) / cols;
        using var bmp = new SKBitmap(cols * cell, rows * cell);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x14, 0x14, 0x14));
            using var grid = new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x2A), Style = SKPaintStyle.Stroke };
            using var white = new SKPaint { Color = new SKColor(0xFA, 0xFA, 0xFA), IsAntialias = true };
            using var mute = new SKPaint { Color = new SKColor(0x86, 0x86, 0x86), IsAntialias = true };
            using var small = new SKFont(SKTypeface.Default, 10);
            double scale = 44.0 / f.EmSquare;
            for (int i = 0; i < f.Glyphs.Count; i++)
            {
                int x = i % cols * cell, y = i / cols * cell;
                c.DrawRect(x + 0.5f, y + 0.5f, cell - 1, cell - 1, grid);
                using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var p in ShapeParser.BuildPaths(f.Glyphs[i]))
                    foreach (var contour in p.Contours)
                    {
                        if (contour.Count == 0) continue;
                        path.MoveTo((float)(contour[0].X0 * scale), (float)(contour[0].Y0 * scale));
                        foreach (var s in contour)
                        {
                            if (s.Curved) path.QuadTo((float)(s.Cx * scale), (float)(s.Cy * scale), (float)(s.X1 * scale), (float)(s.Y1 * scale));
                            else path.LineTo((float)(s.X1 * scale), (float)(s.Y1 * scale));
                        }
                    }
                c.Save();
                c.Translate(x + 14, y + 50);
                c.DrawPath(path, white);
                c.Restore();
                int code = i < f.Codes.Count ? f.Codes[i] : 0;
                c.DrawText(code > 32 ? char.ConvertFromUtf32(Math.Min(code, 0x10FFFF)) : $"#{code}", x + 4, y + cell - 5, small, mute);
            }
        }
        ShowBitmap(bmp.Copy(), pixelArt: false, checker: false);
    }

    void ShowCharacter(int id)
    {
        var bmp = _renderer!.RenderCharacter(id, 1200);
        ShowBitmap(bmp, pixelArt: false);
    }

    void ShowTimeline()
    {
        var doc = _doc!;
        Header("Timeline", $"{Frames(doc.Swf.FrameCount)}  ·  {doc.Swf.FrameRate:0.##} fps{(doc.FrameLabels.Count > 0 ? "  ·  labels: " + string.Join(", ", doc.FrameLabels.Take(8)) : "")}",
            Action("Export frame as PNG…", () => SavePng("frame.png", () => _renderer!.RenderFrame(_currentFrame, 2))));
        ShowFrames(doc.Swf.FrameCount, f => _renderer!.RenderFrame(f), doc.Swf.FrameRate);
    }

    int _currentFrame;

    /// <summary>Frame-by-frame preview with a scrubber and playback.</summary>
    void ShowFrames(int count, Func<int, SKBitmap?> render, double fps = 0)
    {
        if (fps <= 0) fps = _doc!.Swf.FrameRate > 0 ? _doc.Swf.FrameRate : 24;
        count = Math.Max(1, count);
        _currentFrame = 0;
        var image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var checker = new Border { Background = Checker(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = image };
        var slider = new Slider { Minimum = 0, Maximum = count - 1, Value = 0, IsSnapToTickEnabled = true, TickFrequency = 1, MinWidth = 200 };
        var label = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
        var play = new Button { Content = "▶", Width = 40, IsEnabled = count > 1 };
        int token = _selectionToken;
        void Draw(int f)
        {
            if (token != _selectionToken) return;
            _currentFrame = f;
            label.Text = $"Frame {f + 1} / {count}";
            try
            {
                using var bmp = render(f);
                image.Source = bmp != null ? ToAvalonia(bmp, image.Source as WriteableBitmap) : null;
                image.InvalidateVisual();
            }
            catch (Exception ex) { SetStatus("Rendering failed: " + ex.Message); }
        }
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Draw((int)slider.Value);
        };
        play.Click += (_, _) =>
        {
            if (_playTimer != null)
            {
                StopPlayback();
                play.Content = "▶";
                return;
            }
            play.Content = "❚❚";
            _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000 / Math.Clamp(fps, 1, 60)) };
            _playTimer.Tick += (_, _) => slider.Value = (slider.Value + 1) % count;
            _playTimer.Start();
        };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 8) };
        bar.Children.Add(play);
        Grid.SetColumn(slider, 1);
        slider.Margin = new Thickness(12, 0);
        bar.Children.Add(slider);
        Grid.SetColumn(label, 2);
        bar.Children.Add(label);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new Border { Padding = new Thickness(16), Child = checker });
        Grid.SetRow(bar, 1);
        if (count > 1) root.Children.Add(bar);
        PreviewHost.Content = root;
        Draw(0);
    }

    void StopPlayback()
    {
        _playTimer?.Stop();
        _playTimer = null;
    }

    void ShowBitmap(SKBitmap? bmp, bool pixelArt, bool checker = true)
    {
        if (bmp == null)
        {
            PreviewHost.Content = Message("No preview available for this item.");
            return;
        }
        var img = new Image { Source = ToAvalonia(bmp), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        if (pixelArt && bmp.Width < 256) RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        PreviewHost.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new Border
            {
                Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Background = checker ? Checker() : null, Child = img,
            },
        };
        bmp.Dispose();
    }

    static Control Message(string text) => new TextBlock
    {
        Text = text, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap, Foreground = MortisTheme.TextMute,
    };

    void ShowText(string title, string text)
    {
        Header(title, "");
        PreviewHost.Content = CodeView.Create(text);
    }

    static string Frames(int n) => Count(n, "frame");
    static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    /// <summary>
    /// Copies a Skia bitmap straight into an Avalonia bitmap (no PNG encode and decode); <paramref name="reuse"/> is
    /// filled in place when it has the same size, which keeps timeline playback cheap.
    /// </summary>
    static WriteableBitmap ToAvalonia(SKBitmap bmp, WriteableBitmap? reuse = null)
    {
        var size = new PixelSize(Math.Max(1, bmp.Width), Math.Max(1, bmp.Height));
        var wb = reuse != null && reuse.PixelSize == size
            ? reuse
            : new WriteableBitmap(size, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var fb = wb.Lock();
        using var pixmap = bmp.PeekPixels();
        pixmap?.ReadPixels(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), fb.Address, fb.RowBytes);
        return wb;
    }

    static IBrush? _checker;

    /// <summary>Dark checkerboard shown behind transparent images.</summary>
    static IBrush Checker()
    {
        if (_checker != null) return _checker;
        using var bmp = new SKBitmap(16, 16);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0x1A, 0x1A, 0x1A));
            using var p = new SKPaint { Color = new SKColor(0x24, 0x24, 0x24) };
            c.DrawRect(0, 0, 8, 8, p);
            c.DrawRect(8, 8, 8, 8, p);
        }
        _checker = new ImageBrush(ToAvalonia(bmp))
        {
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        };
        return _checker;
    }

    // ------------------------------------------------------------------ files

    async Task SaveFile(string suggested, byte[] data)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save", SuggestedFileName = suggested });
        if (file == null) return;
        await using var s = await file.OpenWriteAsync();
        await s.WriteAsync(data);
        SetStatus($"Saved: {file.Name}");
    }

    async Task SavePng(string suggested, Func<SKBitmap?> render)
    {
        using var bmp = render();
        if (bmp == null) return;
        await SaveFile(suggested, SwfRenderer.EncodePng(bmp));
    }

    async Task OpenTemp(string name, byte[] data)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MortisFlash");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, data);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    async Task OpenNested(BinaryItem bin)
    {
        if (_doc == null) return;
        var parent = _doc;
        var doc = await Task.Run(() => SwfDocument.FromBytes(bin.Data, $"{parent.FileName} › data {bin.Id}.swf"));
        _parents.Push((parent, _currentBytes ?? []));
        ShowDocument(doc, bin.Data);
    }

    async void OnExportAll(object? sender, RoutedEventArgs e) => await ExportAll();

    async Task ExportAll()
    {
        if (_doc == null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export folder", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } baseDir) return;
        var doc = _doc;
        string folder = Path.Combine(baseDir, Path.GetFileNameWithoutExtension(SwfDocument.FileSafe(doc.FileName)) + "_export");
        ExportAllButton.IsEnabled = false;
        OpenFolderButton.IsVisible = false;
        var progress = new Progress<ExportProgress>(p =>
        {
            Status.Text = $"Exporting: {p.Step}…";
            SetProgress(p.Done, p.Total);
        });
        try
        {
            var report = await Task.Run(() => SwfExporter.Export(doc, folder, ExportParts.All | ExportParts.PCode, progress));
            _lastExportFolder = folder;
            SetStatus($"{report.Files} files exported to {folder}" + (report.Errors.Count > 0 ? $" ({report.Errors.Count} error(s), see errors.txt)" : ""));
            OpenFolderButton.IsVisible = true;
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
        }
        finally
        {
            ExportAllButton.IsEnabled = true;
        }
    }

    void OnOpenExportFolder(object? sender, RoutedEventArgs e)
    {
        if (_lastExportFolder != null && Directory.Exists(_lastExportFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastExportFolder}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ status

    void SetStatus(string text, bool busy = false)
    {
        Status.Text = text;
        Progress.IsVisible = busy;
        Progress.IsIndeterminate = busy;
    }

    void SetProgress(int done, int total)
    {
        Progress.IsVisible = true;
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, total);
        Progress.Value = done;
    }
}
