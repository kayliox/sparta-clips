// Capture-source picker backend — enumerate visible top-level windows with live preview thumbnails (JSON for the
// title-bar picker UI). Called off-thread from BridgeApi.RequestCaptureWindows (PrintWindow per window isn't free).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace SpartaClips
{
    internal static class CaptureSources
    {
        // [{ hwnd:"0x…", title, proc, w, h, thumb:"data:image/jpeg;base64,…" }] in z-order (topmost first).
        public static string ListWindowsJson()
        {
            var list = new List<object>();
            try
            {
                uint ownPid = (uint)Environment.ProcessId;
                EnumWindows((h, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(h) || IsIconic(h)) return true;                       // minimized can't be WGC-captured
                        if (((long)GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                        int cloaked = 0; DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4);  // suspended UWP / other virtual desktops
                        if (cloaked != 0) return true;
                        var sb = new StringBuilder(256);
                        if (GetWindowText(h, sb, sb.Capacity) <= 0) return true;
                        string title = sb.ToString().Trim(); if (title.Length == 0) return true;
                        var cls = new StringBuilder(64); GetClassName(h, cls, cls.Capacity);
                        string c = cls.ToString();
                        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return true; // desktop/taskbar shells
                        GetWindowThreadProcessId(h, out uint pid);
                        if (pid == ownPid) return true;                                            // never offer our own UI
                        GetWindowRect(h, out RECT r);
                        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                        if (w < 120 || hh < 90) return true;                                       // splash slivers / widgets
                        string proc = "";
                        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
                        // Never PrintWindow a hung window — it's a synchronous cross-process call with no timeout, and
                        // one not-responding app would freeze this whole enumeration (the picker then never populates).
                        string thumb = IsHungAppWindow(h) ? "" : Thumb(h, w, hh);
                        list.Add(new { hwnd = ((long)h).ToString(), title, proc, w, h = hh, thumb });
                    }
                    catch { }
                    return list.Count < 40;   // sane ceiling — nobody scrolls 40+ windows in a picker
                }, IntPtr.Zero);
            }
            catch (Exception ex) { DiagLog.Line("CLIP", "list windows err: " + ex.Message); }
            return Newtonsoft.Json.JsonConvert.SerializeObject(list);
        }

        // PrintWindow → ~300px JPEG data URI ("" on failure — the UI shows a letterbox placeholder instead).
        // PW_RENDERFULLCONTENT captures DirectX/DComp-composed content (browsers, games) that plain PrintWindow misses.
        private static string Thumb(IntPtr hwnd, int w, int h)
        {
            try
            {
                using var full = new Bitmap(w, h);
                using (var g = Graphics.FromImage(full))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);
                    if (!ok) return "";
                }
                int tw = 300, th = Math.Max(1, (int)Math.Round(300.0 * h / w));
                using var small = new Bitmap(full, tw, th);
                using var ms = new System.IO.MemoryStream();
                var enc = GetJpeg(); if (enc == null) { small.Save(ms, ImageFormat.Png); }
                else
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                    small.Save(ms, enc, ep);
                }
                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch { return ""; }
        }
        private static ImageCodecInfo? GetJpeg()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    }
}
