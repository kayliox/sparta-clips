// A small top-most, click-through, no-activate overlay that confirms "Clip saved" OVER a fullscreen game. Draw-only
// WinForms window — never injects into the game, so it's anti-cheat-safe like the WGC capture. Rounded navy pill +
// a warm accent bar. Ported from Sparta Dock's MainForm.ClipToast.cs into a STANDALONE class so it can fire when the
// app is tray-only (no MainForm) — before: it was a MainForm partial and needed the window to exist.
// Call Show() on the UI thread (TrayCore marshals to it).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpartaClips
{
    internal static class ClipToast
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // toast visible to the USER, invisible to the capture — a "Moment marked" toast must not be recorded into the moment

        private sealed class ToastForm : Form
        {
            public string Text2 = "";
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; return cp; }
            }
            public ToastForm() { DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = Rounded(r, 14);
                using (var bg = new LinearGradientBrush(r, Color.FromArgb(18, 22, 38), Color.FromArgb(9, 12, 22), 90f)) g.FillPath(bg, path);
                var bar = new Rectangle(r.X + 9, r.Y + 11, 5, r.Height - 22);
                using (var ab = new LinearGradientBrush(new Rectangle(bar.X, bar.Y, bar.Width, bar.Height + 1), Color.FromArgb(255, 206, 84), Color.FromArgb(255, 99, 132), 90f))
                using (var abp = Rounded(bar, 2)) g.FillPath(ab, abp);
                var textRect = new Rectangle(r.X + 26, r.Y, r.Width - 32, r.Height);
                TextRenderer.DrawText(g, Text2, Font, textRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                using (var pen = new Pen(Color.FromArgb(55, 255, 255, 255))) g.DrawPath(pen, path);
            }
            private static GraphicsPath Rounded(Rectangle r, int rad)
            {
                int d = rad * 2; var p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure(); return p;
            }
            protected override void OnResize(EventArgs e) { base.OnResize(e); try { using var p = Rounded(new Rectangle(0, 0, Width, Height), 14); Region = new Region(p); } catch { } }
        }

        private static ToastForm? _toast;
        private static System.Windows.Forms.Timer? _timer;

        /// <summary>Flash a brief over-the-game confirmation. Skipped when our own window is focused — the in-WebView
        /// toast already covers that windowed/desktop case (avoids a double toast). UI-thread only.</summary>
        public static void Show(string text)
        {
            try
            {
                IntPtr ui = MainForm.Instance?.IsHandleCreated == true ? MainForm.Instance.Handle : IntPtr.Zero;
                if (ui != IntPtr.Zero && GetForegroundWindow() == ui) return;   // our window is up front → the WebView toast handles it
                var scr = Screen.PrimaryScreen;
                var b = scr?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                if (_toast == null)
                    _toast = new ToastForm
                    {
                        FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                        ShowInTaskbar = false, TopMost = true, Opacity = 0.96,
                        Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
                    };
                _toast.Text2 = text;
                int w = 300, h = 46, x = b.X + 18, y = b.Y + 18;
                _toast.Bounds = new Rectangle(x, y, w, h);
                _toast.Show();
                try { SetWindowDisplayAffinity(_toast.Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }   // never burn the toast into a running capture
                try { _toast.TopMost = true; SetWindowPos(_toast.Handle, new IntPtr(-1) /*HWND_TOPMOST*/, x, y, w, h, SWP_NOACTIVATE); } catch { }
                _toast.Invalidate();
                try { _toast.Update(); } catch { }
                try { _timer?.Stop(); _timer?.Dispose(); } catch { }
                _timer = new System.Windows.Forms.Timer { Interval = 3000 };
                _timer.Tick += (s, e) => { try { _timer?.Stop(); } catch { } try { _toast?.Hide(); } catch { } };
                _timer.Start();
            }
            catch { }
        }
    }
}
