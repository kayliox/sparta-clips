// Per-APP game audio as an NAudio IWaveIn, so AudioSidecar's ring buffer (instant replay) and sidecar WAV (full
// recordings) can capture ONLY the game process tree instead of the default output device's mix.
//
// Why: endpoint loopback (what ScreenRecorderLib records) sits AFTER the endpoint's own volume on virtual devices
// such as SteelSeries Sonar — a "Gaming" channel at 5% recorded game audio ~44 dB down (near silence) even though the
// user heard it fine through Sonar's output. Process loopback taps the app's streams before that point: measured
// level-identical at 5% / 30% / 100% endpoint volume. It is captured in the endpoint's native channel count (see
// ProcessLoopbackCapture) and folded to stereo here, so plain stereo game audio comes through bit-exact.
// Delivers PCM16 / 48 kHz / stereo. Self-contained; deletable with the capture feature.
using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpartaClips
{
    internal sealed class GameAudioCapture : IWaveIn
    {
        private readonly ProcessLoopbackCapture _pl;
        private readonly int _ch;
        private byte[] _out = new byte[9600];
        private int _stopped;

        public WaveFormat WaveFormat { get; set; } = new WaveFormat(ProcessLoopbackCapture.RATE, 16, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        /// <summary>Throws if per-app capture is unavailable (Windows older than 10 2004, process gone, …).</summary>
        public GameAudioCapture(int pid, string label = "game")
        {
            int want = EndpointChannels();
            ProcessLoopbackCapture pl;
            try { pl = new ProcessLoopbackCapture(pid, want); }
            catch when (want != 2) { pl = new ProcessLoopbackCapture(pid, 2); }   // multichannel refused → stereo still works (just Windows' downmix)
            _pl = pl; _ch = pl.CHANNELS;
            _pl.DataAvailable += OnData;
            _pl.Stopped += () => { if (System.Threading.Interlocked.Exchange(ref _stopped, 1) == 0) RecordingStopped?.Invoke(this, new StoppedEventArgs()); };
            DiagLog.Line("CLIP", label + " audio: per-app capture pid " + pid + " (" + _ch + "ch" + (_ch != want ? ", wanted " + want : "") + ")");
        }

        // Channel layout of the default output device (where the game plays unless the user re-routed it per-app).
        private static int EndpointChannels()
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                int c = dev.AudioClient.MixFormat.Channels;
                return c == 6 || c == 8 ? c : 2;
            }
            catch { return 2; }
        }

        public void StartRecording() => _pl.Start();
        public void StopRecording() { try { _pl.Dispose(); } catch { } }   // Dispose ends the loop → Stopped → RecordingStopped (once)
        public void Dispose() => StopRecording();

        private void OnData(byte[] buf, int bytes)
        {
            if (_ch == 2) { DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes)); return; }
            // 5.1 / 7.1 → stereo, UN-normalized: FL/FR at unity (plain stereo games stay bit-exact), centre and
            // surrounds at -3 dB, LFE dropped; clamp guards true-surround peaks.
            int frames = bytes / (_ch * 2), outBytes = frames * 4;
            if (_out.Length < outBytes) _out = new byte[outBytes];
            for (int f = 0; f < frames; f++)
            {
                int o = f * _ch * 2;
                int S(int c) => (short)(buf[o + c * 2] | (buf[o + c * 2 + 1] << 8));
                // 5.1 (0x3F): FL FR FC LFE BL BR · 7.1 (0x63F): FL FR FC LFE BL BR SL SR
                double c3 = 0.7071 * S(2);
                double l = S(0) + c3 + 0.7071 * S(4), r = S(1) + c3 + 0.7071 * S(5);
                if (_ch == 8) { l += 0.7071 * S(6); r += 0.7071 * S(7); }
                short L = (short)Math.Clamp((int)Math.Round(l), short.MinValue, short.MaxValue);
                short R = (short)Math.Clamp((int)Math.Round(r), short.MinValue, short.MaxValue);
                int w = f * 4;
                _out[w] = (byte)L; _out[w + 1] = (byte)(L >> 8); _out[w + 2] = (byte)R; _out[w + 3] = (byte)(R >> 8);
            }
            DataAvailable?.Invoke(this, new WaveInEventArgs(_out, outBytes));
        }
    }
}
