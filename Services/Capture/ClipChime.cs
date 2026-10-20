// "Clip captured" sound, played NATIVELY (NAudio → default device) the instant the save-replay hotkey lands, so the
// user gets feedback even when the app is tray-only behind a fullscreen game. The sound ("Neon Charge" — a synthwave
// chord charging up into a metallic shing + sub hit) is synthesized offline by tools/make_chime.py into chime.wav and
// EMBEDDED in the assembly, so it plays bit-identical everywhere. before: an in-code 1.3 s A-minor choir stab.
using System;
using System.IO;
using System.Reflection;
using NAudio.Wave;

namespace SpartaClips
{
    internal static class ClipChime
    {
        private static byte[]? _wav;   // the embedded WAV, read once

        public static void Play()
        {
            try
            {
                _wav ??= Load();
                if (_wav == null) return;
                var ms = new MemoryStream(_wav, writable: false);
                var rdr = new WaveFileReader(ms);
                var wo = new WaveOutEvent();
                wo.Init(rdr);
                wo.PlaybackStopped += (s, e) => { try { wo.Dispose(); } catch { } try { rdr.Dispose(); } catch { } try { ms.Dispose(); } catch { } };
                wo.Play();
            }
            catch { }
        }

        private static byte[]? Load()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SpartaClips.chime.wav");
            if (s == null) return null;
            using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray();
        }
    }
}
