# verify_elevated.py — verify the elevated-autostart Scheduled Task end-to-end (everything short of the reboot).
#
# Checks: task exists · runs at HighestAvailable · logon trigger · points at the deployed exe with --tray ·
# HKCU Run key removed (task replaces it) · and, if the app is up with CDP, that the LIVE process elevation matches.
# Prints the remaining HUMAN steps (UAC approval / sign-out test).  Run:  python tools/verify_elevated.py
import os, re, subprocess, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # cp1252 console chokes on arrows/box chars

TASK = "SpartaClips"


def run(args):
    p = subprocess.run(args, capture_output=True, text=True)
    return p.returncode, p.stdout + p.stderr


def main():
    ok_all = True

    code, xml = run(["schtasks", "/Query", "/TN", TASK, "/XML"])
    if code != 0:
        print("TASK NOT INSTALLED — the 'Start elevated' toggle hasn't been enabled (or UAC was cancelled).")
        print("To install: Settings → Sparta Clips → 'Start elevated (anti-cheat games)' → approve the UAC prompt,")
        print("then re-run this script.")
        sys.exit(2)

    def field(name, pattern, expect_desc):
        nonlocal ok_all
        m = re.search(pattern, xml, re.S)
        ok = bool(m)
        ok_all &= ok
        print(("PASS  " if ok else "FAIL  ") + f"{name}: {expect_desc}" + ("" if ok else "  — not found in task XML"))
        return m

    field("run level", r"<RunLevel>\s*HighestAvailable\s*</RunLevel>", "HighestAvailable (elevated)")
    field("trigger", r"<LogonTrigger>", "at logon")
    cmd = re.search(r"<Command>\s*\"?([^\"<]+)\"?\s*</Command>", xml)
    args = re.search(r"<Arguments>\s*([^<]*)</Arguments>", xml)
    exe_ok = bool(cmd and os.path.exists(cmd.group(1).strip()))
    tray_ok = bool(args and "--tray" in args.group(1))
    ok_all &= exe_ok and tray_ok
    print(("PASS  " if exe_ok else "FAIL  ") + f"command exists: {cmd.group(1).strip() if cmd else '?'}")
    print(("PASS  " if tray_ok else "FAIL  ") + "arguments include --tray (starts hidden)")

    code, out = run(["reg", "query", r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "/v", "SpartaClips"])
    runkey_gone = code != 0
    ok_all &= runkey_gone
    print(("PASS  " if runkey_gone else "FAIL  ") + "HKCU Run key removed (the task replaces it)"
          + ("" if runkey_gone else " — BOTH mechanisms present: expect a benign duplicate-launch at logon"))

    # If the app is reachable over CDP, confirm the LIVE process elevation (only true after a task-launched start).
    try:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from uidrive import Cdp
        d = Cdp(9333, "spartaclips.local").eval(
            "(async () => JSON.parse(await chrome.webview.hostObjects.clipsBridge.GetEngineDiag()))()")
        print(f"INFO  live app elevation right now: {d.get('elevated')} "
              "(False is EXPECTED until the app is started BY the task — sign out/in to test)")
    except BaseException:
        print("INFO  app not reachable over CDP — live elevation not checked (fine)")

    print("\nRemaining HUMAN steps:")
    print("  1. Sign out and back in (or run the task once from Task Scheduler).")
    print("  2. Tray icon appears; run `python tools/verify_elevated.py` with the app in CDP mode → elevation True.")
    print("  3. Focus an ELEVATED window (e.g. an admin terminal) and press the save-replay hotkey → replay saves.")
    sys.exit(0 if ok_all else 1)


if __name__ == "__main__":
    main()
