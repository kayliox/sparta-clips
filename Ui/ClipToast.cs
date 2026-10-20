// Over-the-game notification ("Saving replay…" → "Replay saved", errors, warnings). A top-most, click-through,
// no-activate PER-PIXEL-ALPHA window (UpdateLayeredWindow) drawn in the app's style: dark card, faint rim, a status
// icon (gradient check / spinning ring / amber ! / red ✕), a title + detail line, and a thin "time left" bar. Fades +
// slides in and out. Draw-only (never injects into the game) and excluded from screen capture, so it is anti-cheat-
// safe and never burned into the clip it announces.
// before: a 300×46 Region-clipped pill (jagged corners), one line of text, no animation, and it only appeared once the
// clip was FINISHED — after an AV1 conversion that could be seconds after the hotkey.
// Call Show() on the UI thread (TrayCore marshals to it).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpartaClips
{
    internal enum ToastKind { Success, Busy, Warning, Error }

    internal static class ClipToast
    {
        // ── layout at 96 DPI (scaled per monitor) ──
        private const int H = 70, RADIUS = 16, MARGIN = 20, ICON = 36;
        private const int MIN_W = 220, MAX_W = 520;   // card width follows the text (before: a fixed 360 px — far too wide for short messages)
        private const int ANIM_IN_MS = 190, ANIM_OUT_MS = 230, SLIDE = 12;

        private static ToastForm? _form;
        private static Timer? _tick;
        private static ToastKind _kind;
        private static string _title = "", _sub = "";
        private static DateTime _shownAt, _expiresAt, _hideAt;
        private static bool _hiding, _visible;
        private static float _spin;
        private static float _curW = -1;          // animated width (logical px); snaps on a fresh show, eases when the text changes
        private static string _fitKey = ""; private static float _fitW;

        /// <summary>Show (or update in place) the over-the-game notification. Skipped while our own window is focused —
        /// the in-WebView toast covers that case. Busy stays up until replaced (capped at 90 s).</summary>
        public static void Show(ToastKind kind, string title, string sub = "")
        {
            try
            {
                IntPtr ui = MainForm.Instance?.IsHandleCreated == true ? MainForm.Instance.Handle : IntPtr.Zero;
                if (ui != IntPtr.Zero && GetForegroundWindow() == ui) return;

                _form ??= new ToastForm();
                if (!_form.IsHandleCreated) _form.CreateControl();
                bool wasVisible = _visible && !_hiding;
                _kind = kind; _title = title ?? ""; _sub = sub ?? "";
                var now = DateTime.UtcNow;
                if (!wasVisible) { _shownAt = now; _curW = -1; }   // already up → morph in place (no second slide-in)
                _expiresAt = now + TimeSpan.FromMilliseconds(kind switch { ToastKind.Busy => 90000, ToastKind.Success => 3200, _ => 4800 });
                _hiding = false; _visible = true;

                if (!_form.Visible)
                {
                    ShowWindow(_form.Handle, 4 /*SW_SHOWNOACTIVATE*/);
                    try { SetWindowDisplayAffinity(_form.Handle, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/); } catch { }
                }
                if (_tick == null) { _tick = new Timer { Interval = 16 }; _tick.Tick += (s, e) => Frame(); }
                _tick.Start();
                Frame();
            }
            catch { }
        }

        private static void Frame()
        {
            if (_form == null) return;
            var now = DateTime.UtcNow;
            if (!_hiding && now >= _expiresAt) { _hiding = true; _hideAt = now; }

            double tin = Math.Clamp((now - _shownAt).TotalMilliseconds / ANIM_IN_MS, 0, 1);
            double alpha = EaseOut(tin), offset = (1 - EaseOut(tin)) * -SLIDE;
            if (_hiding)
            {
                double tout = Math.Clamp((now - _hideAt).TotalMilliseconds / ANIM_OUT_MS, 0, 1);
                alpha *= 1 - EaseIn(tout); offset += EaseIn(tout) * -SLIDE * 0.6;
                if (tout >= 1) { _tick?.Stop(); _visible = false; ShowWindow(_form.Handle, 0 /*SW_HIDE*/); return; }
            }
            _spin = (_spin + 7f) % 360f;

            float sc = Math.Max(1f, _form.DeviceDpi / 96f);
            var scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            float target = FitWidth(sc);
            _curW = _curW < 0 ? target : _curW + (target - _curW) * 0.22f;   // "Saving replay…" → longer "Replay saved · Game" grows smoothly
            if (Math.Abs(target - _curW) < 0.5f) _curW = target;
            int w = (int)Math.Round(_curW * sc), h = (int)(H * sc);
            int x = scr.X + (int)(MARGIN * sc), y = scr.Y + (int)((MARGIN + offset) * sc);

            double life = _kind == ToastKind.Busy ? -1
                : Math.Clamp((_expiresAt - now).TotalMilliseconds / (_expiresAt - _shownAt).TotalMilliseconds, 0, 1);
            using var bmp = Render(w, h, sc, life);
            Present(_form.Handle, bmp, x, y, (byte)Math.Round(alpha * 255));
        }

        private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
        private static double EaseIn(double t) => t * t;

        // ── drawing ──
        private static readonly Color AccentA = Color.FromArgb(255, 59, 78), AccentB = Color.FromArgb(155, 92, 246);

        private static Bitmap Render(int w, int h, float sc, double life)
        {
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // ClearType (sub-pixel, the crisp text Windows uses everywhere). It needs OPAQUE pixels under the glyphs, so the
            // card itself is fully opaque — only the rounded corners are transparent. before: greyscale AA on a 97%-alpha
            // card read soft/"not HD" at 100% display scaling.
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            var card = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);
            using (var path = Rounded(card, RADIUS * sc))
            {
                using (var bg = new LinearGradientBrush(card, Color.FromArgb(255, 30, 30, 38), Color.FromArgb(255, 15, 15, 20), 90f)) g.FillPath(bg, path);
                // time-left bar along the bottom edge (clipped to the card's rounded shape)
                if (life > 0)
                {
                    var st = g.Save(); g.SetClip(path);
                    float bh = 2.5f * sc, bw = (float)(card.Width * life);
                    var bar = new RectangleF(card.X, card.Bottom - bh, Math.Max(1f, bw), bh);
                    using (var ab = new LinearGradientBrush(new RectangleF(card.X, bar.Y, card.Width, bh), AccentA, AccentB, 0f)) g.FillRectangle(ab, bar);
                    g.Restore(st);
                }
                using (var rim = new Pen(Color.FromArgb(34, 255, 255, 255), 1f)) g.DrawPath(rim, path);
            }

            float isz = ICON * sc, ix = 16 * sc, iy = (h - isz) / 2f;
            DrawIcon(g, new RectangleF(ix, iy, isz, isz), sc);

            float tx = ix + isz + 13 * sc, tw = w - tx - 14 * sc;
            using var fTitle = TitleFont(sc);
            using var fSub = SubFont(sc);
            using var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
            bool two = _sub.Length > 0;
            float lineH = h / 2f;
            using (var b = new SolidBrush(Color.FromArgb(246, 246, 250)))
                g.DrawString(_title, fTitle, b, new RectangleF(tx, two ? h * 0.13f : 0, tw, two ? lineH * 0.9f : h), fmt);
            if (two) using (var b = new SolidBrush(Color.FromArgb(160, 160, 172)))
                g.DrawString(_sub, fSub, b, new RectangleF(tx, h * 0.5f, tw, lineH * 0.78f), fmt);
            return bmp;
        }

        private static Font TitleFont(float sc) => new("Segoe UI Semibold", 11.25f * sc, FontStyle.Regular, GraphicsUnit.Point);
        private static Font SubFont(float sc) => new("Segoe UI", 9.25f * sc, FontStyle.Regular, GraphicsUnit.Point);

        // Card width (logical px) = icon + gap + the wider of the two text lines + right padding, clamped. Cached per text.
        private static float FitWidth(float sc)
        {
            string key = _title + "|" + _sub + "|" + sc;
            if (key == _fitKey) return _fitW;
            float textW = 0;
            try
            {
                using var bmp = new Bitmap(1, 1); using var g = Graphics.FromImage(bmp);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using var fT = TitleFont(sc); using var fS = SubFont(sc);
                textW = Math.Max(g.MeasureString(_title, fT).Width, _sub.Length > 0 ? g.MeasureString(_sub, fS).Width : 0) / sc;
            }
            catch { textW = 240; }
            _fitKey = key;
            _fitW = Math.Clamp(16 + ICON + 13 + textW + 18, MIN_W, MAX_W);
            return _fitW;
        }

        private static void DrawIcon(Graphics g, RectangleF r, float sc)
        {
            float pw = 2.6f * sc;
            switch (_kind)
            {
                case ToastKind.Busy:
                    using (var track = new Pen(Color.FromArgb(40, 255, 255, 255), pw)) g.DrawEllipse(track, Inset(r, pw));
                    using (var arcBrush = new LinearGradientBrush(r, AccentA, AccentB, 45f))
                    using (var arc = new Pen(arcBrush, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(arc, Inset(r, pw), _spin, 100f);
                    break;
                case ToastKind.Success:
                    using (var fill = new LinearGradientBrush(r, AccentA, AccentB, 45f)) g.FillEllipse(fill, r);
                    using (var p = new Pen(Color.White, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[] { Pt(r, .29f, .52f), Pt(r, .44f, .66f), Pt(r, .72f, .37f) });
                    break;
                case ToastKind.Warning:
                    using (var fill = new SolidBrush(Color.FromArgb(255, 176, 32))) g.FillEllipse(fill, r);
                    using (var p = new Pen(Color.FromArgb(28, 20, 4), pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { g.DrawLine(p, Pt(r, .5f, .27f), Pt(r, .5f, .56f)); g.FillEllipse(new SolidBrush(Color.FromArgb(28, 20, 4)), new RectangleF(r.X + r.Width * .5f - pw * .75f, r.Y + r.Height * .71f - pw * .75f, pw * 1.5f, pw * 1.5f)); }
                    break;
                default:
                    using (var fill = new SolidBrush(Color.FromArgb(255, 77, 94))) g.FillEllipse(fill, r);
                    using (var p = new Pen(Color.White, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { g.DrawLine(p, Pt(r, .35f, .35f), Pt(r, .65f, .65f)); g.DrawLine(p, Pt(r, .65f, .35f), Pt(r, .35f, .65f)); }
                    break;
            }
        }
        private static PointF Pt(RectangleF r, float fx, float fy) => new(r.X + r.Width * fx, r.Y + r.Height * fy);
        private static RectangleF Inset(RectangleF r, float d) => new(r.X + d / 2, r.Y + d / 2, r.Width - d, r.Height - d);
        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            float d = rad * 2; var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ── per-pixel-alpha window ──
        private sealed class ToastForm : Form
        {
            public ToastForm() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true; }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {   // LAYERED (per-pixel alpha) | NOACTIVATE | TOOLWINDOW | TRANSPARENT (click-through) | TOPMOST
                get { var cp = base.CreateParams; cp.ExStyle |= 0x00080000 | 0x08000000 | 0x00000080 | 0x00000020 | 0x00000008; return cp; }
            }
        }

        private static void Present(IntPtr hwnd, Bitmap bmp, int x, int y, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT(); var pos = new POINT { x = x, y = y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
                UpdateLayeredWindow(hwnd, screenDc, ref pos, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
                SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    }
}
