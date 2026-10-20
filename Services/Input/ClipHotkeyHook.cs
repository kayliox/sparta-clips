// Low-level keyboard hook for the clip hotkeys (record / save-replay). RegisterHotKey's WM_HOTKEY is NOT delivered
// while a fullscreen, anti-cheat game (League/TFT under Vanguard) has focus — even when we're elevated — so the
// global hotkey "does nothing in game." A WH_KEYBOARD_LL hook sees the keystroke earlier in the input path and fires
// regardless of the foreground app: the same mechanism OBS / SteelSeries use to make in-game hotkeys work. Runs in
// the (elevated) launcher; matches are consumed and the action fires off-thread. Paired with the RegisterHotKey path
// (deduped in MainForm) so the desktop case keeps working if the hook ever fails to install.
using System;
using System.Runtime.InteropServices;

namespace SpartaClips
{
    internal static class ClipHotkeyHook
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        private static IntPtr _hook;
        private static HookProc? _proc;   // hold a ref so the delegate isn't GC'd while the hook is live
        private static uint _recMods, _recVk, _repMods, _repVk, _markMods, _markVk;   // 0 vk = unbound
        private static Action? _onRecord, _onReplay, _onMark;

        /// <summary>Set the current combos (from the user's settings) + the actions to fire. Call before Install/on change.</summary>
        public static void Configure(uint recMods, uint recVk, uint repMods, uint repVk, uint markMods, uint markVk,
            Action onRecord, Action onReplay, Action onMark)
        {
            _recMods = recMods; _recVk = recVk; _repMods = repMods; _repVk = repVk; _markMods = markMods; _markVk = markVk;
            _onRecord = onRecord; _onReplay = onReplay; _onMark = onMark;
        }

        /// <summary>Install on the CALLING thread (must pump messages — call from the UI thread). Idempotent. Returns
        /// whether the hook is active — false means global hotkeys won't fire (e.g. blocked by security software).</summary>
        public static bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            try { _proc = HookCallback; _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0); }
            catch { _hook = IntPtr.Zero; }
            bool ok = _hook != IntPtr.Zero;
            try { DiagLog.Line("CLIP", "kbd hook " + (ok ? "installed" : "FAILED")); } catch { }
            return ok;
        }
        public static void Uninstall() { try { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } } catch { } }

        private static uint CurMods()
        {
            uint m = 0;
            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= MOD_CONTROL;
            if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) m |= MOD_ALT;
            if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) m |= MOD_SHIFT;
            if (((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0) m |= MOD_WIN;
            return m;
        }

        // Hot path — EVERY keystroke system-wide runs through this serialized hook chain, so the non-matching case must
        // be as close to free as possible: compare the vk FIRST (almost never matches) and only then read the modifier
        // state (4 GetAsyncKeyState syscalls). Logging happens on the dispatched worker, never in the callback — a
        // blocked file write here stalls all input and risks the OS silently evicting the hook (LowLevelHooksTimeout).
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        uint vk = (uint)Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode is the first field
                        bool rep = _repVk != 0 && vk == _repVk, rec = _recVk != 0 && vk == _recVk, mark = _markVk != 0 && vk == _markVk;
                        if (rep || rec || mark)
                        {
                            uint mods = CurMods();
                            if (rep && mods == _repMods) { Fire("save replay", _onReplay); return (IntPtr)1; }
                            if (rec && mods == _recMods) { Fire("record", _onRecord); return (IntPtr)1; }
                            if (mark && mods == _markMods) { Fire("mark", _onMark); return (IntPtr)1; }
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        private static void Fire(string what, Action? a)
        {
            System.Threading.Tasks.Task.Run(() => { try { DiagLog.Line("CLIP", "hotkey(hook): " + what); } catch { } try { a?.Invoke(); } catch { } });
        }
    }
}
