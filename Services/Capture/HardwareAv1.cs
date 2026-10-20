// Detects a hardware AV1 *encoder* on this PC via Media Foundation MFT enumeration — no ffmpeg download required —
// so the UI can grey out the AV1 encoder option on GPUs that can't do it (software AV1 is too slow to offer).
// Modern Win11 drivers (NVIDIA RTX 40+, Intel Arc, AMD RX 7000+) register an AV1 encoder MFT; we just count them.
// Result is cached after the first probe. Self-contained + deletable with the rest of the capture feature.
using System;
using System.Runtime.InteropServices;

namespace SpartaClips
{
    internal static class HardwareAv1
    {
        private static bool? _avail;
        public static bool Available => _avail ??= Detect();

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int Version, int dwFlags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll", PreserveSig = true)]
        private static extern int MFTEnumEx(Guid guidCategory, uint Flags,
            [In] MFT_REGISTER_TYPE_INFO[]? pInputType, [In] MFT_REGISTER_TYPE_INFO[]? pOutputType,
            out IntPtr pppMFTActivate, out uint pnumMFTActivate);

        private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
        private static readonly Guid MFMediaType_Video          = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_AV1          = new("31305641-0000-0010-8000-00aa00389b71"); // FCC('AV01')
        private const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
        private const int  MFSTARTUP_LITE = 1;

        private static bool Detect()
        {
            bool started = false;
            try
            {
                started = MFStartup(0x00020070, MFSTARTUP_LITE) == 0;   // balanced Startup/Shutdown — net-zero on the MF refcount
                var outType = new[] { new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_AV1 } };
                int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE, null, outType, out IntPtr arr, out uint count);
                if (hr != 0) return false;
                if (arr != IntPtr.Zero)
                {
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                        if (p != IntPtr.Zero) Marshal.Release(p);   // release each IMFActivate
                    }
                    Marshal.FreeCoTaskMem(arr);
                }
                bool ok = count > 0;
                DiagLog.Line("CLIP", "hardware AV1 encoder: " + (ok ? "yes (" + count + ")" : "no"));
                return ok;
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "av1 hw detect err: " + ex.Message); return false; }
            finally { if (started) { try { MFShutdown(); } catch { } } }
        }
    }
}
