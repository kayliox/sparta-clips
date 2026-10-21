// Game-clip capture (ScreenRecorderLib → Windows.Graphics.Capture + Media Foundation hardware H.264).
// PHASE 1/1.5: manual + auto-armed full-session recording to an MP4 named GameName_dd.MM.yyyy.HH.mm.mp4, with the
// quality/mic/capture-mode/storage-cap settings from the "Game Capture" pane applied via RecorderOptions (BuildOptions).
// SEPARATE MIC TRACK: when enabled the mic is kept OUT of the MP4 and captured to a sidecar <stem>.mic.wav (NAudio
// WASAPI) so the clip editor can show/trim/mix it as its own track. (Instant-replay ring buffer = Phase 2.)
// SELF-CONTAINED + deletable: drop this file, ClipLibrary.cs, BridgeApi.Capture.cs and the MainForm/launch hooks
// and the launcher behaves exactly as before.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorderLib;

namespace SpartaClips
{
    internal static class ClipRecorder
    {
        private static Recorder? _rec;
        private static volatile bool _recording;
        private static string _customDir = "";

        public static bool IsRecording => _recording;
        /// <summary>Seconds since the current recording started (0 when idle) — lets a window opened MID-recording
        /// (hotkey start with no UI) show the true elapsed instead of counting from window-open.</summary>
        public static double RecElapsedSec => _recording ? Math.Max(0, (Environment.TickCount64 - _recStartTick) / 1000.0) : 0;

