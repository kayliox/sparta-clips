// One device-bound WASAPI audio capture that feeds either a sidecar WAV (full recordings) or an in-memory PCM16
// ring buffer (instant replay). Powers BOTH the separate microphone track and the separate "chat" track — many
// headsets route voice-chat audio to its own output device (to hear Discord apart from the game), which we capture
// here via OUTPUT loopback. Float→PCM16 conversion; the device is resolved live by id ("" = system default) so a
// settings change applies on the next capture. Self-contained; deletable with the capture feature.
using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpartaClips
{
    internal sealed class AudioSidecar
    {
        private readonly Func<string> _deviceId = () => "";   // current device id ("" = default); read at start so changes apply
        private readonly bool _loopback;            // true = capture an OUTPUT device's mix (chat); false = a mic input
        private readonly string _tag;               // DiagLog label ("mic" / "chat" / "game")
        private readonly Func<IWaveIn?>? _factory;  // custom source (the per-app game capture); null result = nothing to capture

        public AudioSidecar(Func<string> deviceId, bool loopback, string tag) { _deviceId = deviceId; _loopback = loopback; _tag = tag; }
        public AudioSidecar(Func<IWaveIn?> factory, string tag) { _factory = factory; _tag = tag; }

        // Surface a capture-start failure (e.g. Windows mic-privacy blocked) to the user. Wired by ClipRecorder → Notify.
        public static Action<string>? OnFail;

        private IWaveIn? MakeCapture()
        {
            if (_factory != null) return _factory();
            string id = _deviceId() ?? "";
            // "app:<process>" = capture ONE app's audio (e.g. Discord voice) per-app instead of an output device:
            // immune to that device's volume slider (SteelSeries Sonar "Chat" at 13% recorded ~-25 dB). null = the
            // app isn't running → no chat track this time (retried when the next replay segment starts).
            if (_loopback && id.StartsWith("app:", StringComparison.OrdinalIgnoreCase)) return MakeAppCapture(id.Substring(4));
            try
            {
                if (!string.IsNullOrEmpty(id))
                {
                    var dev = new MMDeviceEnumerator().GetDevice(id);
                    if (dev != null) return _loopback ? new WasapiLoopbackCapture(dev) : (IWaveIn)new WasapiCapture(dev);
                }
            }
            catch (Exception ex) { DiagLog.Line("CLIP", _tag + " device unavailable, using default: " + ex.Message); }
            return _loopback ? new WasapiLoopbackCapture() : (IWaveIn)new WasapiCapture();
        }

        private bool _appMissingLogged;
        private IWaveIn? MakeAppCapture(string proc)
        {
            int pid = RootPid(proc);
            if (pid <= 0) { if (!_appMissingLogged) DiagLog.Line("CLIP", _tag + ": " + proc + " isn't running — no " + _tag + " track"); _appMissingLogged = true; return null; }
            _appMissingLogged = false;
            try { return new GameAudioCapture(pid, _tag); }
            catch (Exception ex) { DiagLog.Line("CLIP", _tag + " app capture failed: " + ex.Message); return null; }
        }
        // Electron apps (Discord) run many same-named processes; the FIRST one started is the root of the tree, and
        // process loopback includes its whole tree (the voice engine lives in a child).
        private static int RootPid(string proc)
        {
            int pid = 0; DateTime first = DateTime.MaxValue;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
                using (p) { try { if (p.StartTime < first) { first = p.StartTime; pid = p.Id; } } catch { } }
            return pid;
        }

        // ── Sidecar WAV (full recording) ──
        private IWaveIn? _cap;
        private WaveFileWriter? _writer;
        private string _path = "";
        private readonly object _lock = new object();

        public void StartSidecar(string wavPath)
        {
            StopSidecar(false);   // never leave a previous capture/writer running — two captures into one writer doubled the audio
            try
            {
                _path = wavPath;
                _cap = MakeCapture();
                if (_cap == null) { _path = ""; return; }   // custom source has nothing to capture (e.g. no game window)
                var fmt = _cap.WaveFormat;
                lock (_lock) { _writer = new WaveFileWriter(_path, new WaveFormat(fmt.SampleRate, 16, fmt.Channels)); }
                _cap.DataAvailable += (s, a) =>
                {
                    lock (_lock)
                    {
                        if (_writer == null) return;
                        try { WritePcm16(fmt, a.Buffer, a.BytesRecorded, b => _writer!.WriteByte(b), (buf, off, cnt) => _writer!.Write(buf, off, cnt)); } catch { }
                    }
                };
                var cap = _cap;   // each capture's stop disposes only ITSELF — never a newer capture (see StartRing)
                cap.RecordingStopped += (s, a) => { System.Threading.Interlocked.CompareExchange(ref _cap, null, cap); try { cap.Dispose(); } catch { } };
                cap.StartRecording();
                DiagLog.Line("CLIP", _tag + " sidecar -> " + Path.GetFileName(_path));
            }
            catch (Exception ex) { DiagLog.Line("CLIP", _tag + " sidecar err: " + ex.Message); try { OnFail?.Invoke(_tag); } catch { } StopSidecar(false); }
        }

        public void StopSidecar(bool keep)
        {
            if (_cap == null && _writer == null) return;
            try { _cap?.StopRecording(); } catch { }
            lock (_lock) { try { _writer?.Dispose(); } catch { } _writer = null; }   // finalize the WAV header now
            if (!keep && !string.IsNullOrEmpty(_path)) { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }
            _path = "";   // RecordingStopped disposes the capture device
        }

        // ── Ring buffer (instant replay) ──
        private IWaveIn? _ringCap;
        private byte[]? _ring;
        private int _ringPos, _ringFilled, _rate, _ch;
        private readonly object _ringLock = new object();
        /// <summary>True while a replay ring is capturing — for the engine diagnostics snapshot.</summary>
        public bool RingActive { get { lock (_ringLock) return _ring != null; } }

        public void StartRing(int seconds)
        {
            StopRing();   // never leave a previous capture running — two captures feeding one ring doubled the audio
            try
            {
                var cap = MakeCapture();
                _ringCap = cap;
                if (cap == null) return;
                var fmt = cap.WaveFormat;
                _rate = fmt.SampleRate; _ch = fmt.Channels;
                lock (_ringLock) { _ring = new byte[Math.Max(1, _rate * _ch * 2 * Math.Max(seconds, 1))]; _ringPos = 0; _ringFilled = 0; }
                cap.DataAvailable += (s, a) =>
                {
                    lock (_ringLock)
                    {
                        if (_ring == null || !ReferenceEquals(_ringCap, cap)) return;   // a stopping capture's late data stays out of the new ring
                        WritePcm16(fmt, a.Buffer, a.BytesRecorded, RingByte, (buf, off, cnt) => { for (int i = 0; i < cnt; i++) RingByte(buf[off + i]); });
                    }
                };
                // before: "var c = _ringCap; _ringCap = null; c.Dispose()" — when the OLD capture's stop arrived after
                // StartRing had already installed a NEW one, it disposed the new capture while it was starting.
                cap.RecordingStopped += (s, a) => { System.Threading.Interlocked.CompareExchange(ref _ringCap, null, cap); try { cap.Dispose(); } catch { } };
                cap.StartRecording();
                DiagLog.Line("CLIP", _tag + " ring on (" + seconds + "s)");
            }
            catch (Exception ex) { DiagLog.Line("CLIP", _tag + " ring err: " + ex.Message); StopRing(); }
        }
        private void RingByte(byte b) { _ring![_ringPos] = b; _ringPos = (_ringPos + 1) % _ring.Length; if (_ringFilled < _ring.Length) _ringFilled++; }
        public void StopRing()
        {
            try { _ringCap?.StopRecording(); } catch { }
            lock (_ringLock) { _ring = null; _ringFilled = 0; _ringPos = 0; }
        }
        public void DumpRing(string outWav, int seconds)
        {
            byte[] data; int rate, ch;
            lock (_ringLock)
            {
                if (_ring == null || _ringFilled == 0) return;
                rate = _rate; ch = _ch; int frame = ch * 2;
                int want = Math.Min(_ringFilled, rate * frame * Math.Max(seconds, 1)); want -= want % frame;
                if (want <= 0) return;
                data = new byte[want];
                int start = ((_ringPos - want) % _ring.Length + _ring.Length) % _ring.Length;
                // The unwrap is at most two contiguous runs — before: a per-byte modulo loop (~115 MB at replayLen
                // 600) held _ringLock long enough to starve the WASAPI DataAvailable callback (audible gap at save).
                int first = Math.Min(want, _ring.Length - start);
                Buffer.BlockCopy(_ring, start, data, 0, first);
                if (want > first) Buffer.BlockCopy(_ring, 0, data, first, want - first);
            }
            try { using var w = new WaveFileWriter(outWav, new WaveFormat(rate, 16, ch)); w.Write(data, 0, data.Length); DiagLog.Line("CLIP", _tag + " -> " + Path.GetFileName(outWav)); }
            catch (Exception ex) { DiagLog.Line("CLIP", _tag + " dump err: " + ex.Message); }
        }

        // WASAPI shared-mode hands us 32-bit float; the editor + Media Foundation want PCM16. Convert per-sample,
        // else write through (already PCM). writeByte gets PCM16 little-endian bytes; writeRun passes PCM straight on.
        private static void WritePcm16(WaveFormat fmt, byte[] buf, int count, Action<byte> writeByte, Action<byte[], int, int> writeRun)
        {
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
                for (int i = 0; i + 4 <= count; i += 4)
                {
                    int v = (int)Math.Round(BitConverter.ToSingle(buf, i) * 32767f);
                    if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                    writeByte((byte)(v & 0xFF)); writeByte((byte)((v >> 8) & 0xFF));
                }
            else writeRun(buf, 0, count);
        }
    }
}
