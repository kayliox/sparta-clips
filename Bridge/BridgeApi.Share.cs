// JS↔C# surface for native clip sharing. Renders the clip (mix sidecars + trim + size tier + fps) under a chosen
// filename and copies it to the clipboard so the user can paste it straight into Discord (Ctrl+V). Off-thread;
// reports via window.onShare(stage, pct, clipName). No webhook/OAuth — Discord receives a pasted file.
using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace SpartaClips
{
    public partial class BridgeApi
    {
        // Copy the already-rendered share file to the clipboard so the user can paste it into Discord (Ctrl+V).
        public void CopyRenderedShare()
        {
            string path = _form?.DragSharePath ?? "";
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { PostToWeb("window.onShare && window.onShare(\"notready\",0,\"\")"); return; }
            try
            {
                _form?.Invoke(new Action(() => { try { var c = new System.Collections.Specialized.StringCollection(); c.Add(path); Clipboard.SetFileDropList(c); } catch { } }));
                PostToWeb("window.onShare && window.onShare(\"copied\",100,\"\")");
            }
            catch { PostToWeb("window.onShare && window.onShare(\"error\",0,\"\")"); }
        }

        // ── SteelSeries-style drag-out: render the file ahead of time, then a native window over the popup's drag box
        //    starts a real OS file drag into Discord (see MainForm.DragShare). Render → window.onShareRendered(ok). ──
        private static int _shareReqSeq;   // only the LATEST tier/fps render arms the drag file (older passes are ignored)
        private static System.Threading.CancellationTokenSource? _shareCts;   // the in-flight render — cancelled by the next one
        public void CancelShareRender() { try { System.Threading.Interlocked.Exchange(ref _shareCts, null)?.Cancel(); } catch { } }
        public void RenderShareForDrag(string clipName, string outName, int tierMb, int fps, double vidIn, double vidOut, double durSec, string mixJson)
        {
            ClipShare.Mix? mix = null;
            try { if (!string.IsNullOrWhiteSpace(mixJson)) mix = JsonConvert.DeserializeObject<ClipShare.Mix>(mixJson); } catch { }
            int myReq = System.Threading.Interlocked.Increment(ref _shareReqSeq);
            var cts = new System.Threading.CancellationTokenSource();
            try { System.Threading.Interlocked.Exchange(ref _shareCts, cts)?.Cancel(); } catch { }   // kill the superseded render's ffmpeg now
            bool Latest() => myReq == System.Threading.Volatile.Read(ref _shareReqSeq);
            _form?.SetDragSharePath("");   // a render is starting → nothing to drag yet
            Task.Run(async () =>
            {
                try
                {
                    PostToWeb("window.onShare && window.onShare(\"render\",10," + JsonConvert.SerializeObject(clipName) + ")");
                    string? path = await ClipShare.RenderAsync(clipName, outName, tierMb, fps, vidIn, vidOut, durSec,
                        (stage, pct) => { if (Latest()) PostToWeb("window.onShare && window.onShare(" + JsonConvert.SerializeObject(stage) + "," + pct + "," + JsonConvert.SerializeObject(clipName) + ")"); }, mix, cts.Token);
                    if (!Latest()) return;   // a newer render superseded this one → don't arm the stale file
                    long bytes = 0; try { if (path != null && System.IO.File.Exists(path)) bytes = new System.IO.FileInfo(path).Length; } catch { }
                    _form?.SetDragSharePath(path ?? "");
                    PostToWeb("window.onShareRendered && window.onShareRendered(" + (path != null ? "true" : "false") + "," + bytes + ")");
                }
                catch { if (Latest()) PostToWeb("window.onShareRendered && window.onShareRendered(false,0)"); }
            });
        }
        public void ShareDragArm(string json) { try { var r = JsonConvert.DeserializeObject<RectDto>(json); if (r != null) _form.ShowDragProxy(r.lf, r.tf, r.wf, r.hf); } catch { } }
        public void ShareDragDisarm() { try { _form.HideDragProxy(); } catch { } }
    }
}
