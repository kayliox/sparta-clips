// Entry point. Single-instance: a second launch pokes the running app to the front and exits. Runs as a tray app
// (TrayCore ApplicationContext) with the WebView2 window created on demand. "--tray" starts hidden (used by the
// auto-start Run key). Clip playback is an in-page HTML5 <video> now — no libVLC, so no plugin-cache warm needed.
using System;
using System.Threading;
using System.Windows.Forms;

namespace SpartaClips
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            args ??= Array.Empty<string>();

            // Elevated-autostart helper verbs (launched via UAC "runas" by ElevatedTask.RunElevated): perform the one
            // schtasks action and exit — BEFORE the single-instance mutex, so they work while the app is running.
            if (Array.Exists(args, a => string.Equals(a, "--install-elevated-task", StringComparison.OrdinalIgnoreCase)))
            { Environment.Exit(ElevatedTask.Install() ? 0 : 1); return; }
            if (Array.Exists(args, a => string.Equals(a, "--uninstall-elevated-task", StringComparison.OrdinalIgnoreCase)))
            { Environment.Exit(ElevatedTask.Uninstall() ? 0 : 1); return; }

            bool startHidden = Array.Exists(args, a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            using var instanceLock = new Mutex(true, @"Local\SpartaClips.SingleInstance", out bool firstInstance);
            using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SpartaClips.ShowWindow");
            if (!firstInstance)
            {
                // Only an EXPLICIT relaunch wakes the running instance's window — a duplicate silent launch (the
                // autostart Run key firing "--tray" while the app already runs) used to pop the UI uninvited.
                if (!startHidden) { try { showSignal.Set(); } catch { } }
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Crash log → the same spartaclips.log the Settings "Open logs" opens, tagged CRASH. UI-thread exceptions
            // are caught (logged, app keeps running); background/domain/unobserved ones are logged before teardown.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => { try { DiagLog.Crash("CRASH", "UI-thread exception:\n" + e.Exception); } catch { } };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { DiagLog.Crash("CRASH", "fatal exception:\n" + (e.ExceptionObject?.ToString() ?? "(unknown)")); } catch { } };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { try { DiagLog.Crash("CRASH", "unobserved task exception:\n" + e.Exception); e.SetObserved(); } catch { } };

            try { DiagLog.Mark("App start"); } catch { }
            System.Threading.Tasks.Task.Run(TempMaintenance.SweepStartup);   // clear last session's transient temp + age-prune caches, off the startup path
            var ctx = new TrayCore(startHidden);
            var waker = new Thread(() =>
            {
                while (true)
                {
                    try { showSignal.WaitOne(); } catch { break; }   // handle disposed → process exiting
                    try { TrayCore.Instance?.OpenWindow(null); } catch { }
                }
            }) { IsBackground = true, Name = "SecondInstanceWaker" };
            waker.Start();

            Application.Run(ctx);
            GC.KeepAlive(instanceLock);
        }
    }
}
