// The always-on heart of Sparta Clips. Runs as the ApplicationContext on the STA/message-pump thread with NO WebView2
// resident: it owns the ClipRecorder engine, the low-level hotkey hook, the instant-replay arming policy and the tray
// icon. The WebView2 window (MainForm) is created lazily on "Open" and disposed on close (releaseUiOnClose), so the
// idle footprint is just this + the (optional) replay ring buffer. There is no game launcher to arm replay for us, so
// the arming policy ("always" while running, or "game" = only when a fullscreen game is foreground) lives here.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpartaClips
{
    internal sealed class TrayCore : ApplicationContext
    {
        public static TrayCore? Instance { get; private set; }
        // Set by the bridge while the UI is open so recorder events also reach JS (window.onCapture); null otherwise.
        public static Action<string, string>? WebSink;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        private readonly NotifyIcon _tray;
        private readonly Control _ui = new();   // hidden marshaling sink so background recorder events can hit the UI thread
        private MainForm? _win;
        private string _armPolicy = "always";
        private bool _replayEnabled = true;   // "instant replay" master toggle; the JS settings push it via SetReplayEnabled
        private System.Windows.Forms.Timer? _gameWatch;
        private long _lastRecFire, _lastRepFire;

        public bool Quitting { get; private set; }

        public TrayCore(bool startHidden)
        {
            Instance = this;
            var _ = _ui.Handle;   // force-create the handle on this (message-pump) thread for marshaling

            AppConfig.ApplyEngineSettings();   // push saved capture prefs to the engine so hotkey recording works pre-UI

            // Recorder → native toast (ALWAYS, so failures reach the user with no window open — the whole tray-first premise)
            // + JS (when a window is open). Owned here, not the bridge, so it fires tray-only.
            ClipRecorder.Notify = (ev, json) =>
            {
                try
                {
                    // "saving" fires the instant the hotkey lands (with the chime) → the toast morphs into "saved" when the
                    // clip is actually on disk. before: nothing visible until the save + AV1 conversion finished.
                    if      (ev == "saving")    Toast(ToastKind.Busy,    "Saving replay…", "Last " + ClipRecorder.ReplayLenSeconds + " seconds");
                    else if (ev == "saved")     Toast(ToastKind.Success, SavedTitle(json), SavedDetail(json));
                    else if (ev == "recfail")   Toast(ToastKind.Error,   "Recording failed", JsonField(json, "reason") ?? "Try again in a moment");
                    else if (ev == "trimfail")  Toast(ToastKind.Error,   "Couldn't save the replay", "Try again in a moment");
                    else if (ev == "noreplay")  Toast(ToastKind.Warning, "Nothing to save yet", "Instant replay isn't running");
                    else if (ev == "av1fail")   Toast(ToastKind.Warning, "Saved as H.264", "AV1 conversion failed — your clip is safe");
                    else if (ev == "micfail")   Toast(ToastKind.Error,   "Microphone unavailable", "Check Windows microphone privacy settings");
                    else if (ev == "disklow")   Toast(ToastKind.Warning, "Disk almost full", "Recording stopped and saved");
                    else if (ev == "marked")    Toast(ToastKind.Success, "Moment marked", "Bookmark added at this point");
                    else if (ev == "marknorec") Toast(ToastKind.Warning, "Not recording", "Start a recording to mark moments");
                    else if (ev == "state" && json != null)
                    {   // in-game "● REC m:ss" overlay follows the recording state (gated by the settings toggle)
                        if (json.Contains("\"recording\":true")) { if (AppConfig.OverlayEnabled()) _ui.BeginInvoke(new Action(ClipStatusOverlay.Start)); }
                        else if (json.Contains("\"recording\":false")) _ui.BeginInvoke(new Action(ClipStatusOverlay.Stop));
                    }
                }
                catch { }
                // Re-arm the instant-replay ring after a manual recording that had suspended it (Done posts recording:false).
                try
                {
                    if (_suspendedForRecord && ev == "state" && json != null && json.Contains("\"recording\":false"))
                    {
                        _suspendedForRecord = false;
                        _ui.BeginInvoke(new Action(ApplyArmPolicy));
                    }
                }
                catch { }
                try { WebSink?.Invoke(ev, json ?? "{}"); } catch { }
            };

            InstallHotkeys();

            _armPolicy = AppConfig.ArmPolicy();
            _replayEnabled = AppConfig.ReplayEnabled();
            if (AppConfig.AutoHighlight()) HighlightCoordinator.Enabled = true;
            ApplyArmPolicy();

            _tray = BuildTray();

            // Pre-warm the AV1 capability probes off-thread (MFT enumeration + `ffmpeg -encoders`, worst-case seconds).
            // The results are cached, so the UI's synchronous HasHardwareAv1 bridge call later returns instantly instead
            // of blocking the message pump — which also hosts the LL keyboard hook (a stall risks OS hook eviction).
            Task.Run(() => { try { bool warm = HardwareAv1.Available; warm |= Av1Transcoder.HasHardwareAv1(); } catch { } });
            Task.Run(ClipRecorder.RequestBorderlessCaptureAsync);   // hide the Win11 yellow capture border (before the first capture rolls)

            if (!startHidden) OpenWindow(null);
            else ShowFirstRunTrayHint();   // silent launch → tell the user (once) it's alive in the tray

            StartUpdateChecks();
            if (Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "--recovered", StringComparison.OrdinalIgnoreCase)))
            {
                var t = new System.Windows.Forms.Timer { Interval = 2500 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); Toast(ToastKind.Warning, "Sparta Clips restarted", "It stopped unexpectedly — instant replay is running again"); };
                t.Start();
            }
            if (JustUpdated)
            {
                var t = new System.Windows.Forms.Timer { Interval = 2500 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); Toast(ToastKind.Success, "Sparta Clips updated", "Now on version " + AppUpdater.VersionText(AppUpdater.Current)); };
                t.Start();
            }
        }

        // ── Self-update (AppUpdater + UpdatePrompt) ──
        // Launched by the installer after an update ("--updated") → a one-time "updated" notice (native toast + the web UI).
        public static readonly bool JustUpdated = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase));
        private System.Windows.Forms.Timer? _updTimer;
        private ReleaseInfo? _pendingUpdate;          // found, waiting for a moment when no game/recording is up front
        private DateTime _nextUpdCheck = DateTime.UtcNow.AddSeconds(40);
        private bool _updBusy;
        private void StartUpdateChecks()
        {
            System.Threading.Tasks.Task.Run(AppUpdater.CleanupDownloads);
            _updTimer = new System.Windows.Forms.Timer { Interval = 30000 };
            _updTimer.Tick += (s, e) => UpdateTick();
            _updTimer.Start();
        }
        private async void UpdateTick()
        {
            if (_pendingUpdate != null) { TryShowUpdate(); return; }
            if (_updBusy || DateTime.UtcNow < _nextUpdCheck) return;
            _nextUpdCheck = DateTime.UtcNow.AddHours(6);
            if (!AppConfig.AutoUpdate() || !AppUpdater.IsInstalledCopy) return;   // a portable/dev copy never updates itself
            _updBusy = true;
            try
            {
                var r = await AppUpdater.FetchLatestAsync();
                if (r != null && r.Version > AppUpdater.Current && AppUpdater.ShouldOffer(r))
                {
                    try { DiagLog.Line("UPDATE", "available: " + AppUpdater.VersionText(r.Version)); } catch { }
                    _pendingUpdate = r; _ui.BeginInvoke(new Action(TryShowUpdate));   // UI thread, whatever the await resumed on
                }
            }
            catch { }
            finally { _updBusy = false; }
        }
        // Never pop over a game or during a recording — wait (re-tried every 30 s) until the user is back on the desktop.
        private bool _updDeferLogged;
        private void TryShowUpdate()
        {
            if (_pendingUpdate == null || Quitting) return;
            if (UpdatePrompt.IsOpen) { _pendingUpdate = null; return; }
            if (ClipRecorder.IsRecording || ForegroundIsFullscreenGame())
            {
                if (!_updDeferLogged) { _updDeferLogged = true; try { DiagLog.Line("UPDATE", "prompt waits — " + (ClipRecorder.IsRecording ? "recording" : "fullscreen game in front")); } catch { } }
                return;
            }
            var r = _pendingUpdate; _pendingUpdate = null; _updDeferLogged = false;
            // Shown on top but WITHOUT taking focus — whatever the user is typing/playing in keeps the keyboard.
            try { UpdatePrompt.ShowFor(r, WindowIsOpen, activate: false); DiagLog.Line("UPDATE", "prompt shown"); }
            catch (Exception ex) { try { DiagLog.Line("UPDATE", "prompt failed: " + ex); } catch { } }
        }
        // Stricter than ForegroundIsGame for the update prompt: an ordinary MAXIMIZED window (browser, Parsec, an
        // editor) reports a rect covering the whole monitor when that monitor has no taskbar — that's not a game.
        // Borderless-fullscreen / exclusive games have no caption and aren't "zoomed".
        private bool ForegroundIsFullscreenGame()
        {
            if (!ForegroundIsGame()) return false;
            try
            {
                IntPtr fg = GetForegroundWindow();
                bool caption = (GetWindowLong(fg, -16 /*GWL_STYLE*/) & 0x00C00000 /*WS_CAPTION*/) == 0x00C00000;
                return !(IsZoomed(fg) && caption);
            }
            catch { return true; }
        }
        private bool WindowIsOpen() => _win != null && !_win.IsDisposed && _win.Visible;

        /// <summary>"Check now" (settings / tray). Reports {state: checking|uptodate|available|dev|error, latest} to
        /// report (the web UI) — or as a toast when null (tray). An available update opens the prompt right away
        /// (skip/snooze don't apply to a manual check).</summary>
        public void CheckForUpdatesNow(Action<string>? report)
        {
            _ui.BeginInvoke(new Action(async () =>
            {
                void Say(string state, string latest = "") { try { report?.Invoke(Newtonsoft.Json.JsonConvert.SerializeObject(new { state, latest })); } catch { } }
                if (_updBusy) return;
                _updBusy = true; Say("checking");
                try
                {
                    var r = await AppUpdater.FetchLatestAsync();
                    string cur = AppUpdater.VersionText(AppUpdater.Current);
                    if (r == null) { Say("error"); if (report == null) Toast(ToastKind.Warning, "Couldn't check for updates", "Check your connection and try again"); }
                    else if (r.Version <= AppUpdater.Current) { Say("uptodate", AppUpdater.VersionText(r.Version)); if (report == null) Toast(ToastKind.Success, "You're up to date", "Sparta Clips " + cur); }
                    else if (!AppUpdater.IsInstalledCopy) { Say("dev", AppUpdater.VersionText(r.Version)); if (report == null) Toast(ToastKind.Warning, "Version " + AppUpdater.VersionText(r.Version) + " is out", "This copy isn't installed — get it from GitHub"); }
                    else { Say("available", AppUpdater.VersionText(r.Version)); UpdatePrompt.ShowFor(r, WindowIsOpen, activate: true); }
                }
                catch (Exception ex) { Say("error"); try { DiagLog.Line("UPDATE", "manual check failed: " + ex.Message); } catch { } }
                finally { _updBusy = false; }
            }));
        }

        // One-time balloon on the first tray/autostart launch so a silent start doesn't feel like the app "did nothing".
        private void ShowFirstRunTrayHint()
        {
            try
            {
                string marker = System.IO.Path.Combine(AppConfig.Dir, ".trayhint");
                if (System.IO.File.Exists(marker)) return;
                System.IO.Directory.CreateDirectory(AppConfig.Dir);
                System.IO.File.WriteAllText(marker, "1");
                _tray.BalloonTipTitle = "Sparta Clips is running";
                _tray.BalloonTipText  = "Recording in the background. Right-click the tray icon for options, or double-click to open.";
                _tray.ShowBalloonTip(6000);
            }
            catch { }
        }

        // First time the window's X is used: a balloon from the tray icon itself, so the user sees the app is still there.
        public void ShowClosedToTrayHintOnce()
        {
            _ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    string marker = System.IO.Path.Combine(AppConfig.Dir, ".closehint");
                    if (System.IO.File.Exists(marker)) return;
                    System.IO.Directory.CreateDirectory(AppConfig.Dir);
                    System.IO.File.WriteAllText(marker, "1");
                    _tray.BalloonTipTitle = "Sparta Clips is still running";
                    _tray.BalloonTipText  = "It's in the tray by the clock (under ^ if hidden), still saving instant replays. Right-click the icon to quit.";
                    _tray.ShowBalloonTip(7000);
                }
                catch { }
            }));
        }

        // ── Tray icon + menu ──
        private NotifyIcon BuildTray()
        {
            Icon icon;
            try { icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath) ?? SystemIcons.Application; }
            catch { icon = SystemIcons.Application; }

            var menu = new ContextMenuStrip();
            var open   = new ToolStripMenuItem("Open Sparta Clips", null, (s, e) => OpenWindow(null)) { Font = new Font(menu.Font, FontStyle.Bold) };
            var settings = new ToolStripMenuItem("Settings…", null, (s, e) => OpenWindow("settings"));
            var record = new ToolStripMenuItem("Start recording", null, (s, e) => FireRecordToggle());
            var replay = new ToolStripMenuItem("Save replay", null, (s, e) => FireSaveReplay());
            var armed  = new ToolStripMenuItem("Instant replay armed", null, (s, e) => ToggleArmManual());
            var update = new ToolStripMenuItem("Check for updates", null, (s, e) => CheckForUpdatesNow(null));
            var quit   = new ToolStripMenuItem("Quit Sparta Clips", null, (s, e) => QuitApp());
            menu.Items.AddRange(new ToolStripItem[] { open, settings, new ToolStripSeparator(), record, replay, armed, new ToolStripSeparator(), update, quit });
            menu.Opening += (s, e) =>
            {
                try
                {
                    record.Text = ClipRecorder.IsRecording ? "Stop recording" : "Start recording";
                    armed.Checked = ClipRecorder.IsReplayArmed;
                    replay.Enabled = ClipRecorder.IsReplayArmed;
                }
                catch { }
            };

            var tray = new NotifyIcon { Icon = icon, Text = "Sparta Clips", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => OpenWindow(null);
            return tray;
        }

        // ── Lazy WebView2 window ──
        public void OpenWindow(string? view)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_win == null || _win.IsDisposed) { _win = new MainForm(); _win.Show(); }
                    else if (!_win.Visible) _win.Show();
                    _win.WindowState = FormWindowState.Normal;
                    _win.Activate();
                    _win.NavigateView(view);   // deferred until the page is ready for a freshly-created window
                }
                catch (Exception ex) { try { DiagLog.Line("UI", "open err: " + ex.Message); } catch { } }
            }));
        }
        // Called by MainForm when it closes under releaseUiOnClose so the next open builds fresh.
        public void OnWindowReleased() { _win = null; }

        public void QuitApp()
        {
            Quitting = true;
            Watchdog.MarkCleanExit();   // a deliberate quit — the watchdog must not bring the app back
            try { ClipHotkeyHook.Uninstall(); } catch { }
            try { _gameWatch?.Stop(); _gameWatch?.Dispose(); } catch { }
            try { _updTimer?.Stop(); _updTimer?.Dispose(); } catch { }
            // Finalize an in-progress MANUAL recording first — before: only the replay ring was disarmed, so quitting
            // mid-record tore the process down around an unfinalized MP4 (no moov → unplayable) + partial sidecars.
            try { ClipRecorder.StopAndWait(4000); } catch { }
            // Then wait (bounded) for the ring to flush. Run on the pool + Wait so the inner awaits don't resume on
            // this UI thread (which we're blocking).
            try { Task.Run(async () => { try { await ClipRecorder.DisarmAsync(); } catch { } }).Wait(2500); } catch { }
            try { _win?.Close(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            ExitThread();
        }

        // ── Hotkeys (LL keyboard hook — fires even over a fullscreen anti-cheat game) ──
        private void InstallHotkeys()
        {
            var (rm, rv, pm, pv, mm, mv) = AppConfig.Hotkeys();
            ClipHotkeyHook.Configure(rm, rv, pm, pv, mm, mv, FireRecordToggle, FireSaveReplay, FireMarkMoment);
            if (!ClipHotkeyHook.Install()) Toast(ToastKind.Warning, "Hotkeys unavailable", "Try running Sparta Clips as administrator");
        }
        private void Toast(ToastKind kind, string title, string sub) { try { _ui.BeginInvoke(new Action(() => ClipToast.Show(kind, title, sub))); } catch { } }
        private static string? JsonField(string? json, string key)
        {
            try { return string.IsNullOrEmpty(json) ? null : (string?)Newtonsoft.Json.Linq.JObject.Parse(json)[key]; } catch { return null; }
        }
        // "Game_20.10.2026.17.03_replay.mp4" → "Replay saved" / "Game · 30 s"
        private static string SavedTitle(string? json)
        {
            string n = JsonField(json, "name") ?? "";
            return n.Contains("_replay", StringComparison.OrdinalIgnoreCase) ? "Replay saved" : "Clip saved";
        }
        private static string SavedDetail(string? json)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(JsonField(json, "name") ?? "");
            var m = System.Text.RegularExpressions.Regex.Match(n, @"^(.*)_\d{2}\.\d{2}\.\d{4}\.\d{2}\.\d{2}");
            string game = (m.Success ? m.Groups[1].Value : n).Replace('_', ' ').Trim();
            bool replay = n.Contains("_replay", StringComparison.OrdinalIgnoreCase);
            // The real clip length (a replay right after switching windows is shorter than the replay setting).
            double secs = 0;
            try { string? full = ClipLibrary.PathOf(JsonField(json, "name") ?? ""); if (full != null) secs = ClipLibrary.Mp4DurationSeconds(full); } catch { }
            string len = secs >= 60 ? $"{(int)secs / 60}:{(int)secs % 60:00}" : secs > 0 ? Math.Max(1, (int)Math.Round(secs)) + " s" : ClipRecorder.ReplayLenSeconds + " s";
            return (game.Length > 0 ? game + "  ·  " : "") + (replay || secs > 0 ? len : "");
        }
        // Rebind from the settings UI; persisted by the web layer into config.json (state). Returns whether the hook is active.
        public bool SetClipHotkeys(uint recMods, uint recVk, uint repMods, uint repVk, uint markMods, uint markVk)
        {
            try { ClipHotkeyHook.Configure(recMods, recVk, repMods, repVk, markMods, markVk, FireRecordToggle, FireSaveReplay, FireMarkMoment); return ClipHotkeyHook.Install(); }
            catch { return false; }
        }
        // Mark-a-moment hotkey → bookmark the running recording (no-op with a toast if nothing is recording).
        private long _lastMarkFire;
        public void FireMarkMoment()
        {
            long now = Environment.TickCount64; if (now - _lastMarkFire < 400) return; _lastMarkFire = now;
            Task.Run(() => { try { ClipRecorder.MarkMoment(); } catch { } });
        }
        // Manual record must not silently no-op while the instant-replay ring owns the device (the default config).
        // Suspend the ring → record → re-arm on stop (via the Notify recording:false hook above).
        private bool _suspendedForRecord;
        public void FireRecordToggle()
        {
            long now = Environment.TickCount64; if (now - _lastRecFire < 800) return; _lastRecFire = now;
            Task.Run(async () =>
            {
                try
                {
                    if (ClipRecorder.IsRecording) { ClipRecorder.Stop(); return; }   // Done() → re-arm via Notify
                    if (ClipRecorder.IsReplayArmed) { _suspendedForRecord = true; await ClipRecorder.DisarmAsync(); }
                    if (!ClipRecorder.Start(ClipRecorder.LastGameName) && _suspendedForRecord)
                    { _suspendedForRecord = false; _ui.BeginInvoke(new Action(ApplyArmPolicy)); }   // start failed → restore the ring
                }
                catch { }
            });
        }
        public void FireSaveReplay()
        {
            long now = Environment.TickCount64; if (now - _lastRepFire < 800) return; _lastRepFire = now;
            Task.Run(async () => { try { await ClipRecorder.SaveReplayAsync(); } catch { } });
        }

        // ── Instant-replay arming policy (C# owns arming; the JS only reports enabled/policy — it never arms directly) ──
        public void SetArmPolicy(string mode)
        {
            _armPolicy = mode == "game" ? "game" : "always";
            ApplyArmPolicy();
        }
        // The "instant replay" master toggle from settings. before: the JS armed/disarmed the ring itself (built for the
        // launcher's per-game arming, which fought this class). after: the JS pushes only the enabled flag; we arm.
        public void SetReplayEnabled(bool on)
        {
            _replayEnabled = on;
            ApplyArmPolicy();
        }
        private void ApplyArmPolicy()
        {
            try { _gameWatch?.Stop(); _gameWatch?.Dispose(); _gameWatch = null; } catch { }
            if (!_replayEnabled) { Disarm(); return; }
            if (_armPolicy == "game")
            {
                _gameWatch = new System.Windows.Forms.Timer { Interval = 3000 };
                _gameWatch.Tick += (s, e) => { if (ForegroundIsGame()) Arm(""); else Disarm(); };
                _gameWatch.Start();
                if (ForegroundIsGame()) Arm("");
            }
            else Arm("");   // always armed while running
        }
        private void ToggleArmManual() { if (ClipRecorder.IsReplayArmed) Disarm(); else Arm(""); }
        private void Arm(string game)
        {
            // NEVER arm over a live manual recording — ArmAsync Stop()s any active recording first, so the 3s game
            // watcher (armPolicy "game") would cut the user's session to a ~3s stub. The suspended flag covers the
            // window between FireRecordToggle's disarm and Start() flipping IsRecording.
            if (ClipRecorder.IsRecording || _suspendedForRecord) return;
            if (ClipRecorder.IsReplayArmed) return;
            Task.Run(async () =>
            {
                try { await ClipRecorder.ArmAsync(game); if (HighlightCoordinator.Enabled) HighlightCoordinator.Start(); } catch { }
            });
        }
        private void Disarm()
        {
            if (!ClipRecorder.IsReplayArmed) return;
            Task.Run(async () => { try { HighlightCoordinator.Stop(); await ClipRecorder.DisarmAsync(); } catch { } });
        }

        // Heuristic "a fullscreen game is up front": the foreground window covers its whole monitor and isn't the
        // desktop/shell or our own window. Good enough for auto-arm; the user can also force "always".
        private bool ForegroundIsGame()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
                if (_win != null && !_win.IsDisposed && fg == _win.Handle) return false;
                if (!GetWindowRect(fg, out var r)) return false;
                var scr = Screen.FromHandle(fg).Bounds;
                return r.L <= scr.Left && r.T <= scr.Top && r.R >= scr.Right && r.B >= scr.Bottom;
            }
            catch { return false; }
        }
    }
}
