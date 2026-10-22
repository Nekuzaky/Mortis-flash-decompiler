using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MortisFlash.Assets;
using MortisFlash.Avm2;
using MortisFlash.Controls;
using MortisFlash.Edit;

namespace MortisFlash;

// Embedded player, editing (assets, texts, p-code) and SWF rewriting.
public partial class MainWindow
{
    FlashPlayerView? _player;

    // ------------------------------------------------------------------ player

    void OnPlayToggled(object? sender, RoutedEventArgs e)
    {
        bool on = PlayButton.IsChecked == true;
        PlayerPanel.IsVisible = on;
        PlayerSplitter.IsVisible = on;
        BodyGrid.ColumnDefinitions[3].Width = new GridLength(on ? 5 : 0);
        BodyGrid.ColumnDefinitions[4].Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PlayButton.Content = on ? "■  Stop" : "▶  Play";
        // The player is only hidden when stopped: the next Play reuses it instead of starting a new browser
        if (on) ReloadPlayer();
        else _player?.Stop();
    }

    void OnReloadPlayer(object? sender, RoutedEventArgs e)
    {
        if (PlayButton.IsChecked == true) ReloadPlayer();
    }

    void OnClearConsole(object? sender, RoutedEventArgs e)
    {
        _pendingLog.Clear();
        ConsoleBox.Text = "";
    }

    async void OnCapturePlayer(object? sender, RoutedEventArgs e)
    {
        if (_player == null) return;
        var png = await _player.CaptureAsync();
        if (png != null) await SaveFile(Path.GetFileNameWithoutExtension(_doc?.FileName ?? "game") + "_capture.png", png);
    }

    void ReloadPlayer()
    {
        if (_currentBytes == null) return;
        if (_player == null)
        {
            _player = new FlashPlayerView();
            _player.Message += OnPlayerMessage;
            PlayerHost.Content = _player;
        }
        PlayerStatus.Text = "Loading…";
        Log($"— {(_doc?.IsModified == true ? "Running modified version of" : "Running")} {_doc?.FileName} —");
        _player.Load(_currentBytes, _baseFolder);
    }

