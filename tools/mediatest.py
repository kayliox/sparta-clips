# mediatest.py — empirical truth tests for the media pipeline (fixes that were previously verified only by reading).
#
# Builds SYNTHETIC clips with audio tones at known times, drives the real pipeline over CDP, then measures the
# outputs with ffmpeg — so "the mic survives a trimmed share" is a measured dB level, not an argument about filters.
#   A. trimmed share keeps mic audio        (ClipShare tOut fix — audit batch 1)
#   B. >4 GiB largesize mdat: moov found    (FindBox 64-bit fix — sparse file, no real 4 GiB written)
#   C. keyboard-made selection exports the right mic window (lane-follow parity — batch 3)
#   D. HEVC clip → H.264 preview proxy      (path never exercised on this box: native HEVC decode)
# Cleans up everything it creates.  Run:  python tools/mediatest.py
import glob, json, os, re, struct, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from uidrive import Cdp
from smoke import relaunch, PORT
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # cp1252 console chokes on arrows/box chars

BRIDGE = "chrome.webview.hostObjects.clipsBridge"
FF = (glob.glob(os.path.join(os.environ["APPDATA"], "SpartaClips", "tools", "ffmpeg", "*", "bin", "ffmpeg.exe")) + [None])[0]
RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, ok))
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else f"  — {detail}"))


def ff(args, timeout=120):
    p = subprocess.run([FF, "-hide_banner", "-y"] + args, capture_output=True, text=True, timeout=timeout)
    return p.returncode, p.stderr


def tone_db(path, ss, t):
    """Mean volume (dB) in the 750–1000 Hz band over [ss, ss+t] — the 880 Hz 'mic' tone lives here; the 440 Hz
    'game audio' tone does not. > -35 dB = tone present, < -55 dB = absent."""
    code, err = ff(["-ss", str(ss), "-t", str(t), "-i", path,
                    "-af", "highpass=f=750,lowpass=f=1000,volumedetect", "-f", "null", "NUL"])
    m = re.search(r"mean_volume:\s*(-?[\d.]+) dB", err)
    return float(m.group(1)) if m else -120.0


class Web:
    def __init__(self, cdp):
        self.cdp = cdp

    def eval(self, js):
        return self.cdp.eval(js)

    def hook(self, cb_name, slot):
        """Wrap a window.on* callback; first firing stores its args into window.<slot>."""
        self.eval(f"(() => {{ const o = window.{cb_name}; window.{slot} = null;"
                  f" window.{cb_name} = (...a) => {{ if (!window.{slot}) window.{slot} = a; if (o) o(...a); }};"
                  f" window.{slot}_restore = () => {{ window.{cb_name} = o; }}; return 'hooked'; }})()")

    def await_slot(self, slot, secs):
        for _ in range(int(secs * 2)):
            v = self.eval(f"window.{slot} || null")
            if v is not None:
                self.eval(f"window.{slot}_restore && window.{slot}_restore()")
                return v
            time.sleep(0.5)
        self.eval(f"window.{slot}_restore && window.{slot}_restore()")
        return None


def make_assets(clips_dir):
    clip = os.path.join(clips_dir, "_MTTone.mp4")
    mic = os.path.join(clips_dir, "_MTTone.mic.wav")
    # 60 s: testsrc2 video + 440 Hz game audio.
    ff(["-f", "lavfi", "-i", "testsrc2=duration=60:size=640x360:rate=30",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=60",
        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-c:a", "aac", "-shortest",
        "-movflags", "+faststart", clip])
    # Mic sidecar: 880 Hz ONLY during [20, 40] s.
    ff(["-f", "lavfi", "-i", "aevalsrc=if(between(t\\,20\\,40)\\,0.6*sin(880*2*PI*t)\\,0):d=60:s=48000",
        "-c:a", "pcm_s16le", mic])
    return clip, mic


def make_largesize(clips_dir, donor):
    """A >4 GiB mp4 WITHOUT writing 4 GiB: ftyp + 64-bit-largesize mdat (sparse hole) + the donor's moov at the end
    — exactly the shape of a long recording whose mdat passed 4 GiB (moov only findable via the largesize header)."""
    with open(donor, "rb") as f:
        data = f.read()
    boxes, p = {}, 0
    while p + 8 <= len(data):
        size = struct.unpack(">I", data[p:p + 4])[0]
        typ = data[p + 4:p + 8].decode("ascii", "replace")
        if size < 8:
            break
        boxes[typ] = data[p:p + size]
        p += size
    if "ftyp" not in boxes or "moov" not in boxes:
        raise RuntimeError("donor mp4 missing ftyp/moov")
    out = os.path.join(clips_dir, "_MTBig.mp4")
    pad = 4_400_000_000  # mdat payload > 4 GiB → largesize required
    with open(out, "wb") as f:
        f.write(boxes["ftyp"])
        f.write(struct.pack(">I", 1) + b"mdat" + struct.pack(">Q", 16 + pad))  # size=1 → 64-bit largesize follows
    subprocess.run(["fsutil", "sparse", "setflag", out], capture_output=True)   # hole, not 4.4 GB of zeros
    with open(out, "r+b") as f:
        f.seek(len(boxes["ftyp"]) + 16 + pad)
        f.write(boxes["moov"])
    return out


