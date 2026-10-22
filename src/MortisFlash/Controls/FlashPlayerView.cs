using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace MortisFlash.Controls;

/// <summary>
/// Embedded Flash player: Ruffle (web build) running in a WebView2 hosted by a native child window.
/// The SWF is served from memory at https://game.mortisflash/; sibling files (SWFs loaded by the game,
/// XML, external sounds) are served from the original file's folder. Windows only.
/// </summary>
public sealed partial class FlashPlayerView : NativeControlHost
{
    const string PlayerHost = "player.mortisflash";
    const string GameHost = "game.mortisflash";

    CoreWebView2Environment? _env;
    CoreWebView2Controller? _controller;
    IntPtr _hwnd;
    bool _ready;
    byte[]? _swf;
    string? _baseFolder;
    int _version;

    /// <summary>Message from the player: game trace() output, an error or a status update.</summary>
    public event Action<string, string>? Message;

    /// <summary>Ruffle and the player page are embedded resources named <c>Ruffle/&lt;file&gt;</c>, so the executable is self-contained.</summary>
    static readonly System.Reflection.Assembly Bundle = typeof(FlashPlayerView).Assembly;
    public static bool IsAvailable => OperatingSystem.IsWindows() && Bundle.GetManifestResourceInfo("Ruffle/ruffle.js") != null;

    /// <summary>
    /// Folder the embedded Ruffle files are extracted to (once per build), so WebView2 reads and caches them itself
    /// instead of streaming 28 MB of WebAssembly through the UI thread on every load. Null if it cannot be written.
    /// </summary>
    static readonly Lazy<string?> RuffleCache = new(ExtractRuffle);

