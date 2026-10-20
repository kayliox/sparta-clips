# stress.py — replay-engine concurrency stress harness (the risk area no UI smoke test can reach).
#
# Drives the REAL engine through the bridge over CDP and asserts invariants against GetEngineDiag after each phase:
#   1. arm/disarm hammer            — flags + recorder + rings must settle clean
#   2. save-vs-disarm race          — the exact orphan-recorder window (audit batch 1 fix)
#   3. source-switch while armed    — ReArmAsync storm must end with ONE live recorder
#   4. pinned window dies mid-armed — segment failure must auto-restart the buffer (batch 2 fix)
# Cleans up any test replays it caused. Run after engine changes:  python tools/stress.py
import ctypes, json, os, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uidrive import Cdp
from smoke import relaunch, PORT
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # cp1252 console chokes on arrows/box chars

BRIDGE = "chrome.webview.hostObjects.clipsBridge"
RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, ok))
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else f"  — {detail}"))


class Engine:
    def __init__(self, cdp):
        self.cdp = cdp

    def call(self, expr):
        return self.cdp.eval(f"(async () => {{ const b = {BRIDGE}; return {expr}; }})()")

    def diag(self):
        return json.loads(self.call("await b.GetEngineDiag()"))

    def wait_settle(self, pred, secs=12, what="condition"):
        deadline = time.time() + secs
        d = None
        while time.time() < deadline:
            d = self.diag()
            if pred(d):
                return d
            time.sleep(0.4)
        raise AssertionError(f"never settled: {what} — last diag {d}")


def clips_snapshot(clips_dir):
    try:
        return set(os.listdir(clips_dir))
    except OSError:
        return set()


def spawn_pin_window(title="SpartaStressPin"):
    """A window we OWN (win11 notepad/paint are packaged apps whose windows belong to other processes):
    a python-subprocess tkinter window, found by exact title. Returns (process, hwnd)."""
    p = subprocess.Popen([sys.executable, "-c",
        f"import tkinter as tk; r = tk.Tk(); r.title('{title}'); r.geometry('500x400+80+80'); r.mainloop()"])
    user32 = ctypes.windll.user32
    for _ in range(40):
        time.sleep(0.25)
        h = user32.FindWindowW(None, title)
        if h:
            return p, h
    p.kill()
    return p, None


