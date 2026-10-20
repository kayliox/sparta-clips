// Clip library: enumerate / rename / delete the recorded MP4s + folder helpers, for the Gallery (Library tab).
// Duration is read straight from each MP4's mvhd box (no media deps, no per-card <video> loads). Paired with
// ClipRecorder.cs; deletable with the rest of the capture feature.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SpartaClips
{
    internal static class ClipLibrary
    {
        /// <summary>{ dir, totalBytes, clips:[{name,size,modified,duration}] } — newest first.</summary>
        public static string ListJson()
        {
            var clips = new List<object>();
            long total = 0;
            try
            {
                // Raw captures (ClipsDir) + editor exports (EditedDir), newest first across both.
                var files = new List<(FileInfo f, bool edited)>();
                try { foreach (var f in new DirectoryInfo(ClipRecorder.ClipsDir).GetFiles("*.mp4")) files.Add((f, false)); } catch { }
                try { foreach (var f in new DirectoryInfo(ClipRecorder.EditedDir).GetFiles("*.mp4")) files.Add((f, true)); } catch { }
                foreach (var (f, edited) in files.OrderByDescending(x => x.f.LastWriteTimeUtc))
                {
                    double dur = CachedDuration(f);
                    if (f.Length == 0 || dur <= 0) continue;   // skip partial/empty clips so a stray render never shows as "0 MB · 0:00"
                    total += f.Length;
                    string mic = ClipRecorder.MicSidecar(f.FullName);
                    bool hasMic = File.Exists(mic);
                    if (hasMic) { try { total += new FileInfo(mic).Length; } catch { } }
                    string chat = ClipRecorder.ChatSidecar(f.FullName);
                    bool hasChat = File.Exists(chat);
                    if (hasChat) { try { total += new FileInfo(chat).Length; } catch { } }
                    clips.Add(new
                    {
                        name = f.Name,
                        size = f.Length,
                        modified = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        duration = dur,
                        hasMic,
                        hasChat,
                        edited,
                    });
                }
            }
            catch { }
            return JsonConvert.SerializeObject(new { dir = ClipRecorder.ClipsDir, totalBytes = total, clips });
        }

        // (path → duration) keyed by mtime+size: ListJson used to re-open and re-parse EVERY clip's mvhd box on EVERY
        // gallery refresh — the dominant gallery-load cost with a big library on a slow/OneDrive folder.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long tick, long len, double dur)> _durCache
            = new(StringComparer.OrdinalIgnoreCase);
        private static double CachedDuration(FileInfo f)
        {
            long t = f.LastWriteTimeUtc.Ticks;
            if (_durCache.TryGetValue(f.FullName, out var c) && c.tick == t && c.len == f.Length) return c.dur;
            double d = Mp4DurationSeconds(f.FullName);
            _durCache[f.FullName] = (t, f.Length, d);
            return d;
        }

        public static bool Delete(string name)
        {
            try { var p = Resolve(name); if (p != null) { File.Delete(p); TryDelete(ClipRecorder.MicSidecar(p)); TryDelete(ClipRecorder.ChatSidecar(p)); TryDelete(ClipRecorder.MarksSidecar(p)); return true; } } catch { }
            return false;
        }

        // Delete ONLY the mic sidecar (the "Delete mic" button in the editor) — the clip keeps its embedded audio.
        public static bool DeleteMic(string name)
        {
            try { var p = Resolve(name); if (p != null) { TryDelete(ClipRecorder.MicSidecar(p)); return true; } } catch { }
            return false;
        }

        // Delete ONLY the chat sidecar (the editor's "Delete chat" button).
        public static bool DeleteChat(string name)
        {
            try { var p = Resolve(name); if (p != null) { TryDelete(ClipRecorder.ChatSidecar(p)); return true; } } catch { }
            return false;
        }

        public static bool Rename(string oldName, string newName)
        {
            try
            {
                var src = Resolve(oldName); if (src == null) return false;
                string stem = SafeStem(Path.GetFileNameWithoutExtension(newName));
                if (stem.Length == 0) return false;
                string dst = Path.Combine(Path.GetDirectoryName(src)!, stem + ".mp4");   // keep the clip in its own folder (Clips or Edited)
                // Collision check across BOTH folders, not just the clip's own: names are resolved ClipsDir-first, so
                // an EditedDir clip renamed to a ClipsDir clip's name became unreachable — and Delete/Share on its
                // gallery card resolved to (and destroyed) the RAW recording instead.
                if (File.Exists(Path.Combine(ClipRecorder.ClipsDir, stem + ".mp4")) ||
                    File.Exists(Path.Combine(ClipRecorder.EditedDir, stem + ".mp4"))) return false;
                File.Move(src, dst);
                string micSrc = ClipRecorder.MicSidecar(src);   // carry the sidecars along
                if (File.Exists(micSrc)) { try { File.Move(micSrc, ClipRecorder.MicSidecar(dst)); } catch { } }
                string chatSrc = ClipRecorder.ChatSidecar(src);
                if (File.Exists(chatSrc)) { try { File.Move(chatSrc, ClipRecorder.ChatSidecar(dst)); } catch { } }
                string marksSrc = ClipRecorder.MarksSidecar(src);
                if (File.Exists(marksSrc)) { try { File.Move(marksSrc, ClipRecorder.MarksSidecar(dst)); } catch { } }
                return true;
            }
            catch { return false; }
        }
        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        public static void OpenFolder()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ClipRecorder.ClipsDir, UseShellExecute = true }); } catch { }
        }
        public static void Reveal(string name)
        {
            try { var p = Resolve(name); if (p != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + p + "\"", UseShellExecute = true }); } catch { }
        }

        /// <summary>Trim [startSec, endSec] into a new MP4 (Game_…_clip.mp4) via MediaComposition — native, hardware-
        /// accelerated, no external deps. Returns the new file name, or null on failure.</summary>
        // A high-quality MediaComposition render profile matching the source size at the capture bitrate/fps — without
        // this, RenderToFileAsync's default profile re-encodes at a low bitrate and clips look blocky (esp. text/UI).
        public static async System.Threading.Tasks.Task<Windows.Media.MediaProperties.MediaEncodingProfile> MakeProfileAsync(Windows.Storage.StorageFile src, int mbps, int fps, bool hevc)
        {
            uint w = 1920, h = 1080;
            try { var vp = await src.Properties.GetVideoPropertiesAsync(); if (vp.Width > 0 && vp.Height > 0) { w = vp.Width; h = vp.Height; } } catch { }
            var v = hevc ? Windows.Media.MediaProperties.VideoEncodingProperties.CreateHevc()
                         : Windows.Media.MediaProperties.VideoEncodingProperties.CreateH264();
            v.Width = w; v.Height = h;
            v.Bitrate = (uint)Math.Clamp(mbps, 1, 200) * 1_000_000u;
            v.FrameRate.Numerator = (uint)Math.Clamp(fps, 15, 240); v.FrameRate.Denominator = 1;
            return new Windows.Media.MediaProperties.MediaEncodingProfile
            {
                Container = new Windows.Media.MediaProperties.ContainerEncodingProperties { Subtype = "MPEG4" },
                Video = v,
                Audio = Windows.Media.MediaProperties.AudioEncodingProperties.CreateAac(48000, 2, 192000),
            };
        }

        public static async System.Threading.Tasks.Task<string?> TrimAsync(string name, double startSec, double endSec)
        {
            var src = Resolve(name); if (src == null) return null;
            string outName = UniqueName(Path.GetFileNameWithoutExtension(name) + "_clip.mp4", ClipRecorder.EditedDir);
            string outPath = Path.Combine(ClipRecorder.EditedDir, outName);

            // Primary: ffmpeg stream-copy trim (lossless, instant; handles H.264/H.265/AV1). Needs a known duration to bound the cut.
            double total = Mp4DurationSeconds(src);
            if (total > 0)
            {
                double s = Math.Max(0, startSec);
                double e = (endSec <= 0 || endSec > total) ? total : endSec;
                if (e - s >= 0.2 && await ClipFfmpeg.EnsureAsync() && await ClipFfmpeg.TrimAsync(src, s, e, outPath))
                { DiagLog.Line("CLIP", "trim (ff) -> " + outName); return outName; }
            }

            // Fallback (ffmpeg unavailable): MediaComposition re-encode → H.264. Validated; never leaves a 0-byte orphan.
            Windows.Storage.StorageFile? created = null;
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(src);
                var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
                double dur = clip.OriginalDuration.TotalSeconds;
                startSec = Math.Max(0, startSec);
                endSec = (endSec <= 0 || endSec > dur) ? dur : endSec;
                if (endSec - startSec < 0.2) { TryDelete(outPath); return null; }
                clip.TrimTimeFromStart = TimeSpan.FromSeconds(startSec);
                clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, dur - endSec));
                var comp = new Windows.Media.Editing.MediaComposition();
                comp.Clips.Add(clip);
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(ClipRecorder.EditedDir);
                var outFile = await folder.CreateFileAsync(outName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                created = outFile;
                var profile = await MakeProfileAsync(file, ClipRecorder.Mbps, ClipRecorder.Fps, false);
                var res = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
                if (res != Windows.Media.Transcoding.TranscodeFailureReason.None)
                    res = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise);   // fallback to the default profile
                if (res != Windows.Media.Transcoding.TranscodeFailureReason.None || !ValidOut(outPath))
                { try { await outFile.DeleteAsync(); } catch { } DiagLog.Line("CLIP", "trim render failed: " + res); return null; }
                DiagLog.Line("CLIP", "trim -> " + outName);
                return outName;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "trim err: " + ex.Message); if (created != null) { try { await created.DeleteAsync(); } catch { } } TryDelete(outPath); return null; }
        }
        // A finished render is only valid if the file exists, is non-empty, and has a real (readable) duration.
        private static bool ValidOut(string path) { try { return File.Exists(path) && new FileInfo(path).Length > 0 && Mp4DurationSeconds(path) > 0; } catch { return false; } }

        /// <summary>Frame-exact, near-lossless trim for single-track clips: smart-cut (re-encode only the edge GOPs,
        /// copy the middle) and, if that can't apply, fall back to a full re-encode. Returns the new name, or null.</summary>
        public static async System.Threading.Tasks.Task<string?> SmartCutAsync(string name, double startSec, double endSec)
        {
            var src = Resolve(name); if (src == null) return null;
            string outName = UniqueName(Path.GetFileNameWithoutExtension(name) + "_clip.mp4", ClipRecorder.EditedDir);
            string outPath = Path.Combine(ClipRecorder.EditedDir, outName);
            double total = Mp4DurationSeconds(src);
            double s = Math.Max(0, startSec);
            double e = (endSec <= 0 || (total > 0 && endSec > total)) ? (total > 0 ? total : endSec) : endSec;
            if (e - s < 0.2 || !await ClipFfmpeg.EnsureAsync()) { TryDelete(outPath); return null; }
            if (await ClipFfmpeg.SmartCutAsync(src, s, e, outPath)) { DiagLog.Line("CLIP", "smartcut -> " + outName); return outName; }
            // Fallback: full re-encode (frame-exact, always works) — the single-track ComposeAsync path, no audio mix.
            if (await ClipFfmpeg.ComposeAsync(src, null, null, s, e, 1.0, false, 0, 0, 0, 1, false, 0, 0, 0, 1, outPath))
            { DiagLog.Line("CLIP", "smartcut fallback recode -> " + outName); return outName; }
            TryDelete(outPath); return null;
        }

        /// <summary>Compose a new clip from the trimmed video [vidIn,vidOut] (at vidVol) plus, optionally, the mic
        /// sidecar as a background track — positioned at timeline `micOffset`, trimmed to its own [micIn,micOut]
        /// (mic-local seconds) and mixed at micVol. Volumes are 0..2 (1 = unchanged; >1 boosts). Returns the new
        /// name, or null. Used for clips that HAVE a separate mic track; single-track clips still use TrimAsync.</summary>
        public static async System.Threading.Tasks.Task<string?> ComposeAsync(
            string name, double vidIn, double vidOut, double vidVol,
            bool micOn, double micIn, double micOut, double micOffset, double micVol,
            bool chatOn, double chatIn, double chatOut, double chatOffset, double chatVol)
        {
            var src = Resolve(name); if (src == null) return null;
            string outName = UniqueName(Path.GetFileNameWithoutExtension(name) + "_clip.mp4", ClipRecorder.EditedDir);
            string outPath = Path.Combine(ClipRecorder.EditedDir, outName);
            string micPath = ClipRecorder.MicSidecar(src), chatPath = ClipRecorder.ChatSidecar(src);

            // Primary: ffmpeg (video re-encoded → H.264 so the cut is frame-accurate and always previewable; audio mixed).
            if (await ClipFfmpeg.EnsureAsync() && await ClipFfmpeg.ComposeAsync(src, micOn ? micPath : null, chatOn ? chatPath : null,
                    vidIn, vidOut, vidVol, micOn, micIn, micOut, micOffset, micVol, chatOn, chatIn, chatOut, chatOffset, chatVol, outPath))
            { DiagLog.Line("CLIP", "compose (ff) -> " + outName); return outName; }

            // Fallback (ffmpeg unavailable): MediaComposition with the sidecars as background tracks.
            Windows.Storage.StorageFile? created = null;
            try
            {
                var vidFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(src);
                var vid = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(vidFile);
                double vdur = vid.OriginalDuration.TotalSeconds;
                vidIn = Math.Max(0, vidIn);
                vidOut = (vidOut <= 0 || vidOut > vdur) ? vdur : vidOut;
                if (vidOut - vidIn < 0.2) { TryDelete(outPath); return null; }
                vid.TrimTimeFromStart = TimeSpan.FromSeconds(vidIn);
                vid.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, vdur - vidOut));
                vid.Volume = Math.Clamp(vidVol, 0, 2);
                var comp = new Windows.Media.Editing.MediaComposition();
                comp.Clips.Add(vid);
                if (micOn) await AddBgTrack(comp, micPath, vidIn, vidOut, micIn, micOut, micOffset, micVol);
                if (chatOn) await AddBgTrack(comp, chatPath, vidIn, vidOut, chatIn, chatOut, chatOffset, chatVol);

                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(ClipRecorder.EditedDir);
                var outFile = await folder.CreateFileAsync(outName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                created = outFile;
                var profile = await MakeProfileAsync(vidFile, ClipRecorder.Mbps, ClipRecorder.Fps, false);
                var res = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
                if (res != Windows.Media.Transcoding.TranscodeFailureReason.None)
                    res = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise);   // fallback to the default profile
                if (res != Windows.Media.Transcoding.TranscodeFailureReason.None || !ValidOut(outPath))
                { try { await outFile.DeleteAsync(); } catch { } DiagLog.Line("CLIP", "compose render failed: " + res); return null; }
                DiagLog.Line("CLIP", "compose -> " + outName);
                return outName;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "compose err: " + ex.Message); if (created != null) { try { await created.DeleteAsync(); } catch { } } TryDelete(outPath); return null; }
        }

        // MediaComposition fallback helper: add a sidecar WAV as a background track, trimmed to [tIn,tOut], shifted to
        // timeline `tOffset` and clipped to the exported video window [vidIn,vidOut], at volume tVol (0..2).
        private static async System.Threading.Tasks.Task AddBgTrack(Windows.Media.Editing.MediaComposition comp, string wav,
            double vidIn, double vidOut, double tIn, double tOut, double tOffset, double tVol)
        {
            if (!File.Exists(wav)) return;
            var f = await Windows.Storage.StorageFile.GetFileFromPathAsync(wav);
            var trk = await Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(f);
            double tdur = trk.OriginalDuration.TotalSeconds;
            tIn = Math.Clamp(tIn, 0, tdur);
            tOut = (tOut <= 0 || tOut > tdur) ? tdur : tOut;
            double audStart = Math.Max(tOffset, vidIn), audEnd = Math.Min(tOffset + (tOut - tIn), vidOut);
            if (audEnd - audStart < 0.05) return;
            double localStart = tIn + (audStart - tOffset), localEnd = tIn + (audEnd - tOffset);
            trk.TrimTimeFromStart = TimeSpan.FromSeconds(Math.Max(0, localStart));
            trk.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, tdur - localEnd));
            trk.Delay = TimeSpan.FromSeconds(Math.Max(0, audStart - vidIn));
            trk.Volume = Math.Clamp(tVol, 0, 2);
            comp.BackgroundAudioTracks.Add(trk);
        }

        /// <summary>True if the clip is AV1 (MediaComposition can't decode it → editing must go through ffmpeg).</summary>
        public static bool IsAv1Clip(string name) { var p = Resolve(name); return p != null && Av1Transcoder.IsAv1(p); }

        /// <summary>Ensure a cached gallery poster (JPEG) for a clip. Returns the file name (in ThumbDir, served via
        /// clipthumb.local) or null on failure.</summary>
        public static async System.Threading.Tasks.Task<string?> EnsureThumbAsync(string name)
        {
            var p = Resolve(name); if (p == null) return null;
            return await ClipFfmpeg.EnsureThumbAsync(p);
        }

        /// <summary>Compose an AV1 source clip (trim + optional mic/chat mix) via ffmpeg → a new AV1 Game_…_clip.mp4.</summary>
        public static async System.Threading.Tasks.Task<string?> ComposeAv1Async(string name, double vidIn, double vidOut,
            double vidVol, bool micOn, double micIn, double micOut, double micOffset, double micVol,
            bool chatOn, double chatIn, double chatOut, double chatOffset, double chatVol, int kbps)
        {
            var src = Resolve(name); if (src == null) return null;
            string outName = UniqueName(Path.GetFileNameWithoutExtension(name) + "_clip.mp4", ClipRecorder.EditedDir);
            string outPath = Path.Combine(ClipRecorder.EditedDir, outName);
            string micWav = ClipRecorder.MicSidecar(src), chatWav = ClipRecorder.ChatSidecar(src);
            bool ok = await Av1Transcoder.ComposeAsync(src, micOn ? micWav : null, chatOn ? chatWav : null,
                vidIn, vidOut, vidVol, micOn, micIn, micOut, micOffset, micVol, chatOn, chatIn, chatOut, chatOffset, chatVol, outPath, kbps, ClipRecorder.Av1ForceSw);
            if (!ok) { TryDelete(outPath); return null; }   // remove the reserved placeholder / partial AV1 write
            return outName;
        }
        /// <summary>Combine several [in,out] ranges of one clip into a SINGLE new clip (Edited folder). Each range is
        /// composed (H.264 + its audio mix) to a temp, then they're concatenated in order. Returns the new name or null.</summary>
        public static async System.Threading.Tasks.Task<string?> CombineSegmentsAsync(
            string name, (double inS, double outS)[] ranges, double vidVol,
            bool micOn, double micIn, double micOut, double micOffset, double micVol,
            bool chatOn, double chatIn, double chatOut, double chatOffset, double chatVol)
        {
            var src = Resolve(name); if (src == null || ranges.Length == 0) return null;
            if (!await ClipFfmpeg.EnsureAsync()) return null;
            string outName = UniqueName(Path.GetFileNameWithoutExtension(name) + "_combined.mp4", ClipRecorder.EditedDir);
            string outPath = Path.Combine(ClipRecorder.EditedDir, outName);
            string micPath = ClipRecorder.MicSidecar(src), chatPath = ClipRecorder.ChatSidecar(src);
            string tmpDir = Path.Combine(Path.GetTempPath(), "SpartaClipsCombine"); try { Directory.CreateDirectory(tmpDir); } catch { }
            var parts = new List<string>();
            try
            {
                int i = 0;
                foreach (var (inS, outS) in ranges)
                {
                    if (outS - inS < 0.2) continue;
                    string part = Path.Combine(tmpDir, "seg_" + DateTime.Now.Ticks + "_" + (i++) + ".mp4");
                    // Compose each segment to uniform H.264 (+ its overlapping mic/chat mix) so they concat cleanly.
                    if (!await ClipFfmpeg.ComposeAsync(src, micOn ? micPath : null, chatOn ? chatPath : null,
                            inS, outS, vidVol, micOn, micIn, micOut, micOffset, micVol, chatOn, chatIn, chatOut, chatOffset, chatVol, part))
                    { TryDelete(outPath); return null; }
                    parts.Add(part);
                }
                if (parts.Count == 0 || !await ClipFfmpeg.ConcatMp4sAsync(parts, outPath)) { TryDelete(outPath); return null; }
                DiagLog.Line("CLIP", "combine (" + parts.Count + ") -> " + outName);
                return outName;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "combine err: " + ex.Message); TryDelete(outPath); return null; }
            finally { foreach (var p in parts) TryDelete(p); }
        }

        private static string UniqueName(string file) => UniqueName(file, ClipRecorder.ClipsDir);
        // RESERVES the name on disk (zero-byte placeholder via atomic CreateNew): the output name is picked minutes
        // before the render lands, so two concurrent exports of one clip used to compute the SAME name — and the
        // second's Finalize deleted + replaced the first (silent loss). ListJson hides 0-byte files, Finalize /
        // ReplaceExisting overwrite the placeholder, and every failure path TryDeletes it.
        private static string UniqueName(string file, string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
            string p = file;
            for (int n = 2; ; n++)
            {
                string full = Path.Combine(dir, p);
                try { using (new FileStream(full, FileMode.CreateNew)) { } return p; }
                catch (IOException) when (File.Exists(full)) { p = stem + "_" + n + ext; }   // taken → probe the next
                catch { return p; }   // unwritable dir etc. — fall back to the old non-reserving behavior
            }
        }

        /// <summary>Public resolver for the native player: clip name → full path inside the clips dir (or null).</summary>
        internal static string? PathOf(string name) => Resolve(name);

        // Resolve a caller-supplied name to a real file in the clips dir OR the Edited subfolder (reject any path
        // component → no traversal). Raw captures live in ClipsDir; editor exports live in EditedDir.
        private static string? Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) return null;
            string p = Path.Combine(ClipRecorder.ClipsDir, name); if (File.Exists(p)) return p;
            string e = Path.Combine(ClipRecorder.EditedDir, name); if (File.Exists(e)) return e;
            return null;
        }
        private static string SafeStem(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim()) if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ') sb.Append(c);
            return sb.ToString().Trim();
        }

        // ── Minimal MP4 reader: walk top-level boxes → moov → mvhd → timescale + duration. 0 on any miss. ──
        internal static double Mp4DurationSeconds(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long moov = FindBox(fs, 0, fs.Length, "moov", out long moovEnd);
                if (moov < 0) return 0;
                long mvhd = FindBox(fs, moov, moovEnd, "mvhd", out _);
                if (mvhd < 0) return 0;
                fs.Position = mvhd;
                int version = fs.ReadByte();   // mvhd content = version(1)+flags(3)+…
                if (version == 1)
                {
                    fs.Position = mvhd + 20; var b = new byte[12]; if (fs.Read(b, 0, 12) != 12) return 0;
                    uint ts = BE32(b, 0); ulong dur = BE64(b, 4); return ts == 0 ? 0 : (double)dur / ts;
                }
                else
                {
                    fs.Position = mvhd + 12; var b = new byte[8]; if (fs.Read(b, 0, 8) != 8) return 0;
                    uint ts = BE32(b, 0); uint dur = BE32(b, 4); return ts == 0 ? 0 : (double)dur / ts;
                }
            }
            catch { return 0; }
        }
        // Child box of fourcc within [start,end). Returns its CONTENT start (after the 8-byte header); contentEnd = box end.
        private static long FindBox(FileStream fs, long start, long end, string fourcc, out long contentEnd)
        {
            contentEnd = -1;
            long p = start; var hdr = new byte[8]; var big = new byte[8];
            while (p + 8 <= end)
            {
                fs.Position = p;
                if (fs.Read(hdr, 0, 8) != 8) break;
                long size = BE32(hdr, 0);
                string type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
                long head = 8, boxEnd;
                if (size == 0) boxEnd = end;   // "extends to end of file"
                else if (size == 1)            // 64-bit large-size — before: treated as end-of-file, so a moov AFTER a
                {                              // >4 GiB mdat was never found → duration 0 → the clip vanished from the gallery
                    if (fs.Read(big, 0, 8) != 8) break;
                    long ls = (long)BE64(big, 0);
                    if (ls < 16) break;
                    head = 16; boxEnd = p + ls;
                }
                else if (size < 8) break;
                else boxEnd = p + size;
                if (type == fourcc) { contentEnd = boxEnd; return p + head; }
                p = boxEnd;
            }
            return -1;
        }
        private static uint BE32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        private static ulong BE64(byte[] b, int o) { ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i]; return v; }
    }
}
