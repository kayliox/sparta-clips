// "Update available" dialog — native (works with no WebView window open; Sparta Clips usually lives in the tray), drawn
// in the app's style: dark card, Ω icon, version line, "What's new" list, gradient primary button. Choices:
//   Install update → download + verify (progress bar) → start the installer → quit (the installer reopens the app)
//   Later          → hidden; asked again in 24 h (or on a manual "Check now")
//   Skip version   → never offered automatically again for THIS version (the next build is offered as usual)
// TrayCore only shows it when no fullscreen game is in front and nothing is recording, so it never interrupts play.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SpartaClips
{
    internal sealed class UpdatePrompt : Form
    {
        private static readonly Color Bg = Color.FromArgb(0x15, 0x15, 0x19), Surface = Color.FromArgb(0x1c, 0x1c, 0x22),
            Line = Color.FromArgb(0x2c, 0x2c, 0x34), Text1 = Color.FromArgb(0xf3, 0xf3, 0xf6), Text2 = Color.FromArgb(0xa7, 0xa7, 0xb2),
            Text3 = Color.FromArgb(0x6b, 0x6b, 0x76), Accent = Color.FromArgb(0xff, 0x3b, 0x4e), Brand2 = Color.FromArgb(0xa0, 0x5c, 0xff),
            Bad = Color.FromArgb(0xff, 0x6b, 0x6b);

        private static UpdatePrompt? _open;
        public static bool IsOpen => _open != null && !_open.IsDisposed;

        private readonly ReleaseInfo _rel;
        private readonly Func<bool> _reopenWindow;
        private readonly Label _status;
        private readonly Pill _install, _later;
        private readonly LinkLabel _skip;
        private readonly Bar _bar;
        private CancellationTokenSource? _cts;
        private bool _installing;

        /// <summary>Show (or bring forward) the prompt. Call on the UI thread.</summary>
        /// <param name="activate">false (automatic check) = appear on top without stealing keyboard focus.</param>
        public static void ShowFor(ReleaseInfo rel, Func<bool> reopenWindow, bool activate)
        {
            if (IsOpen) { if (activate) _open!.Activate(); return; }
            _open = new UpdatePrompt(rel, reopenWindow) { _activateOnShow = activate };
            _open.Show();
            if (activate) _open.Activate();
        }
        private bool _activateOnShow = true;
        protected override bool ShowWithoutActivation => !_activateOnShow;

        private UpdatePrompt(ReleaseInfo rel, Func<bool> reopenWindow)
        {
            _rel = rel; _reopenWindow = reopenWindow;
            Text = "Sparta Clips update";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true; TopMost = true; KeyPreview = true;
            BackColor = Bg; ForeColor = Text1;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(480, 340);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }

            Font ui = UiFont(9.75f), bold = UiFont(15f, FontStyle.Bold), small = UiFont(9f);

            var icon = new PictureBox { Location = new Point(26, 26), Size = new Size(44, 44), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Bg };
            try { if (Icon != null) icon.Image = new Icon(Icon, 64, 64).ToBitmap(); } catch { }
            var title = new Label { Text = "Update available", Font = bold, ForeColor = Text1, AutoSize = true, Location = new Point(82, 24), BackColor = Bg };
            var sub = new Label
            {
                Text = "Sparta Clips " + AppUpdater.VersionText(rel.Version) + " is ready  ·  you have " + AppUpdater.VersionText(AppUpdater.Current),
                Font = ui, ForeColor = Text2, AutoSize = true, Location = new Point(84, 52), BackColor = Bg,
            };
            var whatsNew = new Label { Text = "WHAT'S NEW", Font = UiFont(8f, FontStyle.Bold), ForeColor = Text3, AutoSize = true, Location = new Point(26, 92), BackColor = Bg };
            var notesHost = new Panel { Location = new Point(26, 112), Size = new Size(428, 132), BackColor = Surface, Padding = new Padding(12, 10, 8, 10) };
            notesHost.Paint += (s, e) => { using var p = new Pen(Line); e.Graphics.DrawRectangle(p, 0, 0, notesHost.Width - 1, notesHost.Height - 1); };
            var notes = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Text1,
                Font = ui, ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, TabStop = false, Cursor = Cursors.Default,
                Text = string.IsNullOrWhiteSpace(rel.Notes) ? "Improvements and fixes." : rel.Notes,
            };
            notes.SelectionChanged += (s, e) => { if (notes.SelectionLength > 0) notes.SelectionLength = 0; };   // display-only
            notes.HandleCreated += (s, e) => { try { SetWindowTheme(notes.Handle, "DarkMode_Explorer", null); } catch { } };   // dark scrollbar
            notesHost.Controls.Add(notes);

            _status = new Label { Font = small, ForeColor = Text2, AutoSize = false, Location = new Point(26, 256), Size = new Size(428, 20), BackColor = Bg, Visible = false };
            _bar = new Bar { Location = new Point(26, 280), Size = new Size(428, 6), Visible = false };

            _skip = new LinkLabel
            {
                Text = "Skip this version", Font = small, AutoSize = true, Location = new Point(26, 302), BackColor = Bg,
                LinkColor = Text3, ActiveLinkColor = Text1, VisitedLinkColor = Text3, LinkBehavior = LinkBehavior.HoverUnderline,
            };
            _skip.LinkClicked += (s, e) => { AppUpdater.Skip(_rel.Version); Close(); };
            _later = new Pill(false) { Text = "Later", Font = ui, Location = new Point(250, 292), Size = new Size(84, 34) };
            _later.Click += (s, e) => { AppUpdater.RemindLater(TimeSpan.FromHours(24)); Close(); };
            _install = new Pill(true) { Text = "Install update", Font = UiFont(9.75f, FontStyle.Bold), Location = new Point(342, 292), Size = new Size(112, 34) };
            _install.Click += (s, e) => Install();

            Controls.AddRange(new Control[] { icon, title, sub, whatsNew, notesHost, _status, _bar, _skip, _later, _install });
            AcceptButton = null;

            // Drag anywhere on the card (there is no title bar).
            foreach (Control c in new Control[] { this, icon, title, sub, whatsNew, _status })
                c.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero); } };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 2 /*DWMWCP_ROUND*/; DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref v, 4); } catch { }
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4); } catch { }
            try { int c = 0x00342c2c; DwmSetWindowAttribute(Handle, 34 /*DWMWA_BORDER_COLOR*/, ref c, 4); } catch { }   // COLORREF 0x00BBGGRR — faint rim
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000 /*CS_DROPSHADOW*/; return cp; } }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !_installing) { AppUpdater.RemindLater(TimeSpan.FromHours(24)); Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && !_installing) { Install(); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            try { _cts?.Cancel(); } catch { }
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e) { if (_open == this) _open = null; base.OnFormClosed(e); }

        private async void Install()
        {
            if (_installing) return;
            _installing = true;
            _install.Enabled = _later.Enabled = false; _skip.Visible = false;
            _status.ForeColor = Text2; _status.Visible = true; _bar.Visible = true; _bar.Value = 0;
            _status.Text = "Downloading…";
            _cts = new CancellationTokenSource();
            var prog = new Progress<(long done, long total)>(p =>
            {
                double f = p.total > 0 ? (double)p.done / p.total : 0;
                _bar.Value = (float)f;
                _status.Text = p.total > 0 ? $"Downloading…  {f * 100:0}%   ({p.done / 1048576.0:0.0} / {p.total / 1048576.0:0.0} MB)" : $"Downloading…  {p.done / 1048576.0:0.0} MB";
            });
            string? setup = await AppUpdater.DownloadAsync(_rel, prog, _cts.Token);
            if (IsDisposed) return;
            if (setup == null)
            {
                Fail("Download failed — check your connection and try again.");
                return;
            }
            _status.Text = "Installing — Sparta Clips will close and reopen in a moment…";
            _bar.Value = 1;
            bool reopenWindow = false; try { reopenWindow = _reopenWindow(); } catch { }
            if (!AppUpdater.LaunchInstaller(setup, reopenWindow)) { Fail("Couldn't start the installer — try again."); return; }
            await System.Threading.Tasks.Task.Delay(600);
            _installing = false;
            try { TrayCore.Instance?.QuitApp(); } catch { }
        }
        private void Fail(string msg)
        {
            _installing = false;
            _status.ForeColor = Bad; _status.Text = msg; _bar.Visible = false;
            _install.Enabled = _later.Enabled = true; _install.Text = "Try again"; _skip.Visible = true;
        }

        private static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
            {
                try { var f = new Font(name, size, style); if (f.Name == name) return f; f.Dispose(); } catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        // ── Rounded pill button (gradient primary / outlined secondary) ──
        private sealed class Pill : Control
        {
            private readonly bool _primary; private bool _hover, _down;
            public Pill(bool primary)
            {
                _primary = primary;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Bg; Cursor = Cursors.Hand;
            }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
            protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(BackColor);
                var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using var path = Round(r, r.Height / 2);
                if (_primary)
                {
                    Color a = Accent, b = Brand2;
                    if (!Enabled) { a = Color.FromArgb(0x3a, 0x2a, 0x30); b = Color.FromArgb(0x30, 0x2a, 0x3a); }
                    else if (_down) { a = Shade(a, -0.12f); b = Shade(b, -0.12f); }
                    else if (_hover) { a = Shade(a, 0.10f); b = Shade(b, 0.10f); }
                    using var br = new LinearGradientBrush(r, a, b, LinearGradientMode.ForwardDiagonal);
                    g.FillPath(br, path);
                }
                else
                {
                    using var br = new SolidBrush(_down ? Color.FromArgb(0x22, 0x22, 0x29) : _hover ? Color.FromArgb(0x26, 0x26, 0x2e) : Surface);
                    g.FillPath(br, path);
                    using var p = new Pen(Color.FromArgb(_hover ? 0x48 : 0x34, 0x48, 0x48, 0x52));
                    g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Color.White : Text3,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            private static Color Shade(Color c, float f) => f >= 0
                ? Color.FromArgb(c.R + (int)((255 - c.R) * f), c.G + (int)((255 - c.G) * f), c.B + (int)((255 - c.B) * f))
                : Color.FromArgb((int)(c.R * (1 + f)), (int)(c.G * (1 + f)), (int)(c.B * (1 + f)));
        }

        // ── Thin gradient progress bar ──
        private sealed class Bar : Control
        {
            private float _v;
            public float Value { get => _v; set { _v = Math.Clamp(value, 0, 1); Invalidate(); } }
            public Bar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Bg; }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(BackColor);
                var full = new RectangleF(0, 0, Width - 0.5f, Height - 0.5f);
                using (var track = Round(full, full.Height / 2)) using (var b = new SolidBrush(Surface)) g.FillPath(b, track);
                float w = Math.Max(Height, (Width - 0.5f) * _v);
                if (_v <= 0) return;
                var fill = new RectangleF(0, 0, w, Height - 0.5f);
                using var path = Round(fill, fill.Height / 2);
                using var br = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), Accent, Brand2, LinearGradientMode.Horizontal);
                g.FillPath(br, path);
            }
        }

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr h, string? app, string? idList);
    }
}