        /// <summary>Live-engine snapshot for the diagnostic tools (tools/stress.py asserts invariants against this —
        /// e.g. "disarmed ⇒ no live recorder, no rotation timer, rings off, buffer not growing"). Read-only.</summary>
        public static string DiagJson()
        {
            int bufFiles = 0; long bufBytes = 0;
            try
            {
                foreach (var f in new DirectoryInfo(BufferDir).GetFiles("*.mp4"))
                {
                    bufFiles++;
                    long len = f.Length;
                    // FileInfo.Length is DIRECTORY metadata — stale for the actively-written segment (NTFS updates it
                    // on flush/close), which made "is the buffer growing?" probes lie. Open for the true EOF.
                    try { using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); len = fs.Length; } catch { }
                    bufBytes += len;
                }
            }
            catch { }
            bool elevated = false;
            try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); elevated = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); } catch { }
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                recording = _recording,
                armed = _armed,
                recAlive = _rec != null,
                repRecAlive = _repRec != null,
                rotTimerAlive = _rotTimer != null,
                bufFiles,
                bufBytes,
                micRing = _micSide.RingActive,
                chatRing = _chatSide.RingActive,
                srcMode = _srcMode,
                srcHwnd = (long)_srcHwnd,
                elevated,
            });
        }
        public static Action<string, string>? Notify;   // (event, jsonDetail) → JS; wired by BridgeApi

        // ── Capture settings (pushed from the "Game Capture" settings pane via BridgeApi.SetCaptureSettings) ──
        private static int  _fps     = 60;       // frames/sec
        private static int  _mbps    = 20;       // video bitrate, Mbps
        private static int  _resH    = 0;        // output height (0 = source size); width derived 16:9
        private static bool _mic     = false;    // include microphone alongside system audio
        private static bool _sepMic  = false;    // record the mic to a separate sidecar (.mic.wav) instead of mixing it in
        private static bool _chat    = false;    // capture a separate "chat" track (.chat.wav) from a chosen output device (loopback)
        private static bool _gameCap = false;    // capture the foreground game window vs the whole display
        private static long _capBytes = 0;       // storage cap (0 = unlimited); oldest clips pruned after each save
        private static string _encoder = "h264"; // "h264" · "h265" · "av1" (final clips are H.264 by default — see ClipFfmpeg)
        private static bool _forceSwEncode;      // set for a software-encode retry after a hardware-encode failure
        private static System.Threading.Timer? _diskTimer;   // stops a long recording if free space runs out
        private const long MinFreeBytes = 1L * 1024 * 1024 * 1024;   // refuse/stop below 1 GB free on the clips drive
        // True when the clips drive has less than the floor free — used to refuse a record before it silently fails on a full disk.
        private static bool DiskLow()
        {
            try { var root = Path.GetPathRoot(Path.GetFullPath(ClipsDir)); if (string.IsNullOrEmpty(root)) return false; return new DriveInfo(root).AvailableFreeSpace < MinFreeBytes; }
            catch { return false; }
        }
        private static bool _av1ForceSw = false;  // AV1 — software choice → always libSVT-AV1 (works on any GPU, e.g. RX 5700 XT)
        internal static bool Av1ForceSw => _av1ForceSw;
        internal static int Mbps => EffectiveMbps();   // resolution-scaled bitrate (see EffectiveMbps) — also what the MediaComposition fallback renders at
        // The bitrate dropdown is calibrated for 1080p. Scale it by the capture's pixel-count (height², same aspect)
        // so a lower resolution makes a proportionally smaller file at the same per-pixel quality — and a higher one
        // keeps quality. Previously the encode ran constant-Quality and the bitrate didn't track resolution at all, so
        // 720p vs 1080p produced the same size + estimate. The JS size estimate mirrors this exact formula.
        private static int EffectiveMbps()
        {
            // Window capture records at the window's NATIVE size (OutputFrameSize unset), so budget for that height —
            // scaling by the 1080 setting starved a 3440x1440 game window (~0.56x the bits it needed).
            int h = _capH > 0 ? _capH : _resH > 0 ? _resH : (System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080);
            double scale = Math.Clamp((h / 1080.0) * (h / 1080.0), 0.25, 4.0);
            return Math.Max(1, (int)Math.Round(_mbps * scale));
        }
        internal static int Fps => _fps;
        private static bool _chime = true;        // play a confirmation chime when an instant replay is captured
        public static void SetSettings(int fps, int mbps, int resH, bool mic, bool sepMic, bool chat, bool gameCapture, long capBytes, int replayLen, string encoder, bool chime)
        {
            bool wasMicRing = _sepMic && _mic;   // which replay rings were wanted BEFORE this change
            bool wasChatRing = _chat;
            // While a per-game profile holds the live fps/mbps/resH, route the user's change into the SAVED globals —
            // otherwise RestoreGameProfile would clobber it with the stale pre-profile snapshot when recording stops.
            if (_profileActive)
            { _savedFps = Math.Clamp(fps, 15, 240); _savedMbps = Math.Clamp(mbps, 1, 200); _savedResH = resH <= 0 ? 0 : Math.Clamp(resH, 480, 4320); }
            else
            { _fps = Math.Clamp(fps, 15, 240); _mbps = Math.Clamp(mbps, 1, 200); _resH = resH <= 0 ? 0 : Math.Clamp(resH, 480, 4320); }
            _mic = mic; _sepMic = sepMic; _chat = chat; _gameCap = gameCapture; _capBytes = Math.Max(0, capBytes);   // (fps/mbps/resH assigned above, profile-aware)
            _replayLen = Math.Clamp(replayLen <= 0 ? 30 : replayLen, 5, 600);
            // encoder: "h264" · "h265" · "av1_hw" · "av1_sw" (legacy "av1" = hardware-preferred)
            _av1ForceSw = encoder == "av1_sw";
            _encoder = (encoder == "h265") ? "h265" : (encoder == "av1" || encoder == "av1_hw" || encoder == "av1_sw") ? "av1" : "h264";
            _chime = chime;
            // Toggling Separate-mic / Chat while a replay is ALREADY armed used to do nothing until a restart (the ring
            // only starts inside ArmAsync, which early-returns when armed) — so the next saved replay had no mic track.
            // Reconcile the rings live so the change takes effect immediately. Through _repGate so it can't race ArmAsync
            // into two captures on one buffer (the doubled-mic bug).
            bool nowMicRing = _sepMic && _mic, nowChatRing = _chat;
            if (_armed && (nowMicRing != wasMicRing || nowChatRing != wasChatRing))
                _ = ReconcileRingsAsync(nowMicRing, wasMicRing, nowChatRing, wasChatRing);
        }
        private static async Task ReconcileRingsAsync(bool micOn, bool micWas, bool chatOn, bool chatWas)
        {
            await _repGate.WaitAsync();
            try
            {
                if (!_armed) return;   // disarmed meanwhile → the next ArmAsync sets up rings fresh
                int ringSec = Math.Max(2 * _replayLen, 120);
                if (micOn && !micWas) _micSide.StartRing(ringSec); else if (!micOn && micWas) _micSide.StopRing();
                if (chatOn && !chatWas) _chatSide.StartRing(ringSec); else if (!chatOn && chatWas) _chatSide.StopRing();
                DiagLog.Line("CLIP", "rings reconciled live (mic=" + micOn + " chat=" + chatOn + ")");
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ring reconcile err: " + ex.Message); }
            finally { _repGate.Release(); }
        }

        // AV1 = transcode-on-save (ScreenRecorderLib captures an H.264 intermediate in TEMP, ffmpeg → AV1 in ClipsDir).
        // Dual-screen forces H.264 end-to-end (locked for compatibility) so it never goes down the AV1 transcode path.
        private static bool Av1Mode => _encoder == "av1" && !_dualOn;
        private static string _finalPath = "";   // for AV1: the ClipsDir destination (recorder writes to a temp first)
        internal static int Av1Kbps => Math.Max(2000, EffectiveMbps() * 600);   // ~0.6x the (resolution-scaled) bitrate, in kbps
        private static string Av1TempDir
        {
            get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsAv1"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }

        /// <summary>The mic sidecar path for a clip's full MP4 path: <stem>.mic.wav (in the same folder).</summary>
        internal static string MicSidecar(string mp4FullPath) =>
            Path.Combine(Path.GetDirectoryName(mp4FullPath) ?? "", Path.GetFileNameWithoutExtension(mp4FullPath) + ".mic.wav");
        /// <summary>The chat sidecar path for a clip's full MP4 path: <stem>.chat.wav (in the same folder).</summary>
        internal static string ChatSidecar(string mp4FullPath) =>
            Path.Combine(Path.GetDirectoryName(mp4FullPath) ?? "", Path.GetFileNameWithoutExtension(mp4FullPath) + ".chat.wav");
        /// <summary>The mark sidecar for a clip's MP4: <stem>.marks.json — mid-record bookmark offsets (seconds).</summary>
        internal static string MarksSidecar(string mp4FullPath) =>
            Path.Combine(Path.GetDirectoryName(mp4FullPath) ?? "", Path.GetFileNameWithoutExtension(mp4FullPath) + ".marks.json");

        // ── Mark-a-moment: a 3rd global hotkey drops a bookmark on the IN-PROGRESS full recording; written to the
        //    clip's .marks.json on save so the editor can show them (works with no window open). Ignored for replay. ──
        private static long _recStartTick;
        private static readonly List<double> _marks = new();
        /// <summary>Hotkey action — bookmark the current instant of the running recording (offset from record start).</summary>
        public static void MarkMoment()
        {
            if (!_recording) { Notify?.Invoke("marknorec", "{}"); return; }
            double sec = Math.Max(0, (Environment.TickCount64 - _recStartTick) / 1000.0);
            lock (_marks) _marks.Add(sec);
            Notify?.Invoke("marked", Newtonsoft.Json.JsonConvert.SerializeObject(new { t = sec }));
        }
        // Flush the buffered marks next to the finished clip (once — the buffer is cleared). No file when nothing was marked.
        private static void WriteMarks(string mp4) => WriteMarks(mp4, SnapshotMarks());
        // Snapshot + clear the shared buffer NOW (at Done), so an AV1 transcode finishing minutes later can't flush a
        // NEWER recording's in-progress marks into the old clip's sidecar (and the old clip's marks aren't lost to
        // Start()'s buffer clear).
        private static double[] SnapshotMarks() { lock (_marks) { var a = _marks.ToArray(); _marks.Clear(); return a; } }
        private static void WriteMarks(string mp4, double[] arr)
        {
            if (arr.Length == 0) return;
            try { File.WriteAllText(MarksSidecar(mp4), Newtonsoft.Json.JsonConvert.SerializeObject(arr)); } catch { }
        }

        // Separate audio tracks (mic input + chat output-loopback), each captured to its own sidecar / replay ring.
        private static string _micDeviceId = "", _chatDeviceId = "";
        private static readonly AudioSidecar _micSide  = new AudioSidecar(() => _micDeviceId, loopback: false, "mic");
        private static readonly AudioSidecar _chatSide = new AudioSidecar(() => _chatDeviceId, loopback: true,  "chat");
        // Per-app GAME audio (see GameAudioCapture): captures only the captured window's process tree, so the clip's
        // sound no longer depends on the output device's volume slider (SteelSeries Sonar at 5% recorded ~silence).
        // Swapped into the finished MP4 by ClipFfmpeg.ReplaceAudioAsync; without a game window (display mode) or on
        // Windows without process loopback, the recorder's own output-device audio stays (the old behavior).
        private static readonly AudioSidecar _gameSide = new AudioSidecar(MakeGameCapture, "game");
        private static int _gamePid;           // pid of the window BuildOptions targeted (0 = display mode / none)
        private static int _gameRingPid;       // pid the replay game ring is capturing
        private static string _gameWav = "";   // full-recording game sidecar (TEMP), consumed by Done()
        private static NAudio.Wave.IWaveIn? MakeGameCapture()
        {
            int pid = _gamePid; if (pid <= 0) return null;
            try { return new GameAudioCapture(pid); }
            catch (Exception ex) { DiagLog.Line("CLIP", "game audio unavailable (keeping device audio): " + ex.Message); return null; }
        }
        private static int GameRingSeconds => Math.Max(2 * _replayLen, 120);
        // Follow the replay buffer's source: re-target the game ring when a new segment captures a different process.
        private static void SyncGameRing()
        {
            if (_gamePid == _gameRingPid) return;
            _gameRingPid = _gamePid;
            if (_gamePid > 0) _gameSide.StartRing(GameRingSeconds); else _gameSide.StopRing();
        }
        public static void SetMicDevice(string? id)  { _micDeviceId  = id ?? ""; }
        public static void SetChatDevice(string? id) { _chatDeviceId = id ?? ""; }

        // ── Dual-screen capture: composite two displays into one clip via a layout designed in the Game Capture pane. ──
        // Layout rects use a "1 unit = 1080 px" space (height reference); the output frame = the bounding box of the
        // placed screens, so side-by-side ≈ 3840×1080, top-bottom ≈ 1920×2160, a single ultrawide ≈ 2560×1080. Dual is
        // locked to H.264 / 60 fps / ~1080-per-screen for maximum compatibility (the quality pane is overridden).
        private struct LRect { public double X, Y, W, H; }
        private static bool _dualOn;
        private static string _monA = "", _monB = "";                                     // display device names ("" = none)
        private static LRect _rectA = new LRect { X = 0,         Y = 0, W = 16.0 / 9, H = 1 };   // default: side-by-side, two 16:9
        private static LRect _rectB = new LRect { X = 16.0 / 9,  Y = 0, W = 16.0 / 9, H = 1 };
        private static string _dualSig = "";                                              // last-applied config (for re-arm-on-change)
        internal static bool DualOn => _dualOn;

        public static void SetDualScreen(bool on, string? monA, string? monB, string? layoutJson)
        {
            string sig = on + "|" + (monA ?? "") + "|" + (monB ?? "") + "|" + (layoutJson ?? "");
            bool changed = sig != _dualSig; _dualSig = sig;
            _dualOn = on; _monA = monA ?? ""; _monB = monB ?? "";
            try
            {
                if (!string.IsNullOrWhiteSpace(layoutJson))
                {
                    var lay = Newtonsoft.Json.JsonConvert.DeserializeObject<DualLayoutDto>(layoutJson!);
                    if (lay?.a != null) _rectA = lay.a.ToRect();
                    if (lay?.b != null) _rectB = lay.b.ToRect();
                }
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "dual layout parse err: " + ex.Message); }
            // Applied on the next arm/record. If a replay buffer is already rolling, re-arm so its rotated segments stay
            // one consistent resolution (the concat-copy on save requires it). Task.Run, not a bare fire-and-forget:
            // the caller is the bridge on the UI thread, and Disarm/Arm's sync prefix + pump-posted continuations
            // (WASAPI teardown, recorder start) otherwise stall the message pump that hosts the keyboard hook.
            if (changed && _armed) { var g = _repGame; _ = Task.Run(() => ReArmAsync(g)); }
        }
        private static async Task ReArmAsync(string? game) { try { await DisarmCoreAsync(); if (_wantArmed) await ArmCoreAsync(game); } catch { } }

        // ── Capture-source override (the title-bar picker / gallery status). Deliberately does NOT touch
        //    _gameCap/dual settings so it can't interfere with them.
        //    "auto" = settings behavior · "display" = force the monitor path · "window" = pin a window. A window pin
        //    is remembered BY PROGRAM (_pinProc, persisted as settings.capturePin): HWNDs die with the app, so a pinned
        //    PCSX2 re-attaches whenever PCSX2 is open (startup, relaunch) and auto-follow covers the gap. ──
        private static string _srcMode = "auto";
        private static IntPtr _srcHwnd = IntPtr.Zero;
        private static string _pinProc = "";
        public static void SetCaptureSource(string mode, long hwnd)
        {
            string m = mode == "display" || mode == "window" ? mode : "auto";
            IntPtr h = m == "window" ? (IntPtr)hwnd : IntPtr.Zero;
            bool changed = m != _srcMode || h != _srcHwnd;
            _srcMode = m; _srcHwnd = h;
            _pinProc = h != IntPtr.Zero ? ProcName(h) : "";
            DiagLog.Line("CLIP", "capture source -> " + m + (h != IntPtr.Zero ? " hwnd=0x" + h.ToString("X") : ""));
            // An armed buffer captured per BuildOptions at arm time — re-arm so the new source applies immediately
            // (same pattern as SetDualScreen; a full recording in progress keeps its source until the next start).
            // Task.Run: the caller is the bridge on the UI thread — the WASAPI/recorder teardown+startup inside
            // Disarm/Arm (and their pump-posted continuations) must never run on the message pump.
            if (changed && _armed) { var g = _repGame; _ = Task.Run(() => ReArmAsync(g)); }
        }
        /// <summary>Startup: restore a persisted program pin (settings.capturePin). Before arming, so no re-arm needed.</summary>
        public static void SetPinnedApp(string proc)
        {
            if (string.IsNullOrWhiteSpace(proc)) return;
            _srcMode = "window"; _pinProc = proc.Trim(); _srcHwnd = FindAppWindow(_pinProc);
            DiagLog.Line("CLIP", "capture pin restored -> " + _pinProc + (_srcHwnd != IntPtr.Zero ? " (running)" : " (not running — following focus until it opens)"));
        }
        private static string ProcName(IntPtr h)
        {
            try { GetWindowThreadProcessId(h, out uint pid); using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName; } catch { return ""; }
        }
        // The pinned program's main window (largest capturable one when it has several), or Zero when it isn't open.
        private static IntPtr FindAppWindow(string proc)
        {
            IntPtr best = IntPtr.Zero; long bestArea = 0;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
                    using (p)
                    {
                        IntPtr h; try { h = p.MainWindowHandle; } catch { continue; }
                        if (!IsCapturableApp(h) || !GetClientRect(h, out var rc)) continue;
                        long a = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                        if (a > bestArea) { bestArea = a; best = h; }
                    }
            }
            catch { }
            return best;
        }
        // The pinned window's live title ("" when nothing valid is pinned) — names the clip after the app being captured.
        private static string SourceWindowTitle()
        {
            try
            {
                if (_srcMode != "window" || _srcHwnd == IntPtr.Zero || !IsWindow(_srcHwnd)) return "";
                var sb = new System.Text.StringBuilder(256);
                return GetWindowText(_srcHwnd, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : "";
            }
            catch { return ""; }
        }
        private sealed class DualLayoutDto { public RectDto? a { get; set; } public RectDto? b { get; set; } }
        private sealed class RectDto
        {
            public double x { get; set; } public double y { get; set; } public double w { get; set; } public double h { get; set; }
            public LRect ToRect() => new LRect { X = x, Y = y, W = Math.Max(0.05, w), H = Math.Max(0.05, h) };
        }

        /// <summary>Where clips are written (default %USERPROFILE%\Videos\Sparta Clips\Clips). Created on demand.</summary>
        public static string ClipsDir
        {
            get { string d = PeekClipsDir; try { Directory.CreateDirectory(d); } catch { } return d; }
        }
        /// <summary>Same path WITHOUT touching the filesystem — for bridge/UI-thread reads (a dead SMB clipFolder
        /// must never block the message pump; CreateDirectory on it stalls for the network timeout).</summary>
        public static string PeekClipsDir => string.IsNullOrWhiteSpace(_customDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sparta Clips", "Clips")
            : _customDir;
        public static void SetClipsDir(string? dir) { _customDir = dir ?? ""; }

        /// <summary>Where editor exports (trims / smart cuts / composed + combined clips) are written — a separate
        /// "Edited" subfolder so they don't clutter the raw instant-replay/recording captures. Created on demand.</summary>
        public static string EditedDir
        {
            get { string d = Path.Combine(ClipsDir, "Edited"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }

        /// <summary>Game title for the file name when a recording is started from the global hotkey (which fires in C#
        /// independently of the WebView, so it works even when the launcher is hidden in the tray). Pushed by JS.</summary>
        // Surface a mic/chat capture failure (Windows mic-privacy blocked, device gone) as a user-visible toast.
        static ClipRecorder() { AudioSidecar.OnFail = tag => { try { Notify?.Invoke("micfail", Newtonsoft.Json.JsonConvert.SerializeObject(new { track = tag })); } catch { } }; }

        public static string LastGameName = "";
        /// <summary>Hotkey toggle — start/stop a full recording without the WebView (tray-safe).</summary>
        public static void Toggle() { try { if (_recording) Stop(); else Start(LastGameName); } catch { } }

        // ── Per-game capture profile override: applied at record start, restored on finish. The replay ring keeps the
        //    global settings (it's pre-buffered), so profiles affect FULL recordings only. ──
        private static bool _profileActive; private static int _savedFps, _savedMbps, _savedResH;
        private static void ApplyGameProfile(string? gameName)
        {
            if (_profileActive) RestoreGameProfile();   // software-retry re-enters Start → re-snapshot from the true globals
            var p = AppConfig.ProfileFor(gameName); if (p == null) return;
            _savedFps = _fps; _savedMbps = _mbps; _savedResH = _resH; _profileActive = true;
            if (p.fps > 0) _fps = Math.Clamp(p.fps, 15, 240);
            if (p.mbps > 0) _mbps = Math.Clamp(p.mbps, 1, 200);
            if (p.resH > 0) _resH = Math.Clamp(p.resH, 480, 4320);   // unset (0) keeps the GLOBAL cap, like fps/mbps — before: it forced source-size
            DiagLog.Line("CLIP", $"game profile '{p.match}' -> {_fps}fps {_mbps}Mbps {(_resH > 0 ? _resH + "p" : "source")}");
        }
        private static void RestoreGameProfile()
        {
            if (!_profileActive) return;
            _fps = _savedFps; _mbps = _savedMbps; _resH = _savedResH; _profileActive = false;
        }

        /// <summary>Begin a full-session recording. gameName (from the launched game) names the file. forceSw = software
        /// encode (used for the automatic retry after a hardware-encode failure).</summary>
        private static readonly object _startGate = new();   // hotkey + tray menu + UI button can all race into Start
        public static bool Start(string? gameName, bool forceSw = false)
        {
            lock (_startGate)
            {
            if (_armed) return false;   // instant-replay buffer owns the capture device; stop it before a full record
            if (_recording) return true;
            if (!forceSw && DiskLow()) { DiagLog.Line("CLIP", "start refused: low disk"); Notify?.Invoke("recfail", Newtonsoft.Json.JsonConvert.SerializeObject(new { reason = "Low disk space — free up at least 1 GB on the clips drive." })); return false; }
            _forceSwEncode = forceSw;
            // No caller passes a real title (the hotkey path always sends "") → resolve the foreground game NOW, so the
            // file gets a real name AND per-game profiles actually match. before: ApplyGameProfile("") never matched.
            // A pinned source window wins outright — the clip is named after the app actually being captured.
            string srcTitle = SourceWindowTitle();
            if (srcTitle.Length > 0) gameName = srcTitle;
            else if (string.IsNullOrWhiteSpace(gameName)) gameName = ForegroundGameName();
            ApplyGameProfile(gameName);   // a matching per-game profile overrides fps/bitrate/resolution for this recording
            try
            {
                string file = Unique(SafeName(string.IsNullOrWhiteSpace(gameName) ? "Desktop" : gameName!)
                              + "_" + DateTime.Now.ToString("dd.MM.yyyy.HH.mm") + ".mp4");
                _finalPath = Path.Combine(ClipsDir, file);
                // AV1 records an H.264 intermediate to TEMP (kept out of the gallery), then ffmpeg transcodes to ClipsDir.
                string path = Av1Mode ? Path.Combine(Av1TempDir, file) : _finalPath;
                _rec = Recorder.CreateRecorder(BuildOptions());
                _rec.OnRecordingComplete += (s, e) => Done(true, e.FilePath, null);
                _rec.OnRecordingFailed   += (s, e) => OnRecFailed(path, e.Error, forceSw);
                _rec.Record(path);
                StartDiskMonitor();
                if (_sepMic && _mic) _micSide.StartSidecar(MicSidecar(_finalPath));    // sidecars sit next to the FINAL clip in ClipsDir
                if (_chat)           _chatSide.StartSidecar(ChatSidecar(_finalPath));
                _gameWav = "";
                if (_gamePid > 0) { _gameWav = Path.Combine(BufferDir, "game_" + DateTime.Now.Ticks + ".wav"); _gameSide.StartSidecar(_gameWav); }
                _ = ClipFfmpeg.EnsureAsync();   // warm up the post-processing engine so the first save uses ffmpeg, not the fallback
                _recStartTick = Environment.TickCount64; lock (_marks) _marks.Clear();   // reset the mark-a-moment buffer for this recording
                _recording = true;
                DiagLog.Line("CLIP", "record start -> " + Path.GetFileName(path));
                Notify?.Invoke("state", "{\"recording\":true}");
                return true;
            }
            catch (Exception ex) { RestoreGameProfile(); DiagLog.Line("CLIP", "start err: " + ex.Message); Notify?.Invoke("recfail", Newtonsoft.Json.JsonConvert.SerializeObject(new { reason = ex.Message })); Dispose(); return false; }
            }
        }

        public static void Stop()
        {
            // Local copy: the recorder's own OnRecordingComplete → Done() can null _rec between the guard and the
            // call — the NRE then fired a spurious "Recording failed" toast for a recording that actually completed.
            var rec = _rec;
            if (!_recording || rec == null) return;
            try { rec.Stop(); } catch (Exception ex) { DiagLog.Line("CLIP", "stop err: " + ex.Message); Done(false, "", ex.Message); }
        }

        // A recording failed. If it was a hardware-encode attempt, retry ONCE with software H.264 (covers a contended
        // NVENC session / iGPU without HEVC-encode / remote-or-virtual GPU) before declaring failure; runs off the
        // recorder's callback thread to avoid re-entrancy.
        private static void OnRecFailed(string path, string? err, bool wasSw)
        {
            StopDiskMonitor();
            if (!wasSw)
            {
                DiagLog.Line("CLIP", "record failed (hardware) -> retrying software: " + err);
                System.Threading.Tasks.Task.Run(() => { try { Dispose(); } catch { } if (!Start(LastGameName, forceSw: true)) Done(false, path, err); });
                return;
            }
            Done(false, path, err);
        }
        private static void StartDiskMonitor()
        {
            StopDiskMonitor();
            // Poll free space during a long recording; if the drive fills, stop (which finalizes + KEEPS the footage so far).
            _diskTimer = new System.Threading.Timer(_ =>
            {
                try { if (_recording && DiskLow()) { DiagLog.Line("CLIP", "disk low mid-record -> stopping"); Notify?.Invoke("disklow", "{}"); Stop(); } } catch { }
            }, null, 20000, 20000);
        }
        private static void StopDiskMonitor() { try { _diskTimer?.Dispose(); } catch { } _diskTimer = null; }

        private static TaskCompletionSource<bool>? _stopWaiter;   // completed when Done() has fully finalized (see StopAndWait)
        private static void Done(bool ok, string path, string? err)
        {
            // Snapshot + detach THIS recording's recorder and marks NOW. before: the trailing Dispose() read the LIVE
            // _rec field — a recording started during the (possibly slow) PruneToCap below got disposed mid-record and
            // its fresh sidecars deleted. after: only the finished instance is disposed; a new Start() is untouched.
            var rec = _rec; _rec = null;
            double[] marks = SnapshotMarks();
            _recording = false;
            RestoreGameProfile();   // hand the engine back its global fps/bitrate/res for the next replay-arm / recording
            StopDiskMonitor();
            _micSide.StopSidecar(ok); _chatSide.StopSidecar(ok);   // finalize (keep) the sidecars on success; drop partials on failure
            string gameWav = _gameWav; _gameWav = "";
            _gameSide.StopSidecar(ok);   // finalized TEMP wav on success (muxed + deleted below); dropped on failure
            DiagLog.Line("CLIP", ok ? ("record complete -> " + path) : ("record FAILED: " + err));
            if (!ok) Notify?.Invoke("recfail", Newtonsoft.Json.JsonConvert.SerializeObject(new { reason = err ?? "unknown" }));   // surface so the user isn't left believing a clip saved

            // AV1: hand the H.264 temp to ffmpeg off-thread; the gallery sees the finished AV1 when it's done.
            if (ok && Av1Mode && !string.IsNullOrEmpty(path))
            {
                string temp = path, final = _finalPath;
                Notify?.Invoke("state", "{\"recording\":false}");
                Notify?.Invoke("transcoding", "{}");
                DisposeRec(rec);
                _stopWaiter?.TrySetResult(true);   // the H.264 intermediate is safely on disk (startup salvage covers a quit mid-transcode)
                System.Threading.Tasks.Task.Run(async () => { await FinishAv1Async(temp, final, marks, gameWav); });
                return;
            }

            if (ok && !string.IsNullOrEmpty(path) && File.Exists(gameWav))
            {
                // Same tail as below, but off the recorder's callback thread: swap in the game audio first.
                Notify?.Invoke("state", "{\"recording\":false}");
                DisposeRec(rec);
                _stopWaiter?.TrySetResult(true);
                string saved = path;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await ClipFfmpeg.ReplaceAudioAsync(saved, gameWav, alignEnd: false); } catch { }
                    TryDelete(gameWav);
                    WriteMarks(saved, marks); PruneToCap(); KickProxy(saved);
                    Notify?.Invoke("saved", Newtonsoft.Json.JsonConvert.SerializeObject(new { path = saved, name = Path.GetFileName(saved) }));
                });
                return;
            }
            TryDelete(gameWav);

            if (ok && !string.IsNullOrEmpty(path))
            {
                WriteMarks(path, marks);
                PruneToCap();
                KickProxy(path);
                Notify?.Invoke("saved", Newtonsoft.Json.JsonConvert.SerializeObject(new { path, name = Path.GetFileName(path) }));
            }
            Notify?.Invoke("state", "{\"recording\":false}");
            DisposeRec(rec);
            _stopWaiter?.TrySetResult(true);
        }

        /// <summary>Stop an in-progress recording and block (bounded) until Done() has finalized the file + sidecars —
        /// used by QuitApp so quitting mid-record doesn't tear the process down around an unfinalized MP4.</summary>
        public static bool StopAndWait(int timeoutMs)
        {
            if (!_recording) return true;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopWaiter = tcs;
            try { Stop(); } catch { }
            try { return tcs.Task.Wait(timeoutMs); } catch { return false; } finally { _stopWaiter = null; }
        }

        // Transcode the H.264 temp → AV1 at the final path. On failure, salvage the H.264 as the clip so nothing is lost.
        private static async System.Threading.Tasks.Task FinishAv1Async(string temp, string final, double[] marks, string gameWav = "")
        {
            if (File.Exists(gameWav)) { try { await ClipFfmpeg.ReplaceAudioAsync(temp, gameWav, alignEnd: false); } catch { } TryDelete(gameWav); }
            bool done = false;
            try { if (await Av1Transcoder.EnsureAsync()) done = await Av1Transcoder.EncodeAsync(temp, final, Av1Kbps, _av1ForceSw, fast: true); }
            catch (Exception ex) { DiagLog.Line("CLIP", "av1 finish err: " + ex.Message); }
            if (done) { try { File.Delete(temp); } catch { } }
            else
            {
                try { if (File.Exists(temp)) { if (File.Exists(final)) File.Delete(final); File.Move(temp, final); } } catch { }
                Notify?.Invoke("av1fail", "{}");
            }
            WriteMarks(final, marks);   // the marks snapshotted at Done — never a newer recording's buffer
            PruneToCap();
            if (File.Exists(final))
            {
                KickProxy(final);
                Notify?.Invoke("saved", Newtonsoft.Json.JsonConvert.SerializeObject(new { path = final, name = Path.GetFileName(final) }));
            }
        }

        // Transcode an already-written replay clip (ClipsDir) to AV1 in place. Returns its name (salvages on failure).
        private static async System.Threading.Tasks.Task<string?> TranscodeReplayAv1(string name)
        {
            try
            {
                string final = Path.Combine(ClipsDir, name);
                if (!File.Exists(final)) return name;
                string temp = Path.Combine(Av1TempDir, name);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                File.Move(final, temp);
                bool done = await Av1Transcoder.EnsureAsync() && await Av1Transcoder.EncodeAsync(temp, final, Av1Kbps, _av1ForceSw, fast: true);
                if (done) { try { File.Delete(temp); } catch { } }
                else { try { if (!File.Exists(final) && File.Exists(temp)) File.Move(temp, final); } catch { } Notify?.Invoke("av1fail", "{}"); }
                return name;
            }
            catch { return name; }
        }

        // Eagerly build the smooth-scrubbing preview proxy the moment a clip is saved, so opening it in the editor is
        // instant + fine-grained (no on-open wait). Runs off-thread at BelowNormal (inside ffmpeg) to stay off the game's back.
        // Long full-session recordings are SKIPPED (a 2h capture = a ~30min transcode + GBs of TEMP the user may never
        // open) — those build their proxy lazily on first editor open instead.
        private const double MaxEagerProxySec = 10 * 60;
        private static void KickProxy(string fullPath)
        {
            try
            {
                Task.Run(async () =>
                {
                    try
                    {
                        double dur = ClipLibrary.Mp4DurationSeconds(fullPath);
                        if (dur > MaxEagerProxySec) { DiagLog.Line("CLIP", "proxy deferred (long clip " + (int)dur + "s)"); return; }
                        await ClipFfmpeg.EnsureProxyAsync(fullPath);
                    }
                    catch { }
                });
            }
            catch { }
        }

        // Dispose OFF the recorder's own callback thread to avoid re-entrancy into the library mid-event.
        // Used by the failure paths (Start catch / OnRecFailed) where the live _rec IS the recorder being torn down.
        private static void Dispose()
        {
            var r = _rec; _rec = null; _recording = false;
            _micSide.StopSidecar(false); _chatSide.StopSidecar(false); _gameSide.StopSidecar(false);   // safety: drop any still-open sidecar (no-op once Done() finalized it)
            DisposeRec(r);
        }
        // Dispose a SPECIFIC recorder instance without touching global state — Done() uses this so its cleanup tail
        // can never kill a recording that started while it was pruning/notifying.
        private static void DisposeRec(Recorder? r)
        {
            if (r != null) System.Threading.Tasks.Task.Run(() => { try { r.Dispose(); } catch { } });
        }

        // Translate the user's capture settings → ScreenRecorderLib RecorderOptions (WGC + hardware H.264).
        // Display mode leaves SourceOptions null (library default = primary monitor); game mode targets the
        // foreground window's handle. faststart keeps the moov up front so the gallery's mvhd duration read works.
        // Windows 11 draws a yellow "capture in progress" border around any window/display captured via Windows
        // Graphics Capture — and the replay buffer re-creates its capture every segment, so it also FLICKERED.
        // IsBorderRequired=false hides it, but only after the app is granted borderless capture (unpackaged desktop
        // apps are granted silently). Called once from TrayCore's background warm-up; a no-op on Windows 10.
        private static readonly Lazy<Task> _borderless = new(RequestBorderlessCoreAsync);   // thread-safe once (TrayCore warm-up + ArmCoreAsync race)
        public static Task RequestBorderlessCaptureAsync() => _borderless.Value;
        private static async Task RequestBorderlessCoreAsync()
        {
            try
            {
                if (!Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Graphics.Capture.GraphicsCaptureAccess", "RequestAccessAsync")) return;
                var r = await Windows.Graphics.Capture.GraphicsCaptureAccess.RequestAccessAsync(Windows.Graphics.Capture.GraphicsCaptureAccessKind.Borderless);
                DiagLog.Line("CLIP", "borderless capture: " + r);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "borderless capture request failed: " + ex.Message); }
        }
        // What the live capture is actually recording (set by BuildOptions per recording / replay segment) — the UI's
        // "Capturing <app>" status reads it so the user can tell when the capture is NOT the window they focused (the
        // replay buffer only re-targets the foreground window when a new segment starts).
        private static IntPtr _capHwnd;
        private static string _capKind = "display";   // "window" | "display" | "dual"
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _appNames = new();
        // Display name for a window's program: the exe's product name ("pcsx2-qt" → "PCSX2"); without one (many games ship
        // no version info, e.g. "TFTClient-Win64-Shipping") the window's own title ("TFT") when it's short; else the exe
        // name with engine build suffixes stripped. before: the raw process name showed up in the status line.
        private static string DisplayAppName(uint pid, IntPtr hwnd)
        {
            string n = FriendlyAppName(pid);
            if (n.Length > 0 && !_noVersionInfo.ContainsKey(pid)) return n;
            string t = hwnd != IntPtr.Zero ? WindowTitle(hwnd) : "";
            if (t.Length > 0 && t.Length <= 40) return t;
            return System.Text.RegularExpressions.Regex.Replace(n, @"[-_ ]?(Win64|Win32|x64|x86)?[-_ ]?(Shipping|Release|Retail)?$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim('-', '_', ' ');
        }
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _noVersionInfo = new();
        private static string FriendlyAppName(uint pid)
        {
            if (pid == 0) return "";
            return _appNames.GetOrAdd(pid, id =>
            {
                try
                {
                    using var pr = System.Diagnostics.Process.GetProcessById((int)id);
                    string n = pr.ProcessName;
                    try
                    {
                        var vi = pr.MainModule?.FileVersionInfo;   // "pcsx2-qt" → "PCSX2"; throws for elevated/protected targets
                        string d = (vi?.ProductName ?? "").Trim();
                        if (d.Length == 0 || d.Length > 40) d = (vi?.FileDescription ?? "").Trim();
                        if (d.Length > 0 && d.Length <= 40) n = d; else _noVersionInfo[id] = true;
                    }
                    catch { _noVersionInfo[id] = true; }
                    return n;
                }
                catch { return ""; }
            });
        }
        private static string WindowTitle(IntPtr h)
        {
            try { var sb = new System.Text.StringBuilder(256); return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : ""; } catch { return ""; }
        }
        /// <summary>Live capture-target snapshot for the UI status line (cheap: a few user32 calls + a per-pid name cache).</summary>
        public static string CaptureTargetJson()
        {
            string state = _recording ? "recording" : _armed ? "replay" : "idle";
            if (state == "idle") return "{\"state\":\"idle\"}";
            string kind = _capKind, title = "", app = "", focusApp = ""; bool gone = false, elsewhere = false;
            IntPtr h = _capHwnd;
            if (kind == "window")
            {
                if (h == IntPtr.Zero || !IsWindow(h)) gone = true;
                else { title = WindowTitle(h); GetWindowThreadProcessId(h, out uint pid); app = DisplayAppName(pid, h); }
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    GetWindowThreadProcessId(fg, out uint fpid);
                    GetWindowThreadProcessId(h, out uint cpid);
                    // Our own window being focused (the user is looking at this status) is not "elsewhere".
                    if (fg != IntPtr.Zero && fg != h && fpid != cpid && fpid != (uint)Environment.ProcessId)
                    { elsewhere = true; focusApp = DisplayAppName(fpid, fg); }
                }
                catch { }
            }
            bool pinned = _srcMode == "window" && kind == "window" && h == _srcHwnd && h != IntPtr.Zero;
            if (pinned) elsewhere = false;   // pinned on purpose: other focused windows are expected, not a warning
            string pinApp = _srcMode == "window" ? _pinProc : "";
            bool pinWaiting = pinApp.Length > 0 && !pinned;   // pinned program isn't open → following focus meanwhile
            return Newtonsoft.Json.JsonConvert.SerializeObject(new { state, kind, title, app, gone, elsewhere, focusApp, pinned, pinApp, pinWaiting });
        }
        // Last foreground window that is a real app (not us, not the desktop/taskbar) — the capture target to fall back
        // on when the foreground is our own window. Polled at 2 Hz (one GetForegroundWindow call — negligible).
        private static IntPtr _lastAppFg;
        private static readonly Timer _fgTracker = new Timer(_ => TrackForeground(), null, 0, 500);
        private static volatile bool _capFallback;   // the replay segment fell back to display capture (only our own window was ever focused)
        private static volatile bool _capFollows;    // segment targets the foreground window automatically (not pinned / display / dual)
        private static int _retargeting;
        private static IntPtr _focusCand; private static int _focusDwell, _pinTick;
        private const int FOLLOW_DWELL_TICKS = 4;   // 4 × 500 ms: a quick alt-tab away and back doesn't throw the replay's history away
        // Instant replay FOLLOWS FOCUS: once the user has stayed in a different app for ~2 s, roll a new buffer segment
        // onto it (before: the target only changed at the next 2-minute rotation, so clicking into the game kept
        // filming the previous window). Manual recordings keep their target; a pinned source window never moves.
        private static void TrackForeground()
        {
            try
            {
                if (++_pinTick % 4 == 0 && _srcMode == "window" && _pinProc.Length > 0 && _armed && !_recording
                    && (_capKind != "window" || !IsWindow(_capHwnd) || ProcName(_capHwnd) != _pinProc))
                {   // every 2 s while the pinned program isn't what we're filming: did it open?
                    var w = FindAppWindow(_pinProc);
                    if (w != IntPtr.Zero && System.Threading.Interlocked.Exchange(ref _retargeting, 1) == 0)
                    {
                        _srcHwnd = w; _focusDwell = 0;
                        DiagLog.Line("CLIP", "replay: pinned " + _pinProc + " is open -> " + WindowTitle(w));
                        _ = Task.Run(async () => { try { await RotateAsync(); } finally { System.Threading.Interlocked.Exchange(ref _retargeting, 0); } });
                        return;
                    }
                }
                var h = GetForegroundWindow();
                if (!IsCapturableApp(h)) { _focusDwell = 0; return; }   // our own window / desktop: keep the current target
                _lastAppFg = h;
                if (!_armed || _recording) { _focusDwell = 0; return; }
                bool retarget = false;
                if (_capFallback) retarget = true;   // filming the full screen only because Sparta Clips had focus at arm
                else if (_capFollows && _capKind == "window" && !SameApp(h, _capHwnd))
                {
                    if (h == _focusCand) _focusDwell++; else { _focusCand = h; _focusDwell = 1; }
                    retarget = _focusDwell >= FOLLOW_DWELL_TICKS;
                }
                else _focusDwell = 0;
                if (retarget && System.Threading.Interlocked.Exchange(ref _retargeting, 1) == 0)
                {
                    _capFallback = false; _focusDwell = 0;
                    DiagLog.Line("CLIP", "replay: following focus -> " + WindowTitle(h));
                    _ = Task.Run(async () => { try { await RotateAsync(); } finally { System.Threading.Interlocked.Exchange(ref _retargeting, 0); } });
                }
            }
            catch { }
        }
        private static bool SameApp(IntPtr a, IntPtr b)
        {
            if (a == b) return true;
            if (b == IntPtr.Zero || !IsWindow(b)) return false;
            GetWindowThreadProcessId(a, out uint pa); GetWindowThreadProcessId(b, out uint pb);
            return pa != 0 && pa == pb;   // another window of the same program (e.g. a PCSX2 dialog) isn't a new target
        }
        private static bool IsCapturableApp(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h) || h == GetShellWindow()) return false;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
                var cls = new System.Text.StringBuilder(64); GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "Progman" || c == "WorkerW" || c == "Windows.UI.Core.CoreWindow") return false;
                // Only real app windows (what the taskbar would show). Transient overlays were followed and then vanished
                // under the recorder — "replay segment failed: The parameter is incorrect" after following the
                // "Snipping Tool Overlay" (measured in the log). Tool windows, owned popups/dialogs, cloaked (other
                // virtual desktop / suspended UWP), minimized and tiny windows are never capture targets.
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
                if (IsIconic(h)) return false;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
                if (!GetClientRect(h, out var rc) || rc.Right - rc.Left < 160 || rc.Bottom - rc.Top < 120) return false;
                return !ShellProcess(pid);
            }
            catch { return false; }
        }
        private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14; private const uint GW_OWNER = 4;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        // Windows shell surfaces that look like normal windows but are overlays / flyouts (screenshot overlay, Start,
        // Search, …) — following one films a layer that disappears seconds later.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _shellPids = new();
        private static readonly string[] SHELL_PROCS = { "ScreenClippingHost", "SnippingTool", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "LockApp" };
        private static bool ShellProcess(uint pid) => _shellPids.GetOrAdd(pid, id =>
        {
            try { using var pr = System.Diagnostics.Process.GetProcessById((int)id); return Array.IndexOf(SHELL_PROCS, pr.ProcessName) >= 0; }
            catch { return false; }
        });
        private static int _capH;   // height of the window being captured (0 = display mode / unknown) — feeds EffectiveMbps
        private static RecorderOptions BuildOptions()
        {
            _capH = 0; _gamePid = 0; _capHwnd = IntPtr.Zero; _capKind = "display"; _capFallback = false; _capFollows = false;
            var opts = new RecorderOptions
            {
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Framerate = _dualOn ? 60 : _fps,   // dual-screen is locked to 60 fps
                    Bitrate = EffectiveMbps() * 1_000_000,
                    IsHardwareEncodingEnabled = !_forceSwEncode,   // software fallback after a hardware-encode failure
                    IsMp4FastStartEnabled = true,
                },
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = true,
                    IsOutputDeviceEnabled = true,        // system / game audio
                    IsInputDeviceEnabled = _mic && !_sepMic,   // mic mixed into the MP4 only when NOT a separate track
                },
            };
            // VBR targeting the (resolution-scaled) bitrate — so the bitrate dropdown AND the resolution both control
            // file size predictably, and the size estimate matches. (Constant-Quality ignored the bitrate, which is why
            // 720p vs 1080p looked identical in the estimate.) HEVC still needs HEVC decode to preview.
            if (!_dualOn && _encoder == "h265") opts.VideoEncoderOptions.Encoder = new H265VideoEncoder { BitrateMode = H265BitrateControlMode.CBR };   // HEVC MFT has no VBR here → CBR (size == bitrate x time, matches the estimate); needs HEVC decode to preview
            else opts.VideoEncoderOptions.Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.UnconstrainedVBR };
            // Session source override first: a valid pinned window beats everything (incl. dual — an explicit pick);
            // "display" forces the monitor path below; "auto" falls through to the settings behavior.
            IntPtr pin = IntPtr.Zero;
            if (_srcMode == "window")
            {
                try { if (_srcHwnd != IntPtr.Zero && IsWindow(_srcHwnd)) pin = _srcHwnd; } catch { }
                if (pin == IntPtr.Zero && _pinProc.Length > 0) { _srcHwnd = FindAppWindow(_pinProc); pin = _srcHwnd; }   // pinned program reopened → new window
                if (pin == IntPtr.Zero) DiagLog.Line("CLIP", "pinned window gone -> auto source" + (_pinProc.Length > 0 ? " (waiting for " + _pinProc + ")" : ""));
            }
            // Dual-screen: composite the selected displays per the designer's layout (overrides resolution + game-window mode).
            if (_dualOn && pin == IntPtr.Zero) { _capKind = "dual"; ApplyDualLayout(opts); return opts; }
            bool winCap = pin != IntPtr.Zero || (_srcMode == "auto" && _gameCap);
            if (_resH > 0 && !winCap)     // display mode: scale to the chosen HEIGHT at the DISPLAY's real aspect ratio,
            {                             // not a hardcoded 16:9 — so ultrawide (e.g. 3440×1440) records 21:9 with no bars
                var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? default;
                int sw = b.Width > 0 ? b.Width : 16, sh = b.Height > 0 ? b.Height : 9;
                int w = (int)Math.Round((double)_resH * sw / sh); w -= w % 2;
                opts.OutputOptions = new OutputOptions { OutputFrameSize = new ScreenSize(w, _resH) };
            }
            // window mode (pinned or foreground game): leave OutputFrameSize unset → native window size (no distortion)
            if (winCap)
            {
                try
                {
                    IntPtr hwnd = pin != IntPtr.Zero ? pin : GetForegroundWindow();
                    _capFollows = pin == IntPtr.Zero;   // auto source → TrackForeground may re-target it
                    // Never capture OUR OWN window (or the desktop/taskbar): opening Sparta Clips focuses it, and the
                    // replay armed at startup was recording the clipper itself. Use the last real app the user was in,
                    // else fall through to display capture.
                    if (pin == IntPtr.Zero && !IsCapturableApp(hwnd)) { var last = _lastAppFg; hwnd = IsCapturableApp(last) ? last : IntPtr.Zero; _capFallback = hwnd == IntPtr.Zero; }
                    if (hwnd != IntPtr.Zero)
                    {
                        _capHwnd = hwnd; _capKind = "window";
                        opts.SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { new WindowRecordingSource(hwnd) { IsBorderRequired = false } } };   // no yellow WGC border (Win11)
                        try { GetWindowThreadProcessId(hwnd, out uint wpid); if (wpid != 0 && wpid != (uint)Environment.ProcessId) _gamePid = (int)wpid; } catch { }
                        if (GetClientRect(hwnd, out var rc) && rc.Bottom - rc.Top > 0)
                        {
                            _capH = rc.Bottom - rc.Top;
                            opts.VideoEncoderOptions.Bitrate = EffectiveMbps() * 1_000_000;   // re-budget for the native window height
                        }
                    }
                }
                catch { }
            }
            return opts;
        }

        // Compose the selected display(s) into the output frame per the settings designer's layout. Rects are in
        // "1 unit = 1080 px"; OutputFrameSize = the bounding box. Each display is Uniform-fit (letterboxed only when
        // its slot's aspect differs) so any monitor — incl. 21:9/32:9 ultrawide — keeps its true shape, never stretched.
        private static void ApplyDualLayout(RecorderOptions opts)
        {
            var items = new List<(string dev, LRect r)>();
            if (!string.IsNullOrWhiteSpace(_monA)) items.Add((_monA, _rectA));
            if (!string.IsNullOrWhiteSpace(_monB)) items.Add((_monB, _rectB));
            // Drop monitors that aren't currently connected so we never hand the library a dead device.
            try
            {
                var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in Recorder.GetDisplays()) if (!string.IsNullOrEmpty(d.DeviceName)) live.Add(d.DeviceName);
                if (live.Count > 0) items.RemoveAll(it => !live.Contains(it.dev));
            }
            catch { }
            if (items.Count == 0) return;   // dual on but nothing valid chosen → fall back to the library default (primary)

            // Larger area first (drawn at the bottom), smaller last (on top) → a PiP overlay sits above the base screen.
            items.Sort((p, q) => (q.r.W * q.r.H).CompareTo(p.r.W * p.r.H));

            const double HREF = 1080.0;   // 1 layout unit = 1080 px
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var it in items)
            {
                minX = Math.Min(minX, it.r.X); minY = Math.Min(minY, it.r.Y);
                maxX = Math.Max(maxX, it.r.X + it.r.W); maxY = Math.Max(maxY, it.r.Y + it.r.H);
            }
            static int Even(double v) { int n = (int)Math.Round(v); return Math.Max(2, n - (n % 2)); }
            int outW = Math.Clamp(Even((maxX - minX) * HREF), 2, 7680);
            int outH = Math.Clamp(Even((maxY - minY) * HREF), 2, 4320);

            var sources = new List<RecordingSourceBase>();
            foreach (var it in items)
                sources.Add(new DisplayRecordingSource(it.dev)
                {
                    Position   = new ScreenPoint((int)Math.Round((it.r.X - minX) * HREF), (int)Math.Round((it.r.Y - minY) * HREF)),
                    OutputSize = new ScreenSize(Even(it.r.W * HREF), Even(it.r.H * HREF)),
                    Stretch    = StretchMode.Uniform,
                    AnchorPoint = Anchor.Center,
                    IsBorderRequired = false,   // no yellow WGC border (Win11)
                });
            opts.SourceOptions = new SourceOptions { RecordingSources = sources };
            opts.OutputOptions = new OutputOptions { OutputFrameSize = new ScreenSize(outW, outH) };
            // The bitrate dropdown is 1080p-calibrated; scale by pixel count so a 3840×1080 dual frame gets ~2× the bits.
            double pxScale = Math.Clamp((double)outW * outH / (1920.0 * 1080.0), 0.5, 4.0);
            opts.VideoEncoderOptions.Bitrate = (int)Math.Round(_mbps * 1_000_000 * pxScale);
            DiagLog.Line("CLIP", "dual layout -> " + sources.Count + " src, out " + outW + "x" + outH);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        // The foreground window's title — used to name a replay when the game wasn't launched from the launcher
        // (Steam/desktop), so a Steam-launched Slay the Spire clip is "Slay_the_Spire_…_replay.mp4", not "Desktop_…".
        private static string ForegroundAppName()
        {
            try
            {
                IntPtr h = GetForegroundWindow(); if (h == IntPtr.Zero) return "";
                var sb = new System.Text.StringBuilder(256);
                return GetWindowText(h, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : "";
            }
            catch { return ""; }
        }
        // Same, but "" when the foreground window is OUR OWN (recording started from the app's Record button) — so the
        // clip is named "Desktop_…" rather than "Sparta_Clips_…" and no game profile falsely matches the app window.
        private static string ForegroundGameName()
        {
            try
            {
                IntPtr h = GetForegroundWindow(); if (h == IntPtr.Zero) return "";
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return "";
                return ForegroundAppName();
            }
            catch { return ""; }
        }

        // Storage cap: after a clip saves, delete the oldest until total ≤ cap (0 = unlimited).
        private static void PruneToCap()
        {
            if (_capBytes <= 0) return;
            try
            {
                var files = new DirectoryInfo(ClipsDir).GetFiles("*.mp4");
                long SizeOf(FileInfo f) { long s = f.Length; try { var m = MicSidecar(f.FullName); if (File.Exists(m)) s += new FileInfo(m).Length; var c = ChatSidecar(f.FullName); if (File.Exists(c)) s += new FileInfo(c).Length; } catch { } return s; }
                long total = 0; foreach (var f in files) total += SizeOf(f);   // count the mic/chat .wav sidecars too, not just the mp4
                if (total <= _capBytes) return;
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));   // oldest first
                foreach (var f in files)
                {
                    if (total <= _capBytes) break;
                    long sz = SizeOf(f);
                    try { f.Delete(); total -= sz; var mic = MicSidecar(f.FullName); if (File.Exists(mic)) File.Delete(mic); var chat = ChatSidecar(f.FullName); if (File.Exists(chat)) File.Delete(chat); var mk = MarksSidecar(f.FullName); if (File.Exists(mk)) File.Delete(mk); DiagLog.Line("CLIP", "cap prune -> " + f.Name); } catch { }
                }
            }
            catch { }
        }

        // ── Instant replay (Phase 2): a continuous background buffer; "Save replay" writes the last N seconds. ──
        // ScreenRecorderLib has no native replay buffer, so we keep two short segments (prev + current) rolling and,
        // on save, finalize the current segment and stitch the tail of prev + current to exactly N seconds via
        // MediaComposition. Rotation finalizes current→prev every ROT seconds (a brief, on-disk-only gap the player
        // never sees); prev + current always cover ≥ ROT ≥ 2·N, so a full-length replay is always available.
        private static Recorder? _repRec;
        private static volatile bool _armed;
        private static int _replayLen = 30;                  // seconds saved per replay
        private static string? _repGame;
        private static string _bufCur = "", _bufPrev = "";
        private static TaskCompletionSource<string>? _segDone;
        private static Timer? _rotTimer;
        private static readonly SemaphoreSlim _repGate = new SemaphoreSlim(1, 1);
        public static bool IsReplayArmed => _armed;
        public static int ReplayLenSeconds => _replayLen;   // exposed for the auto-highlight cooldown

        // Buffer segments live in TEMP (never the clips dir, or they'd show up in the gallery).
        private static string BufferDir
        {
            get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsReplay"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }

        // The LAST user intent (arm vs disarm). Found by tools/stress.py: a DisarmAsync issued while ReArmAsync was
        // mid-transition hit its pre-gate `!_armed` early-out during the transient disarmed state and was silently
        // LOST — the re-arm's second half then re-armed, leaving the engine armed with the UI toggle off. The public
        // entry points record intent; ReArmAsync only re-arms if arming is still what's wanted.
        private static volatile bool _wantArmed;
        public static Task ArmAsync(string? gameName) { _wantArmed = true; return ArmCoreAsync(gameName); }
        public static Task DisarmAsync() { _wantArmed = false; return DisarmCoreAsync(); }

        private static async Task ArmCoreAsync(string? gameName)
        {
            if (_armed) return;
            try { await Task.WhenAny(RequestBorderlessCaptureAsync(), Task.Delay(2000)); } catch { }   // first segment must already be borderless
            try { if (_recording) Stop(); } catch { }
            await _repGate.WaitAsync();
            try
            {
                if (_armed) return;   // re-check INSIDE the lock: the outer guard races (two Task.Run arms both pass it),
                                      // which started the mic ring TWICE → two captures into one buffer → doubled mic audio.
                _repGame = gameName; _armed = true;
                StartSegment();
                int ringSec = Math.Max(2 * _replayLen, 120);
                if (_sepMic && _mic) _micSide.StartRing(ringSec);    // parallel rings → separate replay mic / chat tracks
                if (_chat && !_chatSide.RingActive) _chatSide.StartRing(ringSec);   // StartSegment above may already have started it
                _ = ClipFfmpeg.EnsureAsync();   // warm up post-processing so the first replay save concats via ffmpeg
                int rot = Math.Max(2 * _replayLen, 120) * 1000;
                _rotTimer = new Timer(_ => { _ = RotateAsync(); }, null, rot, rot);
                DiagLog.Line("CLIP", "replay armed (" + _replayLen + "s) -> " + (gameName ?? "Desktop"));
                Notify?.Invoke("replay", "{\"armed\":true}");
                try { HighlightCoordinator.Start(); } catch { }   // auto-highlight follows the live buffer (no-op unless opted in)
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "arm err: " + ex.Message); _armed = false; }
            finally { _repGate.Release(); }
        }

        private static async Task DisarmCoreAsync()
        {
            if (!_armed) return;
            try { _rotTimer?.Dispose(); _rotTimer = null; } catch { }
            await _repGate.WaitAsync();
            try
            {
                // Stop the audio rings INSIDE the gate — before: they were stopped pre-gate, so a SaveReplayAsync
                // holding the gate lost its mic/chat DumpRing (silent no-mic replay) when a disarm raced the save.
                _micSide.StopRing(); _chatSide.StopRing(); _gameSide.StopRing(); _gameRingPid = 0;
                string? finished = await StopSegmentAsync();
                TryDelete(_bufPrev); TryDelete(finished);
                _bufPrev = ""; _bufCur = ""; _armed = false; _repGame = null;
                try { HighlightCoordinator.Stop(); } catch { }
                DiagLog.Line("CLIP", "replay disarmed");
                Notify?.Invoke("replay", "{\"armed\":false}");
            }
            catch { }
            finally { _repGate.Release(); }
        }

        public static async Task SaveReplayAsync()
        {
            if (!_armed) { DiagLog.Line("CLIP", "save replay: not armed (no buffer) — nothing to save"); Notify?.Invoke("noreplay", "{}"); return; }
            DiagLog.Line("CLIP", "save replay: capturing last " + _replayLen + "s");
            if (_chime) ClipChime.Play();   // instant "got it" the moment the hotkey lands — BEFORE the (slow) stop+finalize+stitch
            Notify?.Invoke("saving", "{}");   // → gallery shows an optimistic "saving" card immediately (resolves on "saved")
            string gameTmp = "";
            await _repGate.WaitAsync();
            try
            {
                // Re-check INSIDE the gate (like RotateAsync) — a disarm/re-arm holding the gate flips _armed while this
                // save is queued; without this, the paths below called StartSegment() on a DISARMED engine, spawning an
                // orphan recorder (no rotation timer, nothing ever stops it) that captured to TEMP until process exit.
                if (!_armed) { DiagLog.Line("CLIP", "save replay: disarmed while queued — skipped"); Notify?.Invoke("noreplay", "{}"); return; }
                // Snapshot the mic/chat rings FIRST — they keep rolling during the stop/concat (and the AV1 transcode,
                // which can take a minute), so a late DumpRing captured audio from AFTER the save point. Temp wavs in
                // the buffer dir → moved next to the finished clip on success, deleted on failure.
                string micTmp = "", chatTmp = "";
                if (_sepMic && _mic) { micTmp = Path.Combine(BufferDir, "mic_" + DateTime.Now.Ticks + ".wav"); _micSide.DumpRing(micTmp, _replayLen); }
                if (_chat)           { chatTmp = Path.Combine(BufferDir, "chat_" + DateTime.Now.Ticks + ".wav"); _chatSide.DumpRing(chatTmp, _replayLen); }
                gameTmp = Path.Combine(BufferDir, "game_" + DateTime.Now.Ticks + ".wav"); _gameSide.DumpRing(gameTmp, _replayLen);   // no-op without a game ring
                string? cur = await StopSegmentAsync();
                if (cur == null) { Notify?.Invoke("trimfail", "{}"); StartSegment(); TryDelete(micTmp); TryDelete(chatTmp); return; }
                string src = SourceWindowTitle();   // a pinned source window names the replay after the pinned app
                string capTitle = _capKind == "window" && IsWindow(_capHwnd) ? WindowTitle(_capHwnd) : "";
                string game = src.Length > 0 ? src : !string.IsNullOrWhiteSpace(_repGame) ? _repGame! : capTitle.Length > 0 ? capTitle : ForegroundAppName();   // else the foreground game when not launcher-launched
                string? outName = await ComposeLastSeconds(_bufPrev, cur, _replayLen, game);
                // keep the just-stopped segment as the new prev so coverage continues, then roll a fresh current
                TryDelete(_bufPrev); _bufPrev = cur; StartSegment();
                double syncLag = 0;   // measured game-track correction — mic/chat were dumped at the same instant, so they share it
                if (outName != null && File.Exists(gameTmp))
                {
                    // Sync reference = the raw segment just stopped (`cur`, still on disk as _bufPrev): it is the clip's tail.
                    string clipPath = Path.Combine(ClipsDir, outName);
                    double vid = 0, curLen = 0;
                    try { vid = ClipLibrary.Mp4DurationSeconds(clipPath); curLen = ClipLibrary.Mp4DurationSeconds(cur); } catch { }
                    syncLag = (await ClipFfmpeg.AlignAndReplaceAudioAsync(clipPath, gameTmp, alignEnd: true,
                        syncRef: curLen > 0 && vid > 0 ? cur : null, syncRefOffset: Math.Max(0, vid - curLen))).lag;
                }
                // The mic/chat rings hold replayLen seconds, but the clip can be SHORTER (follow-focus starts a fresh
                // segment; a size change drops the previous one). Both end at the save point → keep only the tail that
                // matches the video. before: a 21 s replay got a 60 s chat track, ~39 s out of sync.
                if (outName != null)
                {
                    double vid = 0; try { vid = ClipLibrary.Mp4DurationSeconds(Path.Combine(ClipsDir, outName)); } catch { }
                    if (vid > 0) { FitSidecar(micTmp, vid, syncLag); FitSidecar(chatTmp, vid, syncLag); }
                }
                if (outName != null && Av1Mode) { Notify?.Invoke("transcoding", "{}"); outName = await TranscodeReplayAv1(outName); }
                if (outName != null)
                {
                    string final = Path.Combine(ClipsDir, outName);
                    MoveSidecar(micTmp, MicSidecar(final)); MoveSidecar(chatTmp, ChatSidecar(final));
                    PruneToCap(); KickProxy(final);
                    Notify?.Invoke("saved", Newtonsoft.Json.JsonConvert.SerializeObject(new { name = outName }));
                }
                else { TryDelete(micTmp); TryDelete(chatTmp); Notify?.Invoke("trimfail", "{}"); }
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "save replay err: " + ex.Message); Notify?.Invoke("trimfail", "{}"); try { if (_armed) StartSegment(); } catch { } }
            finally { TryDelete(gameTmp); _repGate.Release(); }
        }

        private static async Task RotateAsync()
        {
            if (!_armed) return;
            await _repGate.WaitAsync();
            try
            {
                if (!_armed) return;
                string? finished = await StopSegmentAsync();
                TryDelete(_bufPrev); _bufPrev = finished ?? "";
                StartSegment();
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "rotate err: " + ex.Message); }
            finally { _repGate.Release(); }
        }

        // Begin a new buffer segment (caller holds _repGate).
        private static void StartSegment()
        {
            _bufCur = Path.Combine(BufferDir, "seg_" + DateTime.Now.Ticks + ".mp4");
            _segDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcs = _segDone; string p = _bufCur;
            var rec = Recorder.CreateRecorder(BuildOptions());
            SyncGameRing();
            if (_chat && !_chatSide.RingActive) _chatSide.StartRing(GameRingSeconds);   // chat app (e.g. Discord) opened after arming
            _repRec = rec;
            rec.OnRecordingComplete += (s, e) => tcs.TrySetResult(e.FilePath);
            // A segment dying mid-buffer (e.g. the pinned source window was closed) used to go completely unobserved —
            // the "armed" replay was silently dead until the next rotation (≥120 s). Log it and roll a fresh segment
            // (BuildOptions re-resolves the source, falling back to auto if the pinned window is gone).
            rec.OnRecordingFailed += (s, e) =>
            {
                tcs.TrySetResult(p);
                DiagLog.Line("CLIP", "replay segment failed: " + (e.Error ?? "?") + " — restarting buffer");
                _ = Task.Run(() => RestartFailedSegmentAsync(rec));
            };
            _segStart[p] = DateTime.UtcNow;   // real start time → the replay join keeps the gap between segments
            foreach (var k in _segStart.Keys) if (k != p && !File.Exists(k)) _segStart.TryRemove(k, out _);
            rec.Record(p);
        }

        // Replace a dead segment recorder (identity-checked: a rotate/save may already have rolled a new one).
        // The 1 s damp keeps a persistently-failing source (display unplugged) from spinning restart loops.
        private static async Task RestartFailedSegmentAsync(Recorder failed)
        {
            await Task.Delay(1000);
            await _repGate.WaitAsync();
            try
            {
                if (!_armed || !ReferenceEquals(_repRec, failed)) return;
                string? finished = await StopSegmentAsync();   // reaps the dead recorder (tcs already resolved)
                TryDelete(finished);                           // a failed segment's partial file is unusable
                StartSegment();
                DiagLog.Line("CLIP", "replay buffer restarted after segment failure");
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "segment restart err: " + ex.Message); }
            finally { _repGate.Release(); }
        }

        // Stop the current segment and await its finalized file (caller holds _repGate). Returns the path or null.
        private static async Task<string?> StopSegmentAsync()
        {
            var rec = _repRec; var tcs = _segDone; _repRec = null; _segDone = null;
            if (rec == null || tcs == null) return null;
            string path;
            try { rec.Stop(); path = await tcs.Task.ConfigureAwait(false); }
            catch { path = _bufCur; }
            finally { _ = Task.Run(() => { try { rec.Dispose(); } catch { } }); }
            return path;
        }

        // Stitch the last N seconds (prev tail + current) into a Game_…_replay.mp4. Returns the new name, or null.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _segStart = new();
        private static async Task<string?> ComposeLastSeconds(string prev, string cur, int n, string? game)
        {
            double prevSpan = !string.IsNullOrEmpty(prev) && _segStart.TryGetValue(prev, out var ps) && _segStart.TryGetValue(cur, out var cs)
                ? (cs - ps).TotalSeconds : 0;
            string outName = Unique(SafeName(string.IsNullOrWhiteSpace(game) ? "Desktop" : game!)
                             + "_" + DateTime.Now.ToString("dd.MM.yyyy.HH.mm") + "_replay.mp4");
            string outPath = Path.Combine(ClipsDir, outName);

            // Primary: ffmpeg concat + STREAM COPY — exact capture quality, instant, can't fail on encode, and never
            // leaves a partial in the gallery (temp → validate → atomic move). Replaces the old MediaComposition
            // re-encode that produced low-bitrate / 0-byte clips.
            if (ClipFfmpeg.Ready && await ClipFfmpeg.ConcatLastSecondsAsync(prev, cur, n, outPath, prevSpan))
            { DiagLog.Line("CLIP", "replay (ff) -> " + outName); return outName; }

            // Fallback (ffmpeg not yet downloaded): MediaComposition stitch → H.264. Validated; deletes any orphan.
            Windows.Storage.StorageFile? created = null;
            try
            {
                var curFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(cur);
                var curClip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(curFile);
                double curDur = curClip.OriginalDuration.TotalSeconds;
                var comp = new Windows.Media.Editing.MediaComposition();
                if (curDur >= n)
                {
                    curClip.TrimTimeFromStart = TimeSpan.FromSeconds(curDur - n);
                    comp.Clips.Add(curClip);
                }
                else if (!string.IsNullOrEmpty(prev) && File.Exists(prev))
                {
                    double need = n - curDur;
                    var prevFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(prev);
                    var prevClip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(prevFile);
                    double prevDur = prevClip.OriginalDuration.TotalSeconds;
                    if (prevDur > need) prevClip.TrimTimeFromStart = TimeSpan.FromSeconds(prevDur - need);
                    comp.Clips.Add(prevClip); comp.Clips.Add(curClip);
                }
                else comp.Clips.Add(curClip);
                if (comp.Duration.TotalSeconds < 0.2) return null;

                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(ClipsDir);
                var outFile = await folder.CreateFileAsync(outName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                created = outFile;
                var profile = await ClipLibrary.MakeProfileAsync(curFile, _mbps, _fps, false);
                var res = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
                if (res != Windows.Media.Transcoding.TranscodeFailureReason.None)
                    res = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise);   // fallback to the default profile
                if (res != Windows.Media.Transcoding.TranscodeFailureReason.None || !File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                { try { await outFile.DeleteAsync(); } catch { } DiagLog.Line("CLIP", "replay render failed: " + res); return null; }
                DiagLog.Line("CLIP", "replay -> " + outName);
                return outName;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "compose err: " + ex.Message); if (created != null) { try { await created.DeleteAsync(); } catch { } } return null; }
        }
        private static void TryDelete(string? p) { try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); } catch { } }
        // Replay sidecar → exactly `vid` seconds lined up with the video: the ring ends at the save instant, which lands at
        // video time vid + lag (lag = the measured game-track correction). Keeps ring [len-vid-lag, len-lag], padding with
        // silence where that window runs off either end. lag 0 == TrimWavToTail.
        private static void FitSidecar(string path, double vid, double lag)
        {
            if (Math.Abs(lag) < 0.001) { TrimWavToTail(path, vid); return; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string tmp = path + ".fit";
            try
            {
                using (var r = new NAudio.Wave.WaveFileReader(path))
                {
                    var fmt = r.WaveFormat; int block = fmt.BlockAlign; double len = r.TotalTime.TotalSeconds;
                    double start = len - vid - lag, end = len - lag;
                    long Bytes(double sec) { long b = (long)(sec * fmt.AverageBytesPerSecond); return b - b % block; }
                    using var w = new NAudio.Wave.WaveFileWriter(tmp, fmt);
                    if (start < 0) { var z = new byte[Bytes(-start)]; w.Write(z, 0, z.Length); start = 0; }
                    r.Position = Bytes(start); long want = Bytes(Math.Min(end, len) - start); var buf = new byte[block * 4096];
                    while (want > 0) { int n = r.Read(buf, 0, (int)Math.Min(buf.Length, want)); if (n <= 0) break; w.Write(buf, 0, n); want -= n; }
                    if (end > len) { var z = new byte[Bytes(end - len)]; w.Write(z, 0, z.Length); }
                }
                File.Delete(path); File.Move(tmp, path);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "sidecar fit err: " + ex.Message); try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        // Keep only the last `seconds` of a PCM WAV (in place). No-op when it's already that short or missing.
        private static void TrimWavToTail(string path, double seconds)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string tmp = path + ".trim";
            try
            {
                using (var r = new NAudio.Wave.WaveFileReader(path))
                {
                    double extra = r.TotalTime.TotalSeconds - seconds;
                    if (extra < 0.05) return;
                    int block = r.WaveFormat.BlockAlign;
                    long skip = (long)(extra * r.WaveFormat.AverageBytesPerSecond); skip -= skip % block;
                    r.Position = skip;
                    using var w = new NAudio.Wave.WaveFileWriter(tmp, r.WaveFormat);
                    var buf = new byte[block * 4096]; int n;
                    while ((n = r.Read(buf, 0, buf.Length)) > 0) w.Write(buf, 0, n);
                }
                File.Delete(path); File.Move(tmp, path);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "sidecar trim err: " + ex.Message); try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        private static void MoveSidecar(string tmp, string dest) { try { if (tmp.Length > 0 && File.Exists(tmp)) { if (File.Exists(dest)) File.Delete(dest); File.Move(tmp, dest); } } catch { } }

        private static string SafeName(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.Trim()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string r = sb.ToString().Trim('_');
            while (r.Contains("__")) r = r.Replace("__", "_");
            if (r.Length == 0) r = "Clip";
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        // Never clobber a clip recorded in the same minute. Also checks the AV1 temp dir: in AV1 mode the ClipsDir
        // final doesn't exist until the fire-and-forget transcode finishes, but its H.264 temp does the whole time —
        // before: a same-minute re-record reused the name, truncated the temp mid-transcode AND overwrote the
        // finished clip (the "Clip saved" toast pointed at a file that no longer existed).
        private static string Unique(string file)
        {
            string dir = ClipsDir, tmp = Av1TempDir, stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
            string p = file; int n = 2;
            while (File.Exists(Path.Combine(dir, p)) || File.Exists(Path.Combine(tmp, p))) p = stem + "_" + (n++) + ext;
            return p;
        }
    }
}