def main():
    if not FF:
        raise SystemExit("managed ffmpeg not found — open the app once so it downloads")
    relaunch(with_cdp=True)
    web = Web(Cdp(PORT, "spartaclips.local"))
    clips_dir = web.eval(f"(async () => String(await {BRIDGE}.GetClipsDir()))()")
    created = []
    try:
        clip, mic = make_assets(clips_dir)
        created += [clip, mic]

        # ── A. trimmed share keeps the mic ── trim [25,35]: with the OLD tOut bug the whole mic track was dropped
        # (blockEnd 10 s < trim start 25 s); fixed, the 880 Hz tone must be audible across the full 10 s output.
        web.hook("onShareRendered", "_mtShare")
        web.eval(f"{BRIDGE}.RenderShareForDrag('_MTTone.mp4', '_mtshare', 0, 0, 25, 35, 60)")
        r = web.await_slot("_mtShare", 90)
        share = os.path.join(os.environ["TEMP"], "SpartaClipsShare", "_mtshare.mp4")
        if r and r[0] and os.path.exists(share):
            db = tone_db(share, 0, 9.5)
            check("A: trimmed share keeps mic audio (880Hz in band)", db > -35, f"mic band mean {db} dB")
        else:
            check("A: trimmed share keeps mic audio (880Hz in band)", False, f"share render failed: {r}")

        # ── B. >4 GiB largesize mdat: the gallery must SEE the clip (duration from moov beyond the 32-bit horizon) ──
        big = make_largesize(clips_dir, clip)
        created.append(big)
        web.hook("onClips", "_mtClips")
        web.eval(f"{BRIDGE}.RequestClips()")
        r = web.await_slot("_mtClips", 30)
        entry = next((c for c in (r[0]["clips"] if r else []) if c["name"] == "_MTBig.mp4"), None)
        check("B: >4GiB largesize clip visible with real duration", bool(entry and entry["duration"] > 0),
              f"entry: {entry}")

        # ── C. keyboard-made selection exports the right mic window (lane-follow parity) ──
        edited_before = set(os.listdir(os.path.join(clips_dir, "Edited"))) if os.path.isdir(os.path.join(clips_dir, "Edited")) else set()
        opened = web.eval(
            "(async () => { const card = document.querySelector('.gal-card[data-name=\"_MTTone.mp4\"]');"
            " if (!card) return 'no card'; card.click(); await new Promise(r => setTimeout(r, 2500));"
            " const pg = document.getElementById('clipPage'); const v = pg.querySelector('video');"
            " if (pg.hidden || !v) return 'no editor';"
            " v.pause(); v.currentTime = 25; await new Promise(r => setTimeout(r, 700));"
            " window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyI', key: 'i', bubbles: true, cancelable: true }));"
            " await new Promise(r => setTimeout(r, 300)); v.currentTime = 35; await new Promise(r => setTimeout(r, 700));"
            " window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyO', key: 'o', bubbles: true, cancelable: true }));"
            " await new Promise(r => setTimeout(r, 300));"
            " const btn = document.getElementById('clipCreate'); if (!btn) return 'no create btn'; btn.click();"
            " return 'export started'; })()")
        export = None
        if opened == "export started":
            edited_dir = os.path.join(clips_dir, "Edited")
            for _ in range(60):
                time.sleep(1)
                now = set(os.listdir(edited_dir)) if os.path.isdir(edited_dir) else set()
                fresh = [f for f in now - edited_before if f.endswith(".mp4")]
                if fresh and os.path.getsize(os.path.join(edited_dir, fresh[0])) > 50_000:
                    time.sleep(2)  # let it finalize fully
                    export = os.path.join(edited_dir, fresh[0])
                    break
        if export:
            created.append(export)
            db_in = tone_db(export, 1, 8)     # selection [25,35] ⊂ mic tone [20,40] → tone throughout
            check("C: keyboard I/O selection exports the right mic window", db_in > -35,
                  f"mic band in export: {db_in} dB")
            web.eval("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))")
        else:
            check("C: keyboard I/O selection exports the right mic window", False, f"export never landed ({opened})")

        # ── D. HEVC clip → H.264 preview proxy (the no-native-codec path this box never exercises) ──
        hevc = os.path.join(clips_dir, "_MTHevc.mp4")
        ff(["-f", "lavfi", "-i", "testsrc2=duration=4:size=640x360:rate=30",
            "-c:v", "libx265", "-preset", "ultrafast", "-tag:v", "hvc1", hevc])
        created.append(hevc)
        web.hook("onClipProxy", "_mtProxy")
        web.eval(f"{BRIDGE}.EnsureHevcProxy('_MTHevc.mp4')")
        r = web.await_slot("_mtProxy", 60)
        ok = bool(r and r[1])
        if ok:
            proxies = glob.glob(os.path.join(os.environ["TEMP"], "SpartaClipsProxy", "_MTHevc_*.h264.mp4"))
            _, err = ff(["-i", proxies[0], "-f", "null", "NUL"]) if proxies else (1, "")
            ok = bool(proxies) and "Video: h264" in err
        check("D: HEVC proxy builds and is H.264", ok, f"onClipProxy: {r}")
    finally:
        for f in created:
            try:
                os.remove(f)
                print("cleaned:", os.path.basename(f))
            except OSError:
                pass
        web.eval(f"{BRIDGE}.RequestClips()")
        web.cdp.ws.close()
        relaunch(with_cdp=False)

    fails = [n for n, ok in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(fails)}/{len(RESULTS)} checks passed" + (f" — FAILURES: {', '.join(fails)}" if fails else " — all green"))
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
