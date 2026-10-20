// Persistent, top-most, click-through "● REC m:ss" pill shown OVER a fullscreen game while a full recording runs, so
// the user can always tell capture is live (the #1 QA ask — "am I actually recording?"). Draw-only, no-activate,
// WS_EX_TRANSPARENT so clicks pass straight to the game — anti-cheat-safe like ClipToast/the WGC capture, never injects.
// UI-thread only (TrayCore marshals). Same over-exclusive-fullscreen caveat as any WGC-era overlay.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpartaClips
{
    internal static class ClipStatusOverlay
    {
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // pill visible to the USER but invisible to WGC display capture — never burned into the clip

        private sealed class OverlayForm : Form
        {
            public string Label = "REC 0:00";
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {   // WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT (click-through)
                get { var cp = base.CreateParams; cp.ExStyle |= 0x00080000 | 0x08000000 | 0x00000008 | 0x00000080 | 0x00000020; return cp; }
            }
            public OverlayForm() { DoubleBuffered = true; }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = Rounded(r, 13);
                using (var bg = new LinearGradientBrush(r, Color.FromArgb(224, 18, 22, 38), Color.FromArgb(224, 9, 12, 22), 90f)) g.FillPath(bg, path);
                int cy = r.Y + r.Height / 2;
                using (var dot = new SolidBrush(Color.FromArgb(255, 99, 132))) g.FillEllipse(dot, r.X + 12, cy - 5, 10, 10);
                var textRect = new Rectangle(r.X + 30, r.Y, r.Width - 36, r.Height);
                TextRenderer.DrawText(g, Label, Font, textRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                using (var pen = new Pen(Color.FromArgb(55, 255, 255, 255))) g.DrawPath(pen, path);
            }
            private static GraphicsPath Rounded(Rectangle r, int rad)
            {
                int d = rad * 2; var p = new GraphicsPath();
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure(); return p;
            }
            protected override void OnResize(EventArgs e) { base.OnResize(e); try { using var p = Rounded(new Rectangle(0, 0, Width, Height), 13); Region = new Region(p); } catch { } }
        }

        private static OverlayForm? _form;
        private static System.Windows.Forms.Timer? _timer;
        private static long _startTick;
        private const int W = 132, H = 40;

        /// <summary>Show the pill and start the elapsed clock. UI-thread only.</summary>
        public static void Start()
        {
            try
            {
                _startTick = Environment.TickCount64;
                var b = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
                int x = b.Right - W - 18, y = b.Y + 18;
                if (_form == null)
                    _form = new OverlayForm
                    {
                        FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                        ShowInTaskbar = false, TopMost = true, Opacity = 0.95,
                        Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    };
                _form.Label = "REC 0:00";
                _form.Bounds = new Rectangle(x, y, W, H);
                _form.Show();
                try { SetWindowDisplayAffinity(_form.Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }   // else display-mode recordings capture the pill itself
                try { _form.TopMost = true; SetWindowPos(_form.Handle, new IntPtr(-1) /*HWND_TOPMOST*/, x, y, W, H, SWP_NOACTIVATE); } catch { }
                _form.Invalidate();
                try { _timer?.Stop(); _timer?.Dispose(); } catch { }
                _timer = new System.Windows.Forms.Timer { Interval = 1000 };
                _timer.Tick += (s, e) => Tick();
                _timer.Start();
            }
            catch { }
        }

        private static void Tick()
        {
            try
            {
                if (_form == null || !_form.Visible) return;
                long sec = Math.Max(0, (Environment.TickCount64 - _startTick) / 1000);
                _form.Label = "REC " + (sec / 60) + ":" + (sec % 60).ToString("00");
                _form.Invalidate();
                // Keep it pinned above a game that re-asserts topmost focus.
                try { var b = _form.Bounds; SetWindowPos(_form.Handle, new IntPtr(-1), b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE); } catch { }
            }
            catch { }
        }

        /// <summary>Hide the pill. UI-thread only.</summary>
        public static void Stop()
        {
            try { _timer?.Stop(); _timer?.Dispose(); } catch { }
            _timer = null;
            try { _form?.Hide(); } catch { }
        }
    }
}
