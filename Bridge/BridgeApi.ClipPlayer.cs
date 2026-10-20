// BridgeApi (partial) - keyframe probe for the in-page clip editor. Playback itself is now an HTML5 <video> in the
// WebView (web/app.js) — no native surface. C# only supplies keyframe times so the editor can snap the scrubber +
// bookmarks to real landing points; posted back via window.onClipKeyframes.
using Newtonsoft.Json;

namespace SpartaClips
{
    public partial class BridgeApi
    {
        public void GetClipKeyframes(string name)
        {
            // PathOf (File.Exists against the possibly-SMB clips folder) belongs off-thread too, not just the probe.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var orig = ClipLibrary.PathOf(name); if (orig == null) return;
                    PostKeyframes(name, orig);
                }
                catch { }
            });
        }
        private void PostKeyframes(string name, string path)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                var kf = await ClipFfmpeg.KeyframesAsync(path);
                try { PostToWeb("window.onClipKeyframes && window.onClipKeyframes(" + JsonConvert.SerializeObject(name) + "," + JsonConvert.SerializeObject(kf) + ")"); } catch { }
            });
        }

        // Fractional rect {lf,tf,wf,hf} of the WebView client — still used by the drag-to-Discord proxy (BridgeApi.Share).
        private sealed class RectDto { public double lf { get; set; } public double tf { get; set; } public double wf { get; set; } public double hf { get; set; } }
    }
}
