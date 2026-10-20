// Lightweight diagnostic logger — opt-in file log for capturing the Remote Play launch/embed/promote
// sequence (and later the Rocket League controller state). SELF-CONTAINED and disposable: delete this
// file plus the `DiagLog.*` call sites and nothing else changes. Writes to
// %APPDATA%\SpartaClips\logs\spartaclips.log (rotates at ~2 MB, keeps one .old).
using System;
using System.IO;
using System.Text;

namespace SpartaClips
{
    internal static class DiagLog
    {
        public static volatile bool On = true;   // diagnostic build — default ON; toggle from the debug tab
        private static readonly object _gate = new();
        private const long MaxBytes = 2 * 1024 * 1024;
        // Keep ONE appending writer open (flushed per line) instead of CreateDirectory + rotation-stat + open/close per
        // line — bursts (a gallery load spawns dozens of probe logs) were serializing behind file-handle churn.
        private static StreamWriter? _w;
        private static long _written;            // bytes since open, added to the open-time length for cheap rotation checks

        public static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpartaClips", "logs");
        public static string FilePath => Path.Combine(Dir, "spartaclips.log");

        public static void Line(string tag, string msg) { if (On) Write(tag, msg); }

        /// <summary>Always writes, even when diagnostics are toggled OFF — for crashes / critical events that
        /// must be in the log a tester sends regardless of the On switch.</summary>
        public static void Crash(string tag, string msg) => Write(tag, msg);

        private static void Write(string tag, string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + tag + "]  " + msg;
                lock (_gate)
                {
                    if (_w != null && _written > MaxBytes) CloseWriter();
                    if (_w == null)
                    {
                        Directory.CreateDirectory(Dir);
                        string f = FilePath;
                        try { if (File.Exists(f) && new FileInfo(f).Length > MaxBytes) { string old = f + ".old"; if (File.Exists(old)) File.Delete(old); File.Move(f, old); } } catch { }
                        long start = 0; try { if (File.Exists(f)) start = new FileInfo(f).Length; } catch { }
                        _w = new StreamWriter(new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
                        _written = start;
                    }
                    _w.WriteLine(line); _w.Flush();
                    _written += line.Length + 2;
                }
            }
            catch { try { CloseWriter(); } catch { } }
        }
        private static void CloseWriter() { try { _w?.Flush(); _w?.Dispose(); } catch { } _w = null; _written = 0; }

        /// <summary>Section marker (boot, and the start of each Remote Play launch) so captures are easy to find.</summary>
        public static void Mark(string what) => Line("====", what + "  ·  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        public static void Clear() { try { lock (_gate) { CloseWriter(); if (File.Exists(FilePath)) File.Delete(FilePath); } } catch { } }
    }
}
