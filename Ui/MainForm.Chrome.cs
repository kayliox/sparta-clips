// MainForm (partial) — frameless custom window chrome for the 2026 redesign. The default OS title bar + border is
// removed (FormBorderStyle.None, set in the ctor) yet the window KEEPS the native behaviours users expect: Aero Snap,
// Windows 11 Snap Layouts, the drop shadow, resize and the maximize animation. That's achieved the standard way —
// re-add WS_THICKFRAME|WS_CAPTION in CreateParams (so DWM still manages the window) and strip only the *visual* frame
// in WM_NCCALCSIZE. The HTML title bar is the caption: dragging via CSS `app-region: drag` (WebView2
// IsNonClientRegionSupportEnabled) with a JS→WM_NCLBUTTONDOWN fallback, min/max/close via the BridgeApi.Win* methods.
// Resize edges are re-added in WM_NCHITTEST; rounded corners + shadow come from DWM. before: standard Sizable frame.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpartaClips
{
    public partial class MainForm
    {
        // Window styles re-added so DWM keeps snap/shadow/resize even though FormBorderStyle is None.
        private const int WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_THICKFRAME = 0x00040000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
        private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024, WM_NCLBUTTONDOWN = 0x00A1, WM_SIZE = 0x0005;
        private const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int SM_CXFRAME = 32, SM_CYFRAME = 33, SM_CXPADDEDBORDER = 92;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int RESIZE = 6;   // grab thickness for the (invisible) resize border, px

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int idx);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int l, r, t, b; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr lppos; }

        // Keep DWM-managed frame styles so Snap Layouts / Aero Snap / shadow / resize survive the borderless look.
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME | WS_CAPTION | WS_SYSMENU; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11 rounded corners + a 1px frame extension so DWM paints the drop shadow on the borderless window.
            try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }
            // Pin the Win11 border colour to the app surface (COLORREF 0x00BBGGRR of --bg #0d0d10). Otherwise it follows
            // the system accent (red here) and flashes a coloured outline as DWM repaints the border while the WebView
            // lags behind a fast resize.
            try { int border = 0x00100D0D; DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int)); } catch { }
            try { var m = new MARGINS { l = 0, t = 0, r = 0, b = 1 }; DwmExtendFrameIntoClientArea(Handle, ref m); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                    // Strip the standard frame → the whole window becomes client area (no OS title bar/border). When
                    // maximized, inset by the frame: Windows inflates a THICKFRAME maximize by the frame on every side
                    // (the MINMAXINFO clamp does NOT prevent that — verified empirically via the CDP smoke test:
                    // without this inset the client measured work-area+16px, bleeding 8px offscreen on each edge).
                    // With it, client == work area exactly, so screen-edge pixels ARE the HTML buttons (Fitts-safe).
                    if (WindowState == FormWindowState.Maximized)
                    {
                        var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                        int fx = GetSystemMetrics(SM_CXFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
                        int fy = GetSystemMetrics(SM_CYFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
                        p.r0.left += fx; p.r0.top += fy; p.r0.right -= fx; p.r0.bottom -= fy;
                        Marshal.StructureToPtr(p, m.LParam, false);
                    }
                    m.Result = IntPtr.Zero;
                    return;

                case WM_GETMINMAXINFO:
                    ClampMaxToWorkArea(m.LParam);   // keep a maximized borderless window off the taskbar
                    base.WndProc(ref m);
                    return;

                case WM_NCHITTEST:
                    base.WndProc(ref m);
                    // The stripped frame removed the resize borders — re-add them at the window edges. Caption dragging
                    // is handled by the HTML title bar (app-region / JS bridge), so we only need the resize edges here.
                    if ((int)m.Result == HTCLIENT) m.Result = (IntPtr)ResizeHitTest();
                    return;

                case WM_SIZE:
                    base.WndProc(ref m);
                    PostWinStateIfChanged();   // keep the HTML maximize↔restore glyph in step (only on actual state flips — a drag-resize fires WM_SIZE continuously)
                    return;

                case WM_ENTERSIZEMOVE:
                case WM_EXITSIZEMOVE:
                    // Timeline anchors for the drag-resize diagnostics (renderer stalls log via BridgeApi.LogDiag).
                    DiagLog.Line("WIN", (m.Msg == WM_ENTERSIZEMOVE ? "enter" : "exit") + " sizemove  bounds=" + Bounds);
                    base.WndProc(ref m);
                    return;
            }
            base.WndProc(ref m);
        }

        private int ResizeHitTest()
        {
            if (WindowState == FormWindowState.Maximized) return HTCLIENT;
            var p = PointToClient(Cursor.Position);
            int w = ClientSize.Width, h = ClientSize.Height;
            bool l = p.X <= RESIZE, r = p.X >= w - RESIZE, t = p.Y <= RESIZE, b = p.Y >= h - RESIZE;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (b && l) return HTBOTTOMLEFT;
            if (b && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (b) return HTBOTTOM;
            return HTCLIENT;
        }

        private void ClampMaxToWorkArea(IntPtr lParam)
        {
            try
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                var hMon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                if (!GetMonitorInfo(hMon, ref mi)) return;
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                RECT work = mi.rcWork, mon = mi.rcMonitor;
                mmi.MaxPosition = new Point(work.left - mon.left, work.top - mon.top);
                mmi.MaxSize = new Point(work.right - work.left, work.bottom - work.top);
                mmi.MinTrackSize = new Point(MinimumSize.Width, MinimumSize.Height);
                Marshal.StructureToPtr(mmi, lParam, false);
            }
            catch { }
        }

        // ── Entry points for the frameless HTML title bar (called via BridgeApi.Win*; bridge runs on the UI thread). ──
        internal void WinMinimizeSelf() { try { WindowState = FormWindowState.Minimized; } catch { } }
        internal void WinToggleMaxSelf() { try { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; } catch { } }
        internal void WinBeginDragSelf() { BeginModalMouseLoop(HTCAPTION); }

        // The move/size modal loop must start from the message pump, NOT inside the bridge's COM dispatch: SendMessage
        // here kept the WebView2 host-object call open for the ENTIRE drag, starving the browser↔host channel — the
        // app then froze for seconds after the button release while the queued cross-process calls unwound.
        // before: WinBeginDragSelf/WinBeginResizeSelf called ReleaseCapture+SendMessage inline.
        private void BeginModalMouseLoop(int ht)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)ht, IntPtr.Zero); } catch { }
                }));
            }
            catch { }
        }

        // Start a native resize loop from the HTML edge grips. WebView2 fills the client area and swallows the mouse at
        // the window edges, so WM_NCHITTEST never sees them — the grips forward the intended edge here instead.
        internal void WinBeginResizeSelf(string dir)
        {
            try
            {
                if (WindowState == FormWindowState.Maximized) return;
                int ht = dir switch
                {
                    "l" => HTLEFT, "r" => HTRIGHT, "t" => HTTOP, "b" => HTBOTTOM,
                    "tl" => HTTOPLEFT, "tr" => HTTOPRIGHT, "bl" => HTBOTTOMLEFT, "br" => HTBOTTOMRIGHT,
                    _ => 0
                };
                if (ht == 0) return;
                BeginModalMouseLoop(ht);
            }
            catch { }
        }

        // Push the maximize state to the page so it can swap the glyph + square the corners while maximized.
        private int _lastPostedMax = -1;   // -1 = never posted; dedupes the per-WM_SIZE cross-process ExecuteScript
        internal void PostWinState()
        {
            try
            {
                bool max = WindowState == FormWindowState.Maximized;
                _lastPostedMax = max ? 1 : 0;
                _webView?.CoreWebView2?.ExecuteScriptAsync("window.onWinState&&window.onWinState(" + (max ? "true" : "false") + ")");
            }
            catch { }
        }
        private void PostWinStateIfChanged()
        {
            int max = WindowState == FormWindowState.Maximized ? 1 : 0;
            if (max != _lastPostedMax) PostWinState();
        }
    }
}