    void OnPlayerMessage(string type, string text)
    {
        switch (type)
        {
            case "trace":
                Log(text);
                break;
            case "loaded":
                PlayerStatus.Text = text;
                // The game gets the keyboard right away, without an extra click
                _player?.FocusGame();
                break;
            case "shortcut":
                if (text == "F5") OnReloadPlayer(null, new RoutedEventArgs());
                else if (text == "Ctrl+O") OnOpen(null, new RoutedEventArgs());
                else if (text == "Ctrl+S" && SaveSwfButton.IsVisible) OnSaveSwf(null, new RoutedEventArgs());
                break;
            case "error":
                PlayerStatus.Text = "Error";
                Log("! " + text);
                break;
            case "ready":
                PlayerStatus.Text = text == "ruffle" ? "Ready" : "Ruffle not found";
                break;
            case "missing-runtime":
                // Mostly older Windows 10 installations: offer Microsoft's official installer page
                PlayerStatus.Text = "WebView2 runtime missing";
                var install = new Button { Classes = { "accent" }, Content = "Install WebView2…", HorizontalAlignment = HorizontalAlignment.Center };
                install.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
                var box = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
                box.Children.Add(new TextBlock { Text = text + " It is built into Windows 11 and most up-to-date Windows 10 PCs; restart MortisFlash after installing it.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = MortisTheme.TextMute });
                box.Children.Add(install);
                PlayerHost.Content = box;
                _player = null;
                break;
        }
    }

    readonly System.Text.StringBuilder _pendingLog = new();
    bool _logScheduled;

    void Log(string line)
    {
        _pendingLog.Append(line).Append('\n');
        if (_logScheduled) return;
        // Games can trace every frame: lines are batched into at most ten console updates per second
        _logScheduled = true;
        DispatcherTimer.RunOnce(FlushLog, TimeSpan.FromMilliseconds(100));
    }

    void FlushLog()
    {
        _logScheduled = false;
        if (_pendingLog.Length == 0) return;
        var text = (ConsoleBox.Text ?? "") + _pendingLog;
        _pendingLog.Clear();
        // Keep the console light (the text box lays out all of its text): only the most recent output is kept
        const int Max = 48_000;
        if (text.Length > Max)
        {
            int cut = text.IndexOf('\n', text.Length - Max * 3 / 4);
            text = text[(cut < 0 ? text.Length - Max * 3 / 4 : cut + 1)..];
        }
        ConsoleBox.Text = text;
        ConsoleBox.CaretIndex = text.Length;
    }

    // ------------------------------------------------------------------ applying an edit

    /// <summary>Applies an edit, rewrites and re-reads the SWF, keeps the current selection and restarts the game.</summary>
    async Task ApplyEdit(string what, Action<SwfDocument> edit)
    {
        if (_doc == null) return;
        var doc = _doc;
        string? key = Tree.SelectedItem is TreeViewItem { Tag: { } tag } ? KeyOf(tag) : null;
        SetStatus($"{what}…", busy: true);
        try
        {
            int known = doc.Errors.Count;
            var (newDoc, bytes, notes) = await Task.Run(() =>
            {
                edit(doc);
                // Warnings raised by the edit belong to the old document, which Rebuild replaces: keep them for the status bar
                var raised = doc.Errors.Skip(known).ToList();
                var (d, b) = SwfEditor.Rebuild(doc);
                return (d, b, raised);
            });
            ShowDocument(newDoc, bytes, key);
            SetStatus($"{what}: done.{(PlayButton.IsChecked == true ? " The game has been restarted." : " Click “Play” to test it.")}"
                      + string.Concat(notes.Select(n => "  " + n)));
        }
        catch (Exception ex)
        {
            // The original document may have been partially modified: re-read it from its bytes
            if (_currentBytes != null)
            {
                var restored = new SwfDocument(Swf.SwfFile.Parse(_currentBytes), doc.FilePath) { IsModified = doc.IsModified };
                ShowDocument(restored, _currentBytes, key);
            }
            SetStatus($"{what} failed: {ex.Message}");
            throw;
        }
    }

    async void OnSaveSwf(object? sender, RoutedEventArgs e)
    {
        if (_doc == null || _currentBytes == null) return;
        string name = Path.GetFileNameWithoutExtension(_doc.FileName) + "_modified.swf";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save modified SWF",
            SuggestedFileName = name,
            FileTypeChoices = [new FilePickerFileType("Flash file") { Patterns = ["*.swf"] }],
        });
        if (file == null) return;
        await using (var s = await file.OpenWriteAsync())
            await s.WriteAsync(_currentBytes);
        SetStatus($"SWF saved: {file.Name} ({_currentBytes.Length:N0} bytes)");
    }

