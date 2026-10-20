// Elevated autostart — a Task Scheduler logon task with /RL HIGHEST, so the low-level keyboard hook keeps firing
// over ELEVATED fullscreen anti-cheat games (the HKCU Run key launches non-elevated, which those games out-rank).
// Creating/deleting the task needs admin: the app relaunches ITSELF elevated with --install/--uninstall-elevated-task
// (one UAC prompt that names Sparta Clips, not schtasks), and that helper instance runs schtasks and exits.
using System;
using System.Diagnostics;

namespace SpartaClips
{
    internal static class ElevatedTask
    {
        public const string TaskName = "SpartaClips";

        /// <summary>True when the logon task exists (schtasks query — spawn a process, keep OFF the UI thread).</summary>
        public static bool Exists()
        {
            try
            {
                using var p = Start("/Query /TN \"" + TaskName + "\""); if (p == null) return false;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        // The two verbs the ELEVATED helper instance executes (see Program.cs --install/--uninstall-elevated-task).
        public static bool Install()
        {
            string exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            return Run("/Create /F /TN \"" + TaskName + "\" /SC ONLOGON /RL HIGHEST /TR \"\\\"" + exe + "\\\" --tray\"");
        }
        public static bool Uninstall() => Run("/Delete /F /TN \"" + TaskName + "\"");

        /// <summary>From the NON-elevated app: relaunch ourselves elevated to install/uninstall the task (one UAC
        /// prompt), wait, then verify against the OBSERVABLE state — a cancelled prompt just leaves it unchanged.</summary>
        public static bool RunElevated(bool install)
        {
            try
            {
                string exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
                var psi = new ProcessStartInfo(exe, install ? "--install-elevated-task" : "--uninstall-elevated-task")
                { UseShellExecute = true, Verb = "runas" };
                using var p = Process.Start(psi); if (p == null) return false;
                p.WaitForExit(60000);
            }
            catch { /* user cancelled the UAC prompt (Win32Exception 1223) — fall through to the state check */ }
            return Exists() == install;
        }

        private static bool Run(string args)
        {
            try
            {
                using var p = Start(args); if (p == null) return false;
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode != 0) DiagLog.Line("TASK", "schtasks exit " + p.ExitCode + ": " + err.Trim());
                return p.ExitCode == 0;
            }
            catch (Exception ex) { DiagLog.Line("TASK", "schtasks err: " + ex.Message); return false; }
        }
        private static Process? Start(string args) => Process.Start(new ProcessStartInfo("schtasks.exe", args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
    }
}
