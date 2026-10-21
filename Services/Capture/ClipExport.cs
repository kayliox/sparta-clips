// Generalized "export to a file the user picks": an animated GIF of a clip's selected range (two-pass palette for
// clean colour). The MP4 path reuses ClipFfmpeg.ComposeAsync directly (see BridgeApi.ExportFile). Distinct from the
// in-library "Export" (which bakes a new gallery clip) and from Share (Discord). Self-contained, deletable with capture.
using System;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal static class ClipExport
    {
        private static string GifTemp { get { string d = Path.Combine(Path.GetTempPath(), "SpartaClipsShare"); try { Directory.CreateDirectory(d); } catch { } return d; } }

        /// <summary>GIF of [vidIn,vidOut] at fps/width (clamped). Two-pass palettegen→paletteuse. Returns success.</summary>
        public static async Task<bool> ExportGifAsync(string name, string outPath, double vidIn, double vidOut, int fps, int width)
        {
            if (!await ClipFfmpeg.EnsureAsync()) return false;
            string? ff = Av1Transcoder.FfmpegPath; if (ff == null) return false;
            string? src = ClipLibrary.PathOf(name); if (src == null || !File.Exists(src)) return false;
            double dur = vidOut - vidIn; if (dur < 0.1) return false;
            fps = Math.Clamp(fps <= 0 ? 15 : fps, 5, 30);
            width = Math.Clamp(width <= 0 ? 640 : width, 160, 1280);
            if (!outPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) outPath += ".gif";
            string pal = Path.Combine(GifTemp, "pal_" + Guid.NewGuid().ToString("N") + ".png");
            string tmp = Path.Combine(GifTemp, "gif_" + Guid.NewGuid().ToString("N") + ".gif");
            try
            {
                string filt = $"fps={fps},scale={width}:-1:flags=lanczos";
                bool p1 = await Run(ff, $"-nostdin -y -hide_banner -loglevel error -ss {F(vidIn)} -t {F(dur)} -i \"{src}\" -vf \"{filt},palettegen=stats_mode=diff\" \"{pal}\"");
                if (!p1 || !File.Exists(pal)) return false;
                // Render to TEMP then move — before: pass 2 wrote the user's chosen outPath directly, so a failed or
                // killed encode left a corrupt partial .gif sitting at their destination (e.g. Desktop).
                bool p2 = await Run(ff, $"-nostdin -y -hide_banner -loglevel error -ss {F(vidIn)} -t {F(dur)} -i \"{src}\" -i \"{pal}\" -lavfi \"{filt}[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=3\" \"{tmp}\"");
                if (!p2 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0) return false;
                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmp, outPath);
                return true;
            }
            catch (Exception ex) { DiagLog.Line("EXPORT", "gif err: " + ex.Message); return false; }
            finally
            {
                try { if (File.Exists(pal)) File.Delete(pal); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static async Task<bool> Run(string ff, string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi); if (p == null) return false;
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) DiagLog.Line("EXPORT", "ffmpeg " + p.ExitCode + ": " + (err.Length > 180 ? err.Substring(0, 180) : err));
                return p.ExitCode == 0;
            }
            catch (Exception ex) { DiagLog.Line("EXPORT", "ffmpeg run err: " + ex.Message); return false; }
        }
    }
}
