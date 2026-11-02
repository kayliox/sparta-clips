// ClipFolderWatcher — keeps the gallery in sync with changes made OUTSIDE the app (a clip deleted, renamed or copied
// in through Explorer). before: the list only refreshed after the app's own actions, so a clip deleted by hand stayed
// in the gallery until the next save/restart. Watches ClipsDir (+ the Edited subfolder) for .mp4 and its .wav
// sidecars and tells the open window via TrayCore.WebSink ("library"); the page then re-requests the list.
// Partial files are harmless: ClipLibrary.ListJson skips anything without a readable duration.
using System;
using System.IO;
using System.Threading;

namespace SpartaClips
{
    public static class ClipFolderWatcher
    {
        private static readonly object _lock = new();
        private static FileSystemWatcher? _w;
        private static string _dir = "";
        private static Timer? _debounce;

        /// <summary>Watch the current clips folder (idempotent; re-targets when the folder setting changed).</summary>
        public static void Ensure()
        {
            string dir; try { dir = ClipRecorder.PeekClipsDir; } catch { return; }
            lock (_lock)
            {
                if (_w != null && string.Equals(_dir, dir, StringComparison.OrdinalIgnoreCase)) return;
                try { _w?.Dispose(); } catch { }
                _w = null; _dir = dir;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;   // Peek never creates it; a later Ensure retries
                try
                {
                    var w = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,   // Edited\ lives inside ClipsDir
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 64 * 1024,
                    };
                    w.Created += (_, e) => Hit(e.FullPath, 1500);   // a copy-in finishes after Created: refresh once it's likely done
                    w.Deleted += (_, e) => Hit(e.FullPath, 350);
                    w.Renamed += (_, e) => { Hit(e.OldFullPath, 350); Hit(e.FullPath, 350); };
                    w.Error   += (_, _) => Kick(350);               // buffer overflow → just refresh
                    w.EnableRaisingEvents = true;
                    _w = w;
                }
                catch (Exception ex) { DiagLog.Line("CLIP", "folder watch failed: " + ex.Message); }
            }
        }

        private static void Hit(string path, int delayMs)
        {
            string ext = Path.GetExtension(path);
            // .mp4 clips, .wav mic/chat sidecars, and folders (deleting Edited\ removes every export at once)
            if (ext.Length == 0 || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                Kick(delayMs);
        }

        // Trailing debounce: a burst (bulk delete, folder drag) becomes one refresh.
        private static void Kick(int delayMs)
        {
            lock (_lock)
            {
                _debounce ??= new Timer(_ => { try { TrayCore.WebSink?.Invoke("library", "{}"); } catch { } });
                _debounce.Change(delayMs, Timeout.Infinite);
            }
        }
    }
}