    static string? ExtractRuffle()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MortisFlash", "Ruffle");
            string folder = Path.Combine(root, Bundle.ManifestModule.ModuleVersionId.ToString("N"));
            if (File.Exists(Path.Combine(folder, "ruffle.js"))) return folder;
            string temp = folder + ".tmp";
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            foreach (var name in Bundle.GetManifestResourceNames().Where(n => n.StartsWith("Ruffle/", StringComparison.Ordinal)))
            {
                using var src = Bundle.GetManifestResourceStream(name)!;
                using var dst = File.Create(Path.Combine(temp, name["Ruffle/".Length..]));
                src.CopyTo(dst);
            }
            Directory.Move(temp, folder);
            // Previous builds' copies are no longer used
            foreach (var old in Directory.GetDirectories(root).Where(d => !string.Equals(d, folder, StringComparison.OrdinalIgnoreCase)))
            {
                try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public FlashPlayerView()
    {
        this.GetObservable(BoundsProperty).Subscribe(new BoundsObserver(this));
    }

    sealed class BoundsObserver(FlashPlayerView view) : IObserver<Rect>
    {
        public void OnNext(Rect value) => view.UpdateBounds();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    /// <summary>Loads (or reloads) a SWF. Relative files requested by the game are resolved against <paramref name="baseFolder"/>.</summary>
    public void Load(byte[] swf, string? baseFolder)
    {
        _swf = swf;
        _baseFolder = baseFolder;
        _version++;
        if (_ready) _ = Exec($"loadSwf('https://{GameHost}/current.swf?v={_version}')");
    }

    public void Stop()
    {
        _swf = null;
        if (_ready) _ = Exec("stopSwf()");
    }

    public void OpenDevTools() => _controller?.CoreWebView2.OpenDevToolsWindow();

    /// <summary>Gives the keyboard to the game (after a click on the app's buttons, keys would otherwise stay in the app).</summary>
    public void FocusGame()
    {
        try { _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); }
        catch (InvalidOperationException) { }
    }

    public bool IsReady => _ready;

    /// <summary>Captures what the player is currently showing as a PNG.</summary>
    public async Task<byte[]?> CaptureAsync()
    {
        if (_controller == null) return null;
        var ms = new MemoryStream();
        await _controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        return ms.ToArray();
    }

    async Task Exec(string script)
    {
        try
        {
            if (_controller != null) await _controller.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception e)
        {
            Message?.Invoke("error", e.Message);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows() || parent.HandleDescriptor != "HWND") return base.CreateNativeControlCore(parent);
        _hwnd = CreateWindowExW(0, "Static", null, WsChild | WsVisible | WsClipChildren, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.UIThread.Post(() => _ = InitAsync());
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _ready = false;
        _controller?.Close();
        _controller = null;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        else base.DestroyNativeControlCore(control);
    }

    async Task InitAsync()
    {
        try
        {
            string? ruffle = await Task.Run(() => RuffleCache.Value);
            _env = await SharedEnvironment.Value;
            if (_hwnd == IntPtr.Zero) return;
            _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd);
            _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 12, 12, 12);
            _controller.AcceleratorKeyPressed += OnAcceleratorKey;
            var web = _controller.CoreWebView2;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.IsZoomControlEnabled = false;
            // No browser shortcuts inside the game (Ctrl+S "save page", Ctrl+P, Ctrl+F, F5 page reload…); the app's own are forwarded
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            if (ruffle != null) web.SetVirtualHostNameToFolderMapping(PlayerHost, ruffle, CoreWebView2HostResourceAccessKind.Allow);
            else web.AddWebResourceRequestedFilter($"https://{PlayerHost}/*", CoreWebView2WebResourceContext.All);
            web.AddWebResourceRequestedFilter($"https://{GameHost}/*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += OnResourceRequested;
            web.WebMessageReceived += OnWebMessage;
            // Links opened by the game (getURL / navigateToURL) are never opened inside the app
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{PlayerHost}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            UpdateBounds();
            web.Navigate($"https://{PlayerHost}/player.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Message?.Invoke("missing-runtime", "The Microsoft Edge WebView2 runtime was not found. Install it to play games inside the app.");
        }
        catch (Exception e)
        {
            Message?.Invoke("error", "Player unavailable: " + e.Message);
        }
    }

    /// <summary>One browser environment for the whole app: restarting the player does not start new browser processes.</summary>
    static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(() =>
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MortisFlash", "WebView2");
        // Let game audio start without a prior user click
        var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
        return CoreWebView2Environment.CreateAsync(null, data, options);
    });

    /// <summary>App shortcuts pressed while the game has the keyboard: F5, Ctrl+O and Ctrl+S go to the app.</summary>
    void OnAcceleratorKey(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown || e.PhysicalKeyStatus.WasKeyDown != 0) return;
        bool ctrl = GetKeyState(0x11) < 0;
        string? shortcut = (e.VirtualKey, ctrl) switch
        {
            (0x74, false) => "F5",
            (0x4F, true) => "Ctrl+O",
            (0x53, true) => "Ctrl+S",
            _ => null,
        };
        if (shortcut == null) return;
        e.Handled = true;
        Message?.Invoke("shortcut", shortcut);
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            string type = doc.RootElement.GetProperty("type").GetString() ?? "";
            string text = doc.RootElement.GetProperty("text").GetString() ?? "";
            if (type == "ready")
            {
                _ready = true;
                if (_swf != null) _ = Exec($"loadSwf('https://{GameHost}/current.swf?v={_version}')");
            }
            Message?.Invoke(type, text);
        }
        catch (Exception ex)
        {
            Message?.Invoke("error", ex.Message);
        }
    }

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_env == null) return;
        var uri = new Uri(e.Request.Uri);
        byte[]? bytes = null;
        string type = "application/octet-stream";
        string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (uri.Host.Equals(PlayerHost, StringComparison.OrdinalIgnoreCase))
        {
            // Player page and Ruffle, read from the executable's resources
            var stream = Bundle.GetManifestResourceStream("Ruffle/" + path);
            type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript", ".wasm" => "application/wasm", _ => "text/plain",
            };
            e.Response = stream != null
                ? _env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}")
                : _env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        if (path == "current.swf")
        {
            bytes = _swf;
            type = "application/x-shockwave-flash";
        }
        else if (_baseFolder != null && path.Length > 0)
        {
            // File next to the original SWF; never allowed to escape its folder
            string full = Path.GetFullPath(Path.Combine(_baseFolder, path.Replace('/', Path.DirectorySeparatorChar)));
            if (full.StartsWith(Path.GetFullPath(_baseFolder), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                bytes = File.ReadAllBytes(full);
                type = Path.GetExtension(full).ToLowerInvariant() switch
                {
                    ".swf" => "application/x-shockwave-flash", ".xml" => "text/xml", ".txt" => "text/plain",
                    ".json" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
                    ".mp3" => "audio/mpeg", _ => type,
                };
            }
        }
        string headers = $"Content-Type: {type}\nAccess-Control-Allow-Origin: *\nCache-Control: no-store";
        e.Response = bytes != null
            ? _env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", headers)
            : _env.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: *");
    }

    void UpdateBounds()
    {
        if (_controller == null) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale)), h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, w, h);
    }

    const uint WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
