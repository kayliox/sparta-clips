// The WebView2 host window (gallery + clip editor + settings). Created LAZILY by TrayCore on first "Open" and, by
// default, fully disposed when closed to tray (releaseUiOnClose) so the app's idle state carries no Chromium
// render/GPU processes — the whole point of the standalone: record/replay from the tray with a tiny footprint.
// Clip playback is an in-page HTML5 <video> now (no native surface); the drag-to-Discord proxy (MainForm.DragShare.cs)
// + frameless chrome (MainForm.Chrome.cs) are partials of this class. Bootstrap (opacity gate, first-paint handshake,
// ProcessFailed recovery, virtual hosts) is carried from
// Sparta Dock's MainForm.cs, trimmed to the clip shell.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SpartaClips
{
    public partial class MainForm : Form
    {
        // SetWindowPos + HWND_TOPMOST are used by the clip player + drag-share partials to float their native surfaces.
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const uint SWP_NOACTIVATE = 0x0010;

        internal static MainForm? Instance { get; private set; }

        private WebView2 _webView = null!;
        private BridgeApi? _bridge;
        private bool _revealed;
        private string _pendingView = "";   // view to route to once the page is ready ("" = gallery/default)

        public MainForm()
        {
            Instance = this;
            Text = "Sparta Clips";
            FormBorderStyle = FormBorderStyle.None;   // frameless: the HTML title bar (Ui/MainForm.Chrome.cs) is the caption
            MinimumSize = new System.Drawing.Size(900, 600);
            BackColor = System.Drawing.Color.FromArgb(13, 13, 16);   // match the new near-black canvas (--bg) so form/WebView/page share one bg
            // Always open centered at a comfortable default size (clamped to the screen so it fits smaller displays).
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1440, 900);
            int dw = Math.Min(1440, wa.Width - 80), dh = Math.Min(920, wa.Height - 80);
            Size = new System.Drawing.Size(Math.Max(MinimumSize.Width, dw), Math.Max(MinimumSize.Height, dh));
            StartPosition = FormStartPosition.CenterScreen;
            Opacity = 0;   // fade in only once the WebView paints the splash (RevealWindow) — no bare-frame flash

            try
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exe)) Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            }
            catch { }

            _webView = new WebView2 { Dock = DockStyle.Fill };
            try { _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(13, 13, 16); } catch { }
            Controls.Add(_webView);

            Load += async (s, e) => await InitWebViewAsync();
            Activated += (s, e) => { try { _webView?.Focus(); } catch { } };
        }

        private async Task InitWebViewAsync()
        {
            // Fallback reveal: if the first-paint message never arrives (missing runtime / dead renderer), show anyway.
            try { var ft = new System.Windows.Forms.Timer { Interval = 6000 }; ft.Tick += (s, e) => { try { ft.Stop(); ft.Dispose(); } catch { } RevealWindow(); }; ft.Start(); } catch { }
            try
            {
                string userDataFolder = AppConfig.Dir;
                // Occlusion calc left ON (default) so Chromium throttles when the window is minimized → lower idle
                // CPU/RAM. The rest disables background work (networking, updater, sync, cast) and enables HEVC decode.
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-features=Translate,MediaRouter,OptimizationHints,AutofillServerCommunication,InterestFeedContentSuggestions " +
                        "--enable-features=PlatformHEVCDecoderSupport " +
                        "--disable-background-networking --disable-component-update --disable-domain-reliability --disable-sync --no-pings"
                };
#if CDP_HOOK
                // UI-automation hook (smoke tests): SPARTACLIPS_CDP=<port> opens the Chrome DevTools Protocol on
                // localhost so tools/uidrive.py can click/eval/screenshot the real UI. Compiled only into Debug /
                // `deploy.ps1 -TestHooks` builds (see SpartaClips.csproj) and still env-var-gated; 127.0.0.1 only.
                try
                {
                    string? cdp = Environment.GetEnvironmentVariable("SPARTACLIPS_CDP");
                    if (int.TryParse(cdp, out int cdpPort) && cdpPort > 0)
                        options.AdditionalBrowserArguments += " --remote-debugging-port=" + cdpPort;
                }
                catch { }
#endif
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                await _webView.EnsureCoreWebView2Async(env);

#if !DEBUG
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                // Frameless chrome: let the HTML title bar mark draggable regions via CSS `app-region: drag` so
                // WebView2 forwards the caption drag to Windows. Best-effort — older runtimes lack the property
                // (a JS→WM_NCLBUTTONDOWN fallback in app.js covers those). See Ui/MainForm.Chrome.cs.
                try { _webView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true; } catch { }

                _bridge = new BridgeApi(_webView.CoreWebView2, this);
                _webView.CoreWebView2.AddHostObjectToScript("clipsBridge", _bridge);

                _webView.CoreWebView2.ProcessFailed += (s, e) =>
                {
                    try { DiagLog.Crash("CRASH", "WebView2 ProcessFailed: kind=" + e.ProcessFailedKind + " reason=" + e.Reason); } catch { }
                    bool browserGone = e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
                    try { BeginInvoke(new Action(() =>
                    {
                        // _forceClose bypasses the keep-resident hide — a dead browser process can't be re-shown, the
                        // form must actually dispose so OpenWindow builds a fresh one.
                        try { if (browserGone) { _forceClose = true; Close(); TrayCore.Instance?.OpenWindow(null); } else _webView.CoreWebView2.Reload(); }
                        catch { }
                    })); }
                    catch { }
                };

                string webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                string htmlPath = Path.Combine(webRoot, "index.html");
                if (!File.Exists(htmlPath))
                {
                    MessageBox.Show("Could not find web\\index.html next to the executable.",
                        "Sparta Clips", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // True first-paint handshake: the splash IIFE posts "splash-painted" after a double requestAnimationFrame
                // (renderer has actually composited a frame) — reveal then, not on NavigationCompleted (= "document loaded").
                _webView.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    string msg = ""; try { msg = e.TryGetWebMessageAsString() ?? ""; } catch { }
                    if (msg == "splash-painted") RevealWindow();
                };
                _webView.CoreWebView2.NavigationCompleted += (s, e) => { try { _webView.Focus(); } catch { } ApplyPendingView(); PostWinState(); };

                // Serve the UI from a virtual https host (secure context — Web Audio/decodeAudioData for the mic/chat
                // waveforms + setSinkId). Zero-build. Clips + thumbs get their own hosts for secure-context playback.
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "spartaclips.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);
                try { _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "clips.local", ClipRecorder.ClipsDir, CoreWebView2HostResourceAccessKind.Allow); } catch { }
                try { _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "clipthumb.local", ClipFfmpeg.ThumbDir, CoreWebView2HostResourceAccessKind.Allow); } catch { }
                try { _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(   // H.264 previews for non-native HEVC clips
                    "clipproxy.local", ClipProxy.Dir, CoreWebView2HostResourceAccessKind.Allow); } catch { }

                // No-store on our own assets so a deploy is picked up without a stale disk-cache hit (still zero-build).
                string webRootFull = Path.GetFullPath(webRoot);
                _webView.CoreWebView2.AddWebResourceRequestedFilter("https://spartaclips.local/*", CoreWebView2WebResourceContext.All);
                _webView.CoreWebView2.WebResourceRequested += (s, e) =>
                {
                    try
                    {
                        string rel = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath).TrimStart('/');
                        if (rel.Length == 0) rel = "index.html";
                        string full = Path.GetFullPath(Path.Combine(webRootFull, rel));
                        if (!full.StartsWith(webRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return;
                        var stream = new MemoryStream(File.ReadAllBytes(full));
                        string headers = "Content-Type: " + MimeFor(full) + "\r\nCache-Control: no-cache, no-store, must-revalidate";
                        e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
                    }
                    catch { }
                };

                _webView.CoreWebView2.Navigate("https://spartaclips.local/index.html");
            }
            catch (Exception ex)
            {
                try { RevealWindow(); } catch { }
                MessageBox.Show("Failed to start WebView2.\n\n" + ex.Message +
                    "\n\nMake sure the WebView2 Runtime is installed.",
                    "Sparta Clips", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RevealWindow()
        {
            if (_revealed) return; _revealed = true;
            try { Opacity = 1; } catch { }
            try { Activate(); } catch { }
        }

        // Route the page to a view ("settings", or "" for the gallery). Applied now if the page is ready, else deferred
        // to NavigationCompleted (so it works for a freshly-created window whose WebView is still cold).
        public void NavigateView(string? view)
        {
            _pendingView = view ?? "";
            try { if (_webView?.CoreWebView2 != null && _revealed) ApplyPendingView(); } catch { }
        }
        private void ApplyPendingView()
        {
            try { _webView?.CoreWebView2?.ExecuteScriptAsync("window.onRouteView && window.onRouteView(" + Newtonsoft.Json.JsonConvert.SerializeObject(_pendingView) + ")"); }
            catch { }
        }

        private static string MimeFor(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js":   return "text/javascript; charset=utf-8";
                case ".css":  return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png":  return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".svg":  return "image/svg+xml";
                case ".woff2": return "font/woff2";
                case ".woff":  return "font/woff";
                case ".ico":  return "image/x-icon";
                default:      return "application/octet-stream";
            }
        }

        // ── Close = hide to tray (keep recording). Real quit only from the tray/settings. ──
        private bool _forceClose;   // set by ProcessFailed recovery — the WebView is dead, so hide-to-tray must not intercept
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (TrayCore.Instance?.Quitting == true) { TrayCore.WebSink = null; base.OnFormClosing(e); return; }
            // The X keeps Sparta Clips running in the tray — but Windows 11 hides new tray icons behind the ^ overflow, so
            // the first close looked like a quit. Say where it went (once).
            if (e.CloseReason == CloseReason.UserClosing && !_forceClose) { try { TrayCore.Instance?.ShowClosedToTrayHintOnce(); } catch { } }
            if (_forceClose || AppConfig.ReleaseUiOnClose())
            {
                // Let the close proceed → disposes the WebView2 (frees the Chromium render/GPU processes). TrayCore
                // recreates a fresh window on the next open. Only NOW drop the web sink — in the hide-to-tray branch
                // the page stays alive, and clearing the sink there left the gallery deaf to saves after a re-show
                // (EnsureCaptureWired's _captureWired guard never re-registered it).
                try { TrayCore.WebSink = null; } catch { }
                try { TrayCore.Instance?.OnWindowReleased(); } catch { }
                base.OnFormClosing(e);
            }
            else { e.Cancel = true; HideDragProxy(); Hide(); }   // keep resident for instant reopen — the sink stays live; the drag proxy must NOT (it floats at screen coords)
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            try { _dragProxy?.Close(); _dragProxy?.Dispose(); _dragProxy = null; } catch { }   // top-level helper window — nothing else owns it
            base.OnFormClosed(e);
        }

    }
}
