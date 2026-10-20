// Per-APP audio capture (Windows 10 2004+): AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK captures ONLY one
// process tree's audio (e.g. Discord) — no virtual-cable driver, the rest of the system mix is untouched.
// Powers "Feel your audio → Source: <app>" so voice chat can play on the controller speaker while the game
// stays on the PC speakers (the PS5 party-chat experience). Delivers fixed-format PCM16 48 kHz stereo (process
// loopback has no mix format — the caller must dictate one; the engine converts). Throws on failure — callers
// fall back / retry. Interop follows Microsoft's ApplicationLoopback sample.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SpartaClips
{
    internal sealed class ProcessLoopbackCapture : IDisposable
    {
        public const int RATE = 48000, BYTES_PER_SAMPLE = 2;
        public readonly int CHANNELS;   // 2 (default) or the endpoint's native 6/8 — see the ctor note

        public event Action<byte[], int>? DataAvailable;   // PCM16 48 kHz interleaved, CHANNELS wide
        public event Action? Stopped;

        private readonly IAudioClient _client;
        private readonly IAudioCaptureClient _capture;
        private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
        private Thread? _thread;
        private volatile bool _run;
        private byte[] _buf = new byte[9600];   // 50 ms of 48k/16/2 — grown on demand

        // channels: process loopback converts the app's streams into whatever layout the caller asks for. Asking for
        // stereo on a 7.1 endpoint (e.g. SteelSeries Sonar "Gaming") runs Windows' normalized 8->2 downmix, which
        // costs a fixed ~13 dB on plain stereo game audio (measured). Asking for the endpoint's native 6/8 channels
        // gets FL/FR bit-exact; GameAudioCapture folds to stereo itself. 6 = 5.1 (0x3F), 8 = 7.1 (0x63F).
        public ProcessLoopbackCapture(int pid, int channels = 2)
        {
            CHANNELS = channels == 6 || channels == 8 ? channels : 2;
            // Activation params blob: { type = PROCESS_LOOPBACK(2), pid, mode = INCLUDE_TARGET_PROCESS_TREE(0) }
            IntPtr blob = Marshal.AllocHGlobal(12);
            IntPtr pv = Marshal.AllocHGlobal(24);
            IntPtr fmt = IntPtr.Zero;
            try
            {
                Marshal.WriteInt32(blob, 0, 1);   // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1 (NOT 2!)
                Marshal.WriteInt32(blob, 4, pid);
                Marshal.WriteInt32(blob, 8, 0);   // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
                for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 65);             // VT_BLOB
                Marshal.WriteInt32(pv, 8, 12);             // blob.cbSize
                Marshal.WriteInt64(pv, 16, blob.ToInt64()); // blob.pBlobData

                var handler = new Handler();
                Guid iid = typeof(IAudioClient).GUID;
                int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pv, handler, out _);
                if (hr < 0) throw new COMException("ActivateAudioInterfaceAsync", hr);
                if (!handler.Done.Wait(3000) || handler.Op == null) throw new TimeoutException("audio activation");
                handler.Op.GetActivateResult(out int ahr, out object unk);
                if (ahr < 0 || unk == null) throw new COMException("GetActivateResult", ahr);
                _client = (IAudioClient)unk;

                // WAVEFORMATEX: PCM16 / 48 kHz / stereo (process loopback requires a caller-chosen format).
                // WAVEFORMATEX (stereo) or WAVEFORMATEXTENSIBLE (5.1/7.1): PCM16 / 48 kHz — process loopback has no mix
                // format, so the caller dictates one and the engine converts.
                bool ext = CHANNELS > 2;
                fmt = Marshal.AllocHGlobal(40);
                for (int i = 0; i < 40; i++) Marshal.WriteByte(fmt, i, 0);
                Marshal.WriteInt16(fmt, 0, unchecked((short)(ext ? 0xFFFE : 1)));   // WAVE_FORMAT_EXTENSIBLE : PCM
                Marshal.WriteInt16(fmt, 2, (short)CHANNELS);
                Marshal.WriteInt32(fmt, 4, RATE);
                Marshal.WriteInt32(fmt, 8, RATE * CHANNELS * BYTES_PER_SAMPLE);
                Marshal.WriteInt16(fmt, 12, (short)(CHANNELS * BYTES_PER_SAMPLE));
                Marshal.WriteInt16(fmt, 14, BYTES_PER_SAMPLE * 8);
                Marshal.WriteInt16(fmt, 16, (short)(ext ? 22 : 0));                  // cbSize
                if (ext)
                {
                    Marshal.WriteInt16(fmt, 18, BYTES_PER_SAMPLE * 8);                   // wValidBitsPerSample
                    Marshal.WriteInt32(fmt, 20, CHANNELS == 8 ? 0x63F : 0x3F);           // dwChannelMask: 7.1 / 5.1
                    Marshal.Copy(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray(), 0, fmt + 24, 16);   // KSDATAFORMAT_SUBTYPE_PCM
                }
                const uint LOOPBACK = 0x00020000, EVENTCB = 0x00040000;
                hr = _client.Initialize(0 /*shared*/, LOOPBACK | EVENTCB, 200000 /*20 ms*/, 0, fmt, IntPtr.Zero);
                if (hr < 0) throw new COMException("IAudioClient.Initialize", hr);
                hr = _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0) throw new COMException("SetEventHandle", hr);
                Guid capIid = typeof(IAudioCaptureClient).GUID;
                hr = _client.GetService(ref capIid, out object svc);
                if (hr < 0 || svc == null) throw new COMException("GetService(IAudioCaptureClient)", hr);
                _capture = (IAudioCaptureClient)svc;
            }
            catch { _event.Dispose(); throw; }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
                Marshal.FreeHGlobal(pv);
                Marshal.FreeHGlobal(blob);
            }
        }

        public void Start()
        {
            if (_run) return;
            _run = true;
            int hr = _client.Start();
            if (hr < 0) { _run = false; throw new COMException("IAudioClient.Start", hr); }
            _thread = new Thread(Loop) { IsBackground = true, Name = "ProcLoopback" };
            _thread.Start();
        }

        private void Loop()
        {
            int frameBytes = CHANNELS * BYTES_PER_SAMPLE;
            while (_run)
            {
                try
                {
                    _event.WaitOne(200);
                    // Endpoint death surfaces as NEGATIVE HRESULTs, never exceptions ([PreserveSig] COM) — the old
                    // "catch { break; }" was unreachable, so after the target process exited this loop spun at 5 Hz
                    // forever and Stopped never fired. A failing HRESULT now ends the capture properly.
                    while (_run)
                    {
                        int hr = _capture.GetNextPacketSize(out uint next);
                        if (hr < 0) { _run = false; break; }   // endpoint died → stop the capture
                        if (next == 0) break;                  // drained → back to the outer event wait
                        if (_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) < 0) { _run = false; break; }
                        int bytes = (int)frames * frameBytes;
                        if (bytes > 0)
                        {
                            if (_buf.Length < bytes) _buf = new byte[bytes];
                            if ((flags & 2) != 0) Array.Clear(_buf, 0, bytes);   // AUDCLNT_BUFFERFLAGS_SILENT
                            else Marshal.Copy(data, _buf, 0, bytes);
                            try { DataAvailable?.Invoke(_buf, bytes); } catch { }
                        }
                        _capture.ReleaseBuffer(frames);
                    }
                }
                catch { break; }   // defensive only — the COM calls return HRESULTs rather than throwing
            }
            _run = false;
            try { Stopped?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            _run = false;
            try { _event.Set(); } catch { }
            try { _thread?.Join(500); } catch { }   // loop thread is gone before we touch the COM objects below
            try { _client.Stop(); } catch { }
            try { _client.Reset(); } catch { }
            // Release the WASAPI RCWs explicitly: without this, each per-app source switch (rebuild) leaked an
            // IAudioClient + IAudioCaptureClient + its capture session, eventually starving audio until restart.
            try { Marshal.FinalReleaseComObject(_capture); } catch { }
            try { Marshal.FinalReleaseComObject(_client); } catch { }
            try { _event.Dispose(); } catch { }
        }

        // ── COM interop (exact vtable order — do not reorder) ───────────────
        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long def, out long min);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object unk);
        }

        private sealed class Handler : IActivateAudioInterfaceCompletionHandler
        {
            public readonly ManualResetEventSlim Done = new();
            public IActivateAudioInterfaceAsyncOperation? Op;
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Op = op; Done.Set(); }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);
    }
}
