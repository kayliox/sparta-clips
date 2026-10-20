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
                    if      (ev == "saved")    _ui.BeginInvoke(new Action(() => ClipToast.Show("✓  Clip saved")));
                    else if (ev == "recfail")  _ui.BeginInvoke(new Action(() => ClipToast.Show("✗  Recording failed")));
                    else if (ev == "trimfail") _ui.BeginInvoke(new Action(() => ClipToast.Show("✗  Replay save failed")));
                    else if (ev == "noreplay") _ui.BeginInvoke(new Action(() => ClipToast.Show("Nothing to save yet")));
                    else if (ev == "av1fail")  _ui.BeginInvoke(new Action(() => ClipToast.Show("AV1 encode failed — saved as H.264")));
                    else if (ev == "micfail")  _ui.BeginInvoke(new Action(() => ClipToast.Show("✗  Microphone couldn't be captured")));
                    else if (ev == "disklow")  _ui.BeginInvoke(new Action(() => ClipToast.Show("Disk almost full — recording stopped & saved")));
                    else if (ev == "marked")    _ui.BeginInvoke(new Action(() => ClipToast.Show("✓  Moment marked")));
                    else if (ev == "marknorec") _ui.BeginInvoke(new Action(() => ClipToast.Show("Not recording — nothing to mark")));
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
            var quit   = new ToolStripMenuItem("Quit Sparta Clips", null, (s, e) => QuitApp());
            menu.Items.AddRange(new ToolStripItem[] { open, settings, new ToolStripSeparator(), record, replay, armed, new ToolStripSeparator(), quit });
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
            try { ClipHotkeyHook.Uninstall(); } catch { }
            try { _gameWatch?.Stop(); _gameWatch?.Dispose(); } catch { }
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
            if (!ClipHotkeyHook.Install()) _ui.BeginInvoke(new Action(() => ClipToast.Show("⚠  Global hotkeys unavailable — try running as administrator")));
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
