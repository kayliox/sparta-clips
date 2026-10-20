// Makes a cached H.264 "proxy" of an HEVC clip so the in-page <video> can preview it when the OS has NO native HEVC
// codec (the app auto-detects that in JS). Reuses the bundled ffmpeg (x264 veryfast) and serves the result over the
// clipproxy.local virtual host. Self-contained, deletable with the capture feature.
using System;
using System.IO;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal static class ClipProxy
    {
        public static string Dir { get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsProxy"); try { Directory.CreateDirectory(d); } catch { } return d; } }
        private static readonly System.Threading.SemaphoreSlim _gate = new(1, 1);   // one build at a time; a concurrent caller waits and gets the cache hit

        // Returns the proxy file NAME (served as https://clipproxy.local/<file>) or null. Cached by source stem + mtime,
        // so re-opening the same clip is instant and re-edits invalidate the old proxy.
        public static async Task<string?> EnsureAsync(string clipName)
        {
            try
            {
                if (!await ClipFfmpeg.EnsureAsync()) return null;
                string? src = ClipLibrary.PathOf(clipName); if (src == null || !File.Exists(src)) return null;
                string? ff = Av1Transcoder.FfmpegPath; if (ff == null) return null;

                long ticks; try { ticks = File.GetLastWriteTimeUtc(src).Ticks; } catch { ticks = 0; }
                string stem = Path.GetFileNameWithoutExtension(src);
                foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
                string outName = stem + "_" + ticks + ".h264.mp4";
                string dst = Path.Combine(Dir, outName);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) return outName;   // cached hit
                await _gate.WaitAsync();
                try
                {
                    if (File.Exists(dst) && new FileInfo(dst).Length > 0) return outName;   // built while we waited
                    CleanStale(stem, ticks);

                    // Transcode HEVC → H.264 (x264 veryfast, audio stream-copied) into a TEMP file, then move into the
                    // cache path atomically — before: ffmpeg wrote dst directly, so a crash/kill mid-encode left a
                    // truncated file that the "Exists && Length > 0" cache check served as good FOREVER after.
                    string tmp = Path.Combine(Dir, "_x_" + DateTime.Now.Ticks + ".mp4");
                    string args = $"-y -hide_banner -loglevel error -i \"{src}\" -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a copy -movflags +faststart \"{tmp}\"";
                    bool ok = await Run(ff, args);
                    if (!ok) { TryDel(tmp); ok = await Run(ff, args.Replace("-c:a copy", "-c:a aac -b:a 160k")); }   // fallback if audio can't be copied
                    if (ok && File.Exists(tmp) && new FileInfo(tmp).Length > 0 && ClipLibrary.Mp4DurationSeconds(tmp) > 0)
                    { TryDel(dst); File.Move(tmp, dst); return outName; }
                    TryDel(tmp);
                    return null;
                }
                finally { _gate.Release(); }
            }
            catch (Exception ex) { DiagLog.Line("PROXY", "err: " + ex.Message); return null; }
        }

        private static async Task<bool> Run(string ff, string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi); if (p == null) return false;
                string err = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
                if (p.ExitCode != 0) DiagLog.Line("PROXY", "ffmpeg " + p.ExitCode + ": " + (err.Length > 180 ? err.Substring(0, 180) : err));
                return p.ExitCode == 0;
            }
            catch (Exception ex) { DiagLog.Line("PROXY", "run err: " + ex.Message); return false; }
        }

        private static void TryDel(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        // Drop older proxies for the same clip stem so the cache doesn't grow as a clip is re-edited.
        // Names are "<stem>_<ticks>.h264.mp4" — the glob also prefix-matches OTHER clips ("Game" catches
        // "Game_2_<ticks>…"), which used to delete Game_2's valid proxy every time Game rebuilt (cache thrash,
        // full re-transcode on next open). Only a pure-digits remainder is one of ours.
        private static void CleanStale(string stem, long keepTicks)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Dir, stem + "_*.h264.mp4"))
                {
                    string rest = Path.GetFileName(f).Substring(stem.Length + 1);       // "<ticks>.h264.mp4" when it's ours
                    string ticksPart = rest.Split('.')[0];
                    if (!long.TryParse(ticksPart, out long t)) continue;                // another clip's proxy (e.g. Game_2_…)
                    if (t != keepTicks) TryDel(f);
                }
            }
            catch { }
        }
    }
}
