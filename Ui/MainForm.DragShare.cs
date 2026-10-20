// Native "drag the clip out to Discord" support. A web page can't hand the OS a real file (security), so we float
// an almost-invisible, mouse-capturing WinForms window over the popup's drag box and start a true OS file drag
// (DoDragDrop with a FileDrop) on mouse-down — the user drags it straight into Discord's chat. The file is rendered
// ahead of time (SetDragSharePath); the proxy just carries it.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SpartaClips
{
    public partial class MainForm
    {
        private sealed class DragProxyForm : Form
        {
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/; return cp; }
            }
            public DragProxyForm()
            {
                FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false; TopMost = true; BackColor = Color.Black; Opacity = 0.01;   // captures the mouse; the web box shows through
                Cursor = Cursors.Hand;
            }
        }

        private DragProxyForm? _dragProxy;
        private string _dragSharePath = "";
        private double _dragLf, _dragTf, _dragWf, _dragHf;   // last armed fractional rect (re-anchor on window move)
        private bool _dragHooked;

        // Marshal an action onto the UI thread (was in the old clip-player partial; the drag proxy still needs it).
        private void ClipUi(Action a) { if (InvokeRequired) { try { BeginInvoke(a); } catch { } } else a(); }

        /// <summary>The rendered file the drag carries (set by the share render). Cleared while a render is pending.</summary>
        public void SetDragSharePath(string path) { _dragSharePath = path ?? ""; }
        public string DragSharePath => _dragSharePath;

        /// <summary>Place the drag-capture window over the popup's drag box (fractions of the WebView client).</summary>
        public void ShowDragProxy(double lf, double tf, double wf, double hf)
        {
            ClipUi(() =>
            {
                try
                {
                    if (_webView == null) return;
                    _dragLf = lf; _dragTf = tf; _dragWf = wf; _dragHf = hf;
                    if (_dragProxy == null)
                    {
                        _dragProxy = new DragProxyForm();
                        _dragProxy.MouseDown += (s, e) =>
                        {
                            try
                            {
                                if (string.IsNullOrEmpty(_dragSharePath) || !System.IO.File.Exists(_dragSharePath)) return;
                                var data = new DataObject(DataFormats.FileDrop, new[] { _dragSharePath });   // a real OS file drag → Discord
                                _dragProxy!.DoDragDrop(data, DragDropEffects.Copy);
                            }
                            catch { }
                        };
                    }
                    if (!_dragHooked)
                    {
                        // The proxy sits at SCREEN coordinates captured at arm time — without these hooks it desynced:
                        // moving the main window left it floating over whatever app was underneath (swallowing mouse
                        // downs there), and hide-to-tray left it alive over the desktop until the form was disposed.
                        _dragHooked = true;
                        LocationChanged += (s, e) => SyncDragProxy();
                        Resize += (s, e) => SyncDragProxy();
                        VisibleChanged += (s, e) => { if (!Visible) HideDragProxy(); };
                    }
                    PlaceDragProxy();
                    if (!_dragProxy.Visible) _dragProxy.Show();
                    _dragProxy.TopMost = true;
                }
                catch { }
            });
        }
        private void PlaceDragProxy()
        {
            if (_dragProxy == null || _webView == null) return;
            var origin = _webView.PointToScreen(Point.Empty);
            int x = origin.X + (int)Math.Round(_dragLf * _webView.Width);
            int y = origin.Y + (int)Math.Round(_dragTf * _webView.Height);
            int w = Math.Max(1, (int)Math.Round(_dragWf * _webView.Width));
            int h = Math.Max(1, (int)Math.Round(_dragHf * _webView.Height));
            _dragProxy.Bounds = new Rectangle(x, y, w, h);
            try { SetWindowPos(_dragProxy.Handle, new IntPtr(-1) /*HWND_TOPMOST*/, x, y, w, h, SWP_NOACTIVATE); } catch { }
        }
        private void SyncDragProxy() { try { if (_dragProxy != null && _dragProxy.Visible) PlaceDragProxy(); } catch { } }

        public void HideDragProxy() { ClipUi(() => { try { if (_dragProxy != null && _dragProxy.Visible) _dragProxy.Hide(); } catch { } }); }
    }
}
