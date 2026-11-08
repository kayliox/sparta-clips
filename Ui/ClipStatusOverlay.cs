// Persistent, top-most, click-through "● REC m:ss · App" pill shown OVER a fullscreen game while a full recording runs,
// so the user can always tell capture is live (the #1 QA ask — "am I actually recording?"). Draw-only, no-activate,
// WS_EX_TRANSPARENT so clicks pass straight to the game — anti-cheat-safe like ClipToast/the WGC capture, never injects.
// UI-thread only (TrayCore marshals). Same over-exclusive-fullscreen caveat as any WGC-era overlay.
//
// Rendering matches ClipToast: a per-pixel-alpha layered window (UpdateLayeredWindow) — smooth anti-aliased corners +
// a soft shadow, ClearType text on the opaque card. before: a Region-clipped WinForms window (jagged corners, no shadow,
// fixed 132 px wide, always on the PRIMARY monitor even when the pinned app was on another screen).
//
// NEVER in a recording: WDA_EXCLUDEFROMCAPTURE hides the window from every capture path (display duplication, WGC
// monitor capture) — and a pinned-app (window) capture only ever contains that app's own window. Verified 2026-11-02:
// pill on screen over the pinned window → absent from the clip in window, display and pin-fallback modes.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpartaClips
{
    internal static class ClipStatusOverlay
    {
        private sealed class OverlayForm : Form
        {
            public OverlayForm() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true; }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {   // LAYERED (per-pixel alpha) | NOACTIVATE | TOOLWINDOW | TRANSPARENT (click-through) | TOPMOST
                get { var cp = base.CreateParams; cp.ExStyle |= 0x00080000 | 0x08000000 | 0x00000080 | 0x00000020 | 0x00000008; return cp; }
            }
        }

        private static OverlayForm? _form;
        private static Timer? _timer;
        private static DateTime _shownAt, _hideAt;
        private static bool _hiding;
        private static int _frame;

        // Layout in DIPs (scaled by the target monitor's DPI).
        private const float H = 34f, SHADOW = 7f, MARGIN = 18f, MAX_APP_W = 190f;
        private const double FADE_IN_MS = 180, FADE_OUT_MS = 160;
        private static readonly Color Rec = Color.FromArgb(255, 70, 88), Amber = Color.FromArgb(255, 176, 32);

        /// <summary>Show the pill and start the clock. UI-thread only.</summary>
        public static void Start()
        {
            try
            {
                _form ??= new OverlayForm();
                if (!_form.IsHandleCreated) _form.CreateControl();
                _shownAt = DateTime.UtcNow; _hiding = false; _frame = 0;
                if (!_form.Visible)
                {
                    ShowWindow(_form.Handle, 4 /*SW_SHOWNOACTIVATE*/);
                    try { SetWindowDisplayAffinity(_form.Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }   // else display-mode recordings capture the pill itself
                }
                if (_timer == null) { _timer = new Timer { Interval = 50 }; _timer.Tick += (s, e) => Frame(); }
                _timer.Start();
                Frame();
            }
            catch { }
        }

        /// <summary>Fade the pill out. UI-thread only.</summary>
        public static void Stop()
        {
            try
            {
                if (_form == null || !_form.Visible) { _timer?.Stop(); return; }
                if (!_hiding) { _hiding = true; _hideAt = DateTime.UtcNow; }
            }
            catch { }
        }

        private static void Frame()
        {
            try
            {
                if (_form == null) return;
                var now = DateTime.UtcNow;
                double a = Math.Clamp((now - _shownAt).TotalMilliseconds / FADE_IN_MS, 0, 1);
                if (_hiding)
                {
                    double t = Math.Clamp((now - _hideAt).TotalMilliseconds / FADE_OUT_MS, 0, 1);
                    a *= 1 - t;
                    if (t >= 1) { _timer?.Stop(); ShowWindow(_form.Handle, 0 /*SW_HIDE*/); return; }
                }
                bool paused = ClipRecorder.IsPaused;
                // The engine's clip-timeline clock (paused stretches excluded) — frozen while paused.
                long sec = (long)ClipRecorder.RecElapsedSec;
                string time = sec >= 3600 ? $"{sec / 3600}:{sec / 60 % 60:00}:{sec % 60:00}" : $"{sec / 60}:{sec % 60:00}";
                // The dot breathes (~1.4 s) while recording; steady when paused.
                double pulse = paused ? 1 : 0.62 + 0.38 * Math.Cos((now - _shownAt).TotalSeconds * Math.PI * 2 / 1.4);

                // Pin to the chosen corner (Settings → "Recording indicator position", default top-right) of the monitor
                // being recorded: the captured window's screen, else the primary. Re-read ~once a second so a change
                // applies live (the config read is mtime-cached).
                if (_frame % 20 == 0 || _pos == null) { try { _pos = AppConfig.OverlayPos(); } catch { _pos = "tr"; } }
                IntPtr cap = ClipRecorder.CaptureWindow;
                Screen scr; try { scr = cap != IntPtr.Zero ? Screen.FromHandle(cap) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]); } catch { scr = Screen.AllScreens[0]; }
                float sc = MonitorScale(scr);
                string app = AppLabel(cap);

                using var bmp = Render(sc, paused, time, app, pulse);
#if CDP_HOOK
                // Test builds only: the pill is excluded from every capture API (screenshots too), so dump a frame for review.
                try { if (_frame % 20 == 0) bmp.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spartaclips_overlay_" + (paused ? "paused" : "rec") + ".png")); } catch { }
#endif
                var b = scr.Bounds;
                int m = (int)Math.Round((MARGIN - SHADOW) * sc);
                string pos = _pos ?? "tr";
                int x = pos[1] == 'l' ? b.Left + m : pos[1] == 'c' ? b.Left + (b.Width - bmp.Width) / 2 : b.Right - bmp.Width - m;
                int y = pos[0] == 'b' ? b.Bottom - bmp.Height - m : b.Top + m;
                // Re-assert topmost about once a second (a game can re-assert its own topmost focus).
                Present(_form.Handle, bmp, x, y, (byte)Math.Round(a * 255), (_frame++ % 20) == 0);
            }
            catch { }
        }

        private static string? _pos;   // cached overlay position ("tr" etc.)
        private static string AppLabel(IntPtr cap)
        {
            if (cap == IntPtr.Zero) return "Full screen";
            try
            {
                var sb = new System.Text.StringBuilder(256);
                string t = GetWindowText(cap, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : "";
                // Browser/editor titles end in " - App Name": keep the useful leading part.
                int dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash > 8) t = t.Substring(0, dash);
                return t.Length > 0 ? t : "App window";
            }
            catch { return "App window"; }
        }

        // ── drawing ──
        // Fonts cached per DPI scale (the pill re-renders ~20x/s for the breathing dot).
        private static float _fontSc = -1;
        private static Font? _fTag, _fTime, _fApp;
        private static void EnsureFonts(float sc)
        {
            if (Math.Abs(_fontSc - sc) < 0.01f && _fTag != null) return;
            _fTag?.Dispose(); _fTime?.Dispose(); _fApp?.Dispose();
            _fTag = new Font("Segoe UI Semibold", 8.25f * sc, FontStyle.Regular, GraphicsUnit.Point);
            _fTime = new Font("Segoe UI Semibold", 10.75f * sc, FontStyle.Regular, GraphicsUnit.Point);
            _fApp = new Font("Segoe UI", 9f * sc, FontStyle.Regular, GraphicsUnit.Point);
            _fontSc = sc;
        }
        private static readonly StringFormat Fmt = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces, LineAlignment = StringAlignment.Center };
        private static readonly StringFormat FmtEllipsis = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        private static readonly Bitmap _measureBmp = new(1, 1);

        private static Bitmap Render(float sc, bool paused, string time, string app, double pulse)
        {
            EnsureFonts(sc);
            Font fTag = _fTag!, fTime = _fTime!, fApp = _fApp!;
            string tag = paused ? "PAUSED" : "REC";
            float wTag, wTime, wApp;
            using (var mg = Graphics.FromImage(_measureBmp))
            {
                mg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                wTag = mg.MeasureString(tag, fTag, int.MaxValue, Fmt).Width;
                // Widest-digit width so the pill doesn't jitter as the seconds tick (Segoe digits are proportional).
                wTime = mg.MeasureString(System.Text.RegularExpressions.Regex.Replace(time, "[0-9]", "0"), fTime, int.MaxValue, Fmt).Width;
                wApp = Math.Min(MAX_APP_W * sc, mg.MeasureString(app, fApp, int.MaxValue, Fmt).Width + 1);
            }

            float pad = 13 * sc, icon = 10 * sc, gap = 8 * sc, sepGap = 11 * sc;
            float cardW = pad + icon + gap + wTag + 7 * sc + wTime + sepGap + 1 + sepGap + wApp + pad + 2 * sc;
            float cardH = H * sc, sh = SHADOW * sc;
            int w = (int)Math.Ceiling(cardW + 2 * sh), h = (int)Math.Ceiling(cardH + 2 * sh);

            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var card = new RectangleF(sh, sh, cardW, cardH);
            float rad = cardH / 2f;
            // soft drop shadow: stacked, fading rounded rings under the card
            for (int i = (int)sh; i >= 1; i--)
            {
                var r = new RectangleF(card.X - i, card.Y - i + 1.5f * sc, card.Width + 2 * i, card.Height + 2 * i);
                using var p = Rounded(r, rad + i);
                using var br = new SolidBrush(Color.FromArgb((int)(26 * (1 - i / (sh + 1))), 0, 0, 0));
                g.FillPath(br, p);
            }
            Color accent = paused ? Amber : Rec;
            using (var path = Rounded(card, rad))
            {
                // Opaque card (ClearType needs opaque pixels under the glyphs) — same palette as ClipToast.
                using (var bg = new LinearGradientBrush(card, Color.FromArgb(255, 30, 30, 38), Color.FromArgb(255, 15, 15, 20), 90f)) g.FillPath(bg, path);
                // faint accent wash from the left, behind the status icon
                var st = g.Save(); g.SetClip(path);
                var washR = new RectangleF(card.X, card.Y, card.Width * 0.45f, card.Height);
                using (var wash = new LinearGradientBrush(washR, Color.FromArgb(paused ? 34 : 30, accent), Color.FromArgb(0, accent), 0f))
                    g.FillRectangle(wash, washR);
                g.Restore(st);
                float d = 0.5f * sc;
                using var rimPath = Rounded(new RectangleF(card.X + d, card.Y + d, card.Width - 2 * d, card.Height - 2 * d), rad - d);
                using var rim = new Pen(Color.FromArgb(paused ? 120 : 95, accent), 1f * sc);
                g.DrawPath(rim, rimPath);
            }

            float cy = card.Y + cardH / 2f, x = card.X + pad;
            if (paused)
            {   // two amber pause bars
                float bw = 3.2f * sc, bh = 10.5f * sc, bx = x + (icon - (2 * bw + 2.6f * sc)) / 2f;
                using var br = new SolidBrush(Amber);
                using (var p1 = Rounded(new RectangleF(bx, cy - bh / 2, bw, bh), bw / 2)) g.FillPath(br, p1);
                using (var p2 = Rounded(new RectangleF(bx + bw + 2.6f * sc, cy - bh / 2, bw, bh), bw / 2)) g.FillPath(br, p2);
            }
            else
            {   // breathing red dot with a soft halo
                float dd = 9 * sc, halo = dd + 7 * sc * (float)pulse;
                using (var hb = new SolidBrush(Color.FromArgb((int)(70 * pulse), Rec))) g.FillEllipse(hb, x + icon / 2 - halo / 2, cy - halo / 2, halo, halo);
                using (var db = new SolidBrush(Color.FromArgb((int)(140 + 115 * pulse), Rec))) g.FillEllipse(db, x + icon / 2 - dd / 2, cy - dd / 2, dd, dd);
            }
            x += icon + gap;

            // GDI+ ClearType onto the opaque card (GDI TextRenderer would zero the alpha of a 32bpp bitmap → glyph holes).
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float lineTop = card.Y, lineH = cardH;
            using (var b = new SolidBrush(accent)) g.DrawString(tag, fTag, b, new RectangleF(x, lineTop + 0.5f * sc, wTag + 2, lineH), Fmt);
            x += wTag + 7 * sc;
            using (var b = new SolidBrush(paused ? Color.FromArgb(200, 200, 208) : Color.FromArgb(246, 246, 250))) g.DrawString(time, fTime, b, new RectangleF(x, lineTop, wTime + 2, lineH), Fmt);
            x += wTime + sepGap;
            using (var sep = new Pen(Color.FromArgb(46, 255, 255, 255), 1f)) g.DrawLine(sep, x, cy - 7 * sc, x, cy + 7 * sc);
            x += 1 + sepGap;
            using (var b = new SolidBrush(Color.FromArgb(150, 150, 162))) g.DrawString(app, fApp, b, new RectangleF(x, lineTop, wApp, lineH), FmtEllipsis);
            return bmp;
        }

        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2f);
            float d = rad * 2; var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        private static float MonitorScale(Screen scr)
        {
            try
            {
                var b = scr.Bounds;
                IntPtr mon = MonitorFromPoint(new POINT { x = b.X + b.Width / 2, y = b.Y + b.Height / 2 }, 2 /*NEAREST*/);
                if (GetDpiForMonitor(mon, 0 /*MDT_EFFECTIVE_DPI*/, out uint dx, out _) == 0 && dx > 0) return Math.Max(1f, dx / 96f);
            }
            catch { }
            return 1f;
        }

        // ── per-pixel-alpha window (same as ClipToast) ──
        private static void Present(IntPtr hwnd, Bitmap bmp, int x, int y, byte alpha, bool reassertTop)
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
                if (reassertTop) SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // visible to the USER, invisible to every capture API — never burned into a clip
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    }
}
