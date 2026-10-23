// Renders a clip for sharing: applies the editor trim + mixes the mic/chat sidecars (via ClipFfmpeg.ComposeAsync),
// compresses to a chosen size tier (Original/100/75/50/25 MB) at a chosen fps, and writes ONE MP4 named as the user
// chose. The web layer copies it to the clipboard so it can be pasted straight into Discord (Ctrl+V). Self-contained,
// deletable with the capture feature.
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal static class ClipShare
    {
        private static string ShareDir { get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsShare"); try { Directory.CreateDirectory(d); } catch { } return d; } }
        private static readonly System.Threading.SemaphoreSlim _renderGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Render the share file (mix sidecars + trim + size tier + fps), written as &lt;outName&gt;.mp4 in TEMP.
        /// tierMb 0 = Original, fps 0 = native, vidIn/vidOut &lt;=0 (or equal) = full clip. Returns the path or null.</summary>
        /// <summary>The editor's track mix for a share (mirrors the "Create clip" export options). null = default:
        /// every present sidecar at 100%. Before this, shares ALWAYS mixed mic + chat at 100% — muting the chat track
        /// (e.g. friends' Discord voice) in the editor did nothing to what got posted.</summary>
        public sealed class Mix
        {
            public double vidVol = 1;
            public bool micOn = true; public double micIn = 0, micOut = -1, micOffset = 0, micVol = 1;
            public bool chatOn = true; public double chatIn = 0, chatOut = -1, chatOffset = 0, chatVol = 1;
        }

        public static async Task<string?> RenderAsync(string clipName, string outName, int tierMb, int fps,
            double vidIn, double vidOut, double durSec, Action<string, int>? progress = null, Mix? mix = null,
            System.Threading.CancellationToken ct = default)
        {
            using var _uw = ClipFfmpeg.UserWaiting();   // the user is watching a progress bar: run ffmpeg at normal priority
            using var _cw = ClipFfmpeg.CancelWith(ct);   // a newer tier/fps pick (or closing the popup) kills this render's ffmpeg
            mix ??= new Mix();
            string comp = "";
            // Renders are serialized (fixed temp files) — a superseded one is CANCELLED, so the newest never waits behind it.
            // before: picking 100 MB then 500 MB queued the 500 MB render behind the whole 100 MB encode while the popup sat
            // at "10%" (the older render's progress is hidden once it's stale).
            try { await _renderGate.WaitAsync(ct); } catch (OperationCanceledException) { return null; }
            try
            {
                if (!await ClipFfmpeg.EnsureAsync()) return null;
                string? src = ClipLibrary.PathOf(clipName); if (src == null || !File.Exists(src)) return null;
                string? ff = Av1Transcoder.FfmpegPath; if (ff == null) return null;

                string mic = ClipRecorder.MicSidecar(src); if (!File.Exists(mic) || !mix.micOn || mix.micVol <= 0) mic = "";     // muted → left out
                string chat = ClipRecorder.ChatSidecar(src); if (!File.Exists(chat) || !mix.chatOn || mix.chatVol <= 0) chat = "";
                bool trimmed = vidOut > vidIn + 0.05;
                if (!trimmed && durSec <= 0) { try { durSec = ClipLibrary.Mp4DurationSeconds(src); } catch { } }   // probe real duration so a size-tier budget isn't computed for a phantom 0.2s clip
                double outDur = trimmed ? (vidOut - vidIn) : Math.Max(0.2, durSec);
                var (_, nativeFps, srcH, srcW) = await ClipFfmpeg.MediaInfoAsync(src);
                bool fpsChange = fps > 0 && fps < Math.Round(nativeFps) - 0.5;
                bool needMix = mic.Length > 0 || chat.Length > 0 || Math.Abs(mix.vidVol - 1) > 0.001;

                string dst = Path.Combine(ShareDir, SafeName(outName) + ".mp4"); Del(dst);

                // 1) Composition: mix the sidecars + apply the trim into a temp (reuses the editor's muxer). Skipped
                //    when there's nothing to mix and no trim — then the raw mp4 already IS the full composition.
                string source = src;
                if (needMix || trimmed)
                {
                    progress?.Invoke("render", 20);
                    comp = Path.Combine(ShareDir, "_comp.mp4"); Del(comp);
                    double vin = trimmed ? vidIn : 0, vout = trimmed ? vidOut : (durSec > 0 ? durSec : outDur);
                    // Sidecars are time-aligned with the video from 0, so their block is [0, vout] — before: tOut was
                    // (vout - vin), which ended the mic/chat `vin` seconds early on every trimmed share and dropped
                    // the track entirely when the trim started past the clip's midpoint (silent-voice shares).
                    // Track windows: the editor's own trims when it sent them (micOut >= 0), else the whole sidecar [0, vout].
                    double mOut = mix.micOut >= 0 ? mix.micOut : vout, cOut = mix.chatOut >= 0 ? mix.chatOut : vout;
                    if (!(await ClipFfmpeg.ComposeAsync(src, mic.Length > 0 ? mic : null, chat.Length > 0 ? chat : null,
                            vin, vout, mix.vidVol,
                            mic.Length > 0, mix.micIn, mOut, mix.micOffset, mix.micVol,
                            chat.Length > 0, mix.chatIn, cOut, mix.chatOffset, mix.chatVol, comp,
                            // Untrimmed → only the AUDIO needs mixing: copy the video untouched (original quality, fast).
                            // Trimmed → frame-accurate cut needs a re-encode: near-lossless intermediate; the tier step
                            // below compresses only if it doesn't fit. before: always x264 CRF 20 — a 125 MB clip
                            // "shared at 500 MB" came out as 8.5 MB.
                            trimmed ? "-c:v libx264 -preset veryfast -crf 14 -pix_fmt yuv420p" : "-c:v copy")
                        && File.Exists(comp)))
                    {
                        // FAIL the share — before: it silently fell through to the RAW clip, sharing the full
                        // untrimmed video with a size-tier bitrate budgeted for the trimmed duration.
                        DiagLog.Line("SHARE", "compose failed — share aborted (was about to share untrimmed)");
                        return null;
                    }
                    source = comp;
                }
                if (ct.IsCancellationRequested) return null;

                // 2) Compress-to-tier / fps. Original + native fps + already-fits → just copy the source to the named file.
                long size = new FileInfo(source).Length;
                bool fits = tierMb <= 0 || size <= (long)(tierMb * 1024L * 1024L * 0.97);
                if ((tierMb <= 0 || fits) && !fpsChange) { File.Copy(source, dst, true); return dst; }

                progress?.Invoke("render", 50);
                var filters = new System.Collections.Generic.List<string>();
                if (fpsChange) filters.Add($"fps={fps}");
                string venc; long kb = 0; string hw = ""; int scaleH = 0;
                if (tierMb > 0)
                {
                    double budget = tierMb * 8.0 * 1024 * 1024 * 0.95;                 // bits, with a 5% safety margin
                    double vbps = Math.Max(300_000, (budget - 160_000 * outDur) / outDur);   // leave room for 160 kbps AAC
                    kb = (long)(vbps / 1000);
                    // Small tiers: squeezing 3440x1440 into ~1 Mbps is mush. Drop to the resolution the budget can
                    // actually feed (~0.07 bits/pixel/frame = clean H.264) — 500 MB (Nitro) shares stay native.
                    int h = ShareHeight(vbps, srcW, srcH, fpsChange ? fps : (nativeFps > 1 ? nativeFps : 30));
                    scaleH = h;
                    if (h > 0) { filters.Add($"scale=-2:{h}:flags=lanczos"); DiagLog.Line("SHARE", $"tier {tierMb} MB: {srcW}x{srcH} -> {h}p @ {kb} kbps"); }
                    // GPU only for the heavy high-res encodes (where x264 would take minutes). Small tiers stay on x264:
                    // it's quick at <=900p AND hits the size target — AMF's VBR landed a 10 MB share at 5.1 MB (measured).
                    if ((h > 0 ? h : srcH) >= 1080) hw = HardwareH264();
                    venc = EncArgs(hw, kb);
                }
                else venc = "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";   // Original tier, but fps forced a re-encode
                string vf = filters.Count > 0 ? "-vf \"" + string.Join(",", filters) + "\" " : "";

                // Decode on the GPU (D3D11VA, frames back to system memory; ffmpeg falls back to software if the GPU can't). NOT
                // -hwaccel auto: it didn't pick the fast decoder here (40 s vs 12 s for the same clip). Saved clips are
                // AV1 — a software decode of a 3456x1456 clip took 48 s of a 60 s share on a high-end PC (measured).
                string Args(string v) => $"-nostdin -y -hide_banner -loglevel error -nostats -progress pipe:1 -hwaccel d3d11va -i \"{source}\" {vf}{v} -c:a aac -b:a 160k -movflags +faststart \"{dst}\"";
                Action<double>? onFrac = progress == null ? null : f => progress("render", 50 + (int)(f * 49));
                bool ok = false;
                // AMD: the whole chain on the GPU — D3D11 decode → vpp_amf scale → AMF encode, frames never touch the CPU.
                // 100 MB tier of a 60 s 3456x1456 AV1 replay: 44.6 s → 9.8 s (measured 2026-10-23). Any failure falls back below.
                if (hw == "h264_amf")
                {
                    var gf = new System.Collections.Generic.List<string>();
                    if (fpsChange) gf.Add($"fps={fps}");
                    if (scaleH > 0) { int w = (int)Math.Round((double)srcW * scaleH / Math.Max(1, srcH) / 2) * 2; gf.Add($"vpp_amf=w={w}:h={scaleH}:scale_type=bicubic"); }
                    string gvf = gf.Count > 0 ? "-vf \"" + string.Join(",", gf) + "\" " : "";
                    string gpu = $"-nostdin -y -hide_banner -loglevel error -nostats -progress pipe:1 -hwaccel d3d11va -hwaccel_output_format d3d11 -i \"{source}\" {gvf}{EncArgs(hw, kb).Replace(" -pix_fmt yuv420p", "")} -c:a aac -b:a 160k -movflags +faststart \"{dst}\"";
                    ok = await RunFfmpeg(ff, gpu, onFrac, outDur, ct);
                    if (!ok && !ct.IsCancellationRequested) DiagLog.Line("SHARE", "GPU pipeline failed -> CPU scale");
                }
                if (!ok && !ct.IsCancellationRequested) ok = await RunFfmpeg(ff, Args(venc), onFrac, outDur, ct);
                if (!ok && hw.Length > 0 && !ct.IsCancellationRequested) { DiagLog.Line("SHARE", hw + " failed -> libx264"); hw = ""; ok = await RunFfmpeg(ff, Args(EncArgs("", kb)), onFrac, outDur, ct); }
                if (ct.IsCancellationRequested) return null;
                // Hardware rate control can overshoot on short clips — a file over the tier is one Discord rejects, so
                // redo it with libx264 (tight ABR) a notch lower rather than hand over an unusable share.
                long limit = tierMb > 0 ? tierMb * 1024L * 1024L : long.MaxValue;
                if (ok && File.Exists(dst) && new FileInfo(dst).Length > limit)
                {
                    DiagLog.Line("SHARE", $"{(hw.Length > 0 ? hw : "x264")} overshot {tierMb} MB ({new FileInfo(dst).Length / 1048576.0:F1} MB) -> libx264 retry");
                    ok = await RunFfmpeg(ff, Args(EncArgs("", (long)(kb * 0.88))), onFrac, outDur, ct);
                }
                return (ok && File.Exists(dst) && new FileInfo(dst).Length <= limit) ? dst : null;
            }
            catch (Exception ex) { DiagLog.Line("SHARE", "render err: " + ex.Message); return null; }
            finally { if (comp.Length > 0) Del(comp); _renderGate.Release(); }
        }

        // Tallest standard height (never above the source) the bitrate can feed at ~0.07 bits/pixel/frame; 0 = keep native.
        private static int ShareHeight(double vbps, int w, int h, double fps)
        {
            if (w <= 0 || h <= 0 || fps <= 0) return 0;
            const double BPP = 0.07;
            if (vbps / ((double)w * h * fps) >= BPP) return 0;
            double fitH = Math.Sqrt(vbps / (BPP * fps * ((double)w / h)));
            foreach (int s in new[] { 1440, 1080, 900, 720, 540, 480 }) if (s < h && s <= fitH) return s;
            return Math.Min(h, 360);
        }
        // GPU H.264 for size-tier shares (much faster than x264 on a long clip); "" = software.
        private static string? _hw264;
        private static string HardwareH264()
        {
            if (_hw264 != null) return _hw264;
            foreach (var e in new[] { "h264_amf", "h264_nvenc", "h264_qsv" }) if (Av1Transcoder.EncoderWorks(e)) return _hw264 = e;
            return _hw264 = "";
        }
        private static string EncArgs(string enc, long kb)
        {
            string rate = $"-b:v {kb}k -maxrate {(long)(kb * 1.25)}k -bufsize {kb * 2}k";
            return enc switch
            {
                "h264_amf"   => $"-c:v h264_amf -quality quality -rc vbr_peak {rate} -pix_fmt yuv420p",
                "h264_nvenc" => $"-c:v h264_nvenc -preset p5 -rc vbr {rate} -pix_fmt yuv420p",
                "h264_qsv"   => $"-c:v h264_qsv -preset medium {rate} -pix_fmt yuv420p",
                _            => $"-c:v libx264 -preset faster {rate} -pix_fmt yuv420p",
            };
        }

        private static async Task<bool> RunFfmpeg(string ff, string args, Action<double>? onFrac = null, double durSec = 0,
            System.Threading.CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using var p = System.Diagnostics.Process.Start(psi); if (p == null) return false;
                using var killOnCancel = ct.CanBeCanceled ? ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }) : default;
                var errTask = p.StandardError.ReadToEndAsync();
                if (onFrac == null) await p.StandardOutput.ReadToEndAsync();   // -progress output nobody wants — drain so it can't block
                else
                {
                    int last = -1; string? line;
                    while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                    {
                        if (durSec <= 0 || !line.StartsWith("out_time_us=")) continue;
                        if (!long.TryParse(line.Substring(12), out long us) || us < 0) continue;
                        double f = Math.Clamp(us / 1e6 / durSec, 0, 1); int pct = (int)(f * 100);
                        if (pct != last) { last = pct; onFrac(f); }   // whole-percent steps only — no bridge spam
                    }
                }
                string err = await errTask;
                await p.WaitForExitAsync();
                if (ct.IsCancellationRequested) return false;
                if (p.ExitCode != 0) DiagLog.Line("SHARE", "ffmpeg " + p.ExitCode + ": " + (err.Length > 180 ? err.Substring(0, 180) : err));
                return p.ExitCode == 0;
            }
            catch (Exception ex) { DiagLog.Line("SHARE", "ffmpeg run err: " + ex.Message); return false; }
        }

        private static void Del(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        // Sanitise the user-chosen name → a file-system-safe stem (keeps dots like "18.06.2026"; strips a trailing .mp4).
        private static string SafeName(string s)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "clip" : s;
        }
    }
}