    async Task<byte[]?> PickFile(string title, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        if (files.Count == 0) return null;
        await using var s = await files[0].OpenReadAsync();
        var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    async Task ReplaceImage(SwfImage img)
    {
        var data = await PickFile("Replacement image", "*.png", "*.jpg", "*.jpeg", "*.gif");
        if (data != null) await ApplyEdit($"Replacing image {img.Id}", d => SwfEditor.ReplaceImage(d, Find(d.Images, img.Id), data));
    }

    async Task ReplaceSound(SwfSound snd)
    {
        var data = await PickFile("Replacement sound", "*.mp3", "*.wav");
        if (data == null) return;
        string? note = null;
        bool stream = snd.Tag?.Code is Swf.TagCodes.SoundStreamHead or Swf.TagCodes.SoundStreamHead2;
        await ApplyEdit(stream ? $"Replacing {snd.Name}" : $"Replacing sound {snd.Id}", d =>
        {
            var target = d.Sounds.First(x => x.Id == snd.Id && x.Name == snd.Name);
            if (stream) note = SwfEditor.ReplaceStreamSound(d, target, data);
            else SwfEditor.ReplaceSound(d, target, data);
        });
        if (note != null) SetStatus(note);
    }

    async Task ReplaceBinary(BinaryItem bin)
    {
        var data = await PickFile("Replacement file", "*");
        if (data != null) await ApplyEdit($"Replacing binary data {bin.Id}", d => SwfEditor.ReplaceBinary(d.BinaryData.First(x => x.Id == bin.Id), data));
    }

    static SwfImage Find(List<SwfImage> images, int id) => images.First(i => i.Id == id);

    // ------------------------------------------------------------------ texts

    void ShowTextEditor(SwfText t)
    {
        var box = new TextBox
        {
            Text = t.IsEditText ? t.InitialText : t.ToPlainText(_doc!.Fonts), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120,
            FontFamily = (FontFamily)this.FindResource("MonoFont")!,
        };
        var apply = new Button { Classes = { "accent" }, Content = "Apply", HorizontalAlignment = HorizontalAlignment.Left };
        apply.Click += async (_, _) =>
        {
            string value = box.Text ?? "";
            try
            {
                await ApplyEdit($"Editing text {t.Id}", d =>
                {
                    var target = d.Texts.First(x => x.Id == t.Id);
                    if (target.IsEditText) SwfEditor.SetEditText(target, value);
                    else SwfEditor.SetStaticText(d, target, value);
                });
            }
            catch { /* already reported in the status bar */ }
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        string hint;
        if (t.IsEditText)
            hint = t.Html ? "Initial text of the field (Flash HTML: <b>, <font color=\"#…\">, <br>, etc.)." : "Initial text of the field.";
        else
        {
            var font = t.Runs.Count > 0 && _doc!.Fonts.TryGetValue(t.Runs[0].FontId, out var f) ? f : null;
            hint = "Static text: the first line's font, size and color are kept, and only characters embedded in the font can be used"
                   + (font != null ? $": {font.Charset}" : ".");
        }
        panel.Children.Add(new TextBlock { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap, Text = hint });
        panel.Children.Add(box);
        panel.Children.Add(apply);
        var bmp = _renderer!.RenderCharacter(t.Id, 900);
        if (bmp != null)
            panel.Children.Add(new Border { Background = Checker(), HorizontalAlignment = HorizontalAlignment.Left, Child = new Image { Source = ToAvalonia(bmp), Stretch = Stretch.None } });
        PreviewHost.Content = new ScrollViewer { Content = panel };
    }

    // ------------------------------------------------------------------ p-code

    /// <summary>
    /// Script editor. "ActionScript" mode edits the decompiled source and recompiles it (for AS3, one method body at a
    /// time); "P-code" mode edits the bytecode listing directly.
    /// </summary>
    void ShowScriptEditor(ScriptItem s, bool pcode = false, int methodIndex = 0, string? text = null, Exception? error = null)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, FontSize = 13,
            FontFamily = (FontFamily)this.FindResource("MonoFont")!, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
        };
        var errorText = new TextBlock { Foreground = MortisTheme.Ribbon, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(16, 0, 16, 8) };
        var signature = new TextBlock { FontFamily = editor.FontFamily, FontSize = 12, Foreground = MortisTheme.TextMute, Margin = new Thickness(16, 0, 16, 6), IsVisible = false };
        // Wraps onto a second line when the preview is narrow (game panel open)
        var top = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(16, 10) };
        ComboBox? methods = null;
        List<(string Title, MethodInfo Method, MethodOwner Owner)> list = [];

        var sourceMode = new ToggleButton { Content = "ActionScript", IsChecked = !pcode, Classes = { "chip" } };
        var pcodeMode = new ToggleButton { Content = "P-code", IsChecked = pcode, Classes = { "chip" } };
        ToolTip.SetTip(sourceMode, "Edit the decompiled ActionScript and recompile it");
        ToolTip.SetTip(pcodeMode, "Edit the bytecode listing directly (always possible, even when the source does not compile)");

        void Load()
        {
            errorText.IsVisible = false;
            try
            {
                if (s.As3 is { } u && methods != null && methods.SelectedIndex >= 0)
                {
                    var (title, m, owner) = list[methods.SelectedIndex];
                    signature.Text = As3Printer.MethodSignature(u, m, owner, title.Split(' ')[^1]) + " { … }";
                    signature.IsVisible = !pcode;
                    editor.Text = pcode ? As3Printer.PCodeOf(u.Abc, new NameContext(u.Abc, []), m) : As3Printer.MethodBodySource(u, m, owner);
                }
                else editor.Text = pcode ? s.PCode : s.Source;
            }
            catch (Exception ex)
            {
                editor.Text = "";
                errorText.Text = ex.Message;
                errorText.IsVisible = true;
            }
        }

        if (s.As3 is { } unit)
        {
            list = As3Printer.Methods(unit);
            methods = new ComboBox { MinWidth = 260, ItemsSource = list.Select(m => m.Title).ToList(), SelectedIndex = list.Count > 0 ? Math.Min(methodIndex, list.Count - 1) : -1 };
            methods.SelectionChanged += (_, _) => Load();
            top.Children.Add(new TextBlock { Text = "Method", VerticalAlignment = VerticalAlignment.Center, Foreground = MortisTheme.TextMute });
            top.Children.Add(methods);
        }
        sourceMode.Click += (_, _) => ShowScriptEditor(s, pcode: false, methods?.SelectedIndex ?? 0);
        pcodeMode.Click += (_, _) => ShowScriptEditor(s, pcode: true, methods?.SelectedIndex ?? 0);
        top.Children.Add(sourceMode);
        top.Children.Add(pcodeMode);

