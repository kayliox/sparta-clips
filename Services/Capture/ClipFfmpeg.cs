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
        public static async Task<bool> ConcatLastSecondsAsync(string prev, string cur, int n, string destPath)
        {
            string? ff = Ff; if (ff == null || !File.Exists(cur)) return false;
            double curDur = ClipLibrary.Mp4DurationSeconds(cur);
            string tmp = Path.Combine(TempDir, "rep_" + DateTime.Now.Ticks + ".mp4");
            string list = "", joined = "";
            try
            {
                bool ok;
                // The capture target can change between segments (focus moved to another window) → different frame
                // sizes, which a stream-copy concat turns into a corrupt file. Then the replay is just the current segment.
                if (!(curDur >= n || string.IsNullOrEmpty(prev) || !File.Exists(prev)))
                {
                    var a = await MediaInfoAsync(prev); var b = await MediaInfoAsync(cur);
                    if (a.width != b.width || a.height != b.height) { DiagLog.Line("CLIP", $"replay: segment size changed ({a.width}x{a.height} -> {b.width}x{b.height}) — using the current segment only"); prev = ""; }
                }
                if (curDur >= n || string.IsNullOrEmpty(prev) || !File.Exists(prev))
                {
                    double start = Math.Max(0, curDur - n);
                    ok = await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {F(start)} -i \"{cur}\" -c copy -movflags +faststart \"{tmp}\"", tmp);
                }
                else
                {
                    // Concat prev+cur (identical encoder params → copy-safe via the concat demuxer), then keep last n.
                    list = Path.Combine(TempDir, "list_" + DateTime.Now.Ticks + ".txt");
                    joined = Path.Combine(TempDir, "join_" + DateTime.Now.Ticks + ".mp4");
                    // Concat-demuxer quoting: a ' inside a single-quoted entry must become '\'' (think C:\Users\O'Brien\…\Temp)
                    static string Q(string p2) => p2.Replace("\\", "/").Replace("'", "'\\''");
                    File.WriteAllText(list, "file '" + Q(prev) + "'\nfile '" + Q(cur) + "'\n");
                    ok = await RunAsync(ff, $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{list}\" -c copy -movflags +faststart \"{joined}\"", joined);
                    if (ok)
                    {
                        double total = ClipLibrary.Mp4DurationSeconds(joined);
                        double start = Math.Max(0, total - n);
                        ok = await RunAsync(ff, $"-y -hide_banner -loglevel error -ss {F(start)} -i \"{joined}\" -c copy -movflags +faststart \"{tmp}\"", tmp);
                    }
                }
                return ok && Finalize(tmp, destPath);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "ff concat err: " + ex.Message); TryDel(tmp); return false; }
            finally { TryDel(list); TryDel(joined); }
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
        public static async Task<bool> ReplaceAudioAsync(string mp4, string wav, bool alignEnd)
        {
            string? ff = Ff; if (ff == null || !File.Exists(mp4) || !File.Exists(wav)) return false;
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
                if (vid <= 0 || aud <= 0) return false;
                if (!audible) { DiagLog.Line("CLIP", "game audio silent → keeping the original track"); return false; }
                string seek = "", delay = "";
                if (alignEnd && aud > vid + 0.01) seek = $"-ss {F(aud - vid)} ";                                 // ring longer than the clip → drop its head
                else if (alignEnd && vid > aud + 0.01) delay = $"adelay={(long)((vid - aud) * 1000)}:all=1,";  // ring shorter → right-align it
                string args = $"-y -hide_banner -loglevel error -i \"{mp4}\" {seek}-i \"{wav}\" -map 0:v:0 -map 1:a:0 -c:v copy " +
                              $"-af \"{delay}apad\" -c:a aac -b:a 192k -shortest -movflags +faststart \"{tmp}\"";
                if (!await RunAsync(ff, args, tmp)) return false;
                bool ok = Finalize(tmp, mp4);
                DiagLog.Line("CLIP", ok ? "game audio muxed -> " + Path.GetFileName(mp4) : "game audio mux finalize failed");
                return ok;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "game audio mux err: " + ex.Message); return false; }
            finally { TryDel(tmp); }
        }

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
