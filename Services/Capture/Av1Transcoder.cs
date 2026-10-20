// AV1 clip encoding via a managed FFmpeg (downloaded on first use, see ManagedTools "ffmpeg"). ScreenRecorderLib
// can't emit AV1 and Windows Media Foundation has no AV1 encoder / can't mux av01 into MP4, so AV1 clips are made
// by transcoding the finished recording: hardware av1_nvenc/av1_qsv/av1_amf when present, else software libsvtav1.
// Audio is copied where possible. Also does AV1-source trim/mic-mix (the editor's "Create clip"), since
// MediaComposition can't decode AV1 without the Store extension. Self-contained + deletable with the capture feature.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal static class Av1Transcoder
    {
        private static string _list = "";       // cached `ffmpeg -encoders` output (probed once)
        private static bool _hardware;           // whether the last PickEncoder chose a hardware encoder

        private static ManagedTool? Tool => ManagedTools.Get("ffmpeg");
        public static bool FfmpegReady => Tool != null && ManagedTools.ExePath(Tool) != null;
        public static string? FfmpegPath => Tool == null ? null : ManagedTools.ExePath(Tool);

        /// <summary>Ensure ffmpeg is downloaded (managed-tool flow). progress mirrors ManagedTools states. True when ready.</summary>
        public static async Task<bool> EnsureAsync(Action<string, int, string>? progress = null)
        {
            if (FfmpegReady) return true;
            var t = Tool; if (t == null) return false;
            await ManagedTools.DownloadAndInstallAsync(t, (s, p, m) => progress?.Invoke(s, p, m));
            return FfmpegReady;
        }

        private static string EncoderList()
        {
            if (_list.Length > 0) return _list;
            string? ff = FfmpegPath; if (ff == null) return "";
            try
            {
                var psi = new ProcessStartInfo(ff, "-hide_banner -encoders")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi); if (p == null) return "";
                _list = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
            }
            catch { return ""; }
            return _list;
        }
        // Choose the AV1 encoder. forceSw → always software (libSVT-AV1, the "AV1 — software" dropdown choice). Otherwise
        // prefer a vendor hardware encoder, but only when an AV1 encoder MFT exists (else a listed-but-unusable av1_amf,
        // e.g. on an RX 5700 XT, would fail at runtime — go straight to software).
        private static string PickEncoder(bool forceSw)
        {
            string list = EncoderList(); if (list.Length == 0) return "";
            if (!forceSw && HardwareAv1.Available)
                foreach (var e in new[] { "av1_nvenc", "av1_qsv", "av1_amf" })
                    if (list.Contains(e) && ProbeWorks(e)) { _hardware = true; return e; }
            if (list.Contains("libsvtav1")) { _hardware = false; return "libsvtav1"; }
            if (list.Contains("libaom-av1")) { _hardware = false; return "libaom-av1"; }
            return "";
        }
        // The GPL ffmpeg build LISTS every vendor encoder regardless of the GPU, so "listed" != "usable": on an AMD box
        // av1_nvenc was picked first, failed on every clip (no nvcuda.dll) and fell back to fast software SVT-AV1 —
        // the "so compressed" clips. Prove each candidate with a tiny real encode once and cache the verdict.
        // Runs first on TrayCore's background warm-up, so later callers hit the cache.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _probe = new();
        private static bool ProbeWorks(string enc) => _probe.GetOrAdd(enc, e =>
        {
            string? ff = FfmpegPath; if (ff == null) return false;
            try
            {
                var psi = new ProcessStartInfo(ff, $"-hide_banner -loglevel error -f lavfi -i testsrc2=s=640x360:r=30:d=0.3 -c:v {e} -b:v 2M -f null -")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return false;
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
                bool ok = p.ExitCode == 0;
                DiagLog.Line("CLIP", "av1 probe " + e + ": " + (ok ? "ok" : "unusable (" + Short(err) + ")"));
                return ok;
            }
            catch { return false; }
        });
        /// <summary>Listed AND proven by a tiny real encode (cached) — for picking any vendor encoder, e.g. h264_amf.</summary>
        internal static bool EncoderWorks(string enc) => HasEncoder(enc) && ProbeWorks(enc);
        public static bool HasHardwareAv1() { var e = PickEncoder(false); return e.Length > 0 && _hardware; }
        // True if ffmpeg lists the named encoder (e.g. "h264_amf") — shared with ClipFfmpeg's HEVC-preview picker.
        internal static bool HasEncoder(string e) { var l = EncoderList(); return l.Length > 0 && l.Contains(e); }

        // fast = the capture-transcode path (instant replay / recording): bias the encoder for SPEED so the clip lands
        // in the gallery sooner. For SVT-AV1 we also pass fast-decode=1, which shapes the bitstream to be cheaper to
        // DECODE — so the AV1 clip is also quicker to thumbnail, open and scrub. Editor "Create clip" exports keep the
        // quality presets (fast=false). At a fixed bitrate the visual cost of these is minor for gameplay capture.
        private static string EncArgs(string enc, int kbps, bool fast) => enc switch
        {
            "av1_nvenc" => $"-c:v av1_nvenc -preset {(fast ? "p4" : "p5")} -b:v {kbps}k",   // NVENC: p1 fastest … p7 slowest
            "av1_qsv"   => $"-c:v av1_qsv -preset {(fast ? "veryfast" : "medium")} -b:v {kbps}k",
            "av1_amf"   => $"-c:v av1_amf -quality {(fast ? "balanced" : "quality")} -b:v {kbps}k",   // AMF is cheap on RDNA3 — don't trade quality for speed
            "libsvtav1" => $"-c:v libsvtav1 -preset {(fast ? "10" : "8")}{(fast ? " -svtav1-params fast-decode=1" : "")} -b:v {kbps}k",
            _           => $"-c:v {enc} -b:v {kbps}k",
        };

        /// <summary>Transcode src's video to AV1 → dst (audio copied, faststart). forceSw = the "AV1 — software"
        /// choice (libSVT-AV1). Falls back hw→libsvtav1 if a hardware encoder fails at runtime. True on success.</summary>
        public static async Task<bool> EncodeAsync(string src, string dst, int kbps, bool forceSw, bool fast = false)
        {
            // temp → validate → move (the pipeline contract): before, the multi-minute encode wrote dst directly, so
            // killing the app mid-encode left a headerless orphan .mp4 at the final ClipsDir/EditedDir path forever.
            string tmp = TempEncPath();
            try { return await EncodeRawAsync(src, tmp, kbps, forceSw, fast) && Promote(tmp, dst); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        private static async Task<bool> EncodeRawAsync(string src, string dst, int kbps, bool forceSw, bool fast)
        {
            string? ff = FfmpegPath; if (ff == null || !File.Exists(src)) return false;
            string enc = PickEncoder(forceSw); if (enc.Length == 0) return false;
            string common = "-map 0:v:0 -map 0:a:0? -pix_fmt yuv420p -c:a copy -movflags +faststart";
            string Cmd(string e, bool f) => $"-y -hide_banner -loglevel error -i \"{src}\" {EncArgs(e, kbps, f)} {common} \"{dst}\"";
            if (await RunAsync(ff, Cmd(enc, fast))) return true;
            if (enc != "libsvtav1" && _list.Contains("libsvtav1")) { _hardware = false; DiagLog.Line("CLIP", "av1 hw failed → libsvtav1"); if (await RunAsync(ff, Cmd("libsvtav1", fast))) return true; enc = "libsvtav1"; }
            if (fast) { DiagLog.Line("CLIP", "av1 fast retry → standard preset"); return await RunAsync(ff, Cmd(enc, false)); }   // an unsupported fast-param must never silently drop AV1
            return false;
        }

        /// <summary>Editor "Create clip" for an AV1 source: trim [vidIn,vidOut] (+vidVol) and mix any enabled mic/chat
        /// sidecars (each trimmed to its own [in,out], shifted to timeline offset, +vol) → dst AV1 MP4. Shares the
        /// audio-graph builder with ClipFfmpeg so the AV1 and H.264 compose paths mix identically.</summary>
        public static async Task<bool> ComposeAsync(string src, string? micWav, string? chatWav, double vidIn, double vidOut, double vidVol,
            bool micOn, double micIn, double micOut, double micOffset, double micVol,
            bool chatOn, double chatIn, double chatOut, double chatOffset, double chatVol, string dst, int kbps, bool forceSw)
        {
            string tmp = TempEncPath();   // temp → validate → move (see EncodeAsync)
            try
            {
                return await ComposeRawAsync(src, micWav, chatWav, vidIn, vidOut, vidVol,
                    micOn, micIn, micOut, micOffset, micVol, chatOn, chatIn, chatOut, chatOffset, chatVol, tmp, kbps, forceSw) && Promote(tmp, dst);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        private static string TempEncPath()
        {
            string d = Path.Combine(Path.GetTempPath(), "SpartaClipsAv1");
            try { Directory.CreateDirectory(d); } catch { }
            return Path.Combine(d, "enc_" + Guid.NewGuid().ToString("N") + ".mp4");
        }
        private static bool Promote(string tmp, string dst)
        {
            try
            {
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) return false;
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                return true;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "av1 promote err: " + ex.Message); return false; }
        }
        private static async Task<bool> ComposeRawAsync(string src, string? micWav, string? chatWav, double vidIn, double vidOut, double vidVol,
            bool micOn, double micIn, double micOut, double micOffset, double micVol,
            bool chatOn, double chatIn, double chatOut, double chatOffset, double chatVol, string dst, int kbps, bool forceSw)
        {
            string? ff = FfmpegPath; if (ff == null || !File.Exists(src)) return false;
            string enc = PickEncoder(forceSw); if (enc.Length == 0) return false;
            double dur = vidOut - vidIn; if (dur < 0.2) return false;

            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var sb = new System.Text.StringBuilder();
            var mixLabels = new System.Collections.Generic.List<string> { "va" };
            sb.Append($"[0:a]volume={F(vidVol)}[va];");
            int inputIdx = 1; string extraInputs = "";
            inputIdx = ClipFfmpeg.AppendTrack(sb, mixLabels, ref extraInputs, micOn, micWav, vidIn, vidOut, micIn, micOut, micOffset, micVol, inputIdx, "ma");
            inputIdx = ClipFfmpeg.AppendTrack(sb, mixLabels, ref extraInputs, chatOn, chatWav, vidIn, vidOut, chatIn, chatOut, chatOffset, chatVol, inputIdx, "ca");

            string tail = $"-t {F(dur)} {EncArgs(enc, kbps, false)} -pix_fmt yuv420p -movflags +faststart \"{dst}\"";
            string args;
            if (mixLabels.Count > 1)
            {
                // Brickwall-limit the summed mix so game audio + voice past 0 dBFS don't hard-clip (see ClipFfmpeg note).
                sb.Append(string.Join("", mixLabels.ConvertAll(l => "[" + l + "]")) + $"amix=inputs={mixLabels.Count}:normalize=0,alimiter=limit=0.95[aout]");
                args = $"-y -hide_banner -loglevel error -ss {F(vidIn)} -i \"{src}\"{extraInputs} -filter_complex \"{sb}\" -map 0:v:0 -map \"[aout]\" -c:a aac -b:a 192k {tail}";
            }
            else
            {
                args = $"-y -hide_banner -loglevel error -ss {F(vidIn)} -i \"{src}\" -af volume={F(vidVol)} -map 0:v:0 -map 0:a:0? -c:a aac -b:a 192k {tail}";
            }

            if (await RunAsync(ff, args)) return true;
            if (enc != "libsvtav1" && _list.Contains("libsvtav1")) { _hardware = false; return await RunAsync(ff, args.Replace(EncArgs(enc, kbps, false), EncArgs("libsvtav1", kbps, false))); }
            return false;
        }

        private static async Task<bool> RunAsync(string ff, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return false;
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                // dst is the last quoted token in args
                int q2 = args.LastIndexOf('"'); int q1 = q2 > 0 ? args.LastIndexOf('"', q2 - 1) : -1;
                string dst = (q1 >= 0 && q2 > q1) ? args.Substring(q1 + 1, q2 - q1 - 1) : "";
                bool ok = p.ExitCode == 0 && dst.Length > 0 && File.Exists(dst) && new FileInfo(dst).Length > 0;
                if (!ok) { DiagLog.Line("CLIP", "ffmpeg exit " + p.ExitCode + ": " + Short(err)); try { if (dst.Length > 0 && File.Exists(dst)) File.Delete(dst); } catch { } }
                return ok;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ffmpeg run err: " + ex.Message); return false; }
        }
        private static string Short(string s) { s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); return s.Length > 220 ? s.Substring(0, 220) : s; }

        /// <summary>Cheap dependency-free codec sniff: AV1-in-MP4 carries the 'av01' sample-entry fourcc in moov,
        /// which faststart keeps near the front. Scans the head of the file.</summary>
        public static bool IsAv1(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                int len = (int)Math.Min(fs.Length, 4 * 1024 * 1024);
                var buf = new byte[len]; int read = fs.Read(buf, 0, len);
                byte a = (byte)'a', v = (byte)'v', z = (byte)'0', o = (byte)'1';
                for (int i = 0; i + 4 <= read; i++)
                    if (buf[i] == a && buf[i + 1] == v && buf[i + 2] == z && buf[i + 3] == o) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
