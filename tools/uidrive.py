# uidrive.py — drive the SpartaClips WebView2 UI over the Chrome DevTools Protocol for smoke tests / bug hunting.
#
# The host opens CDP only when launched with SPARTACLIPS_CDP=<port> (see MainForm.InitWebViewAsync):
#   pwsh>  Stop-Process -Name SpartaClips; $env:SPARTACLIPS_CDP=9333; Start-Process .\SpartaClips-final\SpartaClips.exe
#
# Usage:
#   python tools/uidrive.py eval  "<js>"            # evaluate JS in the page (awaits promises), prints JSON result
#   python tools/uidrive.py shot  out.png           # screenshot the page to a PNG
#   python tools/uidrive.py click "<css selector>"  # dispatch a real click on the first match
#   python tools/uidrive.py key   "<code>"          # dispatch a keydown+keyup (e.g. "Space", "KeyI") to the page
#   python tools/uidrive.py targets                 # list CDP targets (debug aid)
# Options: --port 9333 (default) · --target <substring of page url> (default "spartaclips.local")
import base64, json, sys, urllib.request

import websocket  # websocket-client


def http_json(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=5) as r:
        return json.loads(r.read().decode("utf-8"))


def find_target(port, needle):
    pages = [t for t in http_json(port, "/json") if t.get("type") == "page"]
    for t in pages:
        if needle in t.get("url", ""):
            return t
    if pages:
        return pages[0]
    raise SystemExit("no page target — is the app running with SPARTACLIPS_CDP set?")


class Cdp:
    def __init__(self, port, needle):
        t = find_target(port, needle)
        # suppress_origin: Chromium 111+ rejects ws upgrades with a browser Origin header unless
        # --remote-allow-origins is passed; a headerless client connection is always accepted on localhost.
        self.ws = websocket.create_connection(t["webSocketDebuggerUrl"], timeout=30, suppress_origin=True)
        self._id = 0

    def call(self, method, **params):
        self._id += 1
        self.ws.send(json.dumps({"id": self._id, "method": method, "params": params}))
        while True:  # skip events until our reply arrives
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self._id:
                if "error" in msg:
                    raise SystemExit(f"CDP error: {msg['error']}")
                return msg.get("result", {})

    def eval(self, js):
        r = self.call("Runtime.evaluate", expression=js, awaitPromise=True, returnByValue=True)
        ex = r.get("exceptionDetails")
        if ex:
            raise SystemExit("JS exception: " + json.dumps(ex.get("exception", ex), default=str)[:500])
        return r.get("result", {}).get("value")


def main():
    args = sys.argv[1:]
    port, needle = 9333, "spartaclips.local"
    if "--port" in args:
        i = args.index("--port"); port = int(args[i + 1]); del args[i:i + 2]
    if "--target" in args:
        i = args.index("--target"); needle = args[i + 1]; del args[i:i + 2]
    if not args:
        raise SystemExit(__doc__ or "usage: uidrive.py eval|shot|click|key|targets …")
    cmd, rest = args[0], args[1:]

    if cmd == "targets":
        print(json.dumps([{k: t.get(k) for k in ("type", "url", "title")} for t in http_json(port, "/json")], indent=2))
        return

    cdp = Cdp(port, needle)
    if cmd == "eval":
        print(json.dumps(cdp.eval(rest[0]), indent=2, default=str))
    elif cmd == "shot":
        out = rest[0] if rest else "shot.png"
        data = cdp.call("Page.captureScreenshot", format="png")["data"]
        with open(out, "wb") as f:
            f.write(base64.b64decode(data))
        print("wrote", out)
    elif cmd == "click":
        # A real .click() from page context — bubbles through the app's delegated handlers.
        sel = json.dumps(rest[0])
        print(json.dumps(cdp.eval(
            f"(() => {{ const el = document.querySelector({sel}); if (!el) return 'NO MATCH'; el.click(); return 'clicked'; }})()")))
    elif cmd == "key":
        code = json.dumps(rest[0])
        key = json.dumps(rest[1]) if len(rest) > 1 else "null"
        print(json.dumps(cdp.eval(
            f"(() => {{ const code = {code}; const key = {key} || (code === 'Space' ? ' ' : code.replace('Key','').toLowerCase());"
            f" const o = {{ code, key, bubbles: true, cancelable: true }};"
            f" const d = new KeyboardEvent('keydown', o), u = new KeyboardEvent('keyup', o);"
            f" const t = document.activeElement || window; t.dispatchEvent(d); t.dispatchEvent(u);"
            f" return {{ defaultPrevented: d.defaultPrevented, target: (t.id || t.tagName || 'window') }}; }})()")))
    else:
        raise SystemExit("unknown command: " + cmd)


if __name__ == "__main__":
    main()
