// Crash safety net. Sparta Clips lives in the tray for hours, driving native code it doesn't own — the WGC capture /
// recorder library and the GPU vendor's hardware encoder. A fault in there kills the whole process with no managed
// exception to catch (crash dumps 2026-10-21/22: an exception escaping ScreenRecorderLib's frame handler; heap
// corruption detected inside amdh264enc64). To the user that looked like "the app closes on its own after a couple
// of hours" — instant replay silently gone.
//
// The main instance starts a tiny copy of itself with "--watchdog <pid>" (no window, no WinForms, ~15 MB). It waits
// for the app to exit; a deliberate quit (TrayCore.QuitApp → MarkCleanExit) leaves it alone, anything else restarts
// the app to the tray with "--recovered" (which shows a short notice). Guards: no restart during a Windows shutdown,
// when another instance is already running (an installer relaunch / the user), or after 3 restarts in 10 minutes.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpartaClips
{
    internal static class Watchdog
    {
        private static string CleanExitPath => Path.Combine(AppConfig.Dir, ".cleanexit");
        private static string RestartsPath  => Path.Combine(AppConfig.Dir, ".restarts");

        /// <summary>Main instance: launch the watchdog for this process (best effort).</summary>
        public static void Start()
        {
            try
            {
                string? exe = Environment.ProcessPath; if (exe == null) return;
                try { File.Delete(CleanExitPath); } catch { }
                Process.Start(new ProcessStartInfo(exe, "--watchdog " + Environment.ProcessId) { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) { try { DiagLog.Line("APP", "watchdog start failed: " + ex.Message); } catch { } }
        }

        /// <summary>Called on every deliberate quit, just before the process exits.</summary>
        public static void MarkCleanExit()
        {
            try { Directory.CreateDirectory(AppConfig.Dir); File.WriteAllText(CleanExitPath, Environment.ProcessId.ToString()); } catch { }
        }

        /// <summary>Watchdog process body: returns when the watched app exits (restarting it if it crashed).</summary>
        public static void Run(int pid)
        {
            Process app;
            try { app = Process.GetProcessById(pid); } catch { return; }
            using (app)
            {
                try { app.WaitForExit(); } catch { return; }
                int code = 0; try { code = app.ExitCode; } catch { }
                if (WasClean(pid)) return;

                Thread.Sleep(3000);   // an installer / Windows shutdown / taskkill /IM gets to finish (and to stop us) first
                if (GetSystemMetrics(0x2000 /*SM_SHUTTINGDOWN*/) != 0) return;
                if (WasClean(pid)) return;
                if (Mutex.TryOpenExisting(Program.InstanceMutexName, out var m)) { m.Dispose(); return; }   // a real instance is up (only real instances hold it)
                if (!AllowRestart()) { Log($"app exited unexpectedly (0x{code:X8}) — 3 restarts in 10 min, giving up"); return; }

                Log($"app exited unexpectedly (0x{code:X8}) — restarting to the tray");
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--tray --recovered") { UseShellExecute = false }); }
                catch (Exception ex) { Log("restart failed: " + ex.Message); }
            }
        }

        private static bool WasClean(int pid)
        {
            try { return File.Exists(CleanExitPath) && File.ReadAllText(CleanExitPath).Trim() == pid.ToString(); } catch { return false; }
        }
        // Crash-loop guard: at most 3 automatic restarts per 10 minutes.
        private static bool AllowRestart()
        {
            try
            {
                var now = DateTime.UtcNow;
                var recent = (File.Exists(RestartsPath) ? File.ReadAllLines(RestartsPath) : Array.Empty<string>())
                    .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                    .Where(t => now - t < TimeSpan.FromMinutes(10)).ToList();
                if (recent.Count >= 3) return false;
                recent.Add(now);
                Directory.CreateDirectory(AppConfig.Dir);
                File.WriteAllLines(RestartsPath, recent.Select(t => t.ToString("o")));
                return true;
            }
            catch { return true; }
        }
        private static void Log(string m) { try { DiagLog.Line("WATCHDOG", m); } catch { } }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    }
}
