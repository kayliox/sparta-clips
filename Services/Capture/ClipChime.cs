// Short confirmation chime played NATIVELY (NAudio → default device) the instant an instant-replay is captured,
// so the user gets feedback even when the launcher is tray-hidden behind a fullscreen game. Synthesized in code
// (no audio asset to ship). Deletable with the rest of the capture feature.
using System;
using System.IO;
using NAudio.Wave;

namespace SpartaClips
{
    internal static class ClipChime
    {
        public static void Play()
        {
            try
            {
                var ms = new MemoryStream(BuildChime());
                var rdr = new RawSourceWaveStream(ms, new WaveFormat(44100, 16, 1));
                var wo = new WaveOutEvent();
                wo.Init(rdr);
                wo.PlaybackStopped += (s, e) => { try { wo.Dispose(); } catch { } try { rdr.Dispose(); } catch { } try { ms.Dispose(); } catch { } };
                wo.Play();
            }
            catch { }
        }

        // A God-of-War-style choral stab: an A-minor chord spread bass→soprano (A2·E3·A3·C4·E4·A4), each note layered
        // with three slightly detuned voices for a "many singers" choir shimmer + low harmonics for vowel body.
        // A soft swell-in, a gentle tremolo "breath", and a long epic release. ~1.3 s, normalized to avoid clipping.
        private static byte[] BuildChime()
        {
            const int SR = 44100;
            int len = 1300 * SR / 1000;
            var buf = new byte[len * 2];
            double[] notes = { 110.0, 164.81, 220.0, 261.63, 329.63, 440.0 };   // A minor across octaves
            double[] detune = { 1.0, 1.008, 0.992 };                            // 3 voices per note (chorus)
            int swell = SR * 90 / 1000, rel = SR * 600 / 1000;
            double norm = notes.Length * detune.Length * 1.6;
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / SR;
                double env = i < swell ? (double)i / swell : i > len - rel ? Math.Max(0, (double)(len - i) / rel) : 1.0;
                env *= 1.0 + 0.06 * Math.Sin(2 * Math.PI * 5.5 * t);   // slow tremolo = choir "breath"
                double s = 0;
                foreach (var f0 in notes)
                    foreach (var d in detune)
                    {
                        double w = 2 * Math.PI * f0 * d * t;
                        s += Math.Sin(w) + 0.4 * Math.Sin(2 * w) + 0.2 * Math.Sin(3 * w);   // vowel-ish voice
                    }
                int v = (int)(s / norm * 0.72 * env * 32767);
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                buf[i * 2] = (byte)(v & 0xFF); buf[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return buf;
        }
    }
}
