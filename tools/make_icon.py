# make_icon.py — render docs/icon.svg into the app icon (app.ico, every size Windows uses) + docs/icon.png (README).
#
# Renders the SVG once at 1024 px with headless Microsoft Edge (transparent background — no extra SVG libraries
# needed on Windows), then Lanczos-downscales into each icon size with Pillow. Edit docs/icon.svg, then run:
#     pip install pillow      (once)
#     python tools/make_icon.py
import os, shutil, subprocess, sys, tempfile
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SVG = os.path.join(ROOT, "docs", "icon.svg")
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]   # tray/title bar → taskbar → Explorer large icons


def edge():
    for p in (os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
              os.path.expandvars(r"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")):
        if os.path.exists(p):
            return p
    sys.exit("Microsoft Edge not found")


def main():
    tmp = tempfile.mkdtemp(prefix="spartaicon_")
    try:
        html = os.path.join(tmp, "icon.html")
        svg = open(SVG, encoding="utf-8").read()
        open(html, "w", encoding="utf-8").write(
            "<!doctype html><html><body style='margin:0;background:transparent'>"
            "<div style='width:1024px;height:1024px'>" + svg.replace('width="256" height="256"', 'width="1024" height="1024"') +
            "</div></body></html>")
        png = os.path.join(tmp, "icon1024.png")
        subprocess.run([edge(), "--headless=new", "--disable-gpu", "--hide-scrollbars", f"--user-data-dir={tmp}\\prof",
                        "--default-background-color=00000000", "--window-size=1024,1024",
                        f"--screenshot={png}", "file:///" + html.replace("\\", "/")], check=True, capture_output=True)
        big = Image.open(png).convert("RGBA").crop((0, 0, 1024, 1024))
        if big.getpixel((0, 0))[3] != 0:
            sys.exit("render has no transparent background — Edge ignored --default-background-color")
        frames = [big.resize((s, s), Image.LANCZOS) for s in SIZES]
        frames[-1].save(os.path.join(ROOT, "app.ico"), format="ICO", sizes=[(s, s) for s in SIZES],
                        append_images=frames[:-1])
        frames[-1].save(os.path.join(ROOT, "docs", "icon.png"))
        print("wrote app.ico (" + ", ".join(map(str, SIZES)) + " px) + docs/icon.png")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
