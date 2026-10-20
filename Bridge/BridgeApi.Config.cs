// BridgeApi (partial) — settings persistence + app-shell controls for the standalone. The web `state` is mirrored to
// %APPDATA%\SpartaClips\config.json so the tray core can read capture prefs/hotkeys before any window exists.
using System;
using System.Windows.Forms;

namespace SpartaClips
{
    public partial class BridgeApi
    {
        public void SaveConfig(string json) { AppConfig.SaveRaw(json); }
        public string LoadConfig() => AppConfig.LoadRaw();
        public void QuarantineConfig() { try { AppConfig.QuarantineCorrupt(); } catch { } }   // set aside an unreadable config before defaults overwrite it

        // Folder picker for the clips save location → window.onFolderPicked(path|""). The modal must NOT run inside
        // the bridge COM dispatch (its message pump keeps the cross-process call open for the dialog's whole lifetime
        // — the post-modal freeze class fixed for move/size in BeginModalMouseLoop), so it's deferred to the pump via
        // BeginInvoke and answers by callback; the JS wrapper re-wraps that in a promise so call sites still await.
        public void PickFolder(string startDir)
        {
            void Answer(string path) { try { PostToWeb("window.onFolderPicked && window.onFolderPicked(" + Newtonsoft.Json.JsonConvert.SerializeObject(path ?? "") + ")"); } catch { } }
            try
            {
                _form.BeginInvoke(new Action(() =>
                {
                    string result = "";
                    try
                    {
                        using var dlg = new FolderBrowserDialog { Description = "Choose where clips are saved", UseDescriptionForTitle = true };
                        try { if (!string.IsNullOrWhiteSpace(startDir) && System.IO.Directory.Exists(startDir)) dlg.SelectedPath = startDir; } catch { }
                        if (dlg.ShowDialog(_form) == DialogResult.OK) result = dlg.SelectedPath ?? "";
                    }
                    catch { }
                    Answer(result);
                }));
            }
            catch { Answer(""); }
        }

        // Save-file picker for "Export to…" → window.onSaveFilePicked(path|""). Same deferred-modal pattern as
        // PickFolder. filter is a Win32 dialog filter (e.g. "MP4 video|*.mp4"); defaultName pre-fills the file name.
        public void PickSaveFile(string defaultName, string filter)
        {
            void Answer(string path) { try { PostToWeb("window.onSaveFilePicked && window.onSaveFilePicked(" + Newtonsoft.Json.JsonConvert.SerializeObject(path ?? "") + ")"); } catch { } }
            try
            {
                _form.BeginInvoke(new Action(() =>
                {
                    string result = "";
                    try
                    {
                        using var dlg = new SaveFileDialog
                        {
                            Title = "Export clip",
                            FileName = string.IsNullOrWhiteSpace(defaultName) ? "clip" : defaultName,
                            Filter = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter,
                            OverwritePrompt = true, AddExtension = true,
                        };
                        try { string vids = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); if (System.IO.Directory.Exists(vids)) dlg.InitialDirectory = vids; } catch { }
                        if (dlg.ShowDialog(_form) == DialogResult.OK) result = dlg.FileName ?? "";
                    }
                    catch { }
                    Answer(result);
                }));
            }
            catch { Answer(""); }
        }

        // Auto-start on boot (HKCU Run key, "--tray"). The web toggle mirrors this into state too.
        public void SetAutostart(string on) { AppConfig.SetAutostart(on == "true" || on == "1"); }
        public bool IsAutostart() => AppConfig.IsAutostart();

        // ── Elevated autostart: ONE atomic mode-setter ("off" | "on" | "elevated") so a cancelled UAC prompt can't
        // strand a half-transition (e.g. task not created but Run key already removed = autostart silently off).
        // "elevated" = Task Scheduler logon task at highest privileges → hotkeys work over ELEVATED anti-cheat games;
        // "on" = plain HKCU Run key. Off-thread (schtasks spawns + a UAC wait); truth reported via
        // window.onAutostartState({runKey, task}) — the UI reconciles to the OBSERVED state, not the request.
        public void SetAutostartMode(string mode)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string m = mode == "elevated" ? "elevated" : mode == "on" ? "on" : "off";
                    if (m == "elevated")
                    {
                        bool ok = ElevatedTask.Exists() || ElevatedTask.RunElevated(true);
                        if (ok) AppConfig.SetAutostart(false);   // the task replaces the Run key
                    }
                    else
                    {
                        if (ElevatedTask.Exists()) ElevatedTask.RunElevated(false);   // UAC cancel → task stays; state below reports it
                        AppConfig.SetAutostart(m == "on");
                    }
                    DiagLog.Line("TASK", "autostart mode -> " + m);
                }
                catch { }
                PostAutostartState();
            });
        }
        public void RequestAutostartState() { System.Threading.Tasks.Task.Run(PostAutostartState); }
        private void PostAutostartState()
        {
            bool task = false, runKey = false;
            try { task = ElevatedTask.Exists(); } catch { }
            try { runKey = AppConfig.IsAutostart(); } catch { }
            PostToWeb("window.onAutostartState && window.onAutostartState(" + Newtonsoft.Json.JsonConvert.SerializeObject(new { runKey, task }) + ")");
        }

        // Instant-replay arming policy: "always" (armed while running) or "game" (only when a game is foreground).
        public void SetArmPolicy(string mode) { try { TrayCore.Instance?.SetArmPolicy(mode == "game" ? "game" : "always"); } catch { } }

        // Master "instant replay" on/off from settings. C# owns the arming (per arm policy) — the JS never arms directly.
        public void SetReplayEnabled(string on) { try { TrayCore.Instance?.SetReplayEnabled(on == "true" || on == "1"); } catch { } }

        // Fully quit the app from the tray/settings (a window close only hides to tray).
        public void QuitApp() { try { TrayCore.Instance?.QuitApp(); } catch { } }

        // Open the diagnostics log folder.
        public void OpenLogsFolder() { try { System.IO.Directory.CreateDirectory(DiagLog.Dir); Run("explorer.exe", "\"" + DiagLog.Dir + "\""); } catch { } }

        // Renderer-side diagnostics land in the same timeline as the C# ones (the page has no file access of its own).
        public void LogDiag(string msg) { try { if (!string.IsNullOrEmpty(msg)) DiagLog.Line("WEB", msg.Length > 300 ? msg.Substring(0, 300) : msg); } catch { } }

        // ── Frameless window chrome (the custom HTML title bar's controls). Bridge calls run on the UI thread, so
        //    these can drive the form directly. Implementations live in Ui/MainForm.Chrome.cs. ──
        public void WinMinimize()  { try { _form.WinMinimizeSelf(); } catch { } }
        public void WinMaxToggle() { try { _form.WinToggleMaxSelf(); } catch { } }
        public void WinClose()     { try { _form.Close(); } catch { } }
        public void WinBeginDrag() { try { _form.WinBeginDragSelf(); } catch { } }
        public void WinBeginResize(string dir) { try { _form.WinBeginResizeSelf(dir); } catch { } }
    }
}