        var apply = new Button { Classes = { "accent" }, Content = "Apply and test" };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => ShowScript(s);
        apply.Click += async (_, _) =>
        {
            string current = editor.Text ?? "";
            int index = methods?.SelectedIndex ?? -1;
            string path = s.Path;
            int version = _doc!.Swf.Version;
            try
            {
                await ApplyEdit($"Editing {s.Name}", d =>
                {
                    var target = d.Scripts.First(x => x.Path == path);
                    if (target.As2 is { } loc)
                    {
                        if (pcode) SwfEditor.ReplaceAs2(loc, current);
                        else SwfEditor.CompileAs2(loc, current, version);
                    }
                    else if (target.As3 is { } u && index >= 0)
                    {
                        var (_, m, owner) = As3Printer.Methods(u)[index];
                        if (pcode) SwfEditor.ReplaceAs3Method(u.Abc, m, current);
                        else SwfEditor.CompileAs3Method(u.Abc, m, owner, current);
                    }
                });
            }
            catch (Exception ex)
            {
                // Compile or assembly error: stay in the editor, caret on the offending line
                var again = _doc?.Scripts.FirstOrDefault(x => x.Path == s.Path) ?? s;
                ShowScriptEditor(again, pcode, Math.Max(0, index), current, ex);
            }
        };
        top.Children.Add(apply);
        top.Children.Add(cancel);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        root.Children.Add(top);
        Grid.SetRow(errorText, 1);
        root.Children.Add(errorText);
        Grid.SetRow(signature, 2);
        root.Children.Add(signature);
        Grid.SetRow(editor, 3);
        root.Children.Add(editor);
        PreviewHeader.IsVisible = true;
        PreviewTitle.Text = s.Name;
        PreviewSubtitle.Text = (s.Kind == ScriptKind.As3 ? "ActionScript 3" : "ActionScript 1/2") + (pcode ? " · p-code" : " · source");
        PreviewActions.Children.Clear();
        PreviewHost.Content = root;

        Load();
        if (text != null) editor.Text = text;
        if (error != null)
        {
            errorText.Text = error.Message + (error is MortisFlash.Compiler.CompileException && !pcode
                ? "  —  if this construct is not supported yet, switch to P-code to edit the bytecode instead."
                : "");
            errorText.IsVisible = true;
            int line = error switch { AssemblerException ae => ae.Line, MortisFlash.Compiler.CompileException ce => ce.Line, _ => 0 };
            if (line > 0)
            {
                var all = editor.Text ?? "";
                int pos = all.Split('\n').Take(line - 1).Sum(l => l.Length + 1);
                editor.CaretIndex = Math.Min(pos, all.Length);
                editor.Focus();
            }
        }
    }

    // ------------------------------------------------------------------ selection preservation

    static string? KeyOf(object tag) => tag switch
    {
        ScriptItem s => "script:" + s.Path,
        SwfImage i => "image:" + i.Id,
        SwfSound s => "sound:" + (s.Id >= 0 ? s.Id.ToString() : s.Name),
        ShapeItem s => "shape:" + s.Id,
        SpriteItem s => "sprite:" + s.Id,
        SwfButton b => "button:" + b.Id,
        SwfFont f => "font:" + f.Id,
        SwfText t => "text:" + t.Id,
        BinaryItem b => "binary:" + b.Id,
        SwfVideo v => "video:" + v.Id,
        InfoNode n => "info:" + n.Kind,
        TimelineNode => "timeline",
        _ => null,
    };

    bool Reselect(string key)
    {
        if (Tree.ItemsSource is not IEnumerable<TreeViewItem> roots) return false;
        static IEnumerable<TreeViewItem> All(IEnumerable<TreeViewItem> items)
        {
            foreach (var i in items)
            {
                yield return i;
                foreach (var c in All(i.Items.OfType<TreeViewItem>())) yield return c;
            }
        }
        var node = All(roots).FirstOrDefault(n => n.Tag != null && KeyOf(n.Tag) == key);
        if (node == null) return false;
        for (var p = node.Parent as TreeViewItem; p != null; p = p.Parent as TreeViewItem) p.IsExpanded = true;
        Tree.SelectedItem = node;
        return true;
    }
}
