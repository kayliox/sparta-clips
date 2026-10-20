// Startup housekeeping for the %TEMP%\SpartaClips* work dirs. Clears the transient intermediates left by a previous
// (possibly crashed) session and age-prunes the reusable caches, so TEMP doesn't grow without bound. Two safety rules:
// 1) only files from BEFORE this launch are touched (the sweep runs concurrently with the replay ring arming — a
//    fresh seg_*.mp4 must never be deleted); 2) SpartaClipsAv1 is SALVAGED, not wiped — an interrupted AV1 transcode
//    leaves the H.264 intermediate there as the ONLY copy of the footage, so it's moved into the clips folder.
// Best-effort, off-thread, never throws — files another process still holds are simply skipped.
using System;
using System.IO;

namespace SpartaClips
{
    internal static class TempMaintenance
    {
        // Pure intermediates — never reused across sessions; stale entries (older than this launch) are deleted.
        private static readonly string[] Transient =
            { "SpartaClipsReplay", "SpartaClipsShare", "SpartaClipsCombine", "SpartaClipsClip" };
        // Caches keyed by source stem + mtime (thumbnails, scrub/HEVC proxies) — keep recent entries warm, drop only
        // stale ones left behind for long-deleted clips.
        private static readonly string[] Caches = { "SpartaClipsThumb", "SpartaClipsProxy" };

        public static void SweepStartup()
        {
            try
            {
                var launch = DateTime.UtcNow.AddSeconds(-30);   // anything written before ~this launch is a leftover
                string tmp = Path.GetTempPath();
                SalvageAv1(Path.Combine(tmp, "SpartaClipsAv1"), launch);
                foreach (var name in Transient) DeleteOlderThan(Path.Combine(tmp, name), launch);
                var cutoff = DateTime.UtcNow.AddDays(-14);
                foreach (var name in Caches) DeleteOlderThan(Path.Combine(tmp, name), cutoff);
            }
            catch { }
        }

        // An mp4 here means a recording whose AV1 transcode never finished (crash / quit mid-encode) — the temp is the
        // only copy of that footage. Move it into the clips folder as an H.264 recovery instead of deleting it.
        private static void SalvageAv1(string dir, DateTime cutoffUtc)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) >= cutoffUtc) continue;   // could belong to THIS session
                        if (Path.GetExtension(f).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 0)
                        {
                            string dest = Path.Combine(ClipRecorder.ClipsDir,
                                Path.GetFileNameWithoutExtension(f) + "_recovered.mp4");
                            if (!File.Exists(dest)) { File.Move(f, dest); DiagLog.Line("TEMP", "salvaged interrupted AV1 intermediate -> " + Path.GetFileName(dest)); continue; }
                        }
                        File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void DeleteOlderThan(string dir, DateTime cutoffUtc)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                { try { if (File.GetLastWriteTimeUtc(f) < cutoffUtc) File.Delete(f); } catch { } }
            }
            catch { }
        }
    }
}
