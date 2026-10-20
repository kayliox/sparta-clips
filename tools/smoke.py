# smoke.py — one-command UI regression smoke for SpartaClips.
#
# Relaunches the DEPLOYED exe with the CDP hook, drives the real UI through the post-fix checklist via uidrive's
# CDP client, prints PASS/FAIL per check, and leaves a NORMAL (no-CDP) instance running afterwards. Exit code 0 =
# all green. Run after every deploy:  python tools/smoke.py
import json, os, subprocess, sys, time, urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uidrive import Cdp
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # cp1252 console chokes on arrows/box chars

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "SpartaClips-final", "SpartaClips.exe")
PORT = 9333


def relaunch(with_cdp):
    subprocess.run(["taskkill", "/IM", "SpartaClips.exe", "/F"], capture_output=True)
    time.sleep(1.2)
    env = dict(os.environ)
    env.pop("SPARTACLIPS_CDP", None)
    if with_cdp:
        env["SPARTACLIPS_CDP"] = str(PORT)
    subprocess.Popen([EXE], env=env, cwd=os.path.dirname(EXE))
    if with_cdp:
        for _ in range(40):  # wait for the CDP endpoint
            time.sleep(0.5)
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{PORT}/json/version", timeout=1)
                time.sleep(2.5)  # let app.js boot finish
                return
            except OSError:
                pass
        raise SystemExit("CDP endpoint never came up — is this a test build? Deploy with ./deploy.ps1 -TestHooks (normal Release builds omit the CDP hook).")


RESULTS = []


def check(name, fn):
    try:
        ok, detail = fn()
    except BaseException as ex:  # a failed check must not kill the run
        ok, detail = False, f"exception: {ex}"
    RESULTS.append((name, ok, detail))
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else f"  — {detail}"))


def main():
    relaunch(with_cdp=True)
    cdp = Cdp(PORT, "spartaclips.local")
    E = cdp.eval

    # The splash ends ~2s after the page script runs — POLL for it (a fixed sleep raced it and false-failed
    # the boot + Space checks, since the splash's own keydown listener legitimately swallows Space while up).
    check("boot: splash ends + titlebar renders", lambda: (lambda r: (r["splashGone"] and r["srcPill"], r))(E(
        "(async () => { for (let i = 0; i < 40; i++) {"
        "   if (document.getElementById('splash')?.style.display === 'none') break;"
        "   await new Promise(r => setTimeout(r, 250)); }"
        " return { cards: document.querySelectorAll('.gal-card').length,"
        "   splashGone: document.getElementById('splash')?.style.display === 'none',"
        "   srcPill: !!document.getElementById('tbSource') }; })()")))

    check("space key not swallowed (splash fix)", lambda: (lambda r: (r is False, r))(E(
        "(() => { const e = new KeyboardEvent('keydown', { code: 'Space', key: ' ', bubbles: true, cancelable: true });"
        " window.dispatchEvent(e); return e.defaultPrevented; })()")))

    check("bridge: RecElapsedSec / GetClipsDir / IsClipRecording", lambda: (lambda r: (
        isinstance(r["el"], (int, float)) and isinstance(r["dir"], str) and len(r["dir"]) > 3, r))(E(
        "(async () => { const b = chrome.webview.hostObjects.clipsBridge;"
        " return { el: await b.RecElapsedSec(), dir: String(await b.GetClipsDir()), rec: await b.IsClipRecording() }; })()")))

    check("source picker: scan + escape cleanup", lambda: (lambda r: (
        r["opened"] and r["scanned"] and r["closed"] and r["cleared"], r))(E(
        "(async () => { document.getElementById('tbSource').click(); await new Promise(r => setTimeout(r, 150));"
        " const pop = document.getElementById('srcPop'); const opened = !pop.hidden;"
        " document.querySelector('#srcPop .src-mode[data-src=window]').click();"
        " await new Promise(r => setTimeout(r, 2500));"
        " const grid = document.getElementById('srcWinGrid'); const scanned = grid.childElementCount > 0;"
        " document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));"
        " await new Promise(r => setTimeout(r, 100));"
        " return { opened, scanned, closed: pop.hidden, cleared: grid.childElementCount === 0 }; })()")))

    check("editor: open + space play/pause + I-key + escape", lambda: (lambda r: (
        r.get("skipped") or (r["opened"] and r["pausedAfterSpace"] and r["inSet"] and r["closed"]), r))(E(
        "(async () => { const card = document.querySelector('.gal-card'); if (!card) return { skipped: 'no clips' };"
        " card.click(); await new Promise(r => setTimeout(r, 2000));"
        " const pg = document.getElementById('clipPage'); const v = pg.querySelector('video');"
        " const opened = !pg.hidden && !!v;"
        " window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', key: ' ', bubbles: true, cancelable: true }));"
        " await new Promise(r => setTimeout(r, 400)); const pausedAfterSpace = v.paused;"
        " v.currentTime = 3; await new Promise(r => setTimeout(r, 400));"
        " window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyI', key: 'i', bubbles: true, cancelable: true }));"
        " await new Promise(r => setTimeout(r, 200));"
        " const inSet = document.getElementById('clipTlIn')?.textContent !== '0:00';"
        " document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));"
        " await new Promise(r => setTimeout(r, 300));"
        " return { opened, pausedAfterSpace, inSet, closed: pg.hidden }; })()")))

    check("maximize: client flush + corner hits Close + restore", lambda: (lambda r: (
        r["flush"] and r["cornerClose"] and r["gripsHidden"] and r["restored"], r))(E(
        "(async () => { if (document.body.classList.contains('win-maximized'))"   # normalize: start restored
        "   { document.getElementById('winMax').click(); await new Promise(r => setTimeout(r, 500)); }"
        " document.getElementById('winMax').click(); await new Promise(r => setTimeout(r, 700));"
        " const flush = window.innerWidth === screen.availWidth && window.innerHeight === screen.availHeight;"
        " const el = document.elementFromPoint(window.innerWidth - 1, 1);"
        " const cornerClose = !!(el && el.closest && el.closest('#winClose'));"
        " const gripsHidden = getComputedStyle(document.querySelector('.rz-tr')).display === 'none';"
        " document.getElementById('winMax').click(); await new Promise(r => setTimeout(r, 500));"
        " return { flush, cornerClose, gripsHidden, restored: !document.body.classList.contains('win-maximized') }; })()")))

    cdp.ws.close()
    relaunch(with_cdp=False)  # leave a normal instance for the user

    fails = [n for n, ok, _ in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(fails)}/{len(RESULTS)} checks passed" + (f" — FAILURES: {', '.join(fails)}" if fails else " — all green"))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