def main():
    relaunch(with_cdp=True)
    cdp = Cdp(PORT, "spartaclips.local")
    eng = Engine(cdp)
    clips_dir = eng.call("String(await b.GetClipsDir())")
    before = clips_snapshot(clips_dir)

    disarmed_clean = lambda d: (not d["armed"] and not d["repRecAlive"] and not d["rotTimerAlive"]
                                and not d["micRing"] and not d["chatRing"])
    armed_healthy = lambda d: d["armed"] and d["repRecAlive"] and d["rotTimerAlive"]

    # Baseline: whatever the arm policy did at boot, force a known disarmed state first.
    eng.call("b.DisarmReplay()")
    eng.wait_settle(disarmed_clean, what="baseline disarm")

    # ── Phase 1: arm/disarm hammer ──
    try:
        for i in range(8):
            eng.call("b.ArmReplay('')")
            time.sleep(0.15 + (i % 3) * 0.3)
            eng.call("b.DisarmReplay()")
            time.sleep(0.1 + (i % 2) * 0.25)
        d = eng.wait_settle(disarmed_clean, what="hammer settle")
        check("phase 1: arm/disarm hammer settles clean", True)
    except AssertionError as ex:
        check("phase 1: arm/disarm hammer settles clean", False, str(ex))

    # ── Phase 2: save racing disarm (the orphan-recorder window) ──
    try:
        for i in range(6):
            eng.call("b.ArmReplay('')")
            eng.wait_settle(armed_healthy, what="arm before race")
            time.sleep(1.0)
            eng.call("b.SaveReplay(), b.DisarmReplay()")            # near-simultaneous
            time.sleep(0.5)
            eng.call("b.DisarmReplay(), b.SaveReplay()")            # and the other order
            time.sleep(1.5)
            eng.call("b.DisarmReplay()")
        d = eng.wait_settle(disarmed_clean, secs=25, what="race settle")
        b0 = eng.diag()["bufBytes"]; time.sleep(6); b1 = eng.diag()["bufBytes"]
        orphan = b1 > b0  # an orphan recorder keeps writing segments while "disarmed"
        check("phase 2: save-vs-disarm leaves no orphan recorder", not orphan,
              f"buffer grew while disarmed: {b0} -> {b1}")
    except AssertionError as ex:
        check("phase 2: save-vs-disarm leaves no orphan recorder", False, str(ex))

    # ── Phase 3: source-switch storm while armed ──
    try:
        eng.call("b.ArmReplay('')")
        eng.wait_settle(armed_healthy, what="arm before switch storm")
        for mode, hwnd in [("display", ""), ("window", "12345678"), ("auto", ""),
                           ("display", ""), ("auto", "")]:
            eng.call(f"b.SetCaptureSource('{mode}', '{hwnd}')")
            time.sleep(0.6)
        d = eng.wait_settle(lambda d: armed_healthy(d) and d["srcMode"] == "auto", secs=20,
                            what="armed + srcMode auto after storm")
        check("phase 3: source-switch storm keeps one healthy armed buffer", True)
    except AssertionError as ex:
        check("phase 3: source-switch storm keeps one healthy armed buffer", False, str(ex))

    # ── Phase 4: pinned window dies while armed → buffer self-heals ──
    try:
        np, hwnd = spawn_pin_window()
        if not hwnd:
            raise AssertionError("couldn't create/find the pin window")
        eng.call(f"b.SetCaptureSource('window', '{int(hwnd)}')")
        d = eng.wait_settle(lambda d: armed_healthy(d) and d["srcMode"] == "window", secs=15,
                            what="armed on pinned window")
        np.kill(); np.wait()
        time.sleep(5)  # segment failure + 1s damp + restart
        d = eng.wait_settle(armed_healthy, secs=15, what="buffer alive after window death")
        # Growth check needs FRAMES: WGC only delivers on screen change, so a static desktop legitimately
        # produces ~no bytes. Animate a SMALL, low-contrast patch in the window corner while measuring —
        # NEVER a full-screen flash (an earlier version strobed an invert filter: photosensitivity hazard).
        eng.cdp.eval("(() => { const st = document.createElement('style');"
                     " st.textContent = '@keyframes _sw{to{background-position:200% 0}}'; document.head.appendChild(st);"
                     " const d = document.createElement('div'); d.id = '_stressWiggle';"
                     " d.style.cssText = 'position:fixed;left:12px;bottom:12px;width:160px;height:90px;z-index:99999;"
                     "border-radius:12px;opacity:.9;background:linear-gradient(90deg,#223,#446,#223);"
                     "background-size:200% 100%;animation:_sw .6s linear infinite';"
                     " document.body.appendChild(d); setTimeout(() => { d.remove(); st.remove(); }, 25000); return 'wiggle on'; })()")
        # Byte-growth probes lie (the MF sink's open file reports a stale size even through a shared-read open),
        # so assert the USER guarantee instead: after the self-heal, Save Replay must land a real clip.
        time.sleep(5)   # let the fresh segment accumulate footage (the corner wiggle is animating)
        pre = clips_snapshot(clips_dir)
        eng.call("b.SaveReplay()")
        new_clip = None
        for _ in range(40):
            time.sleep(1)
            for f in clips_snapshot(clips_dir) - pre:
                p = os.path.join(clips_dir, f)
                if f.endswith(".mp4") and os.path.exists(p) and os.path.getsize(p) > 100_000:
                    new_clip = f
            if new_clip:
                break
        check("phase 4: pinned-window death self-heals (save-replay lands a clip after)", bool(new_clip),
              "no replay landed after the self-heal")
        eng.call("b.SetCaptureSource('auto', '')")   # teardown's immediate Disarm then RACES this re-arm — that's deliberate (tests the _wantArmed fix)
    except AssertionError as ex:
        check("phase 4: pinned-window death self-heals (buffer keeps recording)", False, str(ex))

    # ── Teardown: restore the boot arm policy's state, clean any replays this run saved ──
    eng.call("b.DisarmReplay()")
    try:
        eng.wait_settle(disarmed_clean, secs=20, what="final disarm")
        check("teardown: engine fully quiescent", True)
    except AssertionError as ex:
        check("teardown: engine fully quiescent", False, str(ex))
    eng.call("b.ArmReplay('')")   # leave the replay armed as it was at boot (replayEnabled default)
    for f in sorted(clips_snapshot(clips_dir) - before):
        try:
            os.remove(os.path.join(clips_dir, f))
            print("cleaned test artifact:", f)
        except OSError:
            pass

    cdp.ws.close()
    relaunch(with_cdp=False)
    fails = [n for n, ok in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(fails)}/{len(RESULTS)} checks passed" + (f" — FAILURES: {', '.join(fails)}" if fails else " — all green"))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
