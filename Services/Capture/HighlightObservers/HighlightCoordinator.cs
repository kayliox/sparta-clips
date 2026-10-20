// Drives the match observers: one background loop polls each running game's observer every 5s and, on a new
// highlight, saves the live instant-replay buffer (the SAME path the hotkey uses) — with a cooldown so a
// teamfight makes ONE clip, not five. Only active while the replay buffer is armed AND the user opted in.
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpartaClips
{
    internal static class HighlightCoordinator
    {
        private static readonly IMatchObserver[] _observers = { new LeagueObserver() };   // LoL + TFT share this client/endpoint
        private static CancellationTokenSource? _cts;
        private static DateTime _lastSave = DateTime.MinValue;
        public static bool Enabled;   // bound to the "Auto-highlight" capture setting (opt-in)

        private static readonly object _startLock = new();
        public static void Start()
        {
            CancellationTokenSource cts;
            lock (_startLock)   // ArmAsync (pool) + SetAutoHighlight (UI) can race here — two loops over one observer doubled the auto-clips, and the losing CTS's loop outlived every Stop()
            {
                if (!Enabled || _cts != null) return;
                cts = new CancellationTokenSource();
                _cts = cts;
            }
            var ct = cts.Token;
            Task.Run(async () =>
            {
                DiagLog.Line("HILITE", "auto-highlight loop on");
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var o in _observers)
                            if (o.ProcessPresent()) await o.PollAsync(OnHighlight, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { DiagLog.Line("HILITE", "poll err: " + ex.Message); }
                    try { await Task.Delay(5000, ct); } catch { break; }
                }
                DiagLog.Line("HILITE", "auto-highlight loop off");
            }, ct);
        }

        public static void Stop() { lock (_startLock) { try { _cts?.Cancel(); } catch { } _cts = null; } }

        private static void OnHighlight(string label)
        {
            int cooldown = Math.Max(10, ClipRecorder.ReplayLenSeconds);   // one clip per replay-length window
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < cooldown) return;
            if (!ClipRecorder.IsReplayArmed) return;                       // buffer must be live to save it
            _lastSave = DateTime.UtcNow;
            DiagLog.Line("HILITE", "auto-clip: " + label);
            try { ClipRecorder.Notify?.Invoke("autoclip", "{\"label\":" + JsonSerializer.Serialize(label) + "}"); } catch { }
            _ = ClipRecorder.SaveReplayAsync();                            // same save the hotkey calls (chime + gallery refresh)
        }
    }
}
