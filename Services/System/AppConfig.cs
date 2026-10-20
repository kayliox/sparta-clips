// Persistent settings for Sparta Clips: a JSON mirror at %APPDATA%\SpartaClips\config.json (the web layer's `state`
// is saved here so the tray core can read capture prefs + hotkeys BEFORE any UI window exists). Also owns the
// autostart Run-key. The web layer both saves the whole mirror (SaveRaw) and pushes live settings via the bridge;
// this class is the startup reader that applies the saved prefs to the ClipRecorder engine when we launch to tray.
using System;
using System.IO;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace SpartaClips
{
    internal static class AppConfig
    {
        public static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpartaClips");
        public static string Path_ => System.IO.Path.Combine(Dir, "config.json");

        // Move an unreadable config.json aside (config.corrupt.*.json) so support can recover it before defaults overwrite it.
        public static void QuarantineCorrupt()
        {
            try { if (File.Exists(Path_)) File.Move(Path_, System.IO.Path.Combine(Dir, "config.corrupt." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json")); } catch { }
        }

        // ── Raw mirror (what the web `state` saves/loads) ──
        public static string LoadRaw()
        {
            try { return File.Exists(Path_) ? File.ReadAllText(Path_) : ""; }
            catch { return ""; }
        }
        public static void SaveRaw(string json)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, json);
                // File.Replace needs exclusive access; a background reader (ProfileFor at a hotkey record start)
                // briefly holds the file with FileShare.Read — before: one IOException silently DISCARDED the
                // user's settings save (the .tmp was orphaned, nothing logged). Retry through the reader's window.
                for (int attempt = 0; ; attempt++)
                {
                    try { if (File.Exists(Path_)) File.Replace(tmp, Path_, null); else File.Move(tmp, Path_); break; }
                    catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(40); }
                }
                lock (_cacheLock) { _cached = null; }   // next Root() re-reads the fresh file
            }
            catch (Exception ex) { DiagLog.Line("CFG", "save failed: " + ex.Message); }
        }

        // Parsed-config cache: Hotkeys/ArmPolicy/OverlayEnabled/ProfileFor are queried on every recording-state event
        // and at every record start — each used to re-read + re-parse the whole config.json. Invalidated by SaveRaw
        // and by mtime (external edits). Root() hands out a DEEP clone so callers can't mutate the cached tree.
        private static readonly object _cacheLock = new();
        private static JObject? _cached;
        private static long _cachedTick;
        private static JObject Root()
        {
            try
            {
                long tick = 0; try { tick = File.Exists(Path_) ? File.GetLastWriteTimeUtc(Path_).Ticks : 0; } catch { }
                lock (_cacheLock)
                {
                    if (_cached != null && _cachedTick == tick) return (JObject)_cached.DeepClone();
                }
                var r = LoadRaw();
                var o = string.IsNullOrWhiteSpace(r) ? new JObject() : JObject.Parse(r);
                lock (_cacheLock) { _cached = o; _cachedTick = tick; }
                return (JObject)o.DeepClone();
            }
            catch { return new JObject(); }
        }
        private static JObject Settings() { var s = Root()["settings"] as JObject; return s ?? new JObject(); }

        // ── Startup: apply the saved capture prefs to the engine (so hotkey recording works before the UI opens) ──
        // Keys mirror the web `state.settings.*` (capture* / replay*) exactly — the frontend is a faithful lift of the
        // Sparta Dock clip UI, and its bridge JSON shapes are unchanged, so these are the same names it writes.
        public static void ApplyEngineSettings()
        {
            try
            {
                var s = Settings();
                int fps       = (int?)s["captureFps"] ?? 60;
                int mbps      = (int?)s["captureBitrate"] ?? 20;
                int resH      = (int?)s["captureResH"] ?? 1080;   // 0 = source size
                bool mic      = (bool?)s["captureMic"] ?? false;
                bool sepMic   = (bool?)s["captureSepMic"] ?? false;
                bool chat     = (bool?)s["captureChat"] ?? false;
                bool gameCap  = ((string?)s["captureMode"] ?? "display") == "game";
                double capGB  = (double?)s["captureCapGB"] ?? 0;
                int replayLen = (int?)s["replayLen"] ?? 30;
                string encoder= NormEncoder((string?)s["captureEncoder"]);
                bool chime    = (bool?)s["replayChime"] ?? true;
                long capBytes = capGB > 0 ? (long)(capGB * 1073741824L) : 0;
                ClipRecorder.SetSettings(fps, mbps, resH, mic, sepMic, chat, gameCap, capBytes, replayLen, encoder, chime);

                string folder = (string?)s["clipFolder"] ?? "";
                if (!string.IsNullOrWhiteSpace(folder)) { try { ClipRecorder.SetClipsDir(folder); } catch { } }
                string micDev = (string?)s["captureMicDevice"] ?? "";
                if (!string.IsNullOrWhiteSpace(micDev)) { try { ClipRecorder.SetMicDevice(micDev); } catch { } }
                string pin = (string?)s["capturePin"] ?? "";   // remembered window pin (process name) — UI writes it from the source picker
                if (!string.IsNullOrWhiteSpace(pin)) { try { ClipRecorder.SetPinnedApp(pin); } catch { } }
                string chatDev = (string?)s["captureChatDevice"] ?? "";
                if (!string.IsNullOrWhiteSpace(chatDev)) { try { ClipRecorder.SetChatDevice(chatDev); } catch { } }
            }
            catch { }
        }
        private static string NormEncoder(string? e) =>
            (e == "h265" || e == "av1_hw" || e == "av1_sw") ? e : (e == "av1" ? "av1_hw" : "h264");

        // ── Runtime prefs the tray core needs ──
        // Hotkeys are stored as {ctrl,alt,shift,win,vk}; convert to MOD flags (ALT 1 · CTRL 2 · SHIFT 4 · WIN 8) + vk.
        public static (uint recMods, uint recVk, uint repMods, uint repVk, uint markMods, uint markVk) Hotkeys()
        {
            var s = Settings();
            var (rm, rv) = HkOf(s["recHotkey"] as JObject, 0x78);   // default Ctrl+Alt+F9
            var (pm, pv) = HkOf(s["repHotkey"] as JObject, 0x79);   // default Ctrl+Alt+F10
            var (mm, mv) = HkOf(s["markHotkey"] as JObject, 0x77);  // default Ctrl+Alt+F8 (mark-a-moment)
            return (rm, rv, pm, pv, mm, mv);
        }
        private static (uint mods, uint vk) HkOf(JObject? hk, uint defVk)
        {
            if (hk == null) return (3, defVk);   // 3 = ALT|CTRL
            uint mods = ((bool?)hk["alt"] == true ? 1u : 0) | ((bool?)hk["ctrl"] == true ? 2u : 0)
                      | ((bool?)hk["shift"] == true ? 4u : 0) | ((bool?)hk["win"] == true ? 8u : 0);
            uint vk = (uint?)hk["vk"] ?? 0;
            return (mods, vk);
        }
        // "always" (arm the replay ring at startup) or "game" (arm only while a fullscreen game is foreground).
        public static string ArmPolicy() { var v = (string?)Settings()["armPolicy"]; return v == "game" ? "game" : "always"; }
        public static bool AutoHighlight() => (bool?)Settings()["autoHighlight"] ?? (bool?)Settings()["autoClip"] ?? false;
        public static bool ReplayEnabled() => (bool?)Settings()["replayEnabled"] ?? true;
        public static bool ReleaseUiOnClose() => (bool?)Settings()["releaseUiOnClose"] ?? true;
        // Show the in-game "● REC" overlay while a full recording runs (default on).
        public static bool OverlayEnabled() => (bool?)Settings()["overlayEnabled"] ?? true;

        // ── Per-game capture profiles: override fps/bitrate/resolution for games whose title/exe CONTAINS "match"
        //    (case-insensitive). Applied at full-recording start only; the replay ring stays on the global settings. ──
        public sealed class GameProfile { public string match = ""; public int fps; public int mbps; public int resH; }
        public static GameProfile? ProfileFor(string? game)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(game)) return null;
                if (Settings()["gameProfiles"] is not JArray arr) return null;
                string g = game.ToLowerInvariant();
                foreach (var it in arr)
                {
                    string m = ((string?)it["match"] ?? "").Trim().ToLowerInvariant();
                    if (m.Length == 0) continue;
                    if (g.Contains(m)) return new GameProfile { match = m, fps = (int?)it["fps"] ?? 0, mbps = (int?)it["mbps"] ?? 0, resH = (int?)it["resH"] ?? 0 };
                }
            }
            catch { }
            return null;
        }
        public static bool StartHidden() => (bool?)Settings()["autostart"] ?? false;

        // ── Autostart (HKCU Run key, "--tray") ──
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "SpartaClips";
        public static bool IsAutostart()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) != null; }
            catch { return false; }
        }
        public static void SetAutostart(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (k == null) return;
                if (on) k.SetValue(RunName, "\"" + (Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath) + "\" --tray");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
            }
            catch { }
        }
    }
}
