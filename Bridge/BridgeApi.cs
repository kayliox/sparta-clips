// The single [ComVisible] object exposed to the WebView front-end as window.chrome.webview.hostObjects.clipsBridge
// (wrapped as clipsNative in JS). Split into concern partials: BridgeApi.Capture / .ClipPlayer / .Share / .Config.
// Every method is best-effort — a failure surfaces as an in-app toast, never a crash. Carried from Sparta Dock's
// BridgeApi.cs, trimmed to the clip surface.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;

namespace SpartaClips
{
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComVisible(true)]
    public partial class BridgeApi
    {
        private readonly CoreWebView2 _core;
        private readonly MainForm _form;

        public BridgeApi(CoreWebView2 core, MainForm form)
        {
            _core = core; _form = form;
        }

        // Marshal a JS call to the UI thread (safe from background tasks); no-op if the WebView isn't ready.
        private void PostToWeb(string script)
        {
            try { _form.BeginInvoke(new Action(() => { try { _core.ExecuteScriptAsync(script); } catch { } })); }
            catch { }
        }

        private static void Run(string file, string args = "") =>
            Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });

        // Thread-safe toast: marshals to the UI thread (use from background tasks).
        private void Toast(string title, string msg) =>
            PostToWeb($"window.showNativeToast && window.showNativeToast({JsonConvert.SerializeObject(title)},{JsonConvert.SerializeObject(msg)})");
    }
}
