// BridgeApi (partial) — game-clip capture (Sparta Clips). JS↔C# surface for record/list/manage; the heavy lifting
// is in Services/Capture (ClipRecorder + ClipLibrary). Listing runs off-thread and returns via window.onClips so
// the WebView2 UI thread is never blocked (see the bridge-UI-thread rule).
using System;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SpartaClips
{
    public partial class BridgeApi
    {
        private bool _captureWired;
        // TrayCore owns ClipRecorder.Notify (it must fire the over-game "clip saved" toast even when no window exists).
        // When the UI IS open, the bridge registers a web sink so recorder events also reach JS (window.onCapture) and
        // the gallery live-refreshes after a save. The sink is cleared when the window closes (MainForm.OnFormClosed).
        // before: EnsureCaptureWired set ClipRecorder.Notify directly + called _form.ShowClipToast.
        // after:  it sets TrayCore.WebSink; TrayCore.Notify handles the toast (works with the window closed).
        private void EnsureCaptureWired()
        {
            if (_captureWired) return; _captureWired = true;
            TrayCore.WebSink = (ev, json) =>
            {
                try { PostToWeb("window.onCapture && window.onCapture(" + JsonConvert.SerializeObject(ev) + "," + json + ")"); } catch { }
                // After a save, push the fresh clip list straight from C# so the gallery live-refreshes. Once — the
                // file is fully finalized before "saved" fires (before: 2 pushes here + 2 web requestClips = 4 full
                // gallery rebuilds per save).
                if (ev == "saved")
                    Task.Run(async () =>
                    {
                        try { await Task.Delay(300); PostToWeb("window.onClips && window.onClips(" + ClipLibrary.ListJson() + ")"); } catch { }
                    });
            };
        }

        // ── Recording (off-thread: capture init/teardown can take a beat) ──
        public void StartClipRecord(string gameName) { EnsureCaptureWired(); ClipRecorder.LastGameName = gameName ?? ""; Task.Run(() => { try { ClipRecorder.Start(gameName); } catch { } }); }
        // JS pushes the current game title so the C# hotkey can name a recording even when the launcher is tray-hidden.
        public void SetClipGame(string title) { ClipRecorder.LastGameName = title ?? ""; }

        // ── Microphone device picker (which mic the separate-track sidecar records) ──
        public string ListMics() => ListEndpoints(NAudio.CoreAudioApi.DataFlow.Capture);
        public void SetMicDevice(string id) { try { ClipRecorder.SetMicDevice(id); } catch { } }

        // ── Chat device picker: OUTPUT (render) devices — many headsets expose a separate "Chat" output so you can
        // hear Discord apart from the game; we loopback-capture the chosen one into the separate chat track. ──
        // Chat source: per-APP voice apps first ("app:<process>", captured regardless of device volume), then the
        // output devices (loopback). Discord is always offered; the others only while running.
        public string ListChatDevices()
        {
            var list = new System.Collections.Generic.List<object> { new { id = "app:Discord", name = "App · Discord (recommended)" } };
            foreach (var (proc, label) in new[] { ("DiscordPTB", "Discord PTB"), ("DiscordCanary", "Discord Canary"), ("TeamSpeak", "TeamSpeak"), ("ts3client_win64", "TeamSpeak 3"), ("mumble", "Mumble") })
                try { var ps = System.Diagnostics.Process.GetProcessesByName(proc); bool on = ps.Length > 0; foreach (var p in ps) p.Dispose(); if (on) list.Add(new { id = "app:" + proc, name = "App · " + label }); } catch { }
            try { list.AddRange(JsonConvert.DeserializeObject<System.Collections.Generic.List<object>>(ListEndpoints(NAudio.CoreAudioApi.DataFlow.Render)) ?? new()); } catch { }
            return JsonConvert.SerializeObject(list);
        }
        public void SetChatDevice(string id) { try { ClipRecorder.SetChatDevice(id); } catch { } }

        // ── Dual-screen capture: monitor picker + layout push (for the Game Capture "Dual screen" designer) ──
        // Displays come from ScreenRecorderLib (the device name DisplayRecordingSource consumes); dimensions/primary
        // are cross-referenced from Screen.AllScreens by device name so the designer can size each screen to its aspect.
        public string ListMonitors()
        {
            try
            {
                var byName = new System.Collections.Generic.Dictionary<string, System.Windows.Forms.Screen>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in System.Windows.Forms.Screen.AllScreens) byName[s.DeviceName] = s;
                var list = new System.Collections.Generic.List<object>(); int i = 1;
                foreach (var d in ScreenRecorderLib.Recorder.GetDisplays())
                {
                    int w = 0, h = 0; bool primary = false;
                    if (!string.IsNullOrEmpty(d.DeviceName) && byName.TryGetValue(d.DeviceName, out var sc)) { w = sc.Bounds.Width; h = sc.Bounds.Height; primary = sc.Primary; }
                    list.Add(new { id = d.DeviceName, name = string.IsNullOrWhiteSpace(d.FriendlyName) ? ("Display " + i) : d.FriendlyName, w, h, primary });
                    i++;
                }
                return JsonConvert.SerializeObject(list);
            }
            catch { return "[]"; }
        }
        // { on, monA, monB, layout:{ a:{x,y,w,h}, b:{x,y,w,h} } } — rects in "1 unit = 1080 px" space (see ClipRecorder).
        public void SetDualScreen(string json)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                ClipRecorder.SetDualScreen(
                    o.Value<bool?>("on") ?? false,
                    o.Value<string>("monA") ?? "",
                    o.Value<string>("monB") ?? "",
                    o["layout"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "SetDualScreen err: " + ex.Message); }
        }

        // ── Capture-source picker (title-bar): session-only override — auto (settings behavior) / desktop / a pinned
        // window. Never persisted, so it can't interfere with the saved capture settings. hwnd as a string: JS numbers
        // are doubles and COM marshaling of a raw 64-bit handle is not worth the risk.
        public void SetCaptureSource(string mode, string hwnd)
        {
            EnsureCaptureWired();
            try { long.TryParse(hwnd, out long h); ClipRecorder.SetCaptureSource(mode ?? "auto", h); } catch { }
        }
        // Enumerate open windows with preview thumbnails → window.onCaptureWindows([...]). Off-thread: PrintWindow per
        // window can take tens of ms each (bridge-UI-thread rule).
        public void RequestCaptureWindows()
        {
            Task.Run(() =>
            {
                string json; try { json = CaptureSources.ListWindowsJson(); } catch { json = "[]"; }
                PostToWeb("window.onCaptureWindows && window.onCaptureWindows(" + json + ")");
            });
        }

        private static string ListEndpoints(NAudio.CoreAudioApi.DataFlow flow)
        {
            try
            {
                var list = new System.Collections.Generic.List<object>();
                using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active))
                {
                    try { list.Add(new { id = d.ID, name = d.FriendlyName }); } catch { } finally { try { d.Dispose(); } catch { } }
                }
                return JsonConvert.SerializeObject(list);
            }
            catch { return "[]"; }
        }

        // Rebind the record / save-replay global hotkeys. mods = MOD_ALT(1)|MOD_CONTROL(2)|MOD_SHIFT(4)|MOD_WIN(8);
        // vk = Windows virtual-key (0 = unbound). Returns "ok" / "failed" (the low-level hook can't detect OS conflicts,
        // so there is no honest "conflict" result — the combo is simply intercepted before the game sees it).
        public string SetClipHotkeys(string json)
        {
            try
            {
                var h = JsonConvert.DeserializeObject<HotkeysDto>(json); if (h == null) return "failed";
                // before: _form.SetClipHotkeys(...). after: TrayCore owns the hotkeys so they persist with the window closed.
                return (TrayCore.Instance?.SetClipHotkeys(h.recordMods, h.recordVk, h.replayMods, h.replayVk, h.markMods, h.markVk) ?? false) ? "ok" : "failed";
            }
            catch { return "failed"; }
        }
        private sealed class HotkeysDto
        {
            public uint recordMods { get; set; }
            public uint recordVk { get; set; }
            public uint replayMods { get; set; }
            public uint replayVk { get; set; }
            public uint markMods { get; set; }
            public uint markVk { get; set; }
        }
        // Read + consume the mid-record bookmark sidecar (written by the mark-a-moment hotkey) → a JSON array of
        // second offsets via window.onClipMarks(name, arr); deletes the sidecar so marks import exactly once.
        // before: GetClipMarks returned the JSON synchronously — file IO against the (possibly SMB) clips folder
        // inside the bridge dispatch on the UI/message-pump thread, the exact hazard CheckFolderWritable documents.
        public void RequestClipMarks(string name)
        {
            EnsureCaptureWired();
            Task.Run(() =>
            {
                string json = "[]";
                try
                {
                    string? p = ClipLibrary.PathOf(name);
                    string side = p == null ? "" : ClipRecorder.MarksSidecar(p);
                    if (side.Length > 0 && System.IO.File.Exists(side))
                    {
                        string raw = System.IO.File.ReadAllText(side);
                        try { System.IO.File.Delete(side); } catch { }
                        // Re-serialize through a typed parse — the payload is injected into ExecuteScript, so a
                        // corrupt sidecar must degrade to [] rather than break the script.
                        try { var arr = JsonConvert.DeserializeObject<double[]>(raw); if (arr != null && arr.Length > 0) json = JsonConvert.SerializeObject(arr); } catch { }
                    }
                }
                catch { }
                PostToWeb("window.onClipMarks && window.onClipMarks(" + JsonConvert.SerializeObject(name) + "," + json + ")");
            });
        }
        public void StopClipRecord() { Task.Run(() => { try { ClipRecorder.Stop(); } catch { } }); }
        public bool IsClipRecording() { try { return ClipRecorder.IsRecording; } catch { return false; } }
        // Live-engine snapshot (JSON) for the diagnostic tools — see ClipRecorder.DiagJson.
        public string GetEngineDiag() { try { return ClipRecorder.DiagJson(); } catch { return "{}"; } }
        public string GetCaptureTarget() { try { return ClipRecorder.CaptureTargetJson(); } catch { return "{\"state\":\"idle\"}"; } }   // gallery "Capturing <app>" line (polled ~1 Hz while visible)
        // Elapsed seconds of the in-progress recording — boot syncs the HUD from these (a hotkey recording started
        // with no window open pushed no "state" event the late-opened page could have seen).
        public double RecElapsedSec() { try { return ClipRecorder.RecElapsedSec; } catch { return 0; } }
        // True when a hardware AV1 encoder exists (MF MFT probe; or ffmpeg's own probe once it's installed) — the UI
        // greys out the AV1 option otherwise, since software AV1 is too slow to be worth offering.
        public bool HasHardwareAv1() { try { return HardwareAv1.Available || (Av1Transcoder.FfmpegReady && Av1Transcoder.HasHardwareAv1()); } catch { return false; } }

        // ── Instant replay (background buffer; "Save replay" writes the last N seconds) ──
        public void ArmReplay(string gameName)  { EnsureCaptureWired(); ClipRecorder.LastGameName = gameName ?? ""; Task.Run(async () => { try { await ClipRecorder.ArmAsync(gameName); } catch { } }); }
        public void DisarmReplay()              { Task.Run(async () => { try { await ClipRecorder.DisarmAsync(); } catch { } }); }
        public void SaveReplay()                { EnsureCaptureWired(); Task.Run(async () => { try { await ClipRecorder.SaveReplayAsync(); } catch { } }); }
        public bool IsReplayArmed()             { try { return ClipRecorder.IsReplayArmed; } catch { return false; } }

        // Auto-highlight (LoL/TFT): opt-in. Reads Riot's official local in-game API (no memory access, Vanguard-safe)
        // and saves a replay on your kills/assists. Follows the buffer — only polls while a replay is armed.
        public void SetAutoHighlight(string on)
        {
            EnsureCaptureWired();
            HighlightCoordinator.Enabled = on == "true" || on == "1";
            if (HighlightCoordinator.Enabled && ClipRecorder.IsReplayArmed) HighlightCoordinator.Start();
            else HighlightCoordinator.Stop();
        }

        // Pre-download the FFmpeg AV1 encoder (managed tool) so the first AV1 recording doesn't stall on it.
        // Progress reuses the managed-tool indicator: window.onManagedInstall("ffmpeg", {state,pct,msg}).
        public void EnsureAv1Encoder()
        {
            Task.Run(async () =>
            {
                try { await Av1Transcoder.EnsureAsync((state, pct, msg) =>
                    PostToWeb("window.onManagedInstall && window.onManagedInstall(\"ffmpeg\"," + JsonConvert.SerializeObject(new { state, pct, msg }) + ")")); }
                catch { }
            });
        }

        // ── Library (callback, never blocks the UI thread) ──
        public void RequestClips()
        {
            EnsureCaptureWired();
            Task.Run(() =>
            {
                string json; try { json = ClipLibrary.ListJson(); } catch { json = "{}"; }
                PostToWeb("window.onClips && window.onClips(" + json + ")");
            });
        }
        public void DeleteClip(string name)            { Task.Run(() => { try { ClipLibrary.Delete(name); } catch (Exception ex) { DiagLog.Line("CLIP", "delete err: " + ex.Message); Toast("Delete failed", "Couldn't delete this clip — it may be open in another app."); } }); }
        // Gallery poster: build (cached) a JPEG thumbnail and hand back its clipthumb.local URL via window.onClipThumb.
        // Replaces the per-card <video> thumbnails (N live decoders) that made the gallery lag and clips slow to open.
        public void EnsureThumb(string name)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                string url = "";
                try { var t = await ClipLibrary.EnsureThumbAsync(name); if (!string.IsNullOrEmpty(t)) url = "https://clipthumb.local/" + Uri.EscapeDataString(t); } catch { }
                PostToWeb("window.onClipThumb && window.onClipThumb(" + JsonConvert.SerializeObject(name) + "," + JsonConvert.SerializeObject(url) + ")");
            });
        }
        // Gallery card tech badges: probe codec + fps (cached) and hand them back via window.onClipInfo.
        public void EnsureClipInfo(string name)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                try
                {
                    var p = ClipLibrary.PathOf(name); if (p == null) return;
                    var (codec, fps, height, width) = await ClipFfmpeg.MediaInfoAsync(p);
                    PostToWeb("window.onClipInfo && window.onClipInfo(" + JsonConvert.SerializeObject(name) + "," + JsonConvert.SerializeObject(new { codec, fps, height, width }) + ")");
                }
                catch { }
            });
        }
        // Non-native HEVC preview: build (cached) an H.264 proxy and hand back its clipproxy.local URL via window.onClipProxy.
        public void EnsureHevcProxy(string name)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                try
                {
                    var file = await ClipProxy.EnsureAsync(name);
                    string url = string.IsNullOrEmpty(file) ? "" : "https://clipproxy.local/" + Uri.EscapeDataString(file!);
                    PostToWeb("window.onClipProxy && window.onClipProxy(" + JsonConvert.SerializeObject(name) + "," + JsonConvert.SerializeObject(url) + ")");
                }
                catch { PostToWeb("window.onClipProxy && window.onClipProxy(" + JsonConvert.SerializeObject(name) + ",\"\")"); }
            });
        }
        public void DeleteMic(string name)             { Task.Run(() => { try { ClipLibrary.DeleteMic(name); } catch { } }); }
        public void DeleteChat(string name)            { Task.Run(() => { try { ClipLibrary.DeleteChat(name); } catch { } }); }
        public void RenameClip(string oldName, string newName) { Task.Run(() => { try { ClipLibrary.Rename(oldName, newName); } catch (Exception ex) { DiagLog.Line("CLIP", "rename err: " + ex.Message); Toast("Rename failed", "Couldn't rename this clip — it may be open in another app."); } }); }
        public void OpenClipsFolder()                  { try { ClipLibrary.OpenFolder(); } catch { } }
        public void RevealClip(string name)            { try { ClipLibrary.Reveal(name); } catch { } }
        public string GetClipsDir()                    { try { return ClipRecorder.PeekClipsDir; } catch { return ""; } }   // Peek: no CreateDirectory on the UI thread (dead SMB path = pump stall)
        public void SetClipsDir(string dir)            { try { ClipRecorder.SetClipsDir(dir); } catch { } }
        // Round-trip write probe so the UI can refuse a read-only / admin-only Save folder before committing.
        // Callback style (→ window.onFolderWritable(ok)) — a dead network path can block on the SMB timeout for tens
        // of seconds, which must never happen on the UI/message-pump thread (it also hosts the keyboard hook).
        public void CheckFolderWritable(string dir)
        {
            Task.Run(() =>
            {
                bool ok;
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) ok = true;
                    else
                    {
                        System.IO.Directory.CreateDirectory(dir);
                        string probe = System.IO.Path.Combine(dir, ".sc_write_" + Environment.ProcessId + ".tmp");
                        System.IO.File.WriteAllText(probe, "x"); System.IO.File.Delete(probe);
                        ok = true;
                    }
                }
                catch { ok = false; }
                PostToWeb("window.onFolderWritable && window.onFolderWritable(" + (ok ? "true" : "false") + ")");
            });
        }

        // Capture prefs from the "Game Capture" settings pane: { fps, mbps, resH, mic, gameCapture, capBytes }.
        public void SetCaptureSettings(string json)
        {
            EnsureCaptureWired();   // JS pushes this at boot → wires ClipRecorder→JS notifications before any hotkey fires
            try
            {
                var s = JsonConvert.DeserializeObject<CaptureSettingsDto>(json);
                if (s == null) return;
                ClipRecorder.SetSettings(s.fps, s.mbps, s.resH, s.mic, s.sepMic, s.chat, s.gameCapture, s.capBytes, s.replayLen, s.encoder, s.chime);
            }
            catch { }
        }
        private sealed class CaptureSettingsDto
        {
            public int fps { get; set; } = 60;
            public int mbps { get; set; } = 20;
            public int resH { get; set; } = 0;
            public bool mic { get; set; }
            public bool sepMic { get; set; }
            public bool chat { get; set; }
            public bool gameCapture { get; set; }
            public long capBytes { get; set; }
            public int replayLen { get; set; } = 30;
            public string encoder { get; set; } = "h264";
            public bool chime { get; set; } = true;
        }

        // Trim a clip to [start,end] → a new Game_…_clip.mp4 (off-thread; native MediaComposition render).
        public void TrimClip(string name, double start, double end)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                string? outName = null;
                try { outName = await ClipLibrary.TrimAsync(name, start, end); } catch { }
                if (outName != null) PostToWeb("window.onCapture && window.onCapture(\"saved\"," + JsonConvert.SerializeObject(new { name = outName }) + ")");
                else PostToWeb("window.onCapture && window.onCapture(\"trimfail\",{})");
            });
        }

        // Pure trim (no audio mix / volume change) → frame-exact smart cut: re-encode only the cut edges, copy the
        // middle (near-lossless + fast). AV1 keeps its codec via the AV1 compose path; H.264/H.265 use SmartCutAsync.
        public void SmartCut(string name, double start, double end)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                string? outName = null;
                try
                {
                    if (ClipLibrary.IsAv1Clip(name))
                        outName = await ClipLibrary.ComposeAv1Async(name, start, end, 1.0, false, 0, 0, 0, 1, false, 0, 0, 0, 1, ClipRecorder.Av1Kbps);
                    else
                        outName = await ClipLibrary.SmartCutAsync(name, start, end);
                }
                catch { }
                if (outName != null) PostToWeb("window.onCapture && window.onCapture(\"saved\"," + JsonConvert.SerializeObject(new { name = outName }) + ")");
                else PostToWeb("window.onCapture && window.onCapture(\"trimfail\",{})");
            });
        }

        // Multi-track compose: trimmed video (+vol) mixed with the trimmed/offset mic sidecar (+vol) → a new clip.
        // opts = { vidIn, vidOut, vidVol, micOn, micIn, micOut, micOffset, micVol } (volumes 0..2, 1 = unchanged).
        public void ComposeClip(string name, string optsJson)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                string? outName = null;
                try
                {
                    var o = JsonConvert.DeserializeObject<ComposeDto>(optsJson) ?? new ComposeDto();
                    // AV1 sources can't go through MediaComposition (no MF AV1 decoder) → ffmpeg path.
                    if (ClipLibrary.IsAv1Clip(name))
                        outName = await ClipLibrary.ComposeAv1Async(name, o.vidIn, o.vidOut, o.vidVol, o.micOn, o.micIn, o.micOut, o.micOffset, o.micVol, o.chatOn, o.chatIn, o.chatOut, o.chatOffset, o.chatVol, ClipRecorder.Av1Kbps);
                    else
                        outName = await ClipLibrary.ComposeAsync(name, o.vidIn, o.vidOut, o.vidVol, o.micOn, o.micIn, o.micOut, o.micOffset, o.micVol, o.chatOn, o.chatIn, o.chatOut, o.chatOffset, o.chatVol);
                }
                catch { }
                if (outName != null) PostToWeb("window.onCapture && window.onCapture(\"saved\"," + JsonConvert.SerializeObject(new { name = outName }) + ")");
                else PostToWeb("window.onCapture && window.onCapture(\"trimfail\",{})");
            });
        }

        // Generalized export to a file the USER picked (not the library): format "mp4" (reuses the editor compose graph:
        // trim + track mix) or "gif" (two-pass palette, no audio). Reports via window.onExport(ok, path).
        public void ExportFile(string name, string outPath, string format, string optsJson)
        {
            EnsureCaptureWired();
            Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    var o = JsonConvert.DeserializeObject<ComposeDto>(optsJson) ?? new ComposeDto();
                    if (string.Equals(format, "gif", StringComparison.OrdinalIgnoreCase))
                        ok = await ClipExport.ExportGifAsync(name, outPath, o.vidIn, o.vidOut, o.gifFps, o.gifWidth);
                    else
                    {
                        string? src = ClipLibrary.PathOf(name);
                        if (src != null && System.IO.File.Exists(src))
                        {
                            string mic = ClipRecorder.MicSidecar(src); if (!System.IO.File.Exists(mic)) mic = "";
                            string chat = ClipRecorder.ChatSidecar(src); if (!System.IO.File.Exists(chat)) chat = "";
                            if (!outPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outPath += ".mp4";
                            ok = await ClipFfmpeg.ComposeAsync(src, mic.Length > 0 ? mic : null, chat.Length > 0 ? chat : null,
                                o.vidIn, o.vidOut, o.vidVol, o.micOn, o.micIn, o.micOut, o.micOffset, o.micVol,
                                o.chatOn, o.chatIn, o.chatOut, o.chatOffset, o.chatVol, outPath);
                        }
                    }
                }
                catch (Exception ex) { DiagLog.Line("EXPORT", "err: " + ex.Message); }
                PostToWeb("window.onExport && window.onExport(" + (ok ? "true" : "false") + "," + JsonConvert.SerializeObject(outPath) + ")");
            });
        }

        // Export several saved segments of one clip — SEQUENTIALLY (no temp-name collisions / CPU storm). combine=false
        // → one clip per segment (smart-cut/compose each); combine=true → all segments concatenated into one clip.
        // Emits "segprogress" {done,total} as each finishes and a "saved" per produced clip so the gallery live-updates.
        public void ExportSegments(string name, string optsJson)
        {
            EnsureCaptureWired();
            void Saved(string n) => PostToWeb("window.onCapture && window.onCapture(\"saved\"," + JsonConvert.SerializeObject(new { name = n }) + ")");
            void Fail() => PostToWeb("window.onCapture && window.onCapture(\"trimfail\",{})");
            Task.Run(async () =>
            {
                try
                {
                    var o = JsonConvert.DeserializeObject<SegExportDto>(optsJson);
                    var segs = o?.segments;
                    if (o == null || segs == null || segs.Length == 0) { Fail(); return; }
                    bool av1 = ClipLibrary.IsAv1Clip(name);
                    bool pure = o.vidVol > 0.999 && o.vidVol < 1.001 && !o.micOn && !o.chatOn;

                    if (o.combine)
                    {
                        var ranges = new (double, double)[segs.Length];
                        for (int j = 0; j < segs.Length; j++) ranges[j] = (segs[j].@in, segs[j].@out);
                        string? outName = await ClipLibrary.CombineSegmentsAsync(name, ranges, o.vidVol, o.micOn, o.micIn, o.micOut, o.micOffset, o.micVol, o.chatOn, o.chatIn, o.chatOut, o.chatOffset, o.chatVol);
                        PostToWeb("window.onCapture && window.onCapture(\"segprogress\"," + JsonConvert.SerializeObject(new { done = 1, total = 1 }) + ")");
                        if (outName != null) Saved(outName); else Fail();
                        return;
                    }

                    int done = 0; bool any = false;
                    foreach (var s in segs)
                    {
                        string? outName;
                        if (av1) outName = await ClipLibrary.ComposeAv1Async(name, s.@in, s.@out, o.vidVol, o.micOn, o.micIn, o.micOut, o.micOffset, o.micVol, o.chatOn, o.chatIn, o.chatOut, o.chatOffset, o.chatVol, ClipRecorder.Av1Kbps);
                        else if (pure) outName = await ClipLibrary.SmartCutAsync(name, s.@in, s.@out);
                        else outName = await ClipLibrary.ComposeAsync(name, s.@in, s.@out, o.vidVol, o.micOn, o.micIn, o.micOut, o.micOffset, o.micVol, o.chatOn, o.chatIn, o.chatOut, o.chatOffset, o.chatVol);
                        done++;
                        PostToWeb("window.onCapture && window.onCapture(\"segprogress\"," + JsonConvert.SerializeObject(new { done, total = segs.Length }) + ")");
                        if (outName != null) { Saved(outName); any = true; }
                    }
                    if (!any) Fail();
                }
                catch (System.Exception ex) { DiagLog.Line("CLIP", "exportsegments err: " + ex.Message); Fail(); }
            });
        }
        private sealed class SegRange { public double @in { get; set; } public double @out { get; set; } }
        private sealed class SegExportDto
        {
            public SegRange[] segments { get; set; } = System.Array.Empty<SegRange>();
            public bool combine { get; set; }
            public double vidVol { get; set; } = 1;
            public bool micOn { get; set; }
            public double micIn { get; set; }
            public double micOut { get; set; }
            public double micOffset { get; set; }
            public double micVol { get; set; } = 1;
            public bool chatOn { get; set; }
            public double chatIn { get; set; }
            public double chatOut { get; set; }
            public double chatOffset { get; set; }
            public double chatVol { get; set; } = 1;
        }
        private sealed class ComposeDto
        {
            public double vidIn { get; set; }
            public double vidOut { get; set; }
            public double vidVol { get; set; } = 1;
            public bool micOn { get; set; }
            public double micIn { get; set; }
            public double micOut { get; set; }
            public double micOffset { get; set; }
            public double micVol { get; set; } = 1;
            public bool chatOn { get; set; }
            public double chatIn { get; set; }
            public double chatOut { get; set; }
            public double chatOffset { get; set; }
            public double chatVol { get; set; } = 1;
            public int gifFps { get; set; }    // GIF export only
            public int gifWidth { get; set; }
        }
    }
}
