// Unified ffmpeg post-processing for clips — the "solid" path (see ShareX: one mature tool owns encode+mux).
// Replaces the Windows.Media.Editing (MediaComposition) renders that re-encoded every save at a low bitrate and
// left 0-byte files when MF's HEVC encoder threw. Stream-COPIES the already-encoded capture wherever possible
// (exact quality, instant, can't fail on encode); re-encodes only when mixing audio / scaling. Every op writes
// to a TEMP file, validates it (size + real duration), then atomically moves it into the clips dir, so the
// gallery never sees a partial/empty file. Shares the downloaded ffmpeg with Av1Transcoder. Deletable with capture.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal static class ClipFfmpeg
    {
        public static bool Ready => Av1Transcoder.FfmpegReady;
        public static Task<bool> EnsureAsync(Action<string, int, string>? progress = null) => Av1Transcoder.EnsureAsync(progress);
        private static string? Ff => Av1Transcoder.FfmpegPath;

        private static string TempDir
        {
            get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsClip"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }
        // Cached gallery poster images (one JPEG per clip). Mapped to clipthumb.local in MainForm. Replaces the old
        // per-card <video> thumbnails, which spun up N media decoders and made the gallery lag / clips slow to open.
        internal static string ThumbDir
        {
            get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsThumb"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }
        private static readonly System.Threading.SemaphoreSlim _thumbGate = new System.Threading.SemaphoreSlim(2, 2);   // throttle first-visit thumb bursts
        private static readonly System.Threading.SemaphoreSlim _infoGate = new System.Threading.SemaphoreSlim(3, 3);    // throttle first-visit ffprobe bursts (a 300-clip gallery else spawns 300 ffprobes at once)

        // Ensure a cached poster JPEG for a clip (one frame ~1.5s in, scaled to 240p). Returns the file name in
        // ThumbDir (served via clipthumb.local), or null on failure. Cheap single-frame extract; HW-decoded where possible.
        public static async Task<string?> EnsureThumbAsync(string srcPath)
        {
            if (!File.Exists(srcPath)) return null;
            string name = Path.GetFileNameWithoutExtension(srcPath) + ".t2.jpg";   // .t2 = versioned key so improved thumbs regenerate
            string thumb = Path.Combine(ThumbDir, name);
            if (ThumbFresh(thumb, srcPath)) return name;
            if (!await EnsureAsync()) return null;
            string? ff = Ff; if (ff == null) return null;
            await _thumbGate.WaitAsync();
            try
            {
                if (ThumbFresh(thumb, srcPath)) return name;   // another request may have built it while we waited
                double dur = ClipLibrary.Mp4DurationSeconds(srcPath);
                // Seek into the meat of the clip (skip intro/loading), then let the thumbnail filter pick the most
                // representative frame nearby (avoids black/transition frames). Higher res + lanczos = crisper cards.
                string ss = dur > 4 ? (dur * 0.4).ToString("0.###", CultureInfo.InvariantCulture) : (dur > 1.5 ? "1" : "0");
                string tmp = Path.Combine(ThumbDir, "_t_" + DateTime.Now.Ticks + ".jpg");
                string args(string hw) => $"-y -hide_banner -loglevel error {hw} -ss {ss} -i \"{srcPath}\" -frames:v 1 -vf \"thumbnail=40,scale=-2:480:flags=lanczos\" -q:v 3 \"{tmp}\"";
                bool ok = await RunAsync(ff, args("-hwaccel auto"), tmp);
                if (!ok) { TryDel(tmp); ok = await RunAsync(ff, args(""), tmp); }
                if (ok) { try { if (File.Exists(thumb)) File.Delete(thumb); File.Move(tmp, thumb); return name; } catch { } }
                TryDel(tmp);
                return null;
            }
            finally { _thumbGate.Release(); }
        }
        private static bool ThumbFresh(string thumb, string src)
        {
            try { return File.Exists(thumb) && File.GetLastWriteTimeUtc(thumb) >= File.GetLastWriteTimeUtc(src) && new FileInfo(thumb).Length > 0; } catch { return false; }
        }
        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ── Instant replay: concat the buffer's prev-tail + current and keep the last n seconds → destPath, all by
        // STREAM COPY (no re-encode → exact capture quality, instant, can never produce a broken/garbage clip).
        // The cut is keyframe-aligned (the start lands on the keyframe at/just-before the mark, like OBS replays). ──
        // prevSpan = REAL time from prev's start to cur's start (0 = unknown). Stopping one recorder and starting the next
        // takes ~0.4 s, so segments have a gap; butting them together dropped it from the video while the continuous game
        // audio kept it → everything before a join (every save AND every 2-min rotation) played ~0.4 s out of sync
        // (measured −418 ms). The gap is kept: the last frame holds for it, the sound stays in sync.
        public static async Task<bool> ConcatLastSecondsAsync(string prev, string cur, int n, string destPath, double prevSpan = 0)
        {
            string? ff = Ff; if (ff == null || !File.Exists(cur)) return false;
            double curDur = ClipLibrary.Mp4DurationSeconds(cur);
            string tmp = Path.Combine(TempDir, "rep_" + DateTime.Now.Ticks + ".mp4");
            string list = "", joined = "";
            try
            {
                bool ok;
                // The capture target can change between segments (follow-focus moved to another window) → different
                // frame sizes, which a stream-copy concat would turn into a corrupt file. Fit the earlier footage into the
                // current frame (scaled, letterboxed — never stretched) and join both in ONE GPU encode, so the replay
                // still holds the full n seconds. before: the earlier footage was dropped — a save right after a window
                // switch produced a 0.8 s "replay".
                if (!(curDur >= n || string.IsNullOrEmpty(prev) || !File.Exists(prev)))
                {
                    var a = await MediaInfoAsync(prev); var b = await MediaInfoAsync(cur);
                    if ((a.width != b.width || a.height != b.height) && b.width > 0 && b.height > 0)
                    {
                        DiagLog.Line("CLIP", $"replay: segment size changed ({a.width}x{a.height} -> {b.width}x{b.height}) — fitting the earlier footage and joining");
                        bool joinedOk = await JoinResizedAsync(ff, prev, cur, n - curDur, curDur, b.width, b.height, tmp, prevSpan);
                        if (joinedOk) return Finalize(tmp, destPath);
                        DiagLog.Line("CLIP", "replay: fit-and-join failed — using the current segment only");
                        prev = "";
                    }
                }
                if (curDur >= n || string.IsNullOrEmpty(prev) || !File.Exists(prev))
                {
                    double start = Math.Max(0, curDur - n);
                    ok = await CutCopyAtKeyframeAsync(ff, cur, start, tmp);
                }
                else
                {
                    // Concat prev+cur (identical encoder params → copy-safe via the concat demuxer), then keep last n.
                    list = Path.Combine(TempDir, "list_" + DateTime.Now.Ticks + ".txt");
                    joined = Path.Combine(TempDir, "join_" + DateTime.Now.Ticks + ".mp4");
                    // Concat-demuxer quoting: a ' inside a single-quoted entry must become '\'' (think C:\Users\O'Brien\…\Temp)
                    static string Q(string p2) => p2.Replace("\\", "/").Replace("'", "'\\''");
                    // outpoint = where BOTH of prev's streams still exist: a segment whose window closed keeps recording
                    // audio with no video (seconds of it) — joined as-is, that left a gap/jump in the audio timeline.
                    var (pv, pa) = await StreamDurationsAsync(prev);
                    double outp = pv > 0 && pa > 0 ? Math.Min(pv, pa) : 0;
                    if (prevSpan > 0 && outp > 0 && prevSpan < outp) outp = prevSpan;
                    double spacing = prevSpan > 0 ? Math.Max(prevSpan, outp) : 0;   // cur starts where it really started
                    static string N(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
                    File.WriteAllText(list, "file '" + Q(prev) + "'\n" + (outp > 0 ? "outpoint " + N(outp) + "\n" : "") +
                        (spacing > 0 ? "duration " + N(spacing) + "\n" : "") + "file '" + Q(cur) + "'\n");
                    ok = await RunAsync(ff, $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{list}\" -c copy -movflags +faststart \"{joined}\"", joined);
                    if (ok)
                    {
                        double total = ClipLibrary.Mp4DurationSeconds(joined);
                        double start = Math.Max(0, total - n);
                        ok = await CutCopyAtKeyframeAsync(ff, joined, start, tmp);
                    }
                }
                return ok && Finalize(tmp, destPath);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ff concat err: " + ex.Message); TryDel(tmp); return false; }
            finally { TryDel(list); TryDel(joined); }
        }

        // Stream-copy cut that starts EXACTLY on a video keyframe (the one at/just before `start`). A copy cut can only begin
        // video at a keyframe (~2.6 s apart in screen capture) while audio starts at the exact time — ffmpeg papers over the
        // difference with an edit list that our later steps (game-audio swap, AV1 transcode) don't honour, so sound ended up
        // a random 0–2.6 s off per save (measured: −464 ms). Cutting on the keyframe makes both streams start together.
        private static async Task<bool> CutCopyAtKeyframeAsync(string ff, string src, double start, string dst)
        {
            double k = 0;
            foreach (var t in await KeyframesAsync(src)) { if (t <= start + 0.0005) k = t; else break; }
            string ss = (k + 0.0005).ToString("0.000000", CultureInfo.InvariantCulture);   // a hair past the keyframe → the seek lands ON it
            return await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {ss} -i \"{src}\" -c copy -avoid_negative_ts make_zero -movflags +faststart \"{dst}\"", dst);
        }

        // Earlier segment (different size) → scaled + letterboxed into the current frame, its last `need` seconds joined in
        // front of the current segment; re-encoded once on the GPU at about the capture's own bitrate. Audio: each segment's
        // own track (silence filled in if one has none).
        private static async Task<bool> JoinResizedAsync(string ff, string prev, string cur, double need, double curDur, int w, int h, string dst, double prevSpan = 0)
        {
            double prevDur = ClipLibrary.Mp4DurationSeconds(prev); if (prevDur <= 0 || need <= 0.05) return false;
            double span = prevSpan > prevDur ? prevSpan : prevDur;              // prev's timeline up to cur's REAL start (gap included)
            double start = Math.Max(0, span - need); if (start > prevDur - 0.05) start = Math.Max(0, prevDur - 0.05);
            double tail = span - start;                                          // last frame holds through the gap
            bool a0 = await HasAudioAsync(prev), a1 = await HasAudioAsync(cur);
            w -= w % 2; h -= h % 2;
            const string AF = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
            string fc =
                // The earlier part's video and audio are forced to EXACTLY `tail` seconds before the concat — VFR capture makes
                // the last frame's duration uncertain, and any length difference shifted everything after the join point
                // (measured: sound up to ~120 ms off, varying per save). tpad clones the last frame; apad+atrim pads/cuts audio.
                $"[0:v]scale={w}:{h}:force_original_aspect_ratio=decrease:flags=lanczos,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,format=yuv420p,tpad=stop_mode=clone:stop_duration={F(span - prevDur + 2)},trim=duration={F(tail)},setpts=PTS-STARTPTS[v0];" +
                $"[1:v]scale={w}:{h}:flags=lanczos,setsar=1,format=yuv420p[v1];" +
                (a0 ? $"[0:a]{AF},apad,atrim=duration={F(tail)},asetpts=PTS-STARTPTS[a0];" : $"anullsrc=r=48000:cl=stereo,atrim=duration={F(tail)},{AF}[a0];") +
                (a1 ? $"[1:a]{AF},asetpts=PTS-STARTPTS[a1];" : $"anullsrc=r=48000:cl=stereo,atrim=duration={F(curDur)},{AF}[a1];") +
                "[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]";
            long kb = 20000;
            try { kb = Math.Clamp((long)(new FileInfo(cur).Length * 8 / Math.Max(1, curDur) / 1000 * 1.1), 4000, 150000); } catch { }
            string enc = HwH264();
            string rate = $"-b:v {kb}k -maxrate {kb * 3 / 2}k -bufsize {kb * 2}k";
            string venc = enc switch
            {
                "h264_amf"   => $"-c:v h264_amf -quality quality -rc vbr_peak {rate}",
                "h264_nvenc" => $"-c:v h264_nvenc -preset p5 -rc vbr {rate}",
                "h264_qsv"   => $"-c:v h264_qsv -preset medium {rate}",
                _            => $"-c:v libx264 -preset veryfast -crf 18",
            };
            string args = $"-y -hide_banner -loglevel error -ss {F(start)} -i \"{prev}\" -i \"{cur}\" -filter_complex \"{fc}\" -map \"[v]\" -map \"[a]\" {venc} -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart \"{dst}\"";
            bool ok = await RunAsync(ff, args, dst);
            if (!ok && enc.Length > 0)   // a hardware encoder can refuse odd sizes → software fallback
                ok = await RunAsync(ff, args.Replace(venc, "-c:v libx264 -preset veryfast -crf 18"), dst);
            return ok;
        }
        private static string? _hw264;
        private static string HwH264()
        {
            if (_hw264 != null) return _hw264;
            foreach (var e in new[] { "h264_amf", "h264_nvenc", "h264_qsv" }) if (Av1Transcoder.EncoderWorks(e)) return _hw264 = e;
            return _hw264 = "";
        }
        private static async Task<(double video, double audio)> StreamDurationsAsync(string path)
        {
            try
            {
                string? ff = Ff; if (ff == null) return (0, 0);
                string probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
                var psi = new ProcessStartInfo(probe, $"-v error -show_entries stream=codec_type,duration -of csv=p=0 \"{path}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return (0, 0);
                var o = p.StandardOutput.ReadToEndAsync(); await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
                double v = 0, a = 0;
                foreach (var line in (await o).Split('\n'))
                {
                    var f = line.Trim().Split(','); if (f.Length < 2) continue;
                    if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) continue;
                    if (f[0] == "video") v = d; else if (f[0] == "audio") a = d;
                }
                return (v, a);
            }
            catch { return (0, 0); }
        }
        private static async Task<bool> HasAudioAsync(string path)
        {
            try
            {
                string? ff = Ff; if (ff == null) return false;
                string probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
                var psi = new ProcessStartInfo(probe, $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return false;
                var o = p.StandardOutput.ReadToEndAsync(); await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
                return (await o).Trim().Length > 0;
            }
            catch { return false; }
        }

        // ── Editor trim [start,end] by STREAM COPY (lossless, fast; works for H.264/H.265/AV1 alike) → destPath. ──
        public static async Task<bool> TrimAsync(string src, double start, double end, string destPath)
        {
            string? ff = Ff; if (ff == null || !File.Exists(src)) return false;
            double dur = end - start; if (dur < 0.2) return false;
            string tmp = Path.Combine(TempDir, "trim_" + DateTime.Now.Ticks + ".mp4");
            try
            {
                bool ok = await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {F(start)} -i \"{src}\" -t {F(dur)} -c copy -movflags +faststart \"{tmp}\"", tmp);
                return ok && Finalize(tmp, destPath);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ff trim err: " + ex.Message); TryDel(tmp); return false; }
        }

        // ── Editor compose: trimmed video [vidIn,vidOut] + optional mic and/or chat sidecar tracks, each trimmed to
        // its own [in,out], shifted to timeline `offset`, at its own volume; mixed → H.264 (frame-accurate, always
        // previewable). Mirrors Av1Transcoder.ComposeAsync's audio graph; video is re-encoded here so the cut is
        // frame-accurate and the output always plays in-app even from an H.265 source. Volumes 0..2 (1 = unchanged). ──
        public static async Task<bool> ComposeAsync(string src, string? micWav, string? chatWav,
            double vidIn, double vidOut, double vidVol,
            bool micOn, double micIn, double micOut, double micOffset, double micVol,
            bool chatOn, double chatIn, double chatOut, double chatOffset, double chatVol,
            string destPath, string? videoArgs = null)
        {
            string? ff = Ff; if (ff == null || !File.Exists(src)) return false;
            double dur = vidOut - vidIn; if (dur < 0.2) return false;
            string tmp = Path.Combine(TempDir, "comp_" + DateTime.Now.Ticks + ".mp4");
            try
            {
                // Build the audio mix graph: video audio (vidVol) + each enabled sidecar overlapping the export window.
                var sb = new System.Text.StringBuilder();
                var mixLabels = new System.Collections.Generic.List<string> { "va" };
                sb.Append($"[0:a]volume={F(vidVol)}[va];");
                int inputIdx = 1;
                string extraInputs = "";
                inputIdx = AppendTrack(sb, mixLabels, ref extraInputs, micOn, micWav, vidIn, vidOut, micIn, micOut, micOffset, micVol, inputIdx, "ma");
                inputIdx = AppendTrack(sb, mixLabels, ref extraInputs, chatOn, chatWav, vidIn, vidOut, chatIn, chatOut, chatOffset, chatVol, inputIdx, "ca");

                string venc = videoArgs ?? "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";   // share passes copy / high-quality args
                string args;
                if (mixLabels.Count > 1)
                {
                    // normalize=0 sums the tracks at their true levels (in-app plays them as separate OS-mixed streams).
                    // A brickwall limiter catches the case where game audio + voice sum past 0 dBFS — otherwise the AAC
                    // buffer hard-clips (harsh highs, "compressed" sound). Transparent when the mix isn't hot.
                    sb.Append(string.Join("", mixLabels.ConvertAll(l => "[" + l + "]")) + $"amix=inputs={mixLabels.Count}:normalize=0,alimiter=limit=0.95[aout]");
                    args = $"-y -hide_banner -loglevel error -ss {F(vidIn)} -i \"{src}\"{extraInputs} -t {F(dur)} -filter_complex \"{sb}\" -map 0:v:0 -map \"[aout]\" {venc} -c:a aac -b:a 192k -movflags +faststart \"{tmp}\"";
                }
                else   // no sidecars overlap → just the (volume-adjusted) clip audio
                {
                    args = $"-y -hide_banner -loglevel error -ss {F(vidIn)} -i \"{src}\" -t {F(dur)} -af volume={F(vidVol)} -map 0:v:0 -map 0:a:0? {venc} -c:a aac -b:a 192k -movflags +faststart \"{tmp}\"";
                }
                bool ok = await RunAsync(ff, args, tmp);
                return ok && Finalize(tmp, destPath);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ff compose err: " + ex.Message); TryDel(tmp); return false; }
        }

        // Append one sidecar track to the filter graph if it's enabled, exists, and overlaps [vidIn,vidOut].
        // Returns the next free input index; pushes the track's input ("-i path") onto extraInputs + its mix label.
        // Shared with Av1Transcoder so the H.264 and AV1 compose paths build the same audio mix.
        internal static int AppendTrack(System.Text.StringBuilder sb, System.Collections.Generic.List<string> mixLabels,
            ref string extraInputs, bool on, string? wav, double vidIn, double vidOut,
            double tIn, double tOut, double tOffset, double tVol, int inputIdx, string label)
        {
            if (!on || string.IsNullOrEmpty(wav) || !File.Exists(wav)) return inputIdx;
            double blockEnd = tOffset + (tOut - tIn);
            double audStart = Math.Max(tOffset, vidIn), audEnd = Math.Min(blockEnd, vidOut);
            if (audEnd - audStart < 0.05) return inputIdx;
            double localStart = tIn + (audStart - tOffset), localEnd = tIn + (audEnd - tOffset);
            long delayMs = (long)Math.Round(Math.Max(0, audStart - vidIn) * 1000);
            extraInputs += $" -i \"{wav}\"";
            sb.Append($"[{inputIdx}:a]atrim=start={F(localStart)}:end={F(localEnd)},asetpts=N/SR/TB,adelay={delayMs}:all=1,volume={F(tVol)}[{label}];");
            mixLabels.Add(label);
            return inputIdx + 1;
        }

        // ── Keyframe timestamps (seconds) for keyframe-aligned editor navigation. The player uses --input-fast-seek,
        // so every seek lands on the keyframe at/just-before the requested time; the editor snaps the scrubber +
        // bookmarks to THESE times so the playhead lands exactly where the pin sits (no bounce). Reads PACKET flags
        // (no decode → fast even on long clips). Cached per (path,mtime). Empty array when ffmpeg/ffprobe isn't ready. ──
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double[]> _kfCache = new();
        public static async Task<double[]> KeyframesAsync(string srcPath)
        {
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return Array.Empty<double>();
            string key; try { key = srcPath + "|" + File.GetLastWriteTimeUtc(srcPath).Ticks; } catch { key = srcPath; }
            if (_kfCache.TryGetValue(key, out var cached)) return cached;
            if (!await EnsureAsync()) return Array.Empty<double>();
            string? ff = Ff; if (ff == null) return Array.Empty<double>();
            string probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
            if (!File.Exists(probe)) return Array.Empty<double>();
            try
            {
                string args = $"-hide_banner -loglevel error -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 \"{srcPath}\"";
                var psi = new ProcessStartInfo(probe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return Array.Empty<double>();
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                // Drain BOTH pipes concurrently — a corrupt clip makes ffprobe spew per-packet errors on stderr, and an
                // unread 4KB stderr pipe blocks it forever (stdout never reaches EOF → this await would hang).
                var soT = p.StandardOutput.ReadToEndAsync(); var seT = p.StandardError.ReadToEndAsync();
                await Task.WhenAll(soT, seT);
                string outp = soT.Result;
                await p.WaitForExitAsync();
                var list = new System.Collections.Generic.List<double>();
                foreach (var line in outp.Split('\n'))
                {
                    int c = line.IndexOf(','); if (c <= 0) continue;
                    if (line.IndexOf('K', c) < 0) continue;   // flags field contains 'K' → keyframe packet
                    if (double.TryParse(line.AsSpan(0, c), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t >= 0) list.Add(t);
                }
                list.Sort();
                var arr = list.ToArray();
                _kfCache[key] = arr;
                return arr;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ff keyframes err: " + ex.Message); return Array.Empty<double>(); }
        }

        // ── Editor preview proxy: a small 720p H.264 copy with DENSE, evenly-spaced keyframes (~every 20 frames), so
        // the keyframe-aligned scrubber/bookmarks land finely instead of on the source's ~2.6s GOP. The recorder
        // (ScreenRecorderLib/Media Foundation) exposes no keyframe-interval, so this is the only way to fine-scrub
        // H.265/H.264 clips in-app. Built lazily on first open (cached per src+mtime in TEMP); the editor plays this,
        // but EXPORTS always re-read the pristine ORIGINAL (full quality, frame-exact). ──
        internal static string ProxyDir
        {
            get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsProxy"); try { Directory.CreateDirectory(d); } catch { } return d; }
        }
        private static readonly System.Threading.SemaphoreSlim _proxyGate = new System.Threading.SemaphoreSlim(1, 1);   // one heavy transcode at a time
        private static bool ProxyFresh(string proxy, string src)
        {
            try { return File.Exists(proxy) && File.GetLastWriteTimeUtc(proxy) >= File.GetLastWriteTimeUtc(src) && new FileInfo(proxy).Length > 0; } catch { return false; }
        }
        /// <summary>The proxy path if one is already built and fresh, else null (non-blocking check).</summary>
        public static string? CachedProxy(string srcPath)
        {
            try { string p = Path.Combine(ProxyDir, Path.GetFileNameWithoutExtension(srcPath) + ".proxy.mp4"); return ProxyFresh(p, srcPath) ? p : null; }
            catch { return null; }
        }
        /// <summary>Build (or reuse) the dense-keyframe preview proxy for a clip. Returns its path, or null on failure.</summary>
        public static async Task<string?> EnsureProxyAsync(string srcPath)
        {
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return null;
            string proxy = Path.Combine(ProxyDir, Path.GetFileNameWithoutExtension(srcPath) + ".proxy.mp4");
            if (ProxyFresh(proxy, srcPath)) return proxy;
            if (!await EnsureAsync()) return null;
            string? ff = Ff; if (ff == null) return null;
            await _proxyGate.WaitAsync();
            try
            {
                if (ProxyFresh(proxy, srcPath)) return proxy;   // built while we waited
                string tmp = Path.Combine(ProxyDir, "_p_" + DateTime.Now.Ticks + ".mp4");
                // 720p, libx264 veryfast, keyframe every 20 frames with scene-cut OFF (evenly spaced → predictable snapping).
                // SOFTWARE decode (no -hwaccel) is intentional + measurably fastest here: HW HEVC decode adds a slow
                // GPU→CPU readback for the CPU scale/encode, so it ran ~2× slower than multi-threaded software decode.
                const string vargs = "-vf scale=-2:720 -c:v libx264 -preset veryfast -crf 23 -g 20 -keyint_min 20 -sc_threshold 0 -pix_fmt yuv420p -threads 0";
                string A(string aud) => $"-y -hide_banner -loglevel error -i \"{srcPath}\" {vargs} {aud} -movflags +faststart \"{tmp}\"";
                bool ok = await RunAsync(ff, A("-c:a copy"), tmp);          // stream-copy the source's AAC audio (instant)
                if (!ok) { TryDel(tmp); ok = await RunAsync(ff, A("-c:a aac -b:a 160k"), tmp); }   // robust path: re-encode audio if copy can't mux
                if (ok) { try { if (File.Exists(proxy)) File.Delete(proxy); File.Move(tmp, proxy); DiagLog.Line("CLIP", "proxy built -> " + Path.GetFileName(proxy)); return proxy; } catch { } }
                TryDel(tmp);
                return null;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "proxy err: " + ex.Message); return null; }
            finally { _proxyGate.Release(); }
        }

        // Codec + framerate + resolution for the gallery card badges (and the editor's aspect-fit). ffprobe reads the
        // stream header only (fast); cached per file. csv order follows -show_entries: codec_name, r_frame_rate, height, width.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string codec, double fps, int height, int width)> _infoCache = new();
        public static async Task<(string codec, double fps, int height, int width)> MediaInfoAsync(string src)
        {
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return ("", 0, 0, 0);
            string key; try { key = src + "|" + File.GetLastWriteTimeUtc(src).Ticks; } catch { key = src; }
            if (_infoCache.TryGetValue(key, out var cached)) return cached;
            if (!await EnsureAsync()) return ("", 0, 0, 0);
            string? ff = Ff; if (ff == null) return ("", 0, 0, 0);
            string probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
            if (!File.Exists(probe)) return ("", 0, 0, 0);
            await _infoGate.WaitAsync();
            try
            {
                // key=value output (default writer) so we parse by NAME — ffprobe ignores the requested field order in
                // csv mode (it emits its own canonical order: codec_name,width,height,r_frame_rate), which silently
                // mis-parsed width/fps when read positionally.
                var psi = new ProcessStartInfo(probe, $"-v error -select_streams v:0 -show_entries stream=codec_name,r_frame_rate,avg_frame_rate,height,width -of default=noprint_wrappers=1 \"{src}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return ("", 0, 0, 0);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // don't starve an active recording/game
                var soT = p.StandardOutput.ReadToEndAsync(); var seT = p.StandardError.ReadToEndAsync();   // drain both — unread stderr deadlocks ffprobe on corrupt files
                await Task.WhenAll(soT, seT); string o = soT.Result; await p.WaitForExitAsync();
                static double Ratio(string s)
                {
                    var fr = s.Split('/');
                    return (fr.Length == 2 && double.TryParse(fr[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                                           && double.TryParse(fr[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0)
                        ? num / den : 0;
                }
                string codec = ""; double rfps = 0, afps = 0; int height = 0, width = 0;
                foreach (var raw in o.Split('\n'))
                {
                    int eq = raw.IndexOf('='); if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                    if (k == "codec_name") codec = v.ToLowerInvariant();
                    else if (k == "width") int.TryParse(v, out width);
                    else if (k == "height") int.TryParse(v, out height);
                    else if (k == "r_frame_rate") rfps = Ratio(v);
                    else if (k == "avg_frame_rate") afps = Ratio(v);
                }
                // Prefer avg_frame_rate (true frames/duration). r_frame_rate is the timebase base rate, which for the
                // variable-frame-rate Windows Graphics Capture reads ~2x nominal (a 120fps clip probes as 240 there).
                double fps = afps > 0 ? afps : rfps;
                var info = (codec, fps, height, width); _infoCache[key] = info; return info;
            }
            catch { return ("", 0, 0, 0); }
            finally { _infoGate.Release(); }
        }

        // The source's video codec name ("hevc" / "h264" / "av1" / …) via ffprobe, lowercased; "" on failure.
        public static async Task<string> VideoCodecAsync(string src)
        {
            if (!await EnsureAsync()) return "";
            string? ff = Ff; if (ff == null) return "";
            string probe = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe.exe");
            if (!File.Exists(probe)) return "";
            try
            {
                var psi = new ProcessStartInfo(probe, $"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 \"{src}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return "";
                var soT = p.StandardOutput.ReadToEndAsync(); var seT = p.StandardError.ReadToEndAsync();   // drain both — unread stderr deadlocks ffprobe on corrupt files
                await Task.WhenAll(soT, seT); string o = soT.Result; await p.WaitForExitAsync();
                return o.Trim().ToLowerInvariant();
            }
            catch { return ""; }
        }

        // ── Smart cut [start,end] → destPath: re-encode ONLY the partial GOPs at the two cut edges and STREAM-COPY the
        // keyframe-aligned middle, so the result is frame-exact, near-lossless (the bulk is untouched), and much faster
        // than a full re-encode on long selections. Segments are joined through MPEG-TS (Annex-B) intermediates — the
        // robust concat path (plain MP4 concat-copy mangled the duration). Returns false (→ caller full-re-encodes) for
        // AV1/other codecs, too-short selections, or if the joined duration doesn't match (a safety net). ──
        public static async Task<bool> SmartCutAsync(string src, double start, double end, string destPath)
        {
            string? ff = Ff; if (ff == null || !File.Exists(src)) return false;
            double totalWanted = end - start; if (totalWanted < 0.5) return false;
            string codec = await VideoCodecAsync(src);
            string venc, bsf;
            if (codec == "hevc") { venc = "-c:v libx265 -preset fast -x265-params log-level=error -crf 20"; bsf = "hevc_mp4toannexb"; }
            else if (codec == "h264") { venc = "-c:v libx264 -preset veryfast -crf 20"; bsf = "h264_mp4toannexb"; }
            else return false;   // AV1/other → not worth the codec-match risk; caller full-re-encodes
            double[] kf = await KeyframesAsync(src); if (kf.Length < 2) return false;
            const double eps = 0.05;
            double mStart = double.NaN, mEnd = double.NaN;
            foreach (var t in kf) { if (t >= start - eps) { mStart = t; break; } }            // first keyframe at/after start
            for (int i = kf.Length - 1; i >= 0; i--) { if (kf[i] <= end + eps) { mEnd = kf[i]; break; } }   // last keyframe at/before end
            if (double.IsNaN(mStart) || double.IsNaN(mEnd) || mEnd - mStart < 0.5) return false;   // not enough copyable middle → full re-encode
            bool hasHead = mStart - start > eps, hasTail = end - mEnd > eps;

            string stamp = DateTime.Now.Ticks.ToString();
            string headTs = Path.Combine(TempDir, "sc_h_" + stamp + ".ts");
            string midTs = Path.Combine(TempDir, "sc_m_" + stamp + ".ts");
            string tailTs = Path.Combine(TempDir, "sc_t_" + stamp + ".ts");
            string tmp = Path.Combine(TempDir, "sc_out_" + stamp + ".mp4");
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                if (hasHead)
                {
                    if (!await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {F(start)} -i \"{src}\" -t {F(mStart - start)} {venc} -c:a aac -f mpegts \"{headTs}\"", headTs)) return false;
                    parts.Add(headTs);
                }
                if (!await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {F(mStart)} -i \"{src}\" -t {F(mEnd - mStart)} -c copy -bsf:v {bsf} -f mpegts \"{midTs}\"", midTs)) return false;
                parts.Add(midTs);
                if (hasTail)
                {
                    if (!await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {F(mEnd)} -i \"{src}\" -t {F(end - mEnd)} {venc} -c:a aac -f mpegts \"{tailTs}\"", tailTs)) return false;
                    parts.Add(tailTs);
                }
                string concat = "concat:" + string.Join("|", parts);
                if (!await RunAsync(ff, $"-y -hide_banner -loglevel error -i \"{concat}\" -c copy -movflags +faststart \"{tmp}\"", tmp)) return false;
                double got = ClipLibrary.Mp4DurationSeconds(tmp);   // safety net: reject a mangled-timestamp join (it happened with plain MP4 concat)
                if (got < totalWanted - 1.0 || got > totalWanted + 1.0) { DiagLog.Line("CLIP", "smartcut dur off: " + F(got) + " vs " + F(totalWanted)); TryDel(tmp); return false; }
                return Finalize(tmp, destPath);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "smartcut err: " + ex.Message); TryDel(tmp); return false; }
            finally { TryDel(headTs); TryDel(midTs); TryDel(tailTs); }
        }

        // Join several already-uniform H.264 mp4s (e.g. composed segments) into one, through MPEG-TS intermediates
        // (the robust concat path). Validated by Finalize. Used by the "combine segments into one clip" export.
        public static async Task<bool> ConcatMp4sAsync(System.Collections.Generic.IReadOnlyList<string> mp4s, string destPath)
        {
            string? ff = Ff; if (ff == null || mp4s.Count == 0) return false;
            string stamp = DateTime.Now.Ticks.ToString();
            string tmp = Path.Combine(TempDir, "cc_" + stamp + ".mp4");
            var tsParts = new System.Collections.Generic.List<string>();
            try
            {
                int i = 0;
                foreach (var m in mp4s)
                {
                    if (!File.Exists(m)) return false;
                    string ts = Path.Combine(TempDir, "cc_" + stamp + "_" + (i++) + ".ts");
                    if (!await RunAsync(ff, $"-y -hide_banner -loglevel error -i \"{m}\" -c copy -bsf:v h264_mp4toannexb -f mpegts \"{ts}\"", ts)) return false;
                    tsParts.Add(ts);
                }
                string concat = "concat:" + string.Join("|", tsParts);
                if (!await RunAsync(ff, $"-y -hide_banner -loglevel error -i \"{concat}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{tmp}\"", tmp)) return false;
                return Finalize(tmp, destPath);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "concat err: " + ex.Message); TryDel(tmp); return false; }
            finally { foreach (var t in tsParts) TryDel(t); }
        }

        /// <summary>Swap a clip's audio for the per-app game track (GameAudioCapture WAV), in place. alignEnd = the WAV is
        /// a replay ring dump that ENDS where the video ends (trimmed / left-padded to fit); else both start together
        /// (full recording). Keeps the original audio — returns false — when the game track is silent (the game made no
        /// sound, or its audio came from another process), so per-app capture can never make a clip quieter.</summary>
        public static async Task<bool> ReplaceAudioAsync(string mp4, string wav, bool alignEnd) => (await AlignAndReplaceAudioAsync(mp4, wav, alignEnd)).ok;

        /// <summary>As ReplaceAudioAsync, and returns the measured sync correction `lag` (seconds; + = the game track belongs
        /// LATER than the save-time estimate). Sidecars dumped at the same instant (mic/chat) need the same shift.</summary>
        public static async Task<(bool ok, double lag)> AlignAndReplaceAudioAsync(string mp4, string wav, bool alignEnd,
            string? syncRef = null, double syncRefOffset = 0)
        {
            string? ff = Ff; if (ff == null || !File.Exists(mp4) || !File.Exists(wav)) return (false, 0);
            string tmp = Path.Combine(TempDir, "_game_" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                double vid = ClipLibrary.Mp4DurationSeconds(mp4), aud;
                bool audible = false;
                using (var r = new NAudio.Wave.WaveFileReader(wav))
                {
                    aud = r.TotalTime.TotalSeconds;
                    var buf = new byte[65536]; int n;   // PCM16: anything above ~-72 dBFS counts as real game audio
                    while (!audible && (n = r.Read(buf, 0, buf.Length)) > 0)
                        for (int i = 0; i + 1 < n; i += 2) { int v = (short)(buf[i] | (buf[i + 1] << 8)); if (v > 8 || v < -8) { audible = true; break; } }
                }
                if (vid <= 0 || aud <= 0) return (false, 0);
                if (!audible) { DiagLog.Line("CLIP", "game audio silent → keeping the original track"); return (false, 0); }
                // Where the game track's first sample lands on the video timeline. The save-time estimate (ring end = video
                // end for replays, start = start for recordings) is off by the recorder's stop/start latency — the clip's
                // own (device-loopback) track IS in sync with the video, so measure the true offset against it.
                // before: end-alignment only → sound ~40 ms late on a light test window, more in heavy games.
                double s0base = alignEnd ? vid - aud : 0;
                bool hasOrig = await HasAudioAsync(mp4);
                // Sync reference: the RAW newest buffer segment when given (exactly what the recorder wrote — its audio is
                // in sync with its video, and it occupies the clip's tail starting at syncRefOffset). The assembled clip's
                // own track is the fallback: joins/cuts can leave gaps in it (a closed window's segment has audio but no
                // video), which made the measurement chase a broken reference.
                double lag = 0;
                if (syncRef != null && File.Exists(syncRef) && await HasAudioAsync(syncRef))
                {
                    double rd = ClipLibrary.Mp4DurationSeconds(syncRef), w = Math.Min(rd, 20);
                    if (rd > 1) lag = await MeasureLagAsync(ff, syncRef, rd - w, w, syncRefOffset + rd - w, wav, s0base);
                }
                else if (hasOrig) { double w = Math.Min(vid, 20); lag = await MeasureLagAsync(ff, mp4, vid - w, w, vid - w, wav, s0base); }
                double s0 = s0base + lag;
                string seek = "", delay = "";
                if (s0 < -0.005) seek = $"-ss {F(-s0)} ";                                          // game track starts before the clip → drop its head
                else if (s0 > 0.005) delay = $"adelay={(long)Math.Round(s0 * 1000)}:all=1,";       // starts later → push it right
                string args = $"-y -hide_banner -loglevel error -i \"{mp4}\" {seek}-i \"{wav}\" -map 0:v:0 -map 1:a:0 -c:v copy " +
                              $"-af \"{delay}apad\" -c:a aac -b:a 192k -shortest -movflags +faststart \"{tmp}\"";
                // The game ring restarts when follow-focus switches windows, so it can be shorter than a joined replay. Then
                // the part BEFORE the switch keeps the clip's own recorded audio instead of going silent.
                double gap = s0;
                if (gap > 0.5 && hasOrig)
                {
                    const string AF = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
                    string fc = $"[0:a]atrim=0:{F(gap)},asetpts=PTS-STARTPTS,{AF}[h];[1:a]{AF},asetpts=PTS-STARTPTS[t];[h][t]concat=n=2:v=0:a=1,apad[a]";   // head + game from s0
                    args = $"-y -hide_banner -loglevel error -i \"{mp4}\" -i \"{wav}\" -filter_complex \"{fc}\" -map 0:v:0 -map \"[a]\" -c:v copy " +
                           $"-c:a aac -b:a 192k -shortest -movflags +faststart \"{tmp}\"";
                }
                if (!await RunAsync(ff, args, tmp)) return (false, 0);
                bool ok = Finalize(tmp, mp4);
                DiagLog.Line("CLIP", ok ? $"game audio muxed (sync {lag * 1000:+0;-0;0} ms) -> " + Path.GetFileName(mp4) : "game audio mux finalize failed");
                return (ok, ok ? lag : 0);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "game audio mux err: " + ex.Message); return (false, 0); }
            finally { TryDel(tmp); }
        }

        // Cross-correlate loudness envelopes (1 kHz) of the clip's own audio (last ≤20 s) against the game track placed at
        // s0base; search ±1.5 s; returns the best shift in seconds, or 0 when there's no confident match (silent / different
        // content) — then the save-time estimate stands. Envelopes make it robust to the device track being quiet (Sonar at
        // 5% still leaves the waveform's shape) and to small spectral differences between the two captures.
        // refFrom/w = the reference window inside refPath; x0 = the CLIP time where that window starts.
        private static async Task<double> MeasureLagAsync(string ff, string refPath, double refFrom, double w, double x0, string wav, double s0base)
        {
            try
            {
                const double M = 1.5; const int SR = 8000, ER = 1000;
                double g0 = Math.Max(0, x0 - M - s0base);                                    // game-track seconds covering [x0-M, x0+w+M]
                var x = await DecodeMonoAsync(ff, $"-ss {F(refFrom)} -i \"{refPath}\" -t {F(w)}", SR);
                var g = await DecodeMonoAsync(ff, $"-ss {F(g0)} -i \"{wav}\" -t {F(w + 2 * M)}", SR);
                if (x == null || g == null || x.Length < SR || g.Length < SR) return 0;
                double[] xe = Envelope(x, SR / ER), ge = Envelope(g, SR / ER);
                double gStartVideo = s0base + g0;                                            // video time of ge[0]
                Center(xe); Center(ge);
                double nx = Math.Sqrt(Dot(xe, xe)); if (nx < 1e-6) return 0;
                int maxLag = (int)(M * ER), best = 0; double bestC = -1;
                for (int L = -maxLag; L <= maxLag; L++)
                {
                    // x(t) vs g(t - L): ge index = (x0 + t/ER - L/ER - gStartVideo) * ER
                    int off = (int)Math.Round((x0 - gStartVideo) * ER) - L;
                    double sum = 0, ng = 0; int lo = Math.Max(0, -off), hi = Math.Min(xe.Length, ge.Length - off);
                    for (int t = lo; t < hi; t++) { double gv = ge[t + off]; sum += xe[t] * gv; ng += gv * gv; }
                    if (hi - lo < xe.Length / 2 || ng <= 0) continue;
                    double c = sum / (nx * Math.Sqrt(ng));
                    if (c > bestC) { bestC = c; best = L; }
                }
                DiagLog.Line("CLIP", $"audio sync: best shift {best} ms (match {bestC:0.00})");
                return bestC >= 0.35 ? best / (double)ER : 0;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "audio sync measure err: " + ex.Message); return 0; }
        }
        private static async Task<short[]?> DecodeMonoAsync(string ff, string input, int sr)
        {
            var psi = new ProcessStartInfo(ff, $"-hide_banner -loglevel error {input} -vn -ac 1 -ar {sr} -f s16le -")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi); if (p == null) return null;
            var errT = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream(); await p.StandardOutput.BaseStream.CopyToAsync(ms); await errT; await p.WaitForExitAsync();
            var b = ms.ToArray(); var s = new short[b.Length / 2]; Buffer.BlockCopy(b, 0, s, 0, s.Length * 2); return s;
        }
        private static double[] Envelope(short[] s, int factor)
        {
            var e = new double[s.Length / factor];
            for (int i = 0; i < e.Length; i++) { double a = 0; for (int k = 0; k < factor; k++) a += Math.Abs((int)s[i * factor + k]); e[i] = a / factor; }
            return e;
        }
        private static void Center(double[] a) { double m = 0; foreach (var v in a) m += v; m /= Math.Max(1, a.Length); for (int i = 0; i < a.Length; i++) a[i] -= m; }
        private static double Dot(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * b[i]; return s; }

        private static async Task<bool> RunAsync(string ff, string args, string dst)
        {
            try
            {
                var psi = new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi); if (p == null) return false;
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // never starve the WebView2 renderer (UI stays smooth while transcoding)
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                bool ok = p.ExitCode == 0 && File.Exists(dst) && new FileInfo(dst).Length > 0;
                if (!ok) { DiagLog.Line("CLIP", "ffmpeg exit " + p.ExitCode + ": " + Short(err)); TryDel(dst); }
                return ok;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ffmpeg run err: " + ex.Message); return false; }
        }

        // Validate a freshly-rendered temp (exists, non-empty, real duration) then atomically move it into place.
        private static bool Finalize(string tmp, string dest)
        {
            try
            {
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0 || ClipLibrary.Mp4DurationSeconds(tmp) <= 0) { TryDel(tmp); return false; }
                string? dir = Path.GetDirectoryName(dest); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                return true;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ff finalize err: " + ex.Message); TryDel(tmp); return false; }
        }

        private static void TryDel(string p) { try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); } catch { } }
        private static string Short(string s) { s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); return s.Length > 220 ? s.Substring(0, 220) : s; }
    }
}
