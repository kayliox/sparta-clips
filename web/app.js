// Sparta Clips - standalone clips recorder/editor front-end. Classic script; runs after the splash. Talks to C# via
// window.chrome.webview.hostObjects.clipsBridge (wrapped as clipsNative). Lifted from Sparta Dock's clips UI.
(function () {
  "use strict";

  // ---------- Visible crash reporter (diagnostic) ----------
  function showFatal(label, err) {
    var msg = (err && (err.stack || err.message)) ? (err.stack || err.message) : String(err);
    var box = document.getElementById("__fatal");
    if (!box) {
      box = document.createElement("div");
      box.id = "__fatal";
      box.style.cssText = "position:fixed;inset:0;z-index:99999;background:#120606;color:#ffd9d9;" +
        "font:13px/1.5 monospace;padding:24px;overflow:auto;white-space:pre-wrap;";
      document.body.appendChild(box);
    }
    box.textContent += "⚠ " + label + "\n" + msg + "\n\n";
  }
  window.addEventListener("error", function (e) { showFatal("JS error", e.error || e.message); });
  window.addEventListener("unhandledrejection", function (e) { showFatal("Promise rejection", e.reason); });

  // ---------- Native bridge wrapper (mirrors spartaNative; renamed to clipsNative) ----------
  const clipsNative = (() => {
    const get = () => (window.chrome && window.chrome.webview && window.chrome.webview.hostObjects)
      ? window.chrome.webview.hostObjects.clipsBridge : null;
    const has = () => !!get();
    const call = async (fn) => {
      const b = get();
      if (!b) return null;
      try { return await fn(b); } catch (e) { console.warn("bridge call failed", e); return null; }
    };
    return {
      has,
      // config / app-shell (standalone-only)
      saveConfig:       (json)       => call(b => b.SaveConfig(String(json || ""))),
      loadConfig:       ()           => call(b => b.LoadConfig()),
      quarantineConfig: ()           => call(b => b.QuarantineConfig()),
      // Deferred-modal pickers: C# answers via window.onFolderPicked/onSaveFilePicked (the dialog can't run inside the
      // bridge COM dispatch) — re-wrapped in a promise so call sites still just `await` a path ("" = cancelled).
      pickFolder:       (initial = "") => new Promise(res => { window.onFolderPicked = (p) => { window.onFolderPicked = null; res(String(p || "")); }; call(b => b.PickFolder(String(initial || ""))); }),
      setAutostart:     (on)         => call(b => b.SetAutostart(!!on)),
      isAutostart:      ()           => call(b => b.IsAutostart()),
      // atomic autostart mode ("off"|"on"|"elevated") — truth comes back via window.onAutostartState({runKey, task})
      setAutostartMode: (m)          => call(b => b.SetAutostartMode(String(m || "off"))),
      requestAutostartState: ()      => call(b => b.RequestAutostartState()),
      setArmPolicy:     (mode)       => call(b => b.SetArmPolicy(String(mode || "always"))),
      quitApp:          ()           => call(b => b.QuitApp()),
      openLogsFolder:   ()           => call(b => b.OpenLogsFolder()),
      getUpdateInfo:    ()           => call(b => b.GetUpdateInfo()),
      checkForUpdates:  ()           => call(b => b.CheckForUpdates()),   // result via window.onUpdateStatus
      logDiag:          (m)          => call(b => b.LogDiag(String(m || ""))),
      // capture-source picker (session-only override; hwnd travels as a string — see BridgeApi.SetCaptureSource)
      setCaptureSource: (mode, hwnd) => call(b => b.SetCaptureSource(String(mode || "auto"), String(hwnd || ""))),
      requestCaptureWindows: ()      => call(b => b.RequestCaptureWindows()),
      // window chrome (frameless custom title bar)
      winMinimize:      ()           => call(b => b.WinMinimize()),
      winMaxToggle:     ()           => call(b => b.WinMaxToggle()),
      winClose:         ()           => call(b => b.WinClose()),
      winBeginDrag:     ()           => call(b => b.WinBeginDrag()),
      winBeginResize:   (dir)        => call(b => b.WinBeginResize(String(dir || ""))),
      // capture
      startClipRecord:  (game)       => call(b => b.StartClipRecord(String(game || ""))),
      setClipGame:      (t)          => call(b => b.SetClipGame(String(t || ""))),
      stopClipRecord:   ()           => call(b => b.StopClipRecord()),
      isClipRecording:  ()           => call(b => b.IsClipRecording()),
      recElapsedSec:    ()           => call(b => b.RecElapsedSec()),
      armReplay:        (game)       => call(b => b.ArmReplay(String(game || ""))),
      disarmReplay:     ()           => call(b => b.DisarmReplay()),
      saveReplay:       ()           => call(b => b.SaveReplay()),
      isReplayArmed:    ()           => call(b => b.IsReplayArmed()),
      setReplayEnabled: (on)         => call(b => b.SetReplayEnabled(on ? "true" : "false")),
      hasHardwareAv1:   ()           => call(b => b.HasHardwareAv1()),
      getCaptureTarget: ()           => call(b => b.GetCaptureTarget()),
      ensureAv1Encoder: ()           => call(b => b.EnsureAv1Encoder()),
      setAutoHighlight: (on)         => call(b => b.SetAutoHighlight(on ? "true" : "false")),
      listMics:         ()           => call(b => b.ListMics()),
      setMicDevice:     (id)         => call(b => b.SetMicDevice(String(id || ""))),
      listChatDevices:  ()           => call(b => b.ListChatDevices()),
      setChatDevice:    (id)         => call(b => b.SetChatDevice(String(id || ""))),
      listMonitors:     ()           => call(b => b.ListMonitors()),
      setDualScreen:    (json)       => call(b => b.SetDualScreen(String(json || "{}"))),
      setClipHotkeys:   (json)       => call(b => b.SetClipHotkeys(String(json || "{}"))),
      requestClips:     ()           => call(b => b.RequestClips()),
      deleteClip:       (n)          => call(b => b.DeleteClip(String(n))),
      ensureThumb:      (n)          => call(b => b.EnsureThumb(String(n))),
      ensureClipInfo:   (n)          => call(b => b.EnsureClipInfo(String(n))),
      ensureHevcProxy:  (n)          => call(b => b.EnsureHevcProxy(String(n))),
      deleteMic:        (n)          => call(b => b.DeleteMic(String(n))),
      deleteChat:       (n)          => call(b => b.DeleteChat(String(n))),
      renameClip:       (o, n)       => call(b => b.RenameClip(String(o), String(n))),
      openClipsFolder:  ()           => call(b => b.OpenClipsFolder()),
      revealClip:       (n)          => call(b => b.RevealClip(String(n))),
      getClipsDir:      ()           => call(b => b.GetClipsDir()),
      setClipsDir:      (dir)        => call(b => b.SetClipsDir(String(dir || ""))),
      checkFolderWritable: (dir)     => call(b => b.CheckFolderWritable(String(dir || ""))),   // async — result via window.onFolderWritable
      setCaptureSettings: (json)     => call(b => b.SetCaptureSettings(String(json || "{}"))),
      trimClip:         (n, s, e)    => call(b => b.TrimClip(String(n), Number(s) || 0, Number(e) || 0)),
      smartCut:         (n, s, e)    => call(b => b.SmartCut(String(n), Number(s) || 0, Number(e) || 0)),
      composeClip:      (n, json)    => call(b => b.ComposeClip(String(n), String(json || "{}"))),
      exportSegments:   (n, json)    => call(b => b.ExportSegments(String(n), String(json || "{}"))),
      exportFile:       (n, p, f, j) => call(b => b.ExportFile(String(n), String(p || ""), String(f || "mp4"), String(j || "{}"))),
      pickSaveFile:     (nm, flt)    => new Promise(res => { window.onSaveFilePicked = (p) => { window.onSaveFilePicked = null; res(String(p || "")); }; call(b => b.PickSaveFile(String(nm || "clip"), String(flt || ""))); }),
      // player — the editor plays in-page via an HTML5 <video>; C# only probes keyframe times to snap the scrubber to.
      getClipKeyframes: (n)          => call(b => b.GetClipKeyframes(String(n))),
      requestClipMarks: (n)          => call(b => b.RequestClipMarks(String(n))),   // → window.onClipMarks(name, arr)
      // share
      copyRenderedShare: ()          => call(b => b.CopyRenderedShare()),
      renderShareForDrag: (n, outName, mb, fps, i, o, d, mix) => call(b => b.RenderShareForDrag(String(n||""), String(outName||""), mb|0, fps|0, +i||0, +o||0, +d||0, String(mix||""))),
      shareDragArm:     (json)       => call(b => b.ShareDragArm(String(json || "{}"))),
      shareDragDisarm:  ()           => call(b => b.ShareDragDisarm()),
      cancelShareRender: ()          => call(b => b.CancelShareRender()),
    };
  })();

  // ---------- Tiny DOM helpers ----------
  const _idRe = /^#[A-Za-z][\w-]*$/;
  const $  = (sel, root = document) => (root === document && typeof sel === "string" && _idRe.test(sel))
    ? document.getElementById(sel.slice(1))
    : root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // ---------- Toasts ----------
  const toastWrap = $("#toastWrap");
  let _lastToastAt = 0, _toastBurst = 0;
  function showToast(title, msg, type) {
    if (!type && /error|fail|unable|denied|couldn'?t|can'?t|not available|no .* found/i.test((title || "") + " " + (msg || ""))) type = "error";
    const el = document.createElement("div");
    el.className = "toast" + (type === "error" ? " error" : "");
    el.innerHTML = `<strong>${esc(title)}</strong>${msg ? `<span>${esc(msg)}</span>` : ""}`;
    const now = performance.now();
    _toastBurst = (now - (_lastToastAt || 0) < 90) ? Math.min(_toastBurst + 1, 4) : 0;
    _lastToastAt = now;
    if (_toastBurst) el.style.animationDelay = (_toastBurst * 70) + "ms";
    if (toastWrap) toastWrap.appendChild(el);
    setTimeout(() => { el.classList.add("fade"); setTimeout(() => el.remove(), 260); }, type === "error" ? 5200 : 3200);
  }
  window.showNativeToast = (title, msg) => showToast(title, msg);

  // ---------- Range-fill helpers (neon groove) ----------
  function rangeFillPct(r) { const mn = +r.min || 0, mx = +r.max || 100; return (mx > mn ? Math.max(0, Math.min(100, (+r.value - mn) / (mx - mn) * 100)) : 0) + "%"; }
  function paintRangeFills(root) { (root || document).querySelectorAll('input[type=range]').forEach(r => r.style.setProperty("--vol", rangeFillPct(r))); }
  // Double-confirm modal → resolves true only after the OK button is clicked TWICE (it re-arms with a warning label).
  function confirmDouble({ title, body, confirmLabel, secondLabel }) {
    return new Promise(resolve => {
      const ov = document.createElement("div"); ov.className = "confirm-ov";
      ov.innerHTML = `<div class="confirm-box" role="dialog" aria-modal="true"><div class="confirm-title">${esc(title)}</div>`
        + `<div class="confirm-body">${body}</div><div class="confirm-actions">`
        + `<button class="gal-btn" data-act="cancel">Keep 1080p</button>`
        + `<button class="gal-btn danger" data-act="ok">${esc(confirmLabel)}</button></div></div>`;
      document.body.appendChild(ov);
      const okB = ov.querySelector('[data-act="ok"]'); let armed = false;
      const close = v => { ov.remove(); document.removeEventListener("keydown", onKey, true); resolve(v); };
      const onKey = e => { if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(false); } };
      document.addEventListener("keydown", onKey, true);
      ov.addEventListener("pointerdown", e => { if (e.target === ov) close(false); });
      ov.querySelector('[data-act="cancel"]').addEventListener("click", () => close(false));
      okB.addEventListener("click", () => { if (!armed) { armed = true; okB.textContent = secondLabel || "Click again to confirm"; okB.classList.add("armed"); } else close(true); });
    });
  }
  // Can this WebView2/Chromium decode HEVC natively? True only when the OS "HEVC Video Extensions" (MS Store) is installed.
  // Scanned at boot + when HEVC is selected in Settings; the result is remembered so playback can pick native vs proxy.
  let _hevcNative = null;   // null=unknown; set true/false by detectHevcNative() at boot + on HEVC selection
  async function detectHevcNative() {
    const types = ['video/mp4; codecs="hvc1.1.6.L93.B0"', 'video/mp4; codecs="hev1.1.6.L93.B0"'];
    let ok = false;
    try { const v = document.createElement("video"); ok = types.some(t => v.canPlayType(t) === "probably"); } catch {}
    if (!ok && navigator.mediaCapabilities && navigator.mediaCapabilities.decodingInfo) {
      for (const t of types) {
        try { const r = await navigator.mediaCapabilities.decodingInfo({ type: "file", video: { contentType: t, width: 1920, height: 1080, bitrate: 8000000, framerate: 60 } }); if (r && r.supported) { ok = true; break; } } catch {}
      }
    }
    _hevcNative = ok; state.settings.hevcNative = ok; save();
    return ok;
  }
  // Single-OK info modal (reuses the confirm styling) — the "Re-scan HEVC" explainer popup.
  function infoConfirm({ title, body, okLabel }) {
    return new Promise(resolve => {
      const ov = document.createElement("div"); ov.className = "confirm-ov";
      ov.innerHTML = `<div class="confirm-box" role="dialog" aria-modal="true"><div class="confirm-title">${esc(title)}</div><div class="confirm-body">${body}</div><div class="confirm-actions"><button class="gal-btn" data-act="cancel">Cancel</button><button class="gal-btn primary" data-act="ok">${esc(okLabel)}</button></div></div>`;
      document.body.appendChild(ov);
      const close = v => { ov.remove(); document.removeEventListener("keydown", onKey, true); resolve(v); };
      const onKey = e => { if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(false); } };
      document.addEventListener("keydown", onKey, true);
      ov.addEventListener("pointerdown", e => { if (e.target === ov) close(false); });
      ov.querySelector('[data-act="cancel"]').addEventListener("click", () => close(false));
      ov.querySelector('[data-act="ok"]').addEventListener("click", () => close(true));
    });
  }
  // First-run welcome — shown once (gated by state.settings.seenOnboarding). Introduces the two hotkeys + the tray model
  //    the first time the window is opened. Skipped in browser-preview mode.
  function maybeShowOnboarding() {
    if (!clipsNative.has() || state.settings.seenOnboarding) return;
    const kbd = hk => `<span class="ob-kbd">${esc(hkLabel(hk))}</span>`;
    const ov = document.createElement("div"); ov.className = "confirm-ov";
    ov.innerHTML = `<div class="confirm-box onboard" role="dialog" aria-modal="true" aria-label="Welcome to Sparta Clips">`
      + `<div class="ob-logo">Ω</div>`
      + `<div class="confirm-title">Welcome to Sparta Clips</div>`
      + `<div class="ob-steps">`
      +   `<div class="ob-step">${kbd(recHk())}<div><b>Record</b><span>Start &amp; stop a full recording from anywhere — even inside a fullscreen game.</span></div></div>`
      +   `<div class="ob-step">${kbd(repHk())}<div><b>Save replay</b><span>Instant replay runs in the background — press this to keep the last few seconds after a moment happens.</span></div></div>`
      +   `<div class="ob-step"><span class="ob-kbd">Ctrl + K</span><div><b>Quick actions</b><span>Open the command palette to jump to any clip or action.</span></div></div>`
      + `</div>`
      + `<p class="ob-foot">Sparta Clips lives in your system tray — closing this window won't stop recording. Tune everything in <b>Settings</b>.</p>`
      + `<div class="confirm-actions"><button class="gal-btn primary" data-act="ok">Get started</button></div></div>`;
    document.body.appendChild(ov);
    const close = () => { ov.remove(); document.removeEventListener("keydown", onKey, true); state.settings.seenOnboarding = true; save(); };
    const onKey = e => { if (e.key === "Escape" || e.key === "Enter") { e.preventDefault(); e.stopPropagation(); close(); } };
    document.addEventListener("keydown", onKey, true);
    ov.querySelector('[data-act="ok"]').addEventListener("click", close);
    try { ov.querySelector('[data-act="ok"]').focus(); } catch {}
  }
  function updateHevcScanState() {
    const el = $("#hevcScanState"); if (!el) return;
    el.classList.toggle("ok", _hevcNative === true);
    el.classList.toggle("warn", _hevcNative === false);
    el.textContent = _hevcNative == null ? "Not checked yet." : (_hevcNative ? "✓ Hardware HEVC detected — HEVC clips preview natively." : "✗ No system HEVC codec — HEVC clips convert to H.264 first.");
  }
  function updateHevcRescanRow() {
    const row = $("#hevcRescanRow"); if (row) row.hidden = state.settings.captureEncoder !== "h265";
    updateHevcScanState();
  }
  async function rescanHevc() {
    const ok = await infoConfirm({
      title: "Re-scan HEVC support",
      body: "This re-checks whether <b>Windows can decode H.265 / HEVC inside the app</b>.<br><br>Native in-app HEVC preview needs Microsoft's <b>HEVC Video Extensions</b> (Microsoft Store). If you've <b>just installed it</b>, re-scan so Sparta Clips previews HEVC clips <b>natively</b> — otherwise it falls back to a one-time <b>H.264 conversion</b> per clip. Recording and export are unaffected either way.",
      okLabel: "Re-scan now"
    });
    if (!ok) return;
    showToast("HEVC support", "Re-scanning…");
    const native = await detectHevcNative();
    updateHevcScanState();
    showToast("HEVC support", native ? "Hardware HEVC detected — HEVC clips now preview natively." : "No system HEVC codec found. Install 'HEVC Video Extensions' from the Microsoft Store, then re-scan.");
  }

  // ---------- Lightweight poller registry (only the record-elapsed timer uses it) ----------
  const _loops = [];
  let _loopTimer = 0;
  function _ensureLoopTimer() {
    if (_loopTimer || !_loops.length) return;
    _loopTimer = setInterval(() => {
      const now = performance.now();
      for (const l of _loops) if (now >= l.next) { l.next = now + l.ms; try { l.fn(); } catch (e) {} }
    }, 250);
  }
  function every(ms, fn) { _loops.push({ ms, fn, next: performance.now() + ms }); _ensureLoopTimer(); }

  // ---------- Persisted state (localStorage + %APPDATA% mirror via C#) ----------
  const STORE_KEY = "spartaClips.v1";
  // Default settings — keep the EXACT keys Sparta Dock uses so the copied-verbatim C# reads them, plus the 3 new
  // standalone keys (armPolicy / autostart / releaseUiOnClose).
  function defaultSettings() {
    return {
      capturePreset: "medium", captureFps: 60, captureBitrate: 20, captureResH: 1080, captureMode: "display",
      captureEncoder: "h264", captureMic: false, captureSepMic: false, captureChat: false, captureCapGB: 0,
      captureMicDevice: "", captureChatDevice: "", replayEnabled: true, replayLen: 30, replayChime: true,
      autoHighlight: false, autoClip: false, clipFolder: "", clipFill: false, clipMicMuted: false, galCardScale: 100,
      recHotkey: { ctrl: true, alt: true, vk: 0x78 }, repHotkey: { ctrl: true, alt: true, vk: 0x79 }, markHotkey: { ctrl: true, alt: true, vk: 0x77 },
      dualScreen: null, theme: "spartan",
      armPolicy: "always", autostart: false, autostartElevated: false, releaseUiOnClose: true, overlayEnabled: true, autoUpdate: true,
      gameProfiles: [],
    };
  }
  function seed() { return { settings: defaultSettings(), clipBookmarks: {}, clipSegments: {} }; }
  // Fill defaults on any saved blob. Kept self-contained (runs at script-eval time via load()).
  function normalize(obj) {
    if (!obj || typeof obj !== "object") return null;
    obj.settings = Object.assign(defaultSettings(), obj.settings || {});
    if (!obj.clipBookmarks || typeof obj.clipBookmarks !== "object") obj.clipBookmarks = {};
    if (!obj.clipSegments || typeof obj.clipSegments !== "object") obj.clipSegments = {};
    return obj;
  }
  function load() {
    let parsed = null;
    try { const raw = localStorage.getItem(STORE_KEY); if (raw) parsed = JSON.parse(raw); }
    catch { return null; }
    if (!parsed) return null;
    try { return normalize(parsed); }
    catch (e) { console.error("normalize() failed — running on the raw saved state", e); return parsed; }
  }
  const lsState = load();
  let state = lsState || seed();
  let _diskTimer = null, _allowDisk = false;
  function flushDisk() { try { if (_allowDisk && clipsNative.has()) clipsNative.saveConfig(JSON.stringify(state)); } catch {} }
  const save = () => {
    try { localStorage.setItem(STORE_KEY, JSON.stringify(state)); } catch {}
    try { localStorage.setItem("sc_theme", state.settings.theme || "spartan"); } catch {}   // splash reads this next launch
    if (_allowDisk && clipsNative.has()) { clearTimeout(_diskTimer); _diskTimer = setTimeout(flushDisk, 300); }
  };

  // ---------- Launcher-only glue that the lifted clips code calls → lean stubs / view switches ----------
  function addonOn() { return true; }          // no addon system here — capture is always "on"
  function libTabOn() { return true; }
  function activateSettingsSection() { showView("settings"); }
  function renderSidebarCtx() {}
  function updateLibActionGlyphs() {}
  function renderLibrary() { if (_view === "gallery") renderGallery($("#galleryHost")); }
  function curGameTitle() { return ""; }        // no game library — the recorder resolves the foreground game itself
  // navigate(target): the lifted code only ever passes "gallery"/"home" → our simple view switch.
  function navigate(target) { showView(target === "settings" ? "settings" : "gallery"); }

  // ---------- View router ----------
  let _view = "gallery";
  function showView(view) {
    _view = (view === "settings") ? "settings" : "gallery";
    $("#view-gallery").hidden = _view !== "gallery";
    $("#view-settings").hidden = _view !== "settings";
    $$(".nav-tab").forEach(b => { const on = b.dataset.view === _view; b.classList.toggle("active", on); b.setAttribute("aria-selected", String(on)); });
    if (_view === "gallery") renderGallery($("#galleryHost"));
    else { syncCapturePane(); syncShellSettings(); buildSettingsNav(); }
  }
  // The C# tray calls this to open the window to a view. ""/null → Gallery; "settings" → Settings.
  window.onRouteView = (view) => { try { closeClipPage(); showView(view === "settings" ? "settings" : "gallery"); } catch {} };   // the editor overlay sits ABOVE the views — without closing it, a tray "Settings" appeared to do nothing
  // Top-tab nav. Clicking a tab while the clip editor is open returns to the tabbed view first.
  $$(".nav-tab").forEach(b => b.addEventListener("click", () => { const pg = $("#clipPage"); if (pg && !pg.hidden) closeClipPage(); showView(b.dataset.view); }));
  // Collapsible settings sections (cosmetic; not persisted).
  // (Settings groups are paged now — see setSettingsPage/onSettingsWheel below; no collapse toggle.)

  // ── Frameless window chrome: minimize / maximize-toggle / close + the maximize↔restore glyph state. Dragging is
  //    handled natively by the title bar's CSS `app-region: drag` (WebView2 IsNonClientRegionSupportEnabled). ──
  { const b = $("#winMin");   if (b) b.addEventListener("click", () => clipsNative.winMinimize()); }
  { const b = $("#winMax");   if (b) b.addEventListener("click", () => clipsNative.winMaxToggle()); }
  { const b = $("#winClose"); if (b) b.addEventListener("click", () => clipsNative.winClose()); }
  // Drag the title bar to move the window. Fires only when WebView2's native app-region drag ISN'T consuming the
  // press (older runtimes) — a self-selecting fallback, never a double-drag. Buttons/tabs opt out via closest().
  { const tb = $("#titlebar"); if (tb) tb.addEventListener("pointerdown", (e) => { if (e.button === 0 && !e.target.closest("button, .nav-tab")) clipsNative.winBeginDrag(); }); }
  // Edge/corner grips start a native resize loop (bridge → WM_NCLBUTTONDOWN with the edge hit-test). No-op maximized.
  $$("#resizeGrips .rz").forEach(g => g.addEventListener("pointerdown", (e) => {
    if (e.button !== 0 || document.body.classList.contains("win-maximized")) return;
    e.preventDefault();
    const t0 = performance.now();
    Promise.resolve(clipsNative.winBeginResize(g.dataset.dir))
      .then(() => clipsNative.logDiag("winBeginResize(" + g.dataset.dir + ") returned after " + Math.round(performance.now() - t0) + "ms"));
  }));
  // Renderer main-thread stall detector: a setInterval that misses by seconds means the page was frozen — report the
  // gap into the shared diag timeline. Hidden windows throttle timers (hide-to-tray), so only visible gaps count.
  { let _hb = performance.now();
    document.addEventListener("visibilitychange", () => { _hb = performance.now(); });   // throttled-while-hidden ticks aren't stalls
    setInterval(() => {
      const now = performance.now(), gap = now - _hb; _hb = now;
      if (gap > 2500 && !document.hidden) clipsNative.logDiag("renderer stall ~" + Math.round(gap) + "ms (heartbeat gap)");
    }, 1000); }   // 1s tick still detects >2.5s gaps — a 250ms tick kept the renderer from coalescing timers all session
  // C# pushes the maximize state → swap the glyph + let CSS square the corners while maximized.
  window.onWinState = (max) => { try { document.body.classList.toggle("win-maximized", !!max); const b = $("#winMax"); if (b) b.title = max ? "Restore" : "Maximize"; } catch {} };
  // Pause the infinite HUD pulse animations while the window is unfocused/hidden (e.g. sitting behind the game) —
  // an always-running animation forces the compositor to produce frames at vsync forever, so the renderer never idles.
  { const upd = () => document.body.classList.toggle("app-idle", document.hidden || !document.hasFocus());
    window.addEventListener("blur", upd); window.addEventListener("focus", upd); document.addEventListener("visibilitychange", upd); upd(); }

  // Capture HUD (title bar) — Record + Save-replay live here now, bound ONCE (not per gallery render). updateClipRecUI drives state.
  { const rb = $("#galRecBtn"); if (rb) rb.addEventListener("click", toggleClipRecord); }
  { const sr = $("#galSaveReplay"); if (sr) sr.addEventListener("click", () => { clipsNative.saveReplay(); showToast("Instant replay", "Saving the last " + (((state.settings.replayLen | 0) || 30)) + "s…"); }); }

  let openSourcePicker = null, syncPinnedFromEngine = null;   // set by the capture-source picker block below; used by the gallery "Capturing …" status
  // Capture-source picker — SESSION-ONLY override (Auto / Desktop / pinned window). Deliberately NOT in state.settings:
  // it resets to Auto on restart and never interferes with the saved capture mode (C# side: ClipRecorder.SetCaptureSource).
  { const btn = $("#tbSource"), pop = $("#srcPop"), wrap = $("#srcWinWrap"), grid = $("#srcWinGrid"), lbl = $("#tbSourceLabel");
    let src = { mode: "auto", hwnd: "", title: "" };
    const modeLabel = () => src.mode === "display" ? "Desktop"
      : src.mode === "window" ? ((src.title || "Window").length > 20 ? src.title.slice(0, 19) + "…" : (src.title || "Window")) : "Auto";
    function syncUi() {   // the button's TEXT is the live "Capturing <app>" status (pollCaptureTarget); this only tracks the mode
      if (lbl) lbl.textContent = modeLabel();
      if (btn) btn.classList.toggle("src-active", src.mode !== "auto");
      $$("#srcPop .src-mode").forEach(m => m.classList.toggle("active", m.dataset.src === src.mode));
    }
    function apply(mode, hwnd, title, proc) {
      src = { mode, hwnd: hwnd || "", title: title || "" };
      // Remember a window pin BY PROGRAM across restarts (C#: AppConfig → ClipRecorder.SetPinnedApp); Auto/Desktop clear it.
      state.settings.capturePin = mode === "window" ? (proc || "") : ""; save();
      clipsNative.setCaptureSource(mode, src.hwnd);
      syncUi();
      const what = mode === "window" ? "Window · " + (title || "") : mode === "display" ? "Desktop" : "Automatic";
      showToast("Capture source", what + (_recording ? " — takes effect on the next recording." : ""));
      setTimeout(pollCaptureTarget, 400);   // refresh the gallery status right after the re-arm picks the new source
    }
    function close() { if (pop) pop.hidden = true; if (grid) grid.innerHTML = ""; }   // drop the decoded data-URI thumbs (megabytes) with the popover
    let anchor = btn;   // the title-bar button, or the gallery "Capturing …" status (openSourcePicker)
    function open(from, showWindows) {
      anchor = from || btn;
      const r = anchor.getBoundingClientRect();
      pop.style.top = Math.round(r.bottom + 8) + "px";
      pop.style.left = Math.round(Math.max(8, Math.min(r.left + r.width / 2 - 190, window.innerWidth - 388))) + "px";
      pop.hidden = false; syncUi();
      if (showWindows || src.mode === "window") { wrap.hidden = false; scanWindows(); } else wrap.hidden = true;
    }
    // Gallery status → the same picker, straight to the open-window grid (pin a window = instant replay records ONLY it).
    // A persisted pin is applied by C# at startup — mirror it here so the picker/title-bar show "window" mode.
    syncPinnedFromEngine = (pinApp) => { if (pinApp && src.mode === "auto") { src = { mode: "window", hwnd: "", title: pinApp }; syncUi(); } };
    openSourcePicker = (from) => { if (pop && !pop.hidden && anchor === from) close(); else if (pop) open(from, true); };
    function scanWindows() { grid.innerHTML = '<div class="src-empty">Scanning open windows…</div>'; clipsNative.requestCaptureWindows(); }
    if (btn && pop) btn.addEventListener("click", () => pop.hidden ? open(btn, true) : close());   // straight to the window grid
    $$("#srcPop .src-mode").forEach(m => m.addEventListener("click", () => {
      const mode = m.dataset.src;
      if (mode === "window") { wrap.hidden = false; scanWindows(); return; }   // pinning happens by picking from the grid
      wrap.hidden = true; apply(mode, "", ""); close();
    }));
    { const rf = $("#srcWinRefresh"); if (rf) rf.addEventListener("click", scanWindows); }
    // C# → open-window list (with data-URI preview thumbs) for the grid. Titles set via textContent — never innerHTML.
    window.onCaptureWindows = (list) => {
      if (!grid) return;
      grid.innerHTML = "";
      (Array.isArray(list) ? list : []).forEach(w => {
        const b = document.createElement("button"); b.className = "src-win" + (src.hwnd === w.hwnd ? " active" : "");
        if (w.thumb) { const im = document.createElement("img"); im.src = w.thumb; im.alt = ""; b.appendChild(im); }
        else { const d = document.createElement("div"); d.className = "src-noimg"; d.textContent = "🗔"; b.appendChild(d); }
        const t = document.createElement("span"); t.className = "src-wtitle"; t.textContent = w.title || w.proc || "Window";
        t.title = (w.title || "") + (w.proc ? " · " + w.proc : "");
        b.appendChild(t);
        b.addEventListener("click", () => { apply("window", w.hwnd, w.title, w.proc); close(); });
        grid.appendChild(b);
      });
      if (!grid.childElementCount) grid.innerHTML = '<div class="src-empty">No capturable windows found.</div>';
    };
    document.addEventListener("pointerdown", (e) => { if (!pop || pop.hidden) return; if (!pop.contains(e.target) && !btn.contains(e.target) && !(anchor && anchor.contains(e.target))) close(); });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });
  }

  // ── recording-elapsed timer: ticks ONLY while recording AND the window is open (no other pollers) ──
  let _recTick = null, _recStart = 0;
  // Reset the epoch ONLY on the idle→recording transition — updateClipRecUI re-invokes this on every render, and an
  // unconditional reset restarted the visible elapsed timer at 0:00 on any background re-render mid-recording.
  function startRecTimer() { if (!_recTick) { _recStart = Date.now(); _recTick = setInterval(paintRecElapsed, 1000); } paintRecElapsed(); }
  function stopRecTimer() { if (_recTick) { clearInterval(_recTick); _recTick = null; } paintRecElapsed(); }
  function paintRecElapsed() {
    const el = $("#galRecElapsed"); if (!el) return;
    if (!_recording) { el.textContent = ""; return; }
    const s = Math.max(0, Math.floor((Date.now() - _recStart) / 1000));
    el.textContent = Math.floor(s / 60) + ":" + String(s % 60).padStart(2, "0");
  }

  // ══════════════════════════════════════════════════════════════════════════════════════════════════
  //  LIFTED FROM SPARTA DOCK — Game Capture settings pane. spartaNative → clipsNative throughout.
  // ══════════════════════════════════════════════════════════════════════════════════════════════════
  let _clips = [], _clipsTotal = 0, _recording = false, _replayArmed = false, _replaySaving = false, _clipFilter = "", _clipQuery = "", _galSel = new Set(), _galSelecting = false, _renaming = false, _clipsLoaded = false, _galStale = false;

  const CAP_PRESETS = { low: { fps: 30, mbps: 10, resH: 720 }, medium: { fps: 60, mbps: 20, resH: 1080 }, high: { fps: 60, mbps: 50, resH: 1080 } };
  let _hwAv1 = null;
  function applyAv1Availability() {
    const sel = $("#capEncoder"); if (!sel) return;
    const hw = _hwAv1 === true;
    const hwOpt = sel.querySelector('option[value="av1_hw"]');
    if (hwOpt) { hwOpt.disabled = !hw; hwOpt.textContent = hw ? "AV1 — hardware (smallest, fast)" : "AV1 — hardware (needs RTX 40 / Arc / RX 7000)"; }
    if (!hw && state.settings.captureEncoder === "av1_hw") {
      state.settings.captureEncoder = "av1_sw"; save(); pushCaptureSettings();
      if (sel) sel.value = "av1_sw";
    }
  }
  function capCfg() {
    const s = state.settings;
    return {
      preset: s.capturePreset || "medium",
      fps: (s.captureFps | 0) || 60,
      mbps: (s.captureBitrate | 0) || 20,
      resH: (s.captureResH == null) ? 1080 : (s.captureResH | 0),
      mode: s.captureMode || "display",
      encoder: (() => { const e = s.captureEncoder; if (e === "h265" || e === "av1_hw" || e === "av1_sw") return e; if (e === "av1") return "av1_hw"; return "h264"; })(),
      mic: s.captureMic === true,
      sepMic: s.captureSepMic === true,
      chat: s.captureChat === true,
      capGB: (s.captureCapGB | 0) || 0,
      replay: s.replayEnabled === true,
      replayLen: (s.replayLen | 0) || 30,
      chime: s.replayChime !== false,
    };
  }
  function pushCaptureSettings() {
    const c = capCfg();
    clipsNative.setCaptureSettings(JSON.stringify({
      fps: c.fps, mbps: c.mbps, resH: c.resH, mic: c.mic, sepMic: c.sepMic, chat: c.chat, encoder: c.encoder,
      gameCapture: c.mode === "game", capBytes: c.capGB * 1073741824, replayLen: c.replayLen, chime: c.chime,
    }));
    renderSizeEstimate();
  }
  function capResScale(resH) {
    const h = (resH | 0) > 0 ? (resH | 0) : Math.round((window.screen && window.screen.height || 1080) * (window.devicePixelRatio || 1));
    return Math.min(4, Math.max(0.25, (h / 1080) * (h / 1080)));
  }
  function estCaptureRates() {
    const c = capCfg();
    const isAv1 = c.encoder === "av1_hw" || c.encoder === "av1_sw" || c.encoder === "av1";
    const effMbps = Math.max(1, c.mbps * capResScale(c.resH));
    let vMbps = isAv1 ? Math.max(2, effMbps * 0.6) : effMbps;
    let mbps = vMbps + 0.25;
    if (c.sepMic) mbps += 0.768;
    if (c.chat) mbps += 1.536;
    const perSec = mbps / 8;
    return { perSec, perMin: perSec * 60, replayMB: perSec * c.replayLen };
  }
  function fmtShareMB(bytes) { const mb = bytes / 1048576; return mb >= 1024 ? (mb / 1024).toFixed(1) + " GB" : mb >= 100 ? Math.round(mb) + " MB" : mb.toFixed(1) + " MB"; }
  // ── "Capturing <app>" status in the gallery bar — shows what the recorder is ACTUALLY capturing, and warns when it
  //    isn't the focused window (the replay buffer only re-targets the foreground window when a new segment starts). ──
  let _capTarget = null, _capTargetBusy = false;
  function captureTargetHtml() {
    const t = _capTarget;
    if (!t || t.state === "idle") return `<span class="gal-target-dot off"></span><span class="gal-target-txt" title="Instant replay is off — click to choose what it records">Replay off</span>`;
    const verb = t.state === "recording" ? "Recording" : "Capturing";
    let what, tip;
    if (t.kind === "dual") { what = "<b>both screens</b>"; tip = "Dual-screen capture"; }
    else if (t.kind === "display") { what = "<b>the full screen</b>"; tip = "Display capture (primary monitor)"; }
    else if (t.gone) { what = "<b>a closed window</b>"; tip = "The captured window was closed — the capture picks a new one at the next segment"; }
    else {
      // App name only — the full window title (e.g. a VS Code workspace path) lives in the tooltip; it never fit the bar.
      const app = t.app || t.title || "a window", title = (t.title && t.title !== app) ? t.title : "";
      what = `<b>${esc(app)}</b>`;
      tip = app + (title ? " — " + title : "");
    }
    if (t.pinned) { what = "📌 " + what; tip = "Pinned: " + tip + " — instant replay records only this window. Click to change."; }
    else if (t.pinWaiting) { what = `${what}<span class="gal-target-title"> · 📌 waiting for ${esc(t.pinApp)}</span>`; tip += ` — pinned ${t.pinApp} isn't open; following focus until it is`; }
    else tip += " · click to pin a specific window";
    const warn = t.elsewhere || t.gone;
    const note = t.elsewhere ? `<span class="gal-target-warn"> · switching…</span>` : "";
    if (t.elsewhere) tip += " — you focused " + (t.focusApp || "another window") + "; instant replay switches to it after ~2 s";
    return `<span class="gal-target-dot${t.state === "recording" ? " rec" : ""}${warn ? " warn" : ""}"></span><span class="gal-target-txt" title="${esc(tip)}">${verb} ${what}${note}</span>`;
  }
  async function pollCaptureTarget() {
    if (_capTargetBusy || document.hidden) return;
    const el = $("#tbSource"); if (!el) return;   // the title-bar capture button (every screen) — before: a gallery-only copy
    _capTargetBusy = true;
    try {
      const raw = await clipsNative.getCaptureTarget(); if (!raw) return;
      const t = JSON.parse(raw), key = JSON.stringify(t);
      if (key === JSON.stringify(_capTarget) && el.childElementCount) return;   // unchanged → no DOM churn
      _capTarget = t; el.innerHTML = captureTargetHtml();
      if (t.pinApp && syncPinnedFromEngine) syncPinnedFromEngine(t.pinApp);
    } catch {} finally { _capTargetBusy = false; }
  }
  setInterval(pollCaptureTarget, 1000);
  document.addEventListener("visibilitychange", pollCaptureTarget);
  function fmtMB(mb) { return mb >= 1024 ? (mb / 1024).toFixed(1) + " GB" : Math.max(1, Math.round(mb)) + " MB"; }
  function renderSizeEstimate() {
    const r = estCaptureRates();
    { const e = $("#capSizeEst"); if (e) e.textContent = "≈ " + r.perSec.toFixed(1) + " MB/s · " + fmtMB(r.perMin) + "/min"; }
    { const e = $("#capReplaySizeEst"); if (e) e.textContent = "≈ " + fmtMB(r.replayMB) + " · " + replayLenLabel((state.settings.replayLen | 0) || 30); }
  }
  function replayLenLabel(n) {
    n = n | 0; if (n < 60) return n + "s";
    const m = Math.floor(n / 60), s = n % 60; return s ? (m + "m " + s + "s") : (m + "m");
  }
  const REPLAY_SNAPS = [10, 30, 60, 90, 120];
  function snapReplayLen(raw) { raw = raw | 0; for (const p of REPLAY_SNAPS) if (Math.abs(raw - p) <= 4) return p; return raw; }

  // ── Rebindable capture hotkeys ──
  const HK_CODE_VK = (() => {
    const m = {};
    for (let i = 1; i <= 24; i++) m["F" + i] = 0x70 + (i - 1);
    for (let c = 65; c <= 90; c++) m["Key" + String.fromCharCode(c)] = c;
    for (let d = 0; d <= 9; d++) { m["Digit" + d] = 0x30 + d; m["Numpad" + d] = 0x60 + d; }
    Object.assign(m, { Space: 0x20, Enter: 0x0D, Tab: 0x09, Backspace: 0x08, Backquote: 0xC0, Minus: 0xBD, Equal: 0xBB, Insert: 0x2D, Delete: 0x2E, Home: 0x24, End: 0x23, PageUp: 0x21, PageDown: 0x22,
      BracketLeft: 0xDB, BracketRight: 0xDD, Backslash: 0xDC, IntlBackslash: 0xE2, Semicolon: 0xBA, Quote: 0xDE, Comma: 0xBC, Period: 0xBE, Slash: 0xBF,
      ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28, Pause: 0x13, ScrollLock: 0x91,
      // Numpad operators send the same key code whether Num Lock is on or off. Numpad Enter is left out on purpose: Windows
      // reports it as the main Enter key, so binding it would also fire on every normal Enter press.
      NumpadSubtract: 0x6D, NumpadAdd: 0x6B, NumpadMultiply: 0x6A, NumpadDivide: 0x6F, NumpadDecimal: 0x6E });
    return m;
  })();
  // Keys that work alone (no Ctrl/Alt/Shift): F-keys, the nav block, ` and the whole numpad — none of them type text in chat
  // or play a role in most games, so a bare press is a safe hotkey.
  const HK_NO_MOD_OK = new Set([0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0xC0, 0x13, 0x91, 0x6A, 0x6B, 0x6D, 0x6E, 0x6F]);
  for (let vk = 0x70; vk <= 0x87; vk++) HK_NO_MOD_OK.add(vk);   // F1–F24
  for (let vk = 0x60; vk <= 0x69; vk++) HK_NO_MOD_OK.add(vk);   // Num 0–9
  const HK_VK_FRIENDLY = { 0xC0: "`", 0xBD: "-", 0xBB: "=", 0x20: "Space", 0x0D: "Enter", 0x09: "Tab", 0x08: "Backspace", 0x2D: "Insert", 0x2E: "Delete", 0x24: "Home", 0x23: "End", 0x21: "PgUp", 0x22: "PgDn",
    0xDB: "[", 0xDD: "]", 0xDC: "\\", 0xE2: "\\", 0xBA: ";", 0xDE: "'", 0xBC: ",", 0xBE: ".", 0xBF: "/",
    0x25: "←", 0x26: "↑", 0x27: "→", 0x28: "↓", 0x13: "Pause", 0x91: "ScrLk",
    0x6D: "Num -", 0x6B: "Num +", 0x6A: "Num *", 0x6F: "Num /", 0x6E: "Num ." };
  function hkVkName(vk) {
    if (HK_VK_FRIENDLY[vk]) return HK_VK_FRIENDLY[vk];
    for (const k in HK_CODE_VK) if (HK_CODE_VK[k] === vk) return k.startsWith("Key") ? k.slice(3) : k.startsWith("Digit") ? k.slice(5) : k.startsWith("Numpad") ? "Num" + k.slice(6) : k;
    return "—";
  }
  function hkLabel(hk) { if (!hk) return "Click to set"; if (!hk.vk) return "Off"; const p = []; if (hk.ctrl) p.push("Ctrl"); if (hk.alt) p.push("Alt"); if (hk.shift) p.push("Shift"); if (hk.win) p.push("Win"); p.push(hkVkName(hk.vk)); return p.join(" + "); }
  const HK_DEFAULTS = { recHotkey: { ctrl: true, alt: true, vk: 0x78 }, repHotkey: { ctrl: true, alt: true, vk: 0x79 }, markHotkey: { ctrl: true, alt: true, vk: 0x77 } };
  function resetHotkey(key) { state.settings[key] = Object.assign({}, HK_DEFAULTS[key]); save(); refreshHotkeyLabels(); pushClipHotkeys(); showToast("Hotkey reset", hkLabel(HK_DEFAULTS[key])); }
  function clearHotkey(key) { state.settings[key] = { vk: 0 }; save(); refreshHotkeyLabels(); pushClipHotkeys(); showToast("Hotkey cleared", "That action has no hotkey now."); }
  function hkMods(hk) { return (hk.alt ? 1 : 0) | (hk.ctrl ? 2 : 0) | (hk.shift ? 4 : 0) | (hk.win ? 8 : 0); }
  function recHk() { return state.settings.recHotkey || { ctrl: true, alt: true, vk: 0x78 }; }
  function repHk() { return state.settings.repHotkey || { ctrl: true, alt: true, vk: 0x79 }; }
  function markHk() { return state.settings.markHotkey || { ctrl: true, alt: true, vk: 0x77 }; }
  function pushClipHotkeys() {
    const r = recHk(), p = repHk(), m = markHk();
    return clipsNative.setClipHotkeys(JSON.stringify({ recordMods: hkMods(r), recordVk: r.vk | 0, replayMods: hkMods(p), replayVk: p.vk | 0, markMods: hkMods(m), markVk: m.vk | 0 }));
  }
  function refreshHotkeyLabels() {
    const a = $("#capRecHotkey"); if (a && !a._listening) a.textContent = hkLabel(recHk());
    const b = $("#capRepHotkey"); if (b && !b._listening) b.textContent = hkLabel(repHk());
    const mk = $("#capMarkHotkey"); if (mk && !mk._listening) mk.textContent = hkLabel(markHk());
    const rec = recHk(), rep = repHk();
    // Mirror the live bindings into the help copy + HUD tooltips so they never lie after a rebind.
    const rh = $("#repHkHint"); if (rh) rh.textContent = (rep && rep.vk) ? hkLabel(rep) : "your Save-replay hotkey";
    const sr = $("#galSaveReplay"); if (sr) sr.title = (rep && rep.vk) ? "Save the last few seconds (" + hkLabel(rep) + ")" : "Save the last few seconds";
    const rb = $("#galRecBtn"); if (rb) rb.title = (rec && rec.vk) ? "Start / stop recording (" + hkLabel(rec) + ")" : "Start / stop recording";
  }
  function bindHotkeyButton(btnId, key) {
    const btn = $(btnId); if (!btn) return;
    btn.addEventListener("click", () => {
      if (btn._listening) return;
      btn._listening = true; const prev = hkLabel(state.settings[key] || HK_DEFAULTS[key]);   // the KEY's own binding — a 2-way ternary showed the replay combo when cancelling a mark-hotkey rebind
      const session = (btn._hkSession = (btn._hkSession || 0) + 1);   // a keydown-completed session leaves its once-blur armed; the token stops it firing end(stale prev) into a LATER session
      btn.classList.add("listening"); btn.textContent = "Press a combo… (Esc cancels)";
      const end = (text) => { btn._listening = false; btn.classList.remove("listening"); btn.textContent = text; document.removeEventListener("keydown", onKey, true); };
      const onKey = (e) => {
        e.preventDefault(); e.stopImmediatePropagation();
        if (e.key === "Escape") { end(prev); return; }
        if (["Control", "Alt", "Shift", "Meta", "OS"].includes(e.key)) return;
        const vk = HK_CODE_VK[e.code];
        if (!vk) { btn.textContent = e.code === "NumpadEnter" ? "Numpad Enter can't be told apart from Enter — pick another key" : "That key can't be a hotkey — try another"; return; }
        const hasMod = e.ctrlKey || e.altKey || e.shiftKey || e.metaKey;
        if (!hasMod && !HK_NO_MOD_OK.has(vk)) { btn.textContent = "Add Ctrl / Alt / Shift — or use an F-key or numpad key"; return; }
        const hk = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, vk };
        state.settings[key] = hk; save(); end(hkLabel(hk));
        const r = pushClipHotkeys();
        if (r && r.then) r.then(res => { if (res === "failed") showToast("Hotkeys unavailable", "Couldn't register global hotkeys — try running Sparta Clips as administrator."); else if (res === "ok") showToast("Hotkey set", hkLabel(hk)); });
      };
      document.addEventListener("keydown", onKey, true);
      btn.addEventListener("blur", () => { if (btn._listening && btn._hkSession === session) end(prev); }, { once: true });
    });
  }
  function applyCapPreset(p) {
    state.settings.capturePreset = p;
    if (p !== "custom") { const v = CAP_PRESETS[p] || CAP_PRESETS.medium; state.settings.captureFps = v.fps; state.settings.captureBitrate = v.mbps; state.settings.captureResH = v.resH; }
  }
  // ── Per-game capture profiles: a small list editor. C# (AppConfig.ProfileFor + ClipRecorder.Start) reads these from
  //    config.json at record start, so a plain save() is all that's needed — no live push. ──
  function gameProfilesList() { return state.settings.gameProfiles || (state.settings.gameProfiles = []); }
  function renderGameProfiles() {
    const host = $("#gameProfiles"); if (!host) return;
    const list = gameProfilesList();
    // Render what's actually STORED — 0/empty means "keep the global setting" (exactly what C# does), shown as an
    // empty field / the "Global" option. before: a stored 0 rendered as a concrete "120"/"1080p" that was never applied.
    host.innerHTML = list.map((p, i) =>
      `<div class="gp-row" data-i="${i}">`
      + `<input class="sgdb-field gp-match" data-k="match" placeholder="Game name (e.g. Valorant)" value="${esc(p.match || "")}" maxlength="60" aria-label="Match text">`
      + `<label class="gp-field"><span>FPS</span><input class="sgdb-field gp-num" data-k="fps" type="number" min="15" max="240" placeholder="global" value="${(p.fps | 0) > 0 ? (p.fps | 0) : ""}"></label>`
      + `<label class="gp-field"><span>Res</span><select class="sgdb-field gp-res" data-k="resH"><option value="0"${(p.resH | 0) <= 0 ? " selected" : ""}>Global</option>${[720, 1080, 1440, 2160].map(r => `<option value="${r}"${(p.resH | 0) === r ? " selected" : ""}>${r}p</option>`).join("")}</select></label>`
      + `<label class="gp-field"><span>Mbps</span><input class="sgdb-field gp-num" data-k="mbps" type="number" min="1" max="200" placeholder="global" value="${(p.mbps | 0) > 0 ? (p.mbps | 0) : ""}"></label>`
      + `<button class="tiny-btn danger gp-del" title="Remove this profile" aria-label="Remove profile">Remove</button>`
      + `</div>`).join("") || `<div class="gp-empty">No profiles yet — add one to give a specific game its own capture quality.</div>`;
    if (!host._wired) {
      host._wired = true;
      const upd = e => {
        const row = e.target.closest(".gp-row"); if (!row) return;
        const p = gameProfilesList()[+row.dataset.i], k = e.target.dataset.k; if (!p || !k) return;
        p[k] = (k === "match") ? e.target.value : (parseInt(e.target.value, 10) || 0); save();
      };
      host.addEventListener("input", upd);
      host.addEventListener("change", upd);   // <select> fires change
      host.addEventListener("click", e => {
        const del = e.target.closest(".gp-del"); if (!del) return;
        gameProfilesList().splice(+del.closest(".gp-row").dataset.i, 1); save(); renderGameProfiles();
      });
    }
  }
  function populateDevices(selId, listFn, savedKey) {
    const sel = $(selId); if (!sel) return;
    const r = listFn(); if (!r || !r.then) return;
    r.then(json => {
      let devs = []; try { devs = JSON.parse(json || "[]"); } catch {}
      const cur = state.settings[savedKey] || "";
      sel.innerHTML = '<option value="">System default</option>' + (devs.length ? devs.map(d => `<option value="${esc(d.id)}">${esc(d.name)}</option>`).join("") : '<option value="" disabled>— no devices detected —</option>');
      sel.value = cur; if (sel.value !== cur) sel.value = "";
    });
  }
  function populateMicDevices()  { populateDevices("#capMicDevice", () => clipsNative.listMics(), "captureMicDevice"); }
  function populateChatDevices() { populateDevices("#capChatDevice", () => clipsNative.listChatDevices(), "captureChatDevice"); }
  function applyMicDeps() {
    const on = state.settings.captureMic === true;
    { const t = $("#toggleCapSepMic"); if (t) t.disabled = !on; }
    { const e = $("#capMicDevice"); if (e) e.disabled = !on; }
  }
  function applyChatDeps() { const on = state.settings.captureChat === true; const e = $("#capChatDevice"); if (e) e.disabled = !on; }
  // ── Dual-screen capture designer ──
  let _monitors = [];
  function DEFAULT_DUAL_LAYOUT() { return { a: { x: 0, y: 0, w: 16 / 9, h: 1 }, b: { x: 16 / 9, y: 0, w: 16 / 9, h: 1 } }; }
  function dualState() {
    let d = state.settings.dualScreen;
    if (!d || typeof d !== "object") d = {};
    if (d.on == null) d.on = false;
    if (typeof d.monA !== "string") d.monA = "";
    if (typeof d.monB !== "string") d.monB = "";
    if (!d.layout || !d.layout.a || !d.layout.b) d.layout = DEFAULT_DUAL_LAYOUT();
    state.settings.dualScreen = d;
    return d;
  }
  function monName(id) { const m = _monitors.find(x => x.id === id); return m ? m.name : "Display"; }
  function aspectOf(id) { const m = _monitors.find(x => x.id === id); return (m && m.w > 0 && m.h > 0) ? m.w / m.h : 16 / 9; }
  function effMonA() { const d = dualState(); return d.monA || (_monitors[0] ? _monitors[0].id : ""); }
  function presetRects(name, arA, arB, hasB) {
    if (!hasB || name === "single") return { a: { x: 0, y: 0, w: arA, h: 1 }, b: { x: 0, y: 1, w: arB, h: 1 } };
    if (name === "stack") { const mw = Math.max(arA, arB); return { a: { x: (mw - arA) / 2, y: 0, w: arA, h: 1 }, b: { x: (mw - arB) / 2, y: 1, w: arB, h: 1 } }; }
    if (name === "pip") { const h = 0.32, w = h * arB; return { a: { x: 0, y: 0, w: arA, h: 1 }, b: { x: arA - w - 0.03, y: 1 - h - 0.03, w, h } }; }
    return { a: { x: 0, y: 0, w: arA, h: 1 }, b: { x: arA, y: 0, w: arB, h: 1 } };
  }
  function applyDualPreset(name) {
    const d = dualState();
    if (name === "swap") { if (d.monB) { const t = d.layout.a; d.layout.a = d.layout.b; d.layout.b = t; } }
    else d.layout = presetRects(name, aspectOf(effMonA()), aspectOf(d.monB), !!d.monB);
    save(); pushDualScreen(); renderDualDesigner();
  }
  function pushDualScreen() {
    const d = dualState();
    clipsNative.setDualScreen(JSON.stringify({ on: d.on, monA: effMonA(), monB: d.monB, layout: d.layout }));
  }
  function dualOutSize() {
    const d = dualState(); const rects = [];
    if (effMonA()) rects.push(d.layout.a); if (d.monB) rects.push(d.layout.b);
    if (!rects.length) return null;
    let minX = 1e9, minY = 1e9, maxX = -1e9, maxY = -1e9;
    rects.forEach(r => { minX = Math.min(minX, r.x); minY = Math.min(minY, r.y); maxX = Math.max(maxX, r.x + r.w); maxY = Math.max(maxY, r.y + r.h); });
    const ev = v => { const n = Math.round(v); return Math.max(2, n - (n % 2)); };
    return { w: ev((maxX - minX) * 1080), h: ev((maxY - minY) * 1080) };
  }
  function updateDualOut() { const out = $("#capDualOut"); if (!out) return; const s = dualOutSize(); out.textContent = s ? (s.w + "×" + s.h + " · H.264 · 60fps") : "—"; }
  function populateMonitors() {
    const r = clipsNative.listMonitors(); if (!r || !r.then) return;
    r.then(json => {
      try { _monitors = JSON.parse(json || "[]"); } catch { _monitors = []; }
      const d = dualState();
      const optList = _monitors.map(m => `<option value="${esc(m.id)}">${esc(m.name)}${m.w ? " · " + m.w + "×" + m.h : ""}${m.primary ? " (primary)" : ""}</option>`).join("");
      const a = $("#capMonA"), b = $("#capMonB");
      if (a) { a.innerHTML = optList || '<option value="">No display</option>'; a.value = effMonA(); }
      if (b) { b.innerHTML = '<option value="">None (single screen)</option>' + optList; b.value = d.monB; }
      renderDualDesigner();
    });
  }
  function renderDualDesigner() {
    const canvas = $("#capDualCanvas"); if (!canvas) return;
    const d = dualState();
    const screens = [];
    if (effMonA()) screens.push({ k: "a", r: d.layout.a, name: monName(effMonA()) });
    if (d.monB)    screens.push({ k: "b", r: d.layout.b, name: monName(d.monB) });
    updateDualOut();
    if (!screens.length) { canvas.innerHTML = ""; return; }
    const cw = canvas.clientWidth || 600, ch = canvas.clientHeight || 210, pad = 16;
    let minX = 1e9, minY = 1e9, maxX = -1e9, maxY = -1e9;
    screens.forEach(s => { minX = Math.min(minX, s.r.x); minY = Math.min(minY, s.r.y); maxX = Math.max(maxX, s.r.x + s.r.w); maxY = Math.max(maxY, s.r.y + s.r.h); });
    const bw = Math.max(.01, maxX - minX), bh = Math.max(.01, maxY - minY);
    const scale = Math.min((cw - 2 * pad) / bw, (ch - 2 * pad) / bh);
    const ox = (cw - bw * scale) / 2 - minX * scale, oy = (ch - bh * scale) / 2 - minY * scale;
    canvas._dual = { scale, ox, oy };
    const two = screens.length === 2;
    canvas.innerHTML = screens.map(s => {
      const left = s.r.x * scale + ox, top = s.r.y * scale + oy, w = s.r.w * scale, h = s.r.h * scale;
      return `<div class="cap-dscreen ${s.k}" data-k="${s.k}" style="left:${left}px;top:${top}px;width:${w}px;height:${h}px">`
        + `<div class="nm">${esc(s.name)}</div>`
        + (two ? `<button class="drop" data-k="${s.k}" title="Remove this screen — record just the other">×</button>` : "")
        + `<div class="hdl" data-k="${s.k}"></div></div>`;
    }).join("");
    wireDualDrag(canvas);
  }
  function setDualRectLive(k, r, box, dl) {
    const d = dualState(); d.layout[k] = r;
    if (box && dl) { box.style.left = (r.x * dl.scale + dl.ox) + "px"; box.style.top = (r.y * dl.scale + dl.oy) + "px"; box.style.width = (r.w * dl.scale) + "px"; box.style.height = (r.h * dl.scale) + "px"; }
    updateDualOut();
  }
  function dropDualScreen(k) {
    const d = dualState();
    if (k === "b") d.monB = "";
    else { d.monA = d.monB || d.monA; d.monB = ""; }
    const a = $("#capMonA"), b = $("#capMonB"); if (a) a.value = effMonA(); if (b) b.value = "";
    applyDualPreset("single");
  }
  function wireDualDrag(canvas) {
    const dl = canvas._dual; if (!dl) return;
    canvas.querySelectorAll(".cap-dscreen").forEach(box => {
      const k = box.getAttribute("data-k");
      const begin = (e, mode) => {
        if (e.pointerType === "mouse" && e.button !== 0) return;
        e.preventDefault(); e.stopPropagation();
        const r0 = Object.assign({}, dualState().layout[k]); const sx = e.clientX, sy = e.clientY;
        box.classList.add("dragging");
        const mv = (ev) => {
          const dx = (ev.clientX - sx) / dl.scale, dy = (ev.clientY - sy) / dl.scale;
          const r = (mode === "resize")
            ? { x: r0.x, y: r0.y, w: Math.max(0.25, r0.w + dx), h: Math.max(0.18, r0.h + dy) }
            : { x: r0.x + dx, y: r0.y + dy, w: r0.w, h: r0.h };
          setDualRectLive(k, r, box, dl);
        };
        const up = () => { window.removeEventListener("pointermove", mv); window.removeEventListener("pointerup", up); window.removeEventListener("pointercancel", up); box.classList.remove("dragging"); save(); pushDualScreen(); renderDualDesigner(); };
        window.addEventListener("pointermove", mv); window.addEventListener("pointerup", up); window.addEventListener("pointercancel", up);
      };
      box.addEventListener("pointerdown", (e) => { if (e.target.classList.contains("drop")) return; begin(e, e.target.classList.contains("hdl") ? "resize" : "move"); });
    });
    canvas.querySelectorAll(".drop").forEach(btn => btn.addEventListener("click", (e) => { e.stopPropagation(); dropDualScreen(btn.getAttribute("data-k")); }));
  }
  function applyDualLock() {
    const dual = dualState().on, custom = capCfg().preset === "custom";
    const dis = (id, off) => { const e = $(id); if (e) e.disabled = off; };
    dis("#capQuality", dual); dis("#capMode", dual); dis("#capEncoder", dual);
    ["#capFps", "#capBitrate", "#capRes"].forEach(id => dis(id, dual || !custom));
  }

  function syncCapturePane() {
    const c = capCfg();
    { const e = $("#capQuality"); if (e) e.value = c.preset; }
    { const e = $("#capFps"); if (e) e.value = String(c.fps); }
    { const e = $("#capBitrate"); if (e) e.value = String(c.mbps); }
    { const e = $("#capRes"); if (e) e.value = String(c.resH); }
    { const e = $("#capMode"); if (e) e.value = c.mode; }
    { const e = $("#capEncoder"); if (e) e.value = c.encoder; }
    updateHevcRescanRow();
    if (_hwAv1 === null) { const r = clipsNative.hasHardwareAv1(); if (r && r.then) r.then(hw => { _hwAv1 = !!hw; applyAv1Availability(); }); } else applyAv1Availability();
    { const e = $("#capCap"); if (e) e.value = String(c.capGB); }
    { const e = $("#capReplayLen"); if (e) e.value = String(c.replayLen); const lab = $("#capReplayLenVal"); if (lab) lab.textContent = replayLenLabel(c.replayLen); }
    { const t = $("#toggleCapMic"); if (t) t.setAttribute("aria-checked", String(c.mic)); }
    { const t = $("#toggleCapSepMic"); if (t) t.setAttribute("aria-checked", String(c.sepMic)); }
    { const t = $("#toggleCapChat"); if (t) t.setAttribute("aria-checked", String(c.chat)); }
    populateMicDevices();
    populateChatDevices();
    applyMicDeps();
    applyChatDeps();
    { const t = $("#toggleCapAuto"); if (t) t.setAttribute("aria-checked", String(state.settings.autoClip === true)); }
    { const t = $("#toggleCapReplay"); if (t) t.setAttribute("aria-checked", String(c.replay)); }
    { const t = $("#toggleCapAutoHighlight"); if (t) t.setAttribute("aria-checked", String(state.settings.autoHighlight === true)); }
    { const t = $("#toggleCapChime"); if (t) t.setAttribute("aria-checked", String(c.chime)); }
    const custom = c.preset === "custom";
    ["#capFps", "#capBitrate", "#capRes"].forEach(id => { const e = $(id); if (e) e.disabled = !custom; });
    { const t = $("#toggleCapDual"); if (t) t.setAttribute("aria-checked", String(dualState().on)); }
    { const b = $("#capDualBody"); if (b) { const off = !dualState().on; b.classList.toggle("locked", off); b.inert = off; } }
    populateMonitors();
    applyDualLock();
    refreshClipFolderLabel();
    refreshHotkeyLabels();
    renderSizeEstimate();
    paintRangeFills($("#view-settings"));
  }
  function refreshClipFolderLabel() {
    const fp = $("#capFolderPath"); if (!fp) return;
    const r = clipsNative.getClipsDir(); if (r && r.then) r.then(p => { if (p) fp.textContent = p; });
  }

  // ── Standalone app-shell settings (arm policy / autostart / close behaviour / quit / logs) ──
  function syncShellSettings() {
    { const e = $("#setArmPolicy"); if (e) e.value = state.settings.armPolicy || "always"; }
    { const t = $("#toggleAutostart"); if (t) t.setAttribute("aria-checked", String(state.settings.autostart === true)); }
    { const t = $("#toggleAutostartElev"); if (t) t.setAttribute("aria-checked", String(state.settings.autostartElevated === true)); }
    { const t = $("#toggleReleaseOnClose"); if (t) t.setAttribute("aria-checked", String(state.settings.releaseUiOnClose !== false)); }
    { const t = $("#toggleOverlay"); if (t) t.setAttribute("aria-checked", String(state.settings.overlayEnabled !== false)); }
    { const t = $("#toggleAutoUpdate"); if (t) t.setAttribute("aria-checked", String(state.settings.autoUpdate !== false)); }
  }
  // A small icon per settings section (matched by keyword in the group heading), for scannable nav chips.
  function sectionIcon(title) {
    const t = (title || "").toLowerCase();
    const S = (inner) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${inner}</svg>`;
    if (t.includes("record")) return S('<rect x="2.5" y="6" width="13" height="12" rx="2"></rect><path d="m15.5 10 5-3v10l-5-3"></path>');
    if (t.includes("dual")) return S('<rect x="2" y="4" width="9" height="12" rx="1.5"></rect><rect x="13" y="7" width="9" height="12" rx="1.5"></rect>');
    if (t.includes("audio")) return S('<path d="M4 9v6h4l5 4V5L8 9z"></path><path d="M17 8.5a5 5 0 0 1 0 7"></path>');
    if (t.includes("automation") || t.includes("storage")) return S('<path d="M13 2 4 14h7l-1 8 9-12h-7z"></path>');
    if (t.includes("replay")) return S('<path d="M3.5 12a8.5 8.5 0 1 0 2.6-6.1"></path><path d="M3.5 4.5v5h5"></path>');
    if (t.includes("folder") || t.includes("hotkey")) return S('<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"></path>');
    return S('<line x1="4" y1="8" x2="20" y2="8"></line><circle cx="9" cy="8" r="2"></circle><line x1="4" y1="16" x2="20" y2="16"></line><circle cx="15" cy="16" r="2"></circle>');
  }
  // Sticky settings sub-nav: one chip per settings group; click expands + scrolls, and a scroll-spy highlights the
  // chip for the section currently in view.
  // Paged settings: only one group ("page") is shown at a time. The wheel advances/retreats between pages — but a
  // page taller than the viewport scrolls internally first, and only pages once it hits its top/bottom edge. Nav-pill
  // chips jump straight to a page. One wheel gesture = one page (a short cooldown debounces touchpad momentum).
  let _setPage = 0, _wheelAccum = 0, _wheelLast = 0;
  // Panel list cached (rebuilt in buildSettingsNav) — the wheel handler is non-passive, so a document-wide
  // querySelectorAll per wheel tick was main-thread work the compositor had to block scrolling on.
  let _setPanels = null;
  function setPanels() { return _setPanels || (_setPanels = $$(".settings-pane .panel.section")); }
  function applyPage(i) {
    const panels = setPanels(); if (!panels.length) return;
    _setPage = Math.max(0, Math.min(panels.length - 1, i));
    panels.forEach((p, k) => { const on = k === _setPage; p.classList.toggle("active", on); if (on) p.scrollTop = 0; });
    const nav = $("#setSubnav"); if (nav) nav.querySelectorAll(".set-navchip").forEach((c, k) => c.classList.toggle("active", k === _setPage));
  }
  function setSettingsPage(i) { applyPage(i); }   // chip click / initial — jump straight to that page
  function onSettingsWheel(e) {
    if (_view !== "settings") return;
    const p = setPanels()[_setPage]; if (!p) return;
    const down = e.deltaY > 0;
    const overflow = p.scrollHeight - p.clientHeight;
    const atBottom = Math.ceil(p.scrollTop + p.clientHeight) >= p.scrollHeight - 1;
    const atTop = p.scrollTop <= 0;
    // A meaningfully-tall page scrolls internally first; otherwise a wheel notch advances exactly one page in REAL TIME
    // (no queued catch-up) — so you land on the page you stopped scrolling on, never coasting to the last one.
    if (overflow > 24 && ((down && !atBottom) || (!down && !atTop))) { _wheelAccum = 0; return; }
    e.preventDefault();
    const now = performance.now();
    if (now - _wheelLast > 200) _wheelAccum = 0;   // a pause starts a fresh gesture
    _wheelLast = now;
    let d = e.deltaY;
    if (e.deltaMode === 1) d *= 16; else if (e.deltaMode === 2) d *= (p.clientHeight || 600);   // lines/pages → px
    _wheelAccum += d;
    if (Math.abs(_wheelAccum) >= 40) {   // one page per wheel notch (no far leaps on a big/fast spin)
      const dir = _wheelAccum > 0 ? 1 : -1; _wheelAccum = 0;
      applyPage(_setPage + dir);
    }
  }
  function buildSettingsNav() {
    const nav = $("#setSubnav"); if (!nav) return;
    _setPanels = null;   // panel set may have changed — refresh the wheel handler's cache
    const panels = setPanels();
    nav.innerHTML = panels.map((p, i) => { const h = p.querySelector(".section-head h3"); const label = h ? h.textContent : ("Section " + (i + 1)); return `<button class="set-navchip" data-i="${i}">${sectionIcon(label)}<span>${esc(label)}</span></button>`; }).join("");
    nav.querySelectorAll(".set-navchip").forEach(chip => chip.addEventListener("click", () => setSettingsPage(chip.dataset.i | 0)));
    const pane = $(".settings-pane");
    if (pane && !pane._wheelWired) { pane._wheelWired = true; pane.addEventListener("wheel", onSettingsWheel, { passive: false }); }
    setSettingsPage(_setPage);
  }
  (function wireShellSettings() {
    { const e = $("#setArmPolicy"); if (e) e.addEventListener("change", () => { state.settings.armPolicy = e.value === "game" ? "game" : "always"; save(); clipsNative.setArmPolicy(state.settings.armPolicy); showToast("Instant replay", state.settings.armPolicy === "game" ? "Arms when a game is detected." : "Always armed while Sparta Clips runs."); }); }
    // Autostart pair — always routed through the atomic setAutostartMode; the C# reply (onAutostartState) reconciles
    // both toggles to the OBSERVED state, so a cancelled UAC prompt simply snaps the switch back.
    { const t = $("#toggleAutostart"); if (t) t.addEventListener("click", () => {
        const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on));
        state.settings.autostart = on;
        const elev = state.settings.autostartElevated === true;
        if (!on && elev) { state.settings.autostartElevated = false; const e = $("#toggleAutostartElev"); if (e) e.setAttribute("aria-checked", "false"); }
        save();
        clipsNative.setAutostartMode(on ? (elev ? "elevated" : "on") : "off");
        showToast("Start with Windows", on ? "Sparta Clips will launch to the tray on sign-in." : (elev ? "Off — Windows will ask for approval to remove the elevated task." : "Off."));
    }); }
    { const t = $("#toggleAutostartElev"); if (t) t.addEventListener("click", () => {
        const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on));
        state.settings.autostartElevated = on;
        if (on && state.settings.autostart !== true) { state.settings.autostart = true; const a = $("#toggleAutostart"); if (a) a.setAttribute("aria-checked", "true"); }   // elevated implies autostart
        save();
        clipsNative.setAutostartMode(on ? "elevated" : (state.settings.autostart === true ? "on" : "off"));
        showToast("Start elevated", on
          ? "Windows will ask for approval to create the scheduled task…"
          : (state.settings.autostart === true ? "Windows will ask for approval to remove the task — falling back to the standard launcher." : "Off."));
    }); }
    // C# reports the observable autostart state (Run key vs scheduled task) — reconcile the saved settings + toggles.
    window.onAutostartState = (st) => { try {
        const task = !!(st && st.task), runKey = !!(st && st.runKey);
        state.settings.autostart = task || runKey;
        state.settings.autostartElevated = task;
        save();
        const a = $("#toggleAutostart"); if (a) a.setAttribute("aria-checked", String(task || runKey));
        const e = $("#toggleAutostartElev"); if (e) e.setAttribute("aria-checked", String(task));
    } catch {} };
    { const t = $("#toggleReleaseOnClose"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.releaseUiOnClose = on; save(); showToast("Keep running when closed", on ? "The X keeps Sparta Clips in the tray." : "The X quits Sparta Clips fully."); }); }
    { const t = $("#toggleOverlay"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.overlayEnabled = on; save(); showToast("Recording indicator", on ? "A ● REC timer shows over the game while recording." : "Off — no in-game recording indicator."); }); }
    { const b = $("#addGameProfile"); if (b) b.addEventListener("click", () => { gameProfilesList().push({ match: "", fps: (state.settings.captureFps | 0) || 120, resH: (state.settings.captureResH | 0) || 1080, mbps: (state.settings.captureBitrate | 0) || 20 }); save(); renderGameProfiles(); const el = $("#gameProfiles"); const inp = el && el.querySelector(".gp-row:last-child .gp-match"); if (inp) inp.focus(); }); }
    renderGameProfiles();
    { const b = $("#openLogsBtn"); if (b) b.addEventListener("click", () => clipsNative.openLogsFolder()); }
    // ── Updates: C# (AppUpdater) checks GitHub releases; this only shows the version, the toggle and "Check now". ──
    let _updInfo = null;
    const updBase = () => !_updInfo ? "Sparta Clips" : "Sparta Clips " + _updInfo.version + (_updInfo.installed ? "" : " · portable copy (updates are for installed copies)");
    const setUpdStatus = (t) => { const s = $("#updStatus"); if (s) s.textContent = t; };
    { const t = $("#toggleAutoUpdate"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.autoUpdate = on; save(); showToast("Automatic updates", on ? "Sparta Clips will ask when a new version is out." : "Off — use “Check for updates” any time."); }); }
    { const b = $("#checkUpdatesBtn"); if (b) b.addEventListener("click", () => { b.disabled = true; clipsNative.checkForUpdates(); setTimeout(() => { b.disabled = false; }, 4000); }); }
    window.onUpdateStatus = (r) => { try {
      const st = r && r.state, v = (r && r.latest) || "";
      const b = $("#checkUpdatesBtn");
      if (st === "checking") { setUpdStatus(updBase() + " · checking…"); return; }
      if (b) b.disabled = false;
      if (st === "uptodate") setUpdStatus(updBase() + " · up to date");
      else if (st === "available") setUpdStatus(updBase() + " · version " + v + " is available");
      else if (st === "dev") setUpdStatus(updBase() + " · version " + v + " is on GitHub");
      else setUpdStatus(updBase() + " · couldn't reach GitHub — try again later");
    } catch {} };
    (async () => { try {
      const j = await clipsNative.getUpdateInfo(); if (!j) return;
      _updInfo = JSON.parse(j); setUpdStatus(updBase());
      const h = $("#appVersionHint"); if (h) h.textContent = "Sparta Clips v" + _updInfo.version;
      if (_updInfo.justUpdated) showToast("Sparta Clips updated", "You're now on version " + _updInfo.version + ".");
    } catch {} })();
    { const b = $("#quitAppBtn"); if (b) b.addEventListener("click", () => clipsNative.quitApp()); }
  })();
  (function wireCapturePane() {
    { const e = $("#capQuality"); if (e) e.addEventListener("change", () => { applyCapPreset(e.value); save(); syncCapturePane(); pushCaptureSettings(); }); }
    { const e = $("#capFps"); if (e) e.addEventListener("change", () => { state.settings.captureFps = e.value | 0; save(); pushCaptureSettings(); }); }
    { const e = $("#capBitrate"); if (e) e.addEventListener("change", () => { state.settings.captureBitrate = e.value | 0; save(); pushCaptureSettings(); }); }
    { const e = $("#capRes"); if (e) e.addEventListener("change", async () => {
        const newRes = e.value | 0;
        // Only warn when there's NO native HEVC codec — then >1080p HEVC previews via a software H.264 conversion (slow).
        // With native decode (OS codec installed) 1440p HEVC plays fine, so no warning is needed.
        if (state.settings.captureEncoder === "h265" && newRes > 1080 && _hevcNative === false) {
          const ok = await confirmDouble({
            title: "High-resolution HEVC preview",
            body: "Your system has <b>no hardware HEVC codec</b>, so <b>H.265 / HEVC above 1080p</b> is converted to H.264 for in-app preview — that can be <b>slow or stutter</b> at 1440p+ / 60fps+. Recording and export are unaffected; this only impacts in-app playback.<br><br>Keep <b>1080p</b> for smooth preview, or record at " + newRes + "p anyway?",
            confirmLabel: "Use " + newRes + "p anyway", secondLabel: "Sure? Click again to confirm"
          });
          if (!ok) { e.value = String(state.settings.captureResH ?? 1080); return; }   // revert the SELECT to the saved value — the hardcoded 1080 used to clobber e.g. a 720p setting on cancel
        }
        state.settings.captureResH = newRes; save(); pushCaptureSettings();
    }); }
    { const e = $("#capMode"); if (e) e.addEventListener("change", () => { state.settings.captureMode = e.value; save(); pushCaptureSettings(); }); }
    { const e = $("#capEncoder"); if (e) e.addEventListener("change", () => {
        state.settings.captureEncoder = e.value;
        updateHevcRescanRow();   // show/hide the "In-app HEVC preview" re-scan row
        save(); pushCaptureSettings();
        // Only NON-native HEVC needs the 1080p guardrail (software preview); with the OS codec, any resolution plays natively.
        if (e.value === "h265") {
          showToast("H.265 / HEVC", "Smallest files. Checking whether your system can preview HEVC natively…");
          detectHevcNative().then(native => {
            updateHevcScanState();
            if (!native && (state.settings.captureResH === 0 || state.settings.captureResH > 1080)) {
              state.settings.captureResH = 1080; const r = $("#capRes"); if (r) r.value = "1080"; save(); pushCaptureSettings();
            }
            showToast("HEVC preview", native
              ? "Hardware HEVC detected — HEVC clips preview natively at any resolution."
              : "No system HEVC codec — HEVC clips convert to H.264 for preview, so resolution is kept at 1080p (higher needs a double-confirm).");
          });
        }
        else if (e.value === "av1_hw") { clipsNative.ensureAv1Encoder(); showToast("AV1 — hardware", "Smallest files, hardware-encoded. Fetching the FFmpeg encoder (one-time)."); }
        else if (e.value === "av1_sw") { clipsNative.ensureAv1Encoder(); showToast("AV1 — software", "Smallest files, encoded on the CPU (libSVT-AV1) — slower, best for short clips & replays. Fetching FFmpeg (one-time)."); }
    }); }
    { const b = $("#hevcRescanBtn"); if (b) b.addEventListener("click", rescanHevc); }
    { const e = $("#capCap"); if (e) e.addEventListener("change", () => { state.settings.captureCapGB = e.value | 0; save(); pushCaptureSettings(); }); }
    { const t = $("#toggleCapMic"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.captureMic = on; save(); pushCaptureSettings(); applyMicDeps(); }); }
    { const t = $("#toggleCapSepMic"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.captureSepMic = on; save(); pushCaptureSettings(); showToast("Separate mic track", on ? "New recordings keep the mic on its own editable track." : "Mic is mixed into the clip."); }); }
    { const e = $("#capMicDevice"); if (e) e.addEventListener("change", () => { state.settings.captureMicDevice = e.value; save(); clipsNative.setMicDevice(e.value); showToast("Microphone", e.value ? (e.options[e.selectedIndex] ? e.options[e.selectedIndex].text : "Selected") : "System default"); }); }
    { const t = $("#toggleCapChat"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.captureChat = on; save(); pushCaptureSettings(); applyChatDeps(); showToast("Chat track", on ? "New recordings capture a separate chat track from the chosen output device." : "Off."); }); }
    { const e = $("#capChatDevice"); if (e) e.addEventListener("change", () => { state.settings.captureChatDevice = e.value; save(); clipsNative.setChatDevice(e.value); showToast("Chat device", e.value ? (e.options[e.selectedIndex] ? e.options[e.selectedIndex].text : "Selected") : "System default"); }); }
    { const t = $("#toggleCapAuto"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.autoClip = on; save(); showToast("Auto-record", on ? "Recording starts automatically when you launch a game." : "Off."); }); }
    { const t = $("#toggleCapReplay"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.replayEnabled = on; save(); applyReplayArm(); showToast("Instant replay", on ? "Armed — a background buffer is rolling; press your Save-replay hotkey any time." : "Off."); }); }
    { const t = $("#toggleCapAutoHighlight"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.autoHighlight = on; save(); clipsNative.setAutoHighlight(on); showToast("Auto-highlight", on ? "On — League/TFT kills & assists auto-save a replay (Instant replay must be on)." : "Off."); }); }
    { const e = $("#capReplayLen"), lab = $("#capReplayLenVal");
      if (e) {
        e.addEventListener("input", () => { const v = snapReplayLen(e.value); if ((e.value | 0) !== v) e.value = v; if (lab) lab.textContent = replayLenLabel(v); const est = $("#capReplaySizeEst"); if (est) est.textContent = "≈ " + fmtMB(estCaptureRates().perSec * v) + " · " + replayLenLabel(v); e.style.setProperty("--vol", rangeFillPct(e)); });
        e.addEventListener("change", () => { state.settings.replayLen = snapReplayLen(e.value); save(); pushCaptureSettings(); });
      } }
    { const t = $("#toggleCapChime"); if (t) t.addEventListener("click", () => { const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on)); state.settings.replayChime = on; save(); pushCaptureSettings(); showToast("Capture sound", on ? "A chime plays when a replay is saved." : "Off."); }); }
    { const t = $("#toggleCapDual"); if (t) t.addEventListener("click", () => {
        const on = t.getAttribute("aria-checked") !== "true"; t.setAttribute("aria-checked", String(on));
        const d = dualState(); d.on = on;
        const body = $("#capDualBody"); if (body) { body.classList.toggle("locked", !on); body.inert = !on; }
        if (on) {
          if (!d.monA && _monitors[0]) d.monA = _monitors[0].id;
          if (!d.monB && _monitors[1]) d.monB = _monitors[1].id;
          const a = $("#capMonA"), b = $("#capMonB"); if (a) a.value = effMonA(); if (b) b.value = d.monB;
          applyDualPreset("side");
        } else { save(); pushDualScreen(); }
        applyDualLock();
        // No toast — the panel un-greying (or greying) is feedback enough.
    }); }
    { const e = $("#capMonA"); if (e) e.addEventListener("change", () => { dualState().monA = e.value; applyDualPreset("side"); }); }
    { const e = $("#capMonB"); if (e) e.addEventListener("change", () => { dualState().monB = e.value; applyDualPreset("side"); }); }
    $$("#capDualBody .cap-dual-preset").forEach(btn => btn.addEventListener("click", () => applyDualPreset(btn.getAttribute("data-dpreset"))));
    { const b = $("#capOpenFolder"); if (b) b.addEventListener("click", () => clipsNative.openClipsFolder()); }
    { const b = $("#capChangeFolder"); if (b) b.addEventListener("click", async () => {
        const dir = await clipsNative.pickFolder(state.settings.clipFolder || "");
        if (!dir) return;
        // Refuse a read-only / admin-only folder before committing. Probed OFF the UI thread in C# (a dead network
        // path can hang on the SMB timeout) — result comes back via window.onFolderWritable; 8s safety timeout.
        const w = await new Promise(res => {
          const t = setTimeout(() => { window.onFolderWritable = null; res(false); }, 8000);
          window.onFolderWritable = ok => { clearTimeout(t); window.onFolderWritable = null; res(!!ok); };
          const r = clipsNative.checkFolderWritable(dir); if (!r || !r.then) { clearTimeout(t); window.onFolderWritable = null; res(true); }   // browser preview: allow
        });
        if (!w) { showToast("Save folder", "Can't write to that folder — pick another (it may be read-only, unreachable, or need administrator rights)."); return; }
        state.settings.clipFolder = dir; save();
        clipsNative.setClipsDir(dir);
        refreshClipFolderLabel();
        clipsNative.requestClips();
        showToast("Save folder", "New clips save here. Existing clips stay in their old folder.");
    }); }
    { const b = $("#capResetFolder"); if (b) b.addEventListener("click", () => {
        if (!state.settings.clipFolder) { showToast("Save folder", "Already using the default folder."); return; }
        state.settings.clipFolder = ""; save();
        clipsNative.setClipsDir("");
        refreshClipFolderLabel();
        clipsNative.requestClips();
        showToast("Save folder", "Reset to the default Videos\\Sparta Clips\\Clips folder.");
    }); }
    bindHotkeyButton("#capRecHotkey", "recHotkey");
    bindHotkeyButton("#capRepHotkey", "repHotkey");
    { const b = $("#capRecHotkeyReset"); if (b) b.addEventListener("click", () => resetHotkey("recHotkey")); }
    { const b = $("#capRecHotkeyClear"); if (b) b.addEventListener("click", () => clearHotkey("recHotkey")); }
    { const b = $("#capRepHotkeyReset"); if (b) b.addEventListener("click", () => resetHotkey("repHotkey")); }
    { const b = $("#capRepHotkeyClear"); if (b) b.addEventListener("click", () => clearHotkey("repHotkey")); }
    bindHotkeyButton("#capMarkHotkey", "markHotkey");
    { const b = $("#capMarkHotkeyReset"); if (b) b.addEventListener("click", () => resetHotkey("markHotkey")); }
    { const b = $("#capMarkHotkeyClear"); if (b) b.addEventListener("click", () => clearHotkey("markHotkey")); }
    refreshHotkeyLabels();
  })();

  let _clipsSig = "";   // content signature of the last rendered list — duplicate pushes skip the full DOM rebuild
  window.onClips = (data) => {
    try {
      const clips = (data && data.clips) || [];
      const total = (data && data.totalBytes) || 0;
      const sig = total + "|" + clips.map(c => c.name + ":" + (c.modified || 0) + ":" + (c.size || 0)).join(",");
      const changed = sig !== _clipsSig || !_clipsLoaded;
      _clipsSig = sig;
      _clips = clips; _clipsTotal = total;
      _clipsLoaded = true;   // first real response arrived — the empty state may now show if there genuinely are no clips
      const editorOpen = (() => { const pg = $("#clipPage"); return pg && !pg.hidden; })();
      // A full gallery innerHTML rebuild underneath the fixed editor overlay is wasted work (every replay save while
      // editing triggered one) — mark it stale and rebuild once when the editor closes.
      if (changed && _view === "gallery") { if (editorOpen) _galStale = true; else renderLibrary(); }
      if (changed && editorOpen) renderRecentClips();
    } catch {}
  };
  const _thumbReq = new Set(), _infoReq = new Set();   // in-flight bridge requests — a gallery rebuild must not re-fire N pending probes
  window.onClipThumb = (name, url) => {
    try {
      if (!url) { _thumbReq.delete(name); return; }   // failed → allow a later render to retry
      thumbMem[name] = url;
      const sel = '[data-name="' + escSel(name) + '"]';   // target the ONE card, not an O(N) scan per callback
      const card = document.querySelector(".gal-card" + sel);
      if (card) { const img = card.querySelector(".gal-thumb-img"); if (img && !img.getAttribute("src")) img.src = url; }
      const it = document.querySelector(".clip-recent-item" + sel);
      if (it) { const im = it.querySelector(".clip-recent-thumb"); if (im && !im.getAttribute("src")) im.src = url; }
    } catch {}
  };
  const infoMem = {};
  // Escape a clip name for use inside a [data-name="…"] attribute selector (names can contain quotes/brackets).
  function escSel(n) { return (window.CSS && CSS.escape) ? CSS.escape(n) : String(n).replace(/["\\]/g, "\\$&"); }
  // Snap a probed avg framerate to the NEAREST standard rate for DISPLAY only. Window capture is variable-rate, so a
  // 25 fps game averages 26-28 and a "120" capture ~120.x — before (±2% window) cards showed "27 FPS". Encoding
  // decisions still use the raw value. Falls back to a plain round when nothing is within 12%.
  function snapFps(fps) {
    const n = fps || 0; if (!n) return 0;
    let best = 0, bd = Infinity;
    for (const s of [24, 25, 30, 48, 50, 60, 72, 90, 100, 120, 144, 165, 240, 360]) { const d = Math.abs(n - s); if (d < bd) { bd = d; best = s; } }
    return bd <= best * 0.12 ? best : Math.round(n);
  }
  function techHTML(info) {
    if (!info) return "";
    const cmap = { hevc: "H.265", h264: "H.264", av1: "AV1" };
    const res = info.height ? (info.height >= 4320 ? "8K" : info.height >= 2160 ? "4K" : info.height + "p") : "";
    const fps = info.fps ? snapFps(info.fps) + " FPS" : "";
    const codec = info.codec ? (cmap[info.codec] || String(info.codec).toUpperCase()) : "";
    return [res, fps, codec].filter(Boolean).map(t => `<span class="gal-tech-chip">${esc(t)}</span>`).join("");
  }
  window.onClipInfo = (name, info) => {
    try {
      if (!info) { _infoReq.delete(name); return; }
      infoMem[name] = info;
      { const card = document.querySelector('.gal-card[data-name="' + escSel(name) + '"]'); if (card) { const el = card.querySelector("[data-tech]"); if (el) el.innerHTML = techHTML(info); } }
      if (name === _clipName && info.width > 0 && info.height > 0) _clipAR = info.width / info.height;   // <video> letterboxes via object-fit
      if (name === _clipName) {
        updateHevcWarn();
        // Codec is known only now (async) — if this turned out to be non-native HEVC loaded directly, switch to the proxy.
        if (info.codec === "hevc" && _hevcNative === false && _clipSrcMode === "direct") setClipSource(_clipName);
      }
    } catch {}
  };
  let _pendingAuto = null;
  window.onCapture = (ev, detail) => {
    try {
      if (ev === "state") { _recording = !!(detail && detail.recording); updateClipRecUI(); }
      else if (ev === "replay") { _replayArmed = !!(detail && detail.armed); updateClipRecUI(); }   // HUD-only state — a full gallery rebuild on every arm/disarm (each game focus change under armPolicy "game") was wasted work
      else if (ev === "saving") { _replaySaving = true; if (_view === "gallery") renderLibrary(); }
      else if (ev === "saved") {
        _replaySaving = false;
        const nm = (detail && detail.name) || "";
        if (nm && _pendingAuto && (Date.now() - _pendingAuto.at) < 12000) {
          const o = state.clipBookmarks || (state.clipBookmarks = {});
          const t = Math.max(0, (((state.settings.replayLen | 0) || 30) - 1.5));
          (o[nm] || (o[nm] = [])).push({ t, label: _pendingAuto.label, auto: true });
          save();
        }
        _pendingAuto = null;
        showToast("Clip saved", nm); clipsNative.requestClips();   // one refresh — C# also pushes the fresh list once after "saved" (was 4 rebuilds per save)
      }
      else if (ev === "autoclip") { _pendingAuto = { label: (detail && detail.label) || "Highlight", at: Date.now() }; showToast("Auto-clip", "Highlight detected" + (detail && detail.label ? " · " + detail.label : "") + " — saving…"); }
      else if (ev === "segprogress") { if (detail && detail.total > 1) showToast("Exporting segments…", "Done " + detail.done + " of " + detail.total); }
      else if (ev === "transcoding") { showToast("Encoding AV1…", "Finishing your clip — it'll appear in the gallery shortly."); }
      else if (ev === "av1fail") { showToast("AV1 encode failed", "Saved the recording as H.264 instead."); }
      else if (ev === "recfail") { _recording = false; updateClipRecUI(); showToast("Recording failed", (detail && detail.reason) ? String(detail.reason).slice(0, 140) : "The capture engine couldn't record. Try a different encoder in Settings, or check the logs."); if (_view === "gallery") renderLibrary(); }
      else if (ev === "micfail") { const chat = detail && detail.track === "chat"; showToast(chat ? "Chat audio" : "Microphone", "Couldn't capture the " + (chat ? "chat device" : "mic") + " — check Windows privacy settings (Microphone) or that the device is connected."); }
      else if (ev === "disklow") { showToast("Disk almost full", "Recording stopped — what was captured has been saved."); }
      else if (ev === "trimfail") { _replaySaving = false; showToast("Save failed", "Couldn't write the clip from that selection."); if (_view === "gallery") renderLibrary(); }
      else if (ev === "noreplay") { _replaySaving = false; showToast("Instant replay", state.settings.replayEnabled === false ? "Instant replay is off — turn it on in Settings." : (state.settings.armPolicy === "game" ? "No game detected — replay only arms while a game is in focus." : "Nothing buffered yet.")); }
      else if (ev === "marked") { showToast("Moment marked", "Bookmarked at " + clipDur((detail && detail.t) || 0) + " — it'll show on the timeline when you open this clip."); }
      else if (ev === "marknorec") { showToast("Nothing to mark", "Start a recording first — the mark hotkey bookmarks the live recording."); }
    } catch {}
  };

  function applyReplayArm() {
    // C# (TrayCore) owns the actual arming, per arm policy. before: this armed/disarmed the ring directly, which
    // fought TrayCore's ownership. after: report only the enabled flag; TrayCore arms/disarms.
    clipsNative.setReplayEnabled(state.settings.replayEnabled === true);
  }
  function toggleClipRecord() {
    if (_recording) clipsNative.stopClipRecord();
    else { clipsNative.startClipRecord(curGameTitle()); showToast("Recording", "Capturing — press again (or your record hotkey) to stop."); }
  }
  function updateClipRecUI() {
    // Instant-replay owns the capture device, so manual Record & Save-replay are mutually exclusive (ClipRecorder.Start
    // no-ops while armed). The HUD shows exactly the ONE capture action that works right now: Record when idle, the
    // amber Save-replay when the replay ring is armed, and "Stop m:ss" while a manual recording is running.
    const armed = _replayArmed && !_recording;
    const rec = $("#galRecBtn");
    if (rec) { rec.hidden = armed; rec.classList.toggle("recording", _recording); const l = rec.querySelector(".gal-rec-label"); if (l) l.textContent = _recording ? "Stop" : "Record"; }
    const sr = $("#galSaveReplay"); if (sr) sr.hidden = !armed;
    const om = $("#tbOmega"); if (om) { om.classList.toggle("rec", _recording); om.classList.toggle("armed", armed); }
    if (_recording) startRecTimer(); else stopRecTimer();
  }
  // Game_dd.MM.yyyy.HH.mm[_replay|_clip|_combined|_recovered][_n].mp4 → { game, date }. The suffixes are what C#
  // actually appends (replay saves, editor trims/combines, AV1-salvage) — without them, every replay fell through to
  // the raw-filename card title and the "All games" filter had no entries for the app's PRIMARY clip type.
  function parseClip(name) {
    const stem = String(name || "").replace(/\.[^.]+$/, "");
    const m = stem.match(/^(.*)_(\d{2})\.(\d{2})\.(\d{4})\.(\d{2})\.(\d{2})(?:_(?:replay|clip|combined|recovered))?(?:_\d+)?(?:_recovered)?$/);
    if (m) return { game: m[1].replace(/_/g, " ").trim(), date: new Date(+m[4], +m[3] - 1, +m[2], +m[5], +m[6]) };
    return { game: "", date: null };
  }
  // What kind of clip a file is, from the suffix C# appends — shown on cards instead of repeating the game name.
  function clipKind(name) {
    const n = String(name || "");
    if (/_replay(?:_\d+)?\.[^.]+$/i.test(n)) return "Replay";
    if (/_(?:clip|combined)(?:_\d+)?\.[^.]+$/i.test(n)) return "Edited";
    if (/_recovered/i.test(n)) return "Recovered";
    return parseClip(n).game ? "Recording" : "";
  }
  // "Transformers Armada · Replay · 17:03" (date added when not today) — the editor title; the raw file name is
  // what Rename edits.
  function clipTitle(name) {
    const p = parseClip(name);
    if (!p.game) return String(name || "").replace(/\.[^.]+$/, "").replace(/_/g, " ");
    const d = p.date, today = d && d.toDateString() === new Date().toDateString();
    const when = d ? (today ? "" : d.toLocaleDateString([], { day: "numeric", month: "short" }) + " ") + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : "";
    return [p.game, clipKind(name), when].filter(Boolean).join("  ·  ");
  }
  function clipRel(ms) { const s = (Date.now() - ms) / 1000; if (s < 60) return "Now"; if (s < 3600) return Math.floor(s / 60) + "m ago"; if (s < 86400) return Math.floor(s / 3600) + "h ago"; return Math.floor(s / 86400) + "d ago"; }
  function clipDur(sec) { sec = Math.round(sec || 0); return Math.floor(sec / 60) + ":" + String(sec % 60).padStart(2, "0"); }
  function clipBytes(b) { if (!b) return "0 MB"; const gb = b / 1073741824; return gb >= 1 ? gb.toFixed(1) + " GB" : Math.max(1, Math.round(b / 1048576)) + " MB"; }
  function clipUrl(name) { return "https://clips.local/" + encodeURIComponent(name); }

  const thumbMem = {};
  const DISK_ICO = `<svg class="gal-storage-ico" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="3"/><path d="M3 14h18"/><circle cx="16.5" cy="17" r=".9" fill="currentColor"/></svg>`;
  function galCardHTML(c) {
    const p = parseClip(c.name);
    const dur = c.duration ? `<span class="gal-dur">${clipDur(c.duration)}</span>` : "";
    const kind = clipKind(c.name), chip = kind ? `<span class="gal-chip">${esc(kind)}</span>` : "";   // before: the game name again (it's already the title)
    const when = p.date ? clipRel(p.date.getTime()) : clipRel(c.modified);
    const sel = _galSel.has(c.name) ? " selected" : "";
    const t = thumbMem[c.name];
    const editedBadge = c.edited ? `<span class="gal-edited">EDITED</span>` : "";
    return `<div class="gal-card${sel}" data-name="${esc(c.name)}" title="${esc(c.name)}" tabindex="0" role="button" aria-label="Open ${esc(p.game || c.name)}">
      <span class="gal-tick" aria-hidden="true">✓</span>
      <div class="gal-thumb"><img class="gal-thumb-img" alt="" loading="lazy"${t ? ` src="${esc(t)}"` : ""}><span class="gal-play">▶</span><button class="gal-more" data-name="${esc(c.name)}" title="More actions" aria-label="More actions"><svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><circle cx="5" cy="12" r="1.7"/><circle cx="12" cy="12" r="1.7"/><circle cx="19" cy="12" r="1.7"/></svg></button>${editedBadge}${dur}</div>
      <div class="gal-meta"><div class="gal-name">${esc(p.game || c.name)}</div><div class="gal-sub">${chip}<span class="gal-when">${when}</span></div><div class="gal-tech" data-tech>${infoMem[c.name] ? techHTML(infoMem[c.name]) : ""}</div></div>
    </div>`;
  }
  // ── Share to Discord ──
  const SHARE_TIERS = [{ v: 0, label: "Original" }, { v: 500, label: "500 MB · Nitro" }, { v: 100, label: "100 MB" }, { v: 50, label: "50 MB" }, { v: 25, label: "25 MB" }, { v: 10, label: "10 MB · free" }];
  let _shareName = "", _shareTier = 25, _shareFps = 0, _shareDur = 0, _shareIn = 0, _shareOut = 0, _shareReady = false, _shareRenderT = null;
  function shareFpsTiers(nativeFps) {
    const n = snapFps(nativeFps || 0); const out = [{ v: 0, label: (n || "—") + " (native)" }];
    for (const f of [90, 60, 30]) if (n && f < n) out.push({ v: f, label: String(f) });
    return out;
  }
  function shareOutName() { return ((($("#shareName") || {}).value || "").trim()) || _shareName.replace(/\.mp4$/i, ""); }
  function setShareDrag(state2, txt) {
    const box = $("#shareDragBox"); if (box) { box.classList.remove("busy", "ready", "err"); box.classList.add(state2); }
    const t = $("#shareDragTxt"); if (t && txt) t.textContent = txt;
  }
  function reportShareDragRect() {
    const box = $("#shareDragBox"); if (!box) return;
    const r = box.getBoundingClientRect(); const iw = window.innerWidth || 1, ih = window.innerHeight || 1;
    try { clipsNative.shareDragArm(JSON.stringify({ lf: r.left / iw, tf: r.top / ih, wf: r.width / iw, hf: r.height / ih })); } catch {}
  }
  function updateShareBtn() {
    const b = $("#shareCopy"); if (!b || b.classList.contains("ok")) return;
    b.classList.toggle("busy", !_shareReady);
    b.textContent = _shareReady ? "Copy for Discord" : "Preparing clip…";
  }
  function scheduleShareRender() {
    _shareReady = false; setShareDrag("busy", "Preparing clip…"); setShareStatus(""); updateShareBtn();
    try { clipsNative.shareDragDisarm(); } catch {}
    clearTimeout(_shareRenderT);
    _shareRenderT = setTimeout(() => { try { clipsNative.renderShareForDrag(_shareName, shareOutName(), _shareTier, _shareFps, _shareIn, _shareOut, _shareDur, JSON.stringify(shareMix(_shareName))); } catch {} }, 450);
  }
  // The share honors the editor's track mix exactly like "Create clip" (mute / volume / track trims). Shared from a
  // gallery card without the editor open → the saved mic/chat mute choices, everything else at 100%.
  function shareMix(name) {
    if (name && name === _clipName) return {
      vidVol: _vidVol / 100,
      micOn: micActive(), micIn: Math.max(0, _micTlStart - _micBase), micOut: Math.max(0, _micTlEnd - _micBase), micOffset: _micTlStart, micVol: _micVol / 100,
      chatOn: chatActive(), chatIn: Math.max(0, _chatTlStart - _chatBase), chatOut: Math.max(0, _chatTlEnd - _chatBase), chatOffset: _chatTlStart, chatVol: _chatVol / 100,
    };
    return { micOn: state.settings.clipMicMuted !== true, chatOn: state.settings.clipChatMuted !== true };
  }
  function openSharePopup(name, vidIn, vidOut) {
    if (!name) return;
    { const saved = state.settings.shareTier; _shareTier = SHARE_TIERS.some(t => t.v === saved) ? saved : 10; }   // last pick sticks (Nitro users pick 500 once); first run = free-upload size
    _shareName = name; _shareFps = 0; _shareIn = +vidIn || 0; _shareOut = +vidOut || 0; _shareReady = false;   // default to a size that uploads to Discord free
    const c = (_clips || []).find(x => x.name === name); _shareDur = c ? (c.duration || 0) : (_clipDur || 0);
    { const e = $("#shareName"); if (e) e.value = name.replace(/\.mp4$/i, ""); }
    renderShareSeg("#shareTier", SHARE_TIERS, _shareTier, v => { _shareTier = v; state.settings.shareTier = v; save(); scheduleShareRender(); });
    renderShareSeg("#shareFps", shareFpsTiers(infoMem[name] && infoMem[name].fps), _shareFps, v => { _shareFps = v; scheduleShareRender(); });
    setShareStatus(""); const p = $("#sharePopup"); if (p) { if (p.parentNode !== document.body) document.body.appendChild(p); p.hidden = false; }
    scheduleShareRender();   // no overlay to hide — the in-page <video> sits under the share sheet's z-index naturally
  }
  function closeSharePopup() { const p = $("#sharePopup"); if (p) p.hidden = true; clearTimeout(_shareRenderT); try { clipsNative.shareDragDisarm(); clipsNative.cancelShareRender(); } catch {} }
  function setShareStatus(t, tone) { const s = $("#shareStatus"); if (!s) return; s.hidden = !t; s.textContent = t || ""; s.classList.toggle("note", tone === "note"); }
  function renderShareSeg(sel, items, cur, onPick) {
    const host = $(sel); if (!host) return;
    host.innerHTML = items.map(it => `<button class="share-opt${it.v === cur ? " on" : ""}" data-v="${it.v}">${esc(it.label)}</button>`).join("");
    host.querySelectorAll(".share-opt").forEach(b => b.addEventListener("click", () => {
      host.querySelectorAll(".share-opt").forEach(x => x.classList.remove("on")); b.classList.add("on"); onPick(b.dataset.v | 0);
    }));
  }
  function doShare() {
    if (!_shareReady) { setShareStatus("Preparing the clip… try again in a moment."); return; }
    setShareStatus("Copying…"); try { clipsNative.copyRenderedShare(); } catch {}
  }
  window.onShareRendered = (ok, bytes) => {
    try {
      if (ok) {
        _shareReady = true;
        const mb = bytes ? fmtShareMB(bytes) : "";   // bytes from C# -> MB (fmtMB takes MB, not bytes)
        setShareDrag("ready", "Drag me into Discord" + (mb ? " · " + mb : ""));
        // Red only when it can't be uploaded at all; a size the user picked on purpose (Nitro) is just a grey note.
        if (bytes && bytes > 500 * 1024 * 1024) setShareStatus("≈ " + mb + " — too big for Discord, even with Nitro (500 MB). Pick a smaller size.");
        else if (bytes && bytes > 10 * 1024 * 1024) setShareStatus("Uploads with Discord Nitro · free accounts are limited to 10 MB", "note");
        else setShareStatus("");
        requestAnimationFrame(reportShareDragRect);
      }
      else { _shareReady = false; setShareDrag("err", "Couldn't prepare — try a smaller size"); }
      updateShareBtn();
    } catch {}
  };
  window.onShare = (stage, pct, name) => {
    try {
      if (stage === "render") { if (!_shareReady) setShareDrag("busy", "Preparing clip… " + (pct | 0) + "%"); else setShareStatus("Preparing… " + (pct | 0) + "%"); }
      else if (stage === "copied") { setShareStatus("Copied ✓ — paste into Discord (Ctrl+V)"); showToast("Clip copied", "Paste it into Discord with Ctrl+V."); const b = $("#shareCopy"); if (b) { b.classList.add("ok"); b.textContent = "Copied ✓"; setTimeout(() => { b.classList.remove("ok"); updateShareBtn(); }, 1400); } }
      else if (stage === "notready") setShareStatus("Still preparing — one moment.");
      else if (stage === "error") setShareStatus("Couldn't render — try a smaller size.");
    } catch {}
  };
  // No managed installs in the standalone (no PCSX2/RPCS3/Remote Play) — a no-op keeps the C# callback contract safe.
  // FFmpeg auto-download progress (AV1 encoder / HEVC-no-codec proxy). Throttle the % toasts; always show terminal states.
  let _dlLast = 0;
  window.onManagedInstall = (tool, info) => {
    try {
      const st = info && info.state, pct = info && info.pct, now = performance.now();
      if (st === "down") { if (now - _dlLast > 1500) { _dlLast = now; showToast("Downloading encoder", "FFmpeg " + (pct != null ? Math.round(pct) + "%" : "…")); } }
      else if (st === "extract") showToast("Installing encoder", "Extracting FFmpeg…");
      else if (st === "done") showToast("Encoder ready", "FFmpeg is installed.");
      else if (st === "fail_download" || st === "failed") { showClipPreparing(false); showToast("Encoder download failed", "Couldn't fetch FFmpeg — check your connection, then try again."); }
    } catch {}
  };
  { const b = $("#shareCopy"); if (b) b.addEventListener("click", doShare); }
  { const b = $("#shareClose"); if (b) b.addEventListener("click", closeSharePopup); }
  { const b = $("#shareName"); if (b) b.addEventListener("change", scheduleShareRender); }   // re-encode on commit, not every keystroke
  { const p = $("#sharePopup"); if (p) p.addEventListener("click", e => { if (e.target === p) closeSharePopup(); }); }
  window.addEventListener("resize", () => { const p = $("#sharePopup"); if (p && !p.hidden && _shareReady) reportShareDragRect(); });
  document.addEventListener("keydown", e => { if (e.key === "Escape") { const p = $("#sharePopup"); if (p && !p.hidden) { e.preventDefault(); e.stopImmediatePropagation(); closeSharePopup(); } } }, true);

  function groupClips(items) {
    const t = new Date(); t.setHours(0, 0, 0, 0); const y = new Date(t); y.setDate(y.getDate() - 1);
    const map = new Map(), order = [];
    for (const c of items) {
      const ms = c.date ? c.date.getTime() : c.modified; const d = new Date(ms); d.setHours(0, 0, 0, 0);
      const label = d.getTime() === t.getTime() ? "Today" : d.getTime() === y.getTime() ? "Yesterday"
        : new Date(ms).toLocaleDateString(undefined, { month: "long", year: "numeric" });
      if (!map.has(label)) { map.set(label, []); order.push(label); }
      map.get(label).push(c);
    }
    return order.map(l => ({ label: l, items: map.get(l) }));
  }

  let _vlcWarmed = false;
  function applyGalSearch(host) {
    host = host || $("#galleryHost"); if (!host) return;
    const q = (_clipQuery || "").toLowerCase();
    // Filter real cards only — never the transient "Saving replay…" placeholder (it has no data-name).
    host.querySelectorAll(".gal-card:not(.gal-saving)").forEach(c => {
      c.style.display = (!q || (c.dataset.name || "").toLowerCase().includes(q)) ? "" : "none";
    });
    // Collapse date-group headers whose grid has no visible card, and show a "no results" note when nothing matches.
    let anyVisible = false;
    host.querySelectorAll(".gal-grid").forEach(grid => {
      const has = [...grid.querySelectorAll(".gal-card")].some(c => c.style.display !== "none");
      if (has) anyVisible = true;
      grid.style.display = (q && !has) ? "none" : "";
      const g = grid.previousElementSibling;
      if (g && g.classList.contains("gal-group")) g.style.display = (q && !has) ? "none" : "";
    });
    const gal = host.querySelector(".gallery");
    if (gal) {
      let nr = gal.querySelector(".gal-noresult");
      if (q && !anyVisible) {
        if (!nr) { nr = document.createElement("div"); nr.className = "gal-noresult gal-empty"; gal.appendChild(nr); }
        nr.innerHTML = `<h3>No matches</h3><p>No clips match “<b>${esc(_clipQuery)}</b>”.</p>`; nr.style.display = "";
      } else if (nr) nr.style.display = "none";
    }
  }
  function renderGallery(host) {
    if (!host) return;
    if (_renaming) return;   // don't blow away an open inline-rename input on a background onClips/onCapture re-render
    const prevScroll = host.scrollTop || 0;   // preserve scroll across background re-renders
    // Preserve search focus + caret across the innerHTML rebuild (a background re-render otherwise blurs it mid-typing).
    const _ae = document.activeElement;
    const _searchFocus = (_ae && _ae.id === "galSearch") ? { s: _ae.selectionStart, e: _ae.selectionEnd } : null;
    const items = _clips.map(c => Object.assign({}, c, parseClip(c.name)));
    const games = [...new Set(items.map(c => c.game).filter(Boolean))].sort();
    const shown = _clipFilter ? items.filter(c => c.game === _clipFilter) : items;
    _galSel.forEach(nm => { if (!_clips.some(c => c.name === nm)) _galSel.delete(nm); });
    const n = _galSel.size;
    const scale = (state.settings.galCardScale | 0) || 100;
    const capGB = (state.settings.captureCapGB | 0) || 0;
    const usedGB = _clipsTotal / 1073741824;
    const pct = capGB > 0 ? Math.min(100, Math.round(usedGB / capGB * 100)) : 0;
    const actions = _galSelecting
      ? `<button class="gal-btn danger" id="galDelBtn" ${n ? "" : "disabled"}>Delete${n ? " (" + n + ")" : ""}</button>
         <button class="gal-btn" id="galRenBtn" ${n === 1 ? "" : "disabled"}>Rename</button>
         <button class="gal-btn" id="galCancelBtn">Cancel</button>`
      : `<button class="gal-btn" id="galSelectBtn" title="Select multiple clips to delete or rename">Select</button>`;
    const storage = capGB > 0
      ? `<div class="gal-storage" title="Clip storage — ${usedGB.toFixed(1)} of ${capGB} GB used">${DISK_ICO}<div class="gal-meter${pct >= 85 ? " full" : ""}"><span style="width:${pct}%"></span></div><span class="gal-meter-txt">${usedGB.toFixed(1)} / ${capGB} GB</span></div>`
      : `<div class="gal-storage" title="Space used by your clips">${DISK_ICO}<span class="gal-meter-txt">${clipBytes(_clipsTotal)} used</span></div>`;
    const header = `<div class="gal-head">
      <div class="gal-search">
        <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
        <input id="galSearch" type="text" placeholder="Search clips…" autocomplete="off" value="${esc(_clipQuery || "")}">
        <select id="galFilter" class="gal-filter" aria-label="Filter by game"><option value="">All games</option>${games.map(g => `<option value="${esc(g)}" ${g === _clipFilter ? "selected" : ""}>${esc(g)}</option>`).join("")}</select>
      </div>
      <div class="gal-actions">${actions}</div>
      <div class="gal-tools">
        <div class="seg gal-size" role="group" aria-label="Thumbnail size">
          <button class="seg-btn${scale <= 75 ? " on" : ""}" data-size="75" title="Small thumbnails">S</button>
          <button class="seg-btn${scale > 75 && scale < 150 ? " on" : ""}" data-size="100" title="Medium thumbnails">M</button>
          <button class="seg-btn${scale >= 150 ? " on" : ""}" data-size="150" title="Large thumbnails">L</button>
        </div>
        <button class="gal-btn ico" id="galOpenBtn" title="Open clips folder"><svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"></path></svg></button>
      </div>
      ${storage}
    </div>`;
    const saving = _replaySaving
      ? `<div class="gal-grid"><div class="gal-card gal-saving" aria-busy="true"><div class="gal-thumb"><span class="gal-spinner"></span></div><div class="gal-meta"><div class="gal-name">Saving replay…</div><div class="gal-sub"><span class="gal-when">just now</span></div></div></div></div>`
      : "";
    const body = shown.length
      ? groupClips(shown).map(g => `<div class="gal-group">${esc(g.label)}</div><div class="gal-grid">${g.items.map(galCardHTML).join("")}</div>`).join("")
      : ((_replaySaving || !_clipsLoaded) ? "" : `<div class="gal-empty"><div class="gal-empty-omega">Ω</div><h3>No clips yet</h3><p>Press <b>Save replay</b> in the title bar to grab the last moments — or turn Instant replay off in Settings for a manual <b>Record</b> button. New clips appear here automatically.</p><button class="gal-btn primary" id="galEmptySettings">Open capture settings</button></div>`);
    host.innerHTML = `<div class="gallery${_galSelecting ? " selecting" : ""}" style="--gal-scale:${(((state.settings.galCardScale | 0) || 100) / 50)}">${header}${saving}${body}</div>`;
    host.scrollTop = prevScroll;
    updateClipRecUI();
    closeCardMenu();
    // Search persists across background re-renders via _clipQuery (fast display filter, no focus loss / no full re-render).
    { const s = $("#galSearch"); if (s) { s.addEventListener("input", () => { _clipQuery = s.value; applyGalSearch(host); }); if (_searchFocus) { s.focus(); try { s.setSelectionRange(_searchFocus.s, _searchFocus.e); } catch {} } } }
    { const f = $("#galFilter"); if (f) f.addEventListener("change", () => { _clipFilter = f.value; renderLibrary(); }); }
    host.querySelectorAll(".gal-size .seg-btn").forEach(b => b.addEventListener("click", () => {
      const v = b.dataset.size | 0; state.settings.galCardScale = v; save();
      const g = host.querySelector(".gallery"); if (g) g.style.setProperty("--gal-scale", v / 50);
      host.querySelectorAll(".gal-size .seg-btn").forEach(x => x.classList.toggle("on", x === b));
    }));
    { const b = $("#galOpenBtn"); if (b) b.addEventListener("click", () => clipsNative.openClipsFolder()); }
    { const b = $("#galSelectBtn"); if (b) b.addEventListener("click", enterGalSelect); }
    { const b = $("#galDelBtn"); if (b) b.addEventListener("click", onGalDelete); }
    { const b = $("#galRenBtn"); if (b) b.addEventListener("click", onGalRename); }
    { const b = $("#galCancelBtn"); if (b) b.addEventListener("click", exitGalSelect); }
    { const b = $("#galEmptySettings"); if (b) b.addEventListener("click", () => showView("settings")); }
    // ONE delegated listener (attached once), not N per render — cards are rebuilt via innerHTML on every re-render.
    if (!host._cardWired) {
      host._cardWired = true;
      host.addEventListener("click", e => {
        const more = e.target.closest(".gal-more");
        if (more) { e.stopPropagation(); openCardMenu(more, more.dataset.name); return; }
        const card = e.target.closest(".gal-card"); if (!card || !card.dataset.name || card.classList.contains("gal-saving")) return;
        if (_galSelecting) toggleGalSel(card.dataset.name); else openClipPage(card.dataset.name);
      });
      host.addEventListener("keydown", e => {
        if (e.key !== "Enter" && e.key !== " ") return;
        const card = e.target.closest && e.target.closest(".gal-card"); if (!card || !card.dataset.name || card.classList.contains("gal-saving")) return;
        e.preventDefault();
        if (_galSelecting) toggleGalSel(card.dataset.name); else openClipPage(card.dataset.name);
      });
    }
    host.querySelectorAll(".gal-card").forEach(card => {
      const nm = card.dataset.name; if (!nm) return;
      if (!thumbMem[nm] && !_thumbReq.has(nm)) { _thumbReq.add(nm); clipsNative.ensureThumb(nm); }
      if (!infoMem[nm] && !_infoReq.has(nm)) { _infoReq.add(nm); clipsNative.ensureClipInfo(nm); }
    });
    applyGalSearch(host);
    paintRecElapsed();
  }
  // Drop a deleted clip's persisted bookmarks/segments — without this every gallery-side delete leaked its entries
  // into state forever (and every save() re-serializes the whole blob).
  function dropClipState(name) { if (state.clipBookmarks) delete state.clipBookmarks[name]; if (state.clipSegments) delete state.clipSegments[name]; }
  // Carry a clip's persisted bookmarks/segments to its new name on rename. The editor's rename did this; the gallery
  // renames didn't — imported in-game marks (whose sidecar C# already deleted) and saved segments silently vanished.
  function migrateClipState(oldName, newName) {
    if (state.clipBookmarks && state.clipBookmarks[oldName]) { state.clipBookmarks[newName] = state.clipBookmarks[oldName]; delete state.clipBookmarks[oldName]; }
    if (state.clipSegments && state.clipSegments[oldName]) { state.clipSegments[newName] = state.clipSegments[oldName]; delete state.clipSegments[oldName]; }
    save();
  }
  function enterGalSelect() { _galSelecting = true; renderLibrary(); }
  function exitGalSelect() { _galSelecting = false; _galSel.clear(); renderLibrary(); }
  function toggleGalSel(name) {
    if (_galSel.has(name)) _galSel.delete(name); else _galSel.add(name);
    const host = $("#galleryHost"); if (host) host.querySelectorAll(".gal-card").forEach(c => { if (c.dataset.name === name) c.classList.toggle("selected", _galSel.has(name)); });
    const n = _galSel.size;
    const del = $("#galDelBtn"); if (del) { del.textContent = "Delete" + (n ? " (" + n + ")" : ""); del.disabled = n === 0; }
    const ren = $("#galRenBtn"); if (ren) ren.disabled = n !== 1;
  }
  function onGalDelete() {
    if (!_galSelecting) { enterGalSelect(); showToast("Delete clips", "Tick the clips to remove, then click Delete."); return; }
    const names = [..._galSel];
    if (!names.length) { exitGalSelect(); return; }
    names.forEach(nm => { clipsNative.deleteClip(nm); dropClipState(nm); });
    save();
    showToast("Deleted", names.length + " clip" + (names.length === 1 ? "" : "s"));
    _galSelecting = false; _galSel.clear();
    setTimeout(() => clipsNative.requestClips(), 300);
  }
  function onGalRename() {
    if (!_galSelecting) { enterGalSelect(); showToast("Rename", "Tick one clip, then click Rename."); return; }
    if (_galSel.size !== 1) { showToast("Rename", "Tick exactly one clip to rename."); return; }
    const name = [..._galSel][0];
    let card = null; ($("#galleryHost") || document).querySelectorAll(".gal-card").forEach(c => { if (c.dataset.name === name) card = c; });
    const lbl = card && card.querySelector(".gal-name"); if (!lbl) return;
    _renaming = true;
    const stem = name.replace(/\.[^.]+$/, "");
    lbl.innerHTML = `<input class="gal-rename-input" type="text" value="${esc(stem)}" maxlength="80">`;
    const inp = lbl.querySelector("input"); inp.focus(); inp.select();
    inp.addEventListener("click", e => e.stopPropagation());
    let fin = false;   // Enter commits then blur fires → without this the rename ran twice (and Esc-cancel could still commit on blur)
    const done = (commit) => {
      if (fin) return; fin = true; _renaming = false;
      const nn = (inp.value || "").trim();
      if (commit && nn && nn !== stem) { migrateClipState(name, nn + ".mp4"); clipsNative.renameClip(name, nn + ".mp4"); showToast("Renamed", nn); }
      _galSelecting = false; _galSel.clear();
      setTimeout(() => clipsNative.requestClips(), 250);
    };
    inp.addEventListener("keydown", e => { e.stopPropagation(); if (e.key === "Enter") { e.preventDefault(); done(true); } else if (e.key === "Escape") { e.preventDefault(); done(false); } });
    inp.addEventListener("blur", () => done(true));
  }

  // ── Per-card overflow menu (Share / Rename / Delete / Open) — one shared floating menu, positioned at the clicked ⋯. ──
  let _cardMenu = null;
  function _cardMenuOutside(e) { if (_cardMenu && !_cardMenu.contains(e.target) && !e.target.closest(".gal-more")) closeCardMenu(); }
  function closeCardMenu() {
    if (!_cardMenu) return;
    try { _cardMenu.remove(); } catch {}
    _cardMenu = null;
    document.removeEventListener("pointerdown", _cardMenuOutside, true);
    window.removeEventListener("resize", closeCardMenu);
  }
  function openCardMenu(btn, name) {
    closeCardMenu();
    const menu = document.createElement("div");
    menu.className = "gal-menu";
    menu.innerHTML = `<button data-act="share">Share to Discord</button><button data-act="rename">Rename</button><button data-act="open">Show in folder</button><button data-act="delete" class="danger">Delete</button>`;
    document.body.appendChild(menu);
    const r = btn.getBoundingClientRect();
    const left = Math.max(8, Math.min(window.innerWidth - menu.offsetWidth - 8, r.right - menu.offsetWidth));
    let top = r.bottom + 6;
    if (top + menu.offsetHeight > window.innerHeight - 8) top = Math.max(8, r.top - menu.offsetHeight - 6);
    menu.style.left = left + "px"; menu.style.top = top + "px";
    let armDelete = false;
    menu.addEventListener("click", (e) => {
      const b = e.target.closest("button"); if (!b) return;
      const act = b.dataset.act;
      if (act === "delete" && !armDelete) { armDelete = true; b.textContent = "Click again to delete"; return; }   // two-click confirm
      closeCardMenu();
      if (act === "share") openSharePopup(name);
      else if (act === "rename") renameCardInline(name);
      else if (act === "open") clipsNative.revealClip(name);
      else if (act === "delete") { clipsNative.deleteClip(name); dropClipState(name); save(); showToast("Deleted", parseClip(name).game || name); setTimeout(() => clipsNative.requestClips(), 300); }
    });
    _cardMenu = menu;
    setTimeout(() => { document.addEventListener("pointerdown", _cardMenuOutside, true); window.addEventListener("resize", closeCardMenu); }, 0);
  }
  function renameCardInline(name) {
    const host = $("#galleryHost"); let card = null;
    (host || document).querySelectorAll(".gal-card").forEach(c => { if (c.dataset.name === name) card = c; });
    const lbl = card && card.querySelector(".gal-name"); if (!lbl) return;
    _renaming = true;   // suppress background re-renders that would destroy the input + the typed value
    const stem = name.replace(/\.[^.]+$/, "");
    lbl.innerHTML = `<input class="gal-rename-input" type="text" value="${esc(stem)}" maxlength="80">`;
    const inp = lbl.querySelector("input"); inp.focus(); inp.select();
    inp.addEventListener("click", e => e.stopPropagation());
    let fin = false;
    const done = (commit) => {
      if (fin) return; fin = true; _renaming = false;
      const nn = (inp.value || "").trim();
      if (commit && nn && nn !== stem) { migrateClipState(name, nn + ".mp4"); clipsNative.renameClip(name, nn + ".mp4"); showToast("Renamed", nn); setTimeout(() => clipsNative.requestClips(), 250); }
      else renderLibrary();
    };
    inp.addEventListener("keydown", e => { e.stopPropagation(); if (e.key === "Enter") { e.preventDefault(); done(true); } else if (e.key === "Escape") { e.preventDefault(); done(false); } });
    inp.addEventListener("blur", () => done(true));
  }
  // ══════════════════════════════════════════════════════════════════════════════════════════════════
  //  LIFTED — Clip player + trim editor (playback clock via onClipTime, mic/chat as Web-Audio buffer sources)
  // ══════════════════════════════════════════════════════════════════════════════════════════════════
  let _clipName = "", _clipDur = 0, _clipIn = 0, _clipOut = 0;
  let _hasMic = false, _micBase = 0, _micTlStart = 0, _micTlEnd = 0, _vidVol = 100, _micVol = 100, _micManual = false, _micMuted = false, _micReadable = true;
  let _hasChat = false, _chatBase = 0, _chatTlStart = 0, _chatTlEnd = 0, _chatVol = 100, _chatManual = false, _chatMuted = false, _chatReadable = true;
  let _micSrcT0 = 0, _micSrcOff = 0, _chatSrcT0 = 0, _chatSrcOff = 0, _lastDrift = 0;   // for periodic A/V drift correction
  let _micWave = null, _chatWave = null, _waveCtx = null;
  let _micBuf = null, _chatBuf = null, _micGainNode = null, _chatGainNode = null;
  let _micSrc = null, _chatSrc = null;
  let _t = 0, _playing = false, _seekDrag = false, _waveRaf = 0;
  let _clipKfs = [];
  let _clipAR = 0;
  let _video = null, _clockRaf = 0, _videoWired = false, _seeking = false, _seekPending = -1, _clipSrcMode = "direct", _atEnd = false;   // in-page HTML5 player
  // Lazily grab the <video> and wire its events ONCE — the element drives the whole clock now (play/pause/seeked/ended).
  function ensureVideo() {
    if (_video) return _video;
    _video = $("#clipVideo"); if (!_video) return null;
    if (!_videoWired) {
      _videoWired = true;
      _video.addEventListener("loadedmetadata", () => {
        if (_video.videoWidth > 0) _clipAR = _video.videoWidth / _video.videoHeight;
        const d = _video.duration;
        if (isFinite(d) && d > 0 && Math.abs((_clipDur || 0) - d) > 0.5) {
          const wasFull = _clipOut <= 0 || _clipOut >= (_clipDur || 0) - 0.05;   // still the untrimmed default?
          _clipDur = d; if (wasFull || _clipOut > d) _clipOut = d;   // follow the corrected real duration (extend, not just clamp)
          if (_micTlEnd <= 0) _micTlEnd = d; if (_chatTlEnd <= 0) _chatTlEnd = d;
          const e = $("#clipTimeDur"); if (e) e.textContent = clipDur(d); renderClipTl();
        }
      });
      _video.addEventListener("play",  () => { _playing = true;  try { _waveCtx && _waveCtx.resume(); } catch {} syncMic(); syncChat(); startClipClock(); updatePlayBtn(); });
      _video.addEventListener("pause", () => { _playing = false; stopMicSrc(); stopChatSrc(); stopClipClock(); updatePlayBtn(); });
      _video.addEventListener("ended", () => { _playing = false; _atEnd = true; stopMicSrc(); stopChatSrc(); stopClipClock(); updatePlayBtn(); });
      _video.addEventListener("seeked", () => {
        _seeking = false;
        if (_seekPending >= 0) { const p = _seekPending; _seekPending = -1; scrubSeek(p); return; }   // fulfil the latest coalesced target
        _t = _video.currentTime || 0; updatePlayheads(); if (_playing) { syncMic(); syncChat(); }
      });
      _video.addEventListener("click", () => { const willPlay = _video.paused; togglePlay(); clipFlash(willPlay); });
      _video.addEventListener("error", () => {
        const pg = $("#clipPage"); if (!pg || pg.hidden || !_video.currentSrc) return;   // ignore teardown / no-src errors
        showClipPreparing(false);
        const info = infoMem[_clipName];
        if (info && info.codec === "hevc" && _hevcNative === false && _clipSrcMode !== "proxy") { setClipSource(_clipName); return; }   // HEVC direct failed → convert
        showToast("Couldn't play this clip", "The file may be corrupt or use a codec your system can't decode.");
      });
    }
    return _video;
  }
  // Smooth playhead while playing (video 'timeupdate' is too coarse) + stop-at-trim-out; stopped on pause/ended.
  function startClipClock() {
    if (_clockRaf) return;
    const tick = () => {
      _clockRaf = 0; if (!_video) return;
      _t = _video.currentTime || 0;
      if (_playing && clipSelActive() && _t >= _clipOut - 0.02) { _video.pause(); _atEnd = true; try { _video.currentTime = _clipOut; } catch {} updatePlayheads(); updatePlayBtn(); return; }   // sit exactly at out (no keyframe-snap away)
      updatePlayheads(); updatePlayBtn();
      // syncMic/syncChat are idempotent (start only when inside the lane, stop when outside) — without this the lanes'
      // trim bounds were ignored during continuous playback: a mic block starting at 5s never began when the playhead
      // reached it, and a started source played past its out-point. driftCorrect then re-aligns running sources.
      if (_playing && performance.now() - _lastDrift > 500) { _lastDrift = performance.now(); syncMic(); syncChat(); driftCorrect(); }
      if (_playing) _clockRaf = requestAnimationFrame(tick);
    };
    _clockRaf = requestAnimationFrame(tick);
  }
  function stopClipClock() { if (_clockRaf) { cancelAnimationFrame(_clockRaf); _clockRaf = 0; } }
  function clipFlash(playing) { const f = $("#clipFlash"); if (!f) return; f.textContent = playing ? "▶" : "⏸"; f.classList.remove("show"); void f.offsetWidth; f.classList.add("show"); }
  // Mic/chat buffer sources run on the AudioContext clock, not the <video> clock — restart any that drifted >80ms so
  // preview alignment stays honest on long clips (export is already exact via ffmpeg adelay).
  function driftCorrect() {
    if (!_waveCtx) return;
    if (_micSrc && _hasMic) { const played = (_waveCtx.currentTime - _micSrcT0) + _micSrcOff, want = _t - _micBase; if (Math.abs(played - want) > 0.08) startMicSrc(want); }
    if (_chatSrc && _hasChat) { const played = (_waveCtx.currentTime - _chatSrcT0) + _chatSrcOff, want = _t - _chatBase; if (Math.abs(played - want) > 0.08) startChatSrc(want); }
  }
  // Reminder chip for high-res HEVC clips without an OS HEVC codec (previewed via a one-time H.264 conversion). Shown once the codec is known.
  function updateHevcWarn() {
    const el = $("#clipHevcWarn"); if (!el) return;
    const info = infoMem[_clipName];
    const slow = !!(info && info.codec === "hevc" && info.height > 1080 && _hevcNative === false);   // only when we fall back to the H.264 conversion
    el.hidden = !slow;
    if (slow) {
      el.textContent = "⚠ HEVC " + info.height + "p · converting for preview";
      el.title = "This clip is HEVC at " + info.height + "p. Since your system has no hardware HEVC codec, the editor previews it via a one-time H.264 conversion. Recording and export are unaffected.";
    }
  }
  function showClipPreparing(on) { const el = $("#clipPrep"); if (el) el.hidden = !on; }
  // Pick the <video> source: HEVC with NO native OS codec → an ffmpeg H.264 proxy (set on window.onClipProxy); everything
  // else (H.264, AV1, or HEVC when the OS codec is installed) plays directly from clips.local in-page.
  function setClipSource(name) {
    const v = ensureVideo(); if (!v) return;
    const info = infoMem[name];
    if (info && info.codec === "hevc" && _hevcNative === false) {
      _clipSrcMode = "proxy"; showClipPreparing(true);
      // Stop the PREVIOUS clip immediately — before: only the direct branch replaced v.src, so the prior clip kept
      // playing (audibly) under the "Preparing preview…" overlay until the proxy landed — or forever if it failed.
      try { v.pause(); v.removeAttribute("src"); v.load(); } catch {}
      clipsNative.ensureHevcProxy(name);
    } else {
      _clipSrcMode = "direct"; showClipPreparing(false);
      try { v.src = clipUrl(name); v.load(); } catch {}
      try { v.currentTime = 0; } catch {}
      v.volume = Math.max(0, Math.min(1, _vidVol / 100));
      v.play().catch(() => {});   // auto-play on open (the card click is a user gesture, so this is allowed)
    }
  }
  window.onClipProxy = (name, url) => {
    const pg = $("#clipPage"); if (!pg || pg.hidden) return;   // editor closed while ffmpeg was converting — a late proxy must not start hidden audio over the gallery
    if (name !== _clipName) return;
    const v = ensureVideo(); if (!v) return;
    showClipPreparing(false);
    if (url) { try { v.src = url; v.load(); } catch {} try { v.currentTime = 0; } catch {} v.volume = Math.max(0, Math.min(1, _vidVol / 100)); v.play().catch(() => {}); }
    else showToast("HEVC preview", "Couldn't prepare an H.264 preview for this clip.");
  };
  function snapKf(t) {
    const a = _clipKfs; if (!a || a.length === 0) return t;
    if (t <= a[0]) return a[0];
    if (t >= a[a.length - 1]) return a[a.length - 1];
    let lo = 0, hi = a.length - 1;
    while (lo < hi) { const m = (lo + hi) >> 1; if (a[m] < t) lo = m + 1; else hi = m; }
    const hiV = a[lo], loV = a[lo - 1];
    return (t - loV) <= (hiV - t) ? loV : hiV;
  }
  function micUrl(name) { return clipUrl(name.replace(/\.mp4$/i, "") + ".mic.wav"); }
  function chatUrl(name) { return clipUrl(name.replace(/\.mp4$/i, "") + ".chat.wav"); }
  function clipSelActive() { const dur = _clipDur || 0; return dur > 0 && (_clipIn > 0.05 || _clipOut < dur - 0.05); }
  function ensureClipGains() {
    if (!_waveCtx) _waveCtx = new (window.AudioContext || window.webkitAudioContext)();
    if (!_micGainNode) { _micGainNode = _waveCtx.createGain(); _micGainNode.connect(_waveCtx.destination); }
    if (!_chatGainNode) { _chatGainNode = _waveCtx.createGain(); _chatGainNode.connect(_waveCtx.destination); }
  }
  function applyClipVols() {
    if (_video) _video.volume = Math.max(0, Math.min(1, _vidVol / 100));   // <video>.volume is 0..1 (>100% boost needs Web-Audio + CORS — TODO)
    ensureClipGains();
    _micGainNode.gain.value = _micMuted ? 0 : Math.max(0, _micVol / 100);
    _chatGainNode.gain.value = _chatMuted ? 0 : Math.max(0, _chatVol / 100);
  }
  function startMicSrc(offsetSec) {
    if (!_micBuf || !_hasMic) return;
    ensureClipGains(); stopMicSrc(); try { _waveCtx.resume(); } catch {}
    const off = Math.max(0, Math.min(offsetSec, _micBuf.duration - 0.01));
    const s = _waveCtx.createBufferSource(); s.buffer = _micBuf; s.connect(_micGainNode);
    s.onended = () => { if (_micSrc === s) _micSrc = null; };
    try { s.start(0, off); } catch { return; }
    _micSrc = s; _micSrcT0 = _waveCtx.currentTime; _micSrcOff = off;
  }
  function stopMicSrc() { const s = _micSrc; _micSrc = null; if (s) { try { s.onended = null; s.stop(); } catch {} try { s.disconnect(); } catch {} } }
  function startChatSrc(offsetSec) {
    if (!_chatBuf || !_hasChat) return;
    ensureClipGains(); stopChatSrc(); try { _waveCtx.resume(); } catch {}
    const off = Math.max(0, Math.min(offsetSec, _chatBuf.duration - 0.01));
    const s = _waveCtx.createBufferSource(); s.buffer = _chatBuf; s.connect(_chatGainNode);
    s.onended = () => { if (_chatSrc === s) _chatSrc = null; };
    try { s.start(0, off); } catch { return; }
    _chatSrc = s; _chatSrcT0 = _waveCtx.currentTime; _chatSrcOff = off;
  }
  function stopChatSrc() { const s = _chatSrc; _chatSrc = null; if (s) { try { s.onended = null; s.stop(); } catch {} try { s.disconnect(); } catch {} } }
  function scheduleWaveRedraw() { if (_waveRaf) return; _waveRaf = requestAnimationFrame(() => { _waveRaf = 0; redrawClipWaves(); }); }
  // The in-page <video> sizes itself via CSS (no native overlay to place), so "tracking" now only redraws waveforms on resize.
  function startClipRectTracking() { window.addEventListener("resize", scheduleWaveRedraw); }
  function stopClipRectTracking() { window.removeEventListener("resize", scheduleWaveRedraw); if (_waveRaf) { cancelAnimationFrame(_waveRaf); _waveRaf = 0; } }
  function updatePlayheads() {
    const dur = _clipDur || 0; if (dur <= 0) return;
    const lp = Math.max(0, Math.min(100, (_t / dur) * 100)) + "%";
    const ph = $("#clipPlayhead"), mph = $("#clipMicPlayhead"), cph = $("#clipChatPlayhead");
    if (ph) ph.style.left = lp; if (mph) mph.style.left = lp; if (cph) cph.style.left = lp;
  }
  let _lastGlyph = "", _lastTimeStr = "", _lastSk = -1;
  function updatePlayBtn() {   // rAF-cadence; only touch the DOM when a value actually changed (time is 1 Hz, glyph rare)
    const glyph = _playing ? "⏸" : "▶";
    if (glyph !== _lastGlyph) { const b = $("#clipPlayPause"); if (b) b.textContent = glyph; _lastGlyph = glyph; }
    const str = clipDur(_t); if (str !== _lastTimeStr) { const c = $("#clipTimeCur"); if (c) c.textContent = str; _lastTimeStr = str; }
    if (!_seekDrag && _clipDur > 0) { const v = Math.round((_t / _clipDur) * 1000); if (v !== _lastSk) { const sk = $("#clipSeek"); if (sk) { sk.value = v; sk.style.setProperty("--vol", rangeFillPct(sk)); } _lastSk = v; } }
  }
  function pauseClipAudio() { stopMicSrc(); stopChatSrc(); }
  // Smooth scrubbing: keep only ONE seek in flight (store the latest target, fire it when the current 'seeked' lands) and
  // use fastSeek() (nearest-frame, no full re-decode) — setting currentTime on every drag event storms the decoder → stutter.
  function scrubSeek(sec) {
    if (!_video) return;
    _atEnd = false;                                       // scrubbing away from the end must not make Play restart at trim-in
    _t = Math.max(0, sec || 0);
    if (_seeking) { _seekPending = _t; return; }
    _seeking = true;
    try { if (_video.fastSeek) _video.fastSeek(_t); else _video.currentTime = _t; }
    catch { _seeking = false; try { _video.currentTime = _t; } catch {} }
  }
  function seekClip(sec) {
    _atEnd = false;                                       // any deliberate seek clears the end-of-clip state
    _seekPending = -1;                                    // this seek is DEFINITIVE — a stale coalesced scrub target must not replay after it lands
    _t = snapKf(Math.max(0, sec || 0));
    stopMicSrc(); stopChatSrc();                          // 'seeked' resumes them if still playing
    if (_video) { try { _video.currentTime = _t; } catch {} }
    updatePlayheads(); updatePlayBtn();
  }
  function togglePlay() {
    const v = ensureVideo(); if (!v) return;
    if (v.paused) {
      // Restart from the start / trim-in when we're at the end — tracked by a flag (currentTime is unreliable: the
      // trim-stop / keyframe-snap can leave it ~0.5s short of the real end, so a value comparison misses it).
      if (_atEnd || v.ended) seekClip(clipSelActive() ? _clipIn : 0);
      v.play().catch(() => {});
    } else v.pause();
  }
  function prevKf(t) { const a = _clipKfs; if (!a || !a.length) return Math.max(0, t - 0.1); let p = 0; for (const k of a) { if (k < t - 0.02) p = k; else break; } return p; }
  function nextKf(t) { const a = _clipKfs, d = _clipDur || t; if (!a || !a.length) return Math.min(d, t + 0.1); for (const k of a) { if (k > t + 0.02) return k; } return a[a.length - 1]; }
  function stepClip(n) {
    const dur = _clipDur || 0; if (dur <= 0) return;
    const t = Math.abs(n) >= 5 ? Math.max(0, Math.min(dur, _t + n)) : (n < 0 ? prevKf(_t) : nextKf(_t));
    seekClip(t);
  }
  // Keep the mic/chat lanes tracking the selection unless the user placed them manually — the SAME rule the handle
  // drags apply. Without this, I/O keys and segment jumps produced a different exported audio mix than a visually
  // identical drag-made selection (the lanes silently kept spanning 0..duration).
  function followLanes() {
    if (_hasMic && !_micManual) { _micTlStart = _clipIn; _micTlEnd = _clipOut; }
    if (_hasChat && !_chatManual) { _chatTlStart = _clipIn; _chatTlEnd = _clipOut; }
  }
  function setInPoint()  { if (_t < _clipOut - 0.1) { _clipIn = Math.max(0, _t); followLanes(); renderClipTl(); showToast("In point set", clipDur(_clipIn)); } }
  function setOutPoint() { if (_t > _clipIn + 0.1) { _clipOut = Math.min(_clipDur || _t, _t); followLanes(); renderClipTl(); showToast("Out point set", clipDur(_clipOut)); } }
  function clipEditorKey(e) {
    const pg = $("#clipPage"); if (!pg || pg.hidden) return;
    { const sp = $("#sharePopup"); if (sp && !sp.hidden) return; }   // the share sheet owns the keys while open — Space was toggling the video underneath it
    if (e.ctrlKey || e.metaKey || e.altKey) return;   // ignore modified combos — bare keys only (so Ctrl+K opens the palette)
    const ae = document.activeElement, tag = ae && ae.tagName;
    const isRange = ae && tag === "INPUT" && (ae.type || "").toLowerCase() === "range";
    if (ae && /^(INPUT|TEXTAREA|SELECT)$/.test(tag) && !isRange) return;
    if (tag === "BUTTON" && e.key === " ") return;   // let Space activate the focused button instead of play/pause
    let ok = true;
    switch (e.key) {
      case " ": case "k": case "K": togglePlay(); break;
      case "ArrowLeft":  if (isRange) ok = false; else stepClip(e.shiftKey ? -5 : -1); break;
      case "ArrowRight": if (isRange) ok = false; else stepClip(e.shiftKey ? 5 : 1); break;
      case ",": stepClip(-1); break;
      case ".": stepClip(1); break;
      case "i": case "I": setInPoint(); break;
      case "o": case "O": setOutPoint(); break;
      case "m": case "M": addBookmark(); break;
      default: ok = false;
    }
    if (ok) { e.preventDefault(); e.stopPropagation(); }
  }

  // ── Bookmarks ──
  function bmList() { const o = state.clipBookmarks || (state.clipBookmarks = {}); return o[_clipName] || (o[_clipName] = []); }
  function jumpBookmark(t) { seekClip(t); const v = ensureVideo(); if (v) v.play().catch(() => {}); }
  function addBookmark() {
    if (!_clipName) return;
    const list = bmList();
    const bm = { t: snapKf(_t || 0), label: "" };
    list.push(bm); list.sort((a, b) => a.t - b.t);
    save(); renderBookmarks();
    renameBookmark(list.indexOf(bm), true);
  }
  function deleteBookmark(i) { const list = bmList(); if (i < 0 || i >= list.length) return; list.splice(i, 1); save(); renderBookmarks(); }
  // Import any mid-record marks (mark-a-moment hotkey) into this clip's bookmarks — once (C# deletes the sidecar on
  // read). Callback style: the sidecar read now runs off-thread in C# (SMB-safe) and answers via onClipMarks.
  function importClipMarks(name) { clipsNative.requestClipMarks(name); }
  window.onClipMarks = (name, arr) => {
    // ALWAYS persist — C# deleted the sidecar when it answered, so dropping the payload because the user already
    // switched clips destroyed the in-game marks permanently. Only the UI refresh is gated on "still current".
    if (!Array.isArray(arr) || !arr.length) return;
    const list = (state.clipBookmarks || (state.clipBookmarks = {}))[name] || (state.clipBookmarks[name] = []);
    let added = 0;
    arr.forEach(t => {
      const sec = Math.max(0, +t || 0);
      if (list.some(b => Math.abs(b.t - sec) < 0.25)) return;   // skip a mark that already has a bookmark
      list.push({ t: _clipName === name ? snapKf(sec) : sec, label: "Marked" }); added++;   // only snap against THIS clip's keyframes
    });
    if (!added) return;
    list.sort((a, b) => a.t - b.t); save();
    if (_clipName === name) { renderBookmarks(); showToast("Marks imported", added + " in-game mark" + (added > 1 ? "s" : "") + " added to this clip's bookmarks."); }
  };
  function renameBookmark(i, isNew) {
    const list = bmList(), bm = list[i]; if (!bm) return;
    const wrap = $("#clipMarks"); if (!wrap) return;
    const chip = wrap.querySelector('.clip-mark-chip[data-i="' + i + '"]'), labelEl = chip && chip.querySelector(".cmk-label");
    if (!labelEl) return;
    const inp = document.createElement("input");
    inp.className = "cmk-input"; inp.value = bm.label || ""; inp.placeholder = "Label…"; inp.maxLength = 40;
    labelEl.replaceWith(inp); inp.focus(); inp.select();
    let closed = false;
    const done = (ok) => {
      if (closed) return; closed = true;
      if (ok) { bm.label = inp.value.trim() || (isNew ? "Bookmark" : bm.label || "Bookmark"); save(); }
      else if (isNew && !bm.label) { const j = bmList().indexOf(bm); if (j >= 0) bmList().splice(j, 1); save(); }
      renderBookmarks();
    };
    inp.addEventListener("keydown", e => { e.stopPropagation(); if (e.key === "Enter") { e.preventDefault(); done(true); } else if (e.key === "Escape") { e.preventDefault(); done(false); } });
    inp.addEventListener("blur", () => done(true));
  }
  function renderBookmarks() {
    const tl = $("#clipTl"), wrap = $("#clipMarks"); if (!tl) return;
    tl.querySelectorAll(".clip-mark").forEach(m => m.remove());
    const dur = _clipDur || 0, list = (state.clipBookmarks && state.clipBookmarks[_clipName]) || [];
    list.forEach(bm => {
      const m = document.createElement("div");
      m.className = "clip-mark" + (bm.auto ? " auto" : ""); m.style.left = (dur > 0 ? Math.max(0, Math.min(100, bm.t / dur * 100)) : 0) + "%";
      m.title = (bm.auto ? "Auto-highlight: " : "") + (bm.label || "Bookmark") + " · " + clipDur(bm.t);
      m.addEventListener("pointerdown", e => {
        e.stopPropagation(); e.preventDefault();
        try { m.setPointerCapture(e.pointerId); } catch {}
        beginTlDrag();
        const startX = e.clientX; let moved = false;
        const move = ev => {
          if (!moved && Math.abs(ev.clientX - startX) <= 3) return;
          moved = true;
          const dur = _clipDur || 0; bm.t = snapKf(Math.max(0, Math.min(dur, clipTlPos(ev.clientX) * dur)));
          m.style.left = (dur > 0 ? bm.t / dur * 100 : 0) + "%";
          m.title = (bm.label || "Bookmark") + " · " + clipDur(bm.t);
        };
        const up = () => {
          endTlDrag();
          m.removeEventListener("pointermove", move); m.removeEventListener("pointerup", up); m.removeEventListener("pointercancel", up);
          try { m.releasePointerCapture(e.pointerId); } catch {}
          if (moved) { bmList().sort((a, b) => a.t - b.t); save(); renderBookmarks(); }
          else jumpBookmark(bm.t);
        };
        m.addEventListener("pointermove", move); m.addEventListener("pointerup", up); m.addEventListener("pointercancel", up);
      });
      tl.appendChild(m);
    });
    if (wrap) {
      wrap.innerHTML = list.map((bm, i) => `<span class="clip-mark-chip${bm.auto ? " auto" : ""}" data-i="${i}"><button class="cmk-go" data-i="${i}" title="Jump + play">${bm.auto ? "⚡" : "▸"}</button><span class="cmk-label" data-i="${i}" title="Click to jump">${esc(bm.label || "Bookmark")}</span><span class="cmk-t">${clipDur(bm.t)}</span><button class="cmk-ren" data-i="${i}" title="Rename">✎</button><button class="cmk-del" data-i="${i}" title="Delete">✕</button></span>`).join("");
      wrap.querySelectorAll(".cmk-go, .cmk-label").forEach(b => b.addEventListener("click", () => jumpBookmark(list[+b.dataset.i].t)));
      wrap.querySelectorAll(".cmk-ren").forEach(b => b.addEventListener("click", () => renameBookmark(+b.dataset.i)));
      wrap.querySelectorAll(".cmk-del").forEach(b => b.addEventListener("click", () => deleteBookmark(+b.dataset.i)));
    }
  }
  // ── Multi-segment export ──
  function segList() { const o = state.clipSegments || (state.clipSegments = {}); return o[_clipName] || (o[_clipName] = []); }
  function segCount() { return ((state.clipSegments && state.clipSegments[_clipName]) || []).length; }
  function addSegment() {
    if (!_clipName) return;
    if (_clipOut - _clipIn < 0.2) { showToast("Add segment", "Select a range with the timeline handles first."); return; }
    const list = segList(); list.push({ in: _clipIn, out: _clipOut }); list.sort((a, b) => a.in - b.in);
    save(); renderSegments(); renderClipTl();
    showToast("Segment added", segCount() + " saved · Create will export " + (segCount() > 1 ? "all of them" : "it"));
  }
  function deleteSegment(i) { const list = segList(); if (i < 0 || i >= list.length) return; list.splice(i, 1); save(); renderSegments(); renderClipTl(); }
  function jumpSegment(i) { const s = segList()[i]; if (!s) return; _clipIn = s.in; _clipOut = s.out; followLanes(); renderClipTl(); seekClip(_clipIn); }
  function renderSegments() {
    const tl = $("#clipTl"), wrap = $("#clipSegs");
    if (tl) tl.querySelectorAll(".clip-seg").forEach(b => b.remove());
    const dur = _clipDur || 0, list = (state.clipSegments && state.clipSegments[_clipName]) || [];
    if (tl && dur > 0) list.forEach(s => {
      const b = document.createElement("div"); b.className = "clip-seg";
      b.style.left = Math.max(0, Math.min(100, s.in / dur * 100)) + "%";
      b.style.width = Math.max(0, Math.min(100, (s.out - s.in) / dur * 100)) + "%";
      tl.appendChild(b);
    });
    if (wrap) {
      wrap.innerHTML = list.map((s, i) => `<span class="clip-seg-chip" data-i="${i}"><button class="cmk-go" data-i="${i}" title="Load this range">▸</button><span class="csg-t">${clipDur(s.in)}–${clipDur(s.out)}</span><button class="cmk-del" data-i="${i}" title="Remove">✕</button></span>`).join("");
      wrap.querySelectorAll(".cmk-go").forEach(b => b.addEventListener("click", () => jumpSegment(+b.dataset.i)));
      wrap.querySelectorAll(".cmk-del").forEach(b => b.addEventListener("click", () => deleteSegment(+b.dataset.i)));
    }
    const cb = $("#clipCombine"); if (cb) cb.hidden = list.length < 2;
  }
  // A track counts toward the export only if present, not muted, AND readable (a corrupt sidecar is silently excluded).
  function micActive() { return _hasMic && !_micMuted && _micReadable; }
  function chatActive() { return _hasChat && !_chatMuted && _chatReadable; }
  function segExportOpts(combine) {
    return {
      segments: segList().map(s => ({ in: s.in, out: s.out })), combine: !!combine,
      vidVol: _vidVol / 100,
      micOn: micActive(), micIn: Math.max(0, _micTlStart - _micBase), micOut: Math.max(0, _micTlEnd - _micBase), micOffset: _micTlStart, micVol: _micVol / 100,
      chatOn: chatActive(), chatIn: Math.max(0, _chatTlStart - _chatBase), chatOut: Math.max(0, _chatTlEnd - _chatBase), chatOffset: _chatTlStart, chatVol: _chatVol / 100,
    };
  }
  function exportRange(inS, outS) {
    if (_vidVol === 100 && !micActive() && !chatActive()) { clipsNative.smartCut(_clipName, inS, outS); return; }
    clipsNative.composeClip(_clipName, JSON.stringify({
      vidIn: inS, vidOut: outS, vidVol: _vidVol / 100,
      micOn: micActive(), micIn: Math.max(0, _micTlStart - _micBase), micOut: Math.max(0, _micTlEnd - _micBase), micOffset: _micTlStart, micVol: _micVol / 100,
      chatOn: chatActive(), chatIn: Math.max(0, _chatTlStart - _chatBase), chatOut: Math.max(0, _chatTlEnd - _chatBase), chatOffset: _chatTlStart, chatVol: _chatVol / 100,
    }));
  }
  // ── Generalized "Export to…" — save the selection (or whole clip) to a user-chosen MP4 or GIF (mix + trim honored). ──
  function exportOpts(inS, outS) {
    return {
      vidIn: inS, vidOut: outS, vidVol: _vidVol / 100,
      micOn: micActive(), micIn: Math.max(0, _micTlStart - _micBase), micOut: Math.max(0, _micTlEnd - _micBase), micOffset: _micTlStart, micVol: _micVol / 100,
      chatOn: chatActive(), chatIn: Math.max(0, _chatTlStart - _chatBase), chatOut: Math.max(0, _chatTlEnd - _chatBase), chatOffset: _chatTlStart, chatVol: _chatVol / 100,
      gifFps: 15, gifWidth: 640,
    };
  }
  async function doExport(format) {
    const n = curClipName(); if (!n) return;
    const sel = clipSelActive(), inS = sel ? _clipIn : 0, outS = sel ? _clipOut : (_clipDur || 0);
    if (outS - inS < 0.1) { showToast("Export", "Nothing to export — the selection is empty."); return; }
    if (format === "gif" && outS - inS > 30) { showToast("GIF is long", "This range is over 30s — the GIF may be very large. Trim the selection for a smaller file."); }
    const gm = parseClip(n).game || n.replace(/\.[^.]+$/, "");
    const stem = (gm || "clip").replace(/[^\w.-]+/g, "_");
    const ext = format === "gif" ? ".gif" : ".mp4";
    const filter = format === "gif" ? "Animated GIF (*.gif)|*.gif" : "MP4 video (*.mp4)|*.mp4";
    const path = await clipsNative.pickSaveFile(stem + ext, filter);
    if (!path) return;
    showToast(format === "gif" ? "Exporting GIF…" : "Exporting MP4…", "Rendering your selection — this can take a moment.");
    clipsNative.exportFile(n, path, format, JSON.stringify(exportOpts(inS, outS)));
  }
  function openExportMenu() {
    const n = curClipName(); if (!n) return;
    const ov = document.createElement("div"); ov.className = "confirm-ov";
    ov.innerHTML = `<div class="confirm-box" role="dialog" aria-modal="true" aria-label="Export clip"><div class="confirm-title">Export clip</div>`
      + `<div class="confirm-body">Save the ${clipSelActive() ? "selected range" : "whole clip"} to a file you choose. MP4 keeps audio; GIF is silent.</div>`
      + `<div class="confirm-actions"><button class="gal-btn" data-act="cancel">Cancel</button><button class="gal-btn" data-act="gif">Animated GIF</button><button class="gal-btn primary" data-act="mp4">Save as MP4</button></div></div>`;
    document.body.appendChild(ov);
    const close = () => { ov.remove(); document.removeEventListener("keydown", onKey, true); };
    const onKey = e => { if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(); } };
    document.addEventListener("keydown", onKey, true);
    ov.addEventListener("pointerdown", e => { if (e.target === ov) close(); });
    ov.querySelector('[data-act="cancel"]').addEventListener("click", close);
    ov.querySelector('[data-act="mp4"]').addEventListener("click", () => { close(); doExport("mp4"); });
    ov.querySelector('[data-act="gif"]').addEventListener("click", () => { close(); doExport("gif"); });
  }
  window.onExport = (ok, path) => {
    if (ok) { const f = String(path || "").split(/[\\/]/).pop(); showToast("Exported", f ? ("Saved " + f + ".") : "Saved to your chosen file."); }
    else showToast("Export failed", "Couldn't write that file — check the location and try again.");
  };
  function renderRecentClips() {
    const host = $("#clipRecent"); if (!host) return;
    // Slim filmstrip: newest ~40 only (perf — fewer DOM nodes + fewer thumbnail jobs than the whole library).
    const list = (_clips || []).slice().sort((a, b) => (b.modified || 0) - (a.modified || 0)).slice(0, 40);
    host.innerHTML = list.map(c => {
      const t = thumbMem[c.name];
      return `<button class="clip-recent-item${c.name === _clipName ? " active" : ""}" data-name="${esc(c.name)}" title="${esc(c.name)}"><img class="clip-recent-thumb" alt="" loading="lazy"${t ? ` src="${esc(t)}"` : ""}>${c.edited ? '<span class="clip-recent-edited">EDITED</span>' : ""}<span class="clip-recent-name">${esc(parseClip(c.name).game || c.name)}</span><span class="clip-recent-dur">${clipDur(c.duration)}</span></button>`;
    }).join("");
    // One delegated click listener (attached once), not one per item per re-render.
    if (!host._wired) { host._wired = true; host.addEventListener("click", e => { const b = e.target.closest(".clip-recent-item"); if (b && b.dataset.name && b.dataset.name !== _clipName) openClipPage(b.dataset.name); }); }
    list.forEach(c => { if (!thumbMem[c.name] && !_thumbReq.has(c.name)) { _thumbReq.add(c.name); clipsNative.ensureThumb(c.name); } });
  }
  function openClipPage(name) {
    const c = _clips.find(x => x.name === name); if (!c) return;
    const p = parseClip(name);
    _clipName = name; _clipDur = c.duration || 0; _clipIn = 0; _clipOut = _clipDur; _t = 0; _playing = false; _atEnd = false; _clipKfs = []; _clipAR = (infoMem[name] && infoMem[name].width > 0) ? infoMem[name].width / infoMem[name].height : 0;
    _hasMic = !!c.hasMic; _micBase = 0; _micTlStart = 0; _micTlEnd = _clipDur; _vidVol = 100; _micVol = 100; _micManual = false; _micMuted = state.settings.clipMicMuted === true; _micReadable = true;
    _hasChat = !!c.hasChat; _chatBase = 0; _chatTlStart = 0; _chatTlEnd = _clipDur; _chatVol = 100; _chatManual = false; _chatMuted = state.settings.clipChatMuted === true; _chatReadable = true;
    stopMicSrc(); stopChatSrc(); _micBuf = null; _chatBuf = null;
    _micWave = null; _chatWave = null;
    if (_hasMic) decodeWave(micUrl(name)).then(w => { if (_clipName !== name) return; _micWave = w; _micBuf = w && w.buffer;
      if (!w) { _micReadable = false; showToast("Mic track", "The mic recording is unreadable — it'll be left out of exports."); }
      else if (w.dur > 0 && Math.abs((_micTlEnd - _micBase) - w.dur) > 0.1) _micTlEnd = _micBase + w.dur;   // match the lane to the real sidecar length
      renderMicLane(); });
    if (_hasChat) decodeWave(chatUrl(name)).then(w => { if (_clipName !== name) return; _chatWave = w; _chatBuf = w && w.buffer;
      if (!w) { _chatReadable = false; showToast("Chat track", "The chat recording is unreadable — it'll be left out of exports."); }
      else if (w.dur > 0 && Math.abs((_chatTlEnd - _chatBase) - w.dur) > 0.1) _chatTlEnd = _chatBase + w.dur;
      renderChatLane(); });
    { const s = $("#clipMicSection"); if (s) s.hidden = !_hasMic; }
    { const r = $("#clipMicVolRow"); if (r) r.hidden = !_hasMic; }
    { const s = $("#clipChatSection"); if (s) s.hidden = !_hasChat; }
    { const r = $("#clipChatVolRow"); if (r) r.hidden = !_hasChat; }
    { const e = $("#clipVidVol"); if (e) e.value = "100"; } { const l = $("#clipVidVolVal"); if (l) l.textContent = "100%"; }
    { const e = $("#clipMicVol"); if (e) e.value = "100"; } { const l = $("#clipMicVolVal"); if (l) l.textContent = "100%"; }
    { const b = $("#clipMicMute"); if (b) b.setAttribute("aria-pressed", String(_micMuted)); }
    { const e = $("#clipChatVol"); if (e) e.value = "100"; } { const l = $("#clipChatVolVal"); if (l) l.textContent = "100%"; }
    { const b = $("#clipChatMute"); if (b) b.setAttribute("aria-pressed", String(_chatMuted)); }
    applyClipVols();
    const setTxt = (id, t) => { const e = $(id); if (e) e.textContent = t; };
    const metaTxt = [clipBytes(c.size), clipDur(c.duration), p.date ? p.date.toLocaleString() : clipRel(c.modified)].filter(Boolean).join("   ·   ");
    setTxt("#clipMeta", metaTxt);
    { const ti = $("#clipTitle"); if (ti) { ti.textContent = clipTitle(name); ti.title = name; } }
    const ri = $("#clipRename"); if (ri) ri.value = name.replace(/\.[^.]+$/, "");
    setRenaming(false);
    const pg = $("#clipPage"); if (pg) pg.hidden = false;
    document.body.classList.add("clip-open");
    renderClipTl(); updatePlayBtn();
    { const d = $("#clipTimeDur"); if (d) d.textContent = clipDur(_clipDur); }
    paintRangeFills($("#clipPage"));
    renderBookmarks();
    importClipMarks(name);
    renderSegments();
    renderRecentClips();
    applyClipFill();
    setClipSource(name);
    updateHevcWarn();
    clipsNative.getClipKeyframes(name);
    clipsNative.ensureClipInfo(name);
    startClipRectTracking();
  }
  function closeClipPage() {
    stopClipClock();
    if (_video) { try { _video.pause(); } catch {} try { _video.removeAttribute("src"); _video.load(); } catch {} }
    stopClipRectTracking();
    stopMicSrc(); stopChatSrc();
    // Release the decoded PCM too — the wave objects hold the full AudioBuffer (~11.5 MB/min/track), which otherwise
    // sat referenced the whole time the user browsed the gallery. _clipName clears so late callbacks can't target us.
    _micBuf = null; _chatBuf = null; _micWave = null; _chatWave = null;
    _clipName = "";
    _playing = false;
    _seeking = false; _seekPending = -1; _atEnd = false;   // a seek in flight when src is removed never fires `seeked` — stuck true, it froze live scrubbing in the NEXT clip (and a stale pending target replayed into it)
    const pg = $("#clipPage"); if (pg) pg.hidden = true;
    document.body.classList.remove("clip-open");
    if (_galStale) { _galStale = false; renderLibrary(); }   // rebuild once for everything saved while the editor covered the gallery
  }
  function curClipName() { return _clipName; }
  function renderClipTl() {
    const dur = _clipDur || 0;
    const pct = x => (dur > 0 ? Math.max(0, Math.min(100, (x / dur) * 100)) : 0);
    const hi = $("#clipHIn"), ho = $("#clipHOut"), fill = $("#clipTlFill");
    if (hi) hi.style.left = pct(_clipIn) + "%";
    if (ho) ho.style.left = pct(_clipOut) + "%";
    if (fill) { fill.style.left = pct(_clipIn) + "%"; fill.style.right = (100 - pct(_clipOut)) + "%"; }
    const setTxt = (id, t) => { const e = $(id); if (e) e.textContent = t; };
    setTxt("#clipTlIn", clipDur(_clipIn)); setTxt("#clipTlOut", clipDur(_clipOut));
    setTxt("#clipSelLen", "selection " + clipDur(Math.max(0, _clipOut - _clipIn)));
    const trimmed = (_clipIn > 0.05 || _clipOut < dur - 0.05);
    const segN = segCount();
    const cc = $("#clipCreate"); if (cc) cc.textContent = segN ? ("Create " + segN + " clip" + (segN > 1 ? "s" : "")) : (trimmed ? ("Create clip · " + clipDur(_clipOut - _clipIn)) : "Create clip");
    renderMicLane();
    renderChatLane();
  }
  async function decodeWave(url) {
    try {
      if (!_waveCtx) _waveCtx = new (window.AudioContext || window.webkitAudioContext)();
      const buf = await (await fetch(url)).arrayBuffer();
      const audio = await _waveCtx.decodeAudioData(buf);
      const ch = audio.getChannelData(0), N = 1600, peaks = new Float32Array(N), step = Math.max(1, Math.floor(ch.length / N));
      for (let i = 0; i < N; i++) {
        let mx = 0; const s = i * step, e = Math.min(ch.length, s + step);
        for (let j = s; j < e; j++) { const a = ch[j] < 0 ? -ch[j] : ch[j]; if (a > mx) mx = a; }
        peaks[i] = mx;
      }
      return { peaks, dur: audio.duration, buffer: audio };
    } catch (e) { console.warn("waveform decode failed", e); return null; }
  }
  function drawWaveInto(canvas, wave, inSec, outSec) {
    if (!canvas) return;
    const w = canvas.clientWidth | 0, h = canvas.clientHeight | 0; if (w <= 0 || h <= 0) return;
    const dpr = window.devicePixelRatio || 1;
    const W = Math.max(1, Math.round(w * dpr)), H = Math.max(1, Math.round(h * dpr));
    if (canvas.width !== W) canvas.width = W;     // only reallocate the backing store when the size actually changed —
    if (canvas.height !== H) canvas.height = H;   // a trim drag redraws per frame and realloc-per-move caused jank
    const ctx = canvas.getContext("2d"); ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!wave || !wave.peaks || wave.dur <= 0) return;
    const peaks = wave.peaks, n = peaks.length, dur = wave.dur, mid = canvas.height / 2, px = canvas.width;
    const i0 = Math.max(0, inSec), i1 = Math.max(i0 + 0.001, outSec);
    ctx.fillStyle = "rgba(255,255,255,.65)";
    for (let x = 0; x < px; x++) {
      const t = i0 + (x / px) * (i1 - i0);
      let idx = Math.round((t / dur) * (n - 1)); idx = idx < 0 ? 0 : idx >= n ? n - 1 : idx;
      const barH = Math.max(1, peaks[idx] * mid * 0.95);
      ctx.fillRect(x, mid - barH, 1, barH * 2);
    }
  }
  function redrawClipWaves() { if (_hasMic) renderMicLane(); if (_hasChat) renderChatLane(); }
  // The block position/labels update immediately (cheap style writes); the waveform repaint — thousands of fillRects —
  // is coalesced through one rAF so a 120Hz pointermove drag paints at most once per frame.
  let _laneWaveRaf = 0;
  function scheduleLaneWaves() {
    if (_laneWaveRaf) return;
    _laneWaveRaf = requestAnimationFrame(() => {
      _laneWaveRaf = 0;
      if (_hasMic) drawWaveInto($("#clipMicWave"), _micWave, _micTlStart - _micBase, _micTlEnd - _micBase);
      if (_hasChat) drawWaveInto($("#clipChatWave"), _chatWave, _chatTlStart - _chatBase, _chatTlEnd - _chatBase);
    });
  }
  function renderMicLane() {
    if (!_hasMic) return;
    const dur = _clipDur || 0, pct = x => (dur > 0 ? Math.max(0, Math.min(100, (x / dur) * 100)) : 0);
    const block = $("#clipMicClip");
    if (block) { block.style.left = pct(_micTlStart) + "%"; block.style.width = Math.max(0, pct(_micTlEnd) - pct(_micTlStart)) + "%"; }
    scheduleLaneWaves();
    const setTxt = (id, t) => { const el = $(id); if (el) el.textContent = t; };
    setTxt("#clipMicTlIn", clipDur(_micTlStart)); setTxt("#clipMicTlOut", clipDur(_micTlEnd));
  }
  function renderChatLane() {
    if (!_hasChat) return;
    const dur = _clipDur || 0, pct = x => (dur > 0 ? Math.max(0, Math.min(100, (x / dur) * 100)) : 0);
    const block = $("#clipChatClip");
    if (block) { block.style.left = pct(_chatTlStart) + "%"; block.style.width = Math.max(0, pct(_chatTlEnd) - pct(_chatTlStart)) + "%"; }
    scheduleLaneWaves();
    const setTxt = (id, t) => { const el = $(id); if (el) el.textContent = t; };
    setTxt("#clipChatTlIn", clipDur(_chatTlStart)); setTxt("#clipChatTlOut", clipDur(_chatTlEnd));
  }
  function syncMic() {
    if (!_micBuf || !_hasMic) return;
    const inBlock = _t >= _micTlStart - 0.02 && _t <= _micTlEnd + 0.02 && (_t - _micBase) < _micBuf.duration - 0.05;   // last clause: a source that ran off the buffer end must not restart-loop its final ms
    if (!_playing || !inBlock) { stopMicSrc(); return; }
    if (!_micSrc) startMicSrc(_t - _micBase);
  }
  function syncChat() {
    if (!_chatBuf || !_hasChat) return;
    const inBlock = _t >= _chatTlStart - 0.02 && _t <= _chatTlEnd + 0.02 && (_t - _chatBase) < _chatBuf.duration - 0.05;
    if (!_playing || !inBlock) { stopChatSrc(); return; }
    if (!_chatSrc) startChatSrc(_t - _chatBase);
  }
  // Timeline x→fraction. During a drag the rect is cached at pointerdown (the timeline can't move mid-drag) — a
  // getBoundingClientRect per pointermove after style writes forced a layout every event.
  let _tlRect = null;
  function beginTlDrag() { const tl = $("#clipTl"); _tlRect = tl ? tl.getBoundingClientRect() : null; document.body.classList.add("clip-dragging"); }
  function endTlDrag() { _tlRect = null; document.body.classList.remove("clip-dragging"); }
  function clipTlPos(clientX) { const r = _tlRect || (($("#clipTl") || null) && $("#clipTl").getBoundingClientRect()); if (!r || !r.width) return 0; return Math.max(0, Math.min(1, (clientX - r.left) / r.width)); }
  function bindClipHandle(id, which) {
    const h = $(id); if (!h) return;
    h.addEventListener("pointerdown", e => {
      e.preventDefault(); e.stopPropagation(); try { h.setPointerCapture(e.pointerId); } catch {}
      beginTlDrag();
      const move = ev => {
        const dur = _clipDur || 0, t = clipTlPos(ev.clientX) * dur;
        if (which === "in") _clipIn = Math.max(0, Math.min(t, _clipOut - 0.2));
        else _clipOut = Math.min(dur, Math.max(t, _clipIn + 0.2));
        if (_hasMic && !_micManual) { _micTlStart = _clipIn; _micTlEnd = _clipOut; }
        if (_hasChat && !_chatManual) { _chatTlStart = _clipIn; _chatTlEnd = _clipOut; }
        scrubSeek(which === "in" ? _clipIn : _clipOut);   // smooth preview while dragging
        renderClipTl();
      };
      const up = () => { endTlDrag(); document.removeEventListener("pointermove", move); document.removeEventListener("pointerup", up); document.removeEventListener("pointercancel", up); seekClip(which === "in" ? _clipIn : _clipOut); };
      document.addEventListener("pointermove", move); document.addEventListener("pointerup", up); document.addEventListener("pointercancel", up);
    });
  }
  function bindMicHandle(id, which) {
    const h = $(id); if (!h) return;
    h.addEventListener("pointerdown", e => {
      e.preventDefault(); e.stopPropagation(); try { h.setPointerCapture(e.pointerId); } catch {}
      beginTlDrag();
      const move = ev => {
        const dur = _clipDur || 0, t = clipTlPos(ev.clientX) * dur;
        if (which === "in") _micTlStart = Math.max(_micBase, Math.min(t, _micTlEnd - 0.2));
        else _micTlEnd = Math.min(dur, Math.max(t, _micTlStart + 0.2));
        _micManual = true;
        renderMicLane(); syncMic();
      };
      const up = () => { endTlDrag(); document.removeEventListener("pointermove", move); document.removeEventListener("pointerup", up); document.removeEventListener("pointercancel", up); };
      document.addEventListener("pointermove", move); document.addEventListener("pointerup", up); document.addEventListener("pointercancel", up);   // a cancelled drag (palm rejection / device change) must not leave the move listener + clip-dragging class stuck
    });
  }
  function bindChatHandle(id, which) {
    const h = $(id); if (!h) return;
    h.addEventListener("pointerdown", e => {
      e.preventDefault(); e.stopPropagation(); try { h.setPointerCapture(e.pointerId); } catch {}
      beginTlDrag();
      const move = ev => {
        const dur = _clipDur || 0, t = clipTlPos(ev.clientX) * dur;
        if (which === "in") _chatTlStart = Math.max(_chatBase, Math.min(t, _chatTlEnd - 0.2));
        else _chatTlEnd = Math.min(dur, Math.max(t, _chatTlStart + 0.2));
        _chatManual = true;
        renderChatLane(); syncChat();
      };
      const up = () => { endTlDrag(); document.removeEventListener("pointermove", move); document.removeEventListener("pointerup", up); document.removeEventListener("pointercancel", up); };
      document.addEventListener("pointermove", move); document.addEventListener("pointerup", up); document.addEventListener("pointercancel", up);   // a cancelled drag (palm rejection / device change) must not leave the move listener + clip-dragging class stuck
    });
  }
  function bindMicDrag() {
    const block = $("#clipMicClip"); if (!block) return;
    block.addEventListener("pointerdown", e => {
      if (e.target.closest(".clip-handle")) return;
      e.preventDefault(); e.stopPropagation(); try { block.setPointerCapture(e.pointerId); } catch {}
      beginTlDrag();
      const dur = _clipDur || 0, x0 = clipTlPos(e.clientX), b0 = _micBase, s0 = _micTlStart, e0 = _micTlEnd;
      const move = ev => {
        let d = (clipTlPos(ev.clientX) - x0) * dur;
        const snap = Math.max(0.3, dur * 0.01);
        if (Math.abs(b0 + d) <= snap) d = -b0;
        if (s0 + d < 0) d = -s0;
        if (e0 + d > dur) d = dur - e0;
        _micBase = b0 + d; _micTlStart = s0 + d; _micTlEnd = e0 + d;
        _micManual = true;
        renderMicLane(); syncMic();
      };
      const up = () => { endTlDrag(); document.removeEventListener("pointermove", move); document.removeEventListener("pointerup", up); document.removeEventListener("pointercancel", up); };
      document.addEventListener("pointermove", move); document.addEventListener("pointerup", up); document.addEventListener("pointercancel", up);   // a cancelled drag (palm rejection / device change) must not leave the move listener + clip-dragging class stuck
    });
  }
  function bindChatDrag() {
    const block = $("#clipChatClip"); if (!block) return;
    block.addEventListener("pointerdown", e => {
      if (e.target.closest(".clip-handle")) return;
      e.preventDefault(); e.stopPropagation(); try { block.setPointerCapture(e.pointerId); } catch {}
      beginTlDrag();
      const dur = _clipDur || 0, x0 = clipTlPos(e.clientX), b0 = _chatBase, s0 = _chatTlStart, e0 = _chatTlEnd;
      const move = ev => {
        let d = (clipTlPos(ev.clientX) - x0) * dur;
        const snap = Math.max(0.3, dur * 0.01);
        if (Math.abs(b0 + d) <= snap) d = -b0;
        if (s0 + d < 0) d = -s0;
        if (e0 + d > dur) d = dur - e0;
        _chatBase = b0 + d; _chatTlStart = s0 + d; _chatTlEnd = e0 + d;
        _chatManual = true;
        renderChatLane(); syncChat();
      };
      const up = () => { endTlDrag(); document.removeEventListener("pointermove", move); document.removeEventListener("pointerup", up); document.removeEventListener("pointercancel", up); };
      document.addEventListener("pointermove", move); document.addEventListener("pointerup", up); document.addEventListener("pointercancel", up);   // a cancelled drag (palm rejection / device change) must not leave the move listener + clip-dragging class stuck
    });
  }
  { const b = $("#clipBack"); if (b) b.addEventListener("click", closeClipPage); }
  window.onClipKeyframes = (name, arr) => {
    if (name !== _clipName) return;
    _clipKfs = Array.isArray(arr) ? arr.filter(x => typeof x === "number" && x >= 0).sort((a, b) => a - b) : [];
  };
  // (Playback clock is now driven by the <video> element's own events + a rAF loop — see ensureVideo / startClipClock.)
  // The trim timeline doubles as the scrub bar (before: a separate seek slider above it did the dragging). Press +
  // drag scrubs with the same pause → coalesced-scrub → snap-to-keyframe → resume path the slider used.
  { const tl = $("#clipTl"); if (tl) {
      let wasPlaying = false, dragging = false;
      const tAt = x => clipTlPos(x) * (_clipDur || 0);
      tl.addEventListener("pointerdown", e => {
        if (e.button !== 0 || e.target.closest(".clip-handle")) return;
        dragging = true; _seekDrag = true; wasPlaying = _playing; _tlRect = tl.getBoundingClientRect();
        const v = ensureVideo(); if (v && !v.paused) v.pause(); pauseClipAudio();
        try { tl.setPointerCapture(e.pointerId); } catch {}
        _t = tAt(e.clientX); scrubSeek(_t); updatePlayheads(); const c = $("#clipTimeCur"); if (c) c.textContent = clipDur(_t);
      });
      tl.addEventListener("pointermove", e => {
        if (!dragging) return;
        _t = tAt(e.clientX); scrubSeek(_t); updatePlayheads(); const c = $("#clipTimeCur"); if (c) c.textContent = clipDur(_t);
      });
      const finish = e => {
        if (!dragging) return;
        dragging = false; _seekDrag = false; _tlRect = null;
        _t = snapKf(tAt(e.clientX)); _seekPending = -1; _atEnd = false;
        if (_video) { try { _video.currentTime = _t; } catch {} if (wasPlaying) _video.play().catch(() => {}); }
        updatePlayheads();
      };
      tl.addEventListener("pointerup", finish);
      tl.addEventListener("pointercancel", finish);
  } }
  { const b = $("#clipPlayPause"); if (b) b.addEventListener("click", togglePlay); }
  { const b = $("#clipStepBack"); if (b) b.addEventListener("click", () => stepClip(-1)); }   // ← keyframe (reuses the tested seek path)
  { const b = $("#clipStepFwd"); if (b) b.addEventListener("click", () => stepClip(1)); }
  window.addEventListener("keydown", clipEditorKey, true);
  { const b = $("#clipAddMark"); if (b) b.addEventListener("click", addBookmark); }
  { const sk = $("#clipSeek"); if (sk) {
      let wasPlaying = false;
      const posT = () => (((sk.value | 0) / 1000) * (_clipDur || 0));
      sk.addEventListener("pointerdown", () => { _seekDrag = true; wasPlaying = _playing; const v = ensureVideo(); if (v && !v.paused) v.pause(); pauseClipAudio(); });
      sk.addEventListener("input", () => { _seekDrag = true; _t = posT(); scrubSeek(_t); sk.style.setProperty("--vol", rangeFillPct(sk)); updatePlayheads(); const c = $("#clipTimeCur"); if (c) c.textContent = clipDur(_t); });
      const finish = () => {
        if (!_seekDrag) return;
        _seekDrag = false; _t = snapKf(posT()); _seekPending = -1; _atEnd = false;   // final position wins — drop any coalesced mid-drag target
        if (_video) { try { _video.currentTime = _t; } catch {} if (wasPlaying) _video.play().catch(() => {}); }
        updatePlayheads();
      };
      sk.addEventListener("change", finish);
      sk.addEventListener("pointerup", finish);
      sk.addEventListener("pointercancel", finish);   // cancelled drag must still land the seek + restore play state
  } }
  bindClipHandle("#clipHIn", "in"); bindClipHandle("#clipHOut", "out");
  bindMicHandle("#clipMicHIn", "in"); bindMicHandle("#clipMicHOut", "out"); bindMicDrag();
  bindChatHandle("#clipChatHIn", "in"); bindChatHandle("#clipChatHOut", "out"); bindChatDrag();
  { const b = $("#clipPreview"); if (b) b.addEventListener("click", () => { seekClip(_clipIn); const v = ensureVideo(); if (v) v.play().catch(() => {}); }); }
  { const e = $("#clipVidVol"), l = $("#clipVidVolVal"); if (e) e.addEventListener("input", () => { _vidVol = e.value | 0; if (l) l.textContent = _vidVol + "%"; e.style.setProperty("--vol", rangeFillPct(e)); applyClipVols(); }); }
  { const e = $("#clipMicVol"), l = $("#clipMicVolVal"); if (e) e.addEventListener("input", () => { _micVol = e.value | 0; if (l) l.textContent = _micVol + "%"; e.style.setProperty("--vol", rangeFillPct(e)); applyClipVols(); }); }
  { const b = $("#clipMicMute"); if (b) b.addEventListener("click", () => { _micMuted = !_micMuted; state.settings.clipMicMuted = _micMuted; save(); b.setAttribute("aria-pressed", String(_micMuted)); applyClipVols(); showToast("Mic track", _micMuted ? "Muted — silenced in preview and left out of the exported clip (saved)." : "Unmuted (saved)."); }); }
  { const e = $("#clipChatVol"), l = $("#clipChatVolVal"); if (e) e.addEventListener("input", () => { _chatVol = e.value | 0; if (l) l.textContent = _chatVol + "%"; e.style.setProperty("--vol", rangeFillPct(e)); applyClipVols(); }); }
  { const b = $("#clipChatMute"); if (b) b.addEventListener("click", () => { _chatMuted = !_chatMuted; state.settings.clipChatMuted = _chatMuted; save(); b.setAttribute("aria-pressed", String(_chatMuted)); applyClipVols(); showToast("Chat track", _chatMuted ? "Muted — silenced in preview and left out of exports (saved)." : "Unmuted (saved)."); }); }
  { const b = $("#clipMicDelBtn"); if (b) b.addEventListener("click", () => {
      const n = curClipName(); if (!n || !_hasMic) return;
      clipsNative.deleteMic(n); _hasMic = false; stopMicSrc(); _micBuf = null;
      { const s = $("#clipMicSection"); if (s) s.hidden = true; } { const r = $("#clipMicVolRow"); if (r) r.hidden = true; }
      showToast("Mic track removed", "The clip keeps its game audio; the separate mic recording was deleted.");
      setTimeout(() => clipsNative.requestClips(), 250);
  }); }
  { const b = $("#clipChatDelBtn"); if (b) b.addEventListener("click", () => {
      const n = curClipName(); if (!n || !_hasChat) return;
      clipsNative.deleteChat(n); _hasChat = false; stopChatSrc(); _chatBuf = null;
      { const s = $("#clipChatSection"); if (s) s.hidden = true; } { const r = $("#clipChatVolRow"); if (r) r.hidden = true; }
      showToast("Chat track removed", "The clip keeps its game audio; the separate chat recording was deleted.");
      setTimeout(() => clipsNative.requestClips(), 250);
  }); }
  { const b = $("#clipAddSeg"); if (b) b.addEventListener("click", addSegment); }
  { const b = $("#clipCombine"); if (b) b.addEventListener("click", () => {
      const n = segCount(); if (n < 1) return;
      clipsNative.exportSegments(_clipName, JSON.stringify(segExportOpts(true)));
      showToast("Combining " + n + " segments…", "Merging into one clip in the Edited folder — it'll appear in the gallery.");
      closeClipPage();
  }); }
  { const b = $("#clipCreate"); if (b) b.addEventListener("click", () => {
      const dur = _clipDur || 0;
      const isPure = (_vidVol === 100 && !_hasMic && !_hasChat);
      if (segCount() > 0) {
        const n = segCount();
        clipsNative.exportSegments(_clipName, JSON.stringify(segExportOpts(false)));
        showToast("Creating " + n + " clips…", (isPure ? "Smart cut" : "Re-encoding") + " each segment to the Edited folder — they'll appear in the gallery.");
        closeClipPage();
        return;
      }
      const trimmed = (_clipIn > 0.05 || _clipOut < dur - 0.05);
      const changed = trimmed || _vidVol !== 100 || _hasMic || _hasChat;
      if (!changed) { showToast("Create clip", "Drag the timeline handles to select a part (or add segments) first."); return; }
      exportRange(_clipIn, _clipOut);
      showToast("Creating clip…", isPure ? ("Smart cut " + clipDur(_clipOut - _clipIn) + " — near-lossless. It'll appear in the gallery (Edited).")
                                         : ((_hasMic || _hasChat) ? "Mixing video + audio tracks — it'll appear in the gallery (Edited)." : "Re-encoding " + clipDur(_clipOut - _clipIn) + " — it'll appear in the gallery (Edited)."));
      closeClipPage();
  }); }
  { const b = $("#clipDeleteBtn"); if (b) b.addEventListener("click", () => { const n = curClipName(); if (!n) return; if (state.clipBookmarks) delete state.clipBookmarks[n]; if (state.clipSegments) delete state.clipSegments[n]; save(); clipsNative.deleteClip(n); closeClipPage(); setTimeout(() => clipsNative.requestClips(), 250); showToast("Deleted", n); }); }
  { const b = $("#clipOpenBtn"); if (b) b.addEventListener("click", () => { const n = curClipName(); if (n) clipsNative.revealClip(n); }); }
  { const b = $("#clipShareBtn"); if (b) b.addEventListener("click", () => { const n = curClipName(); if (n) openSharePopup(n, clipSelActive() ? _clipIn : 0, clipSelActive() ? _clipOut : 0); }); }
  { const b = $("#clipExportBtn"); if (b) b.addEventListener("click", openExportMenu); }
  function applyClipFill() {
    const on = state.settings.clipFill === true;
    const wrap = $("#clipStage"); if (wrap) wrap.classList.toggle("fill", on);   // CSS object-fit cover/contain (was LibVLC CropGeometry)
    const l = $("#clipFillLabel"); if (l) l.textContent = on ? "Fit player" : "Fill player";
  }
  { const b = $("#clipFillBtn"); if (b) b.addEventListener("click", () => { state.settings.clipFill = !(state.settings.clipFill === true); save(); applyClipFill(); }); }
  // Editor ⋯ More menu (Open folder / Delete) — toggle, and close on outside pointerdown or after an action.
  { const b = $("#clipMoreBtn"), m = $("#clipMoreMenu");
    if (b && m) {
      const close = () => { m.hidden = true; b.setAttribute("aria-expanded", "false"); };
      b.addEventListener("click", e => { e.stopPropagation(); const opening = m.hidden; m.hidden = !opening; b.setAttribute("aria-expanded", String(opening)); });
      m.addEventListener("click", close);
      document.addEventListener("pointerdown", e => { if (!m.hidden && !e.target.closest(".clip-more")) close(); });
    } }
  // The header shows a friendly title; Rename swaps in the file-name box (Enter saves, Esc cancels).
  function setRenaming(on) {
    const ti = $("#clipTitle"), ri = $("#clipRename"), b = $("#clipRenameBtn");
    if (ti) ti.hidden = on; if (ri) ri.hidden = !on; if (b) b.textContent = on ? "Save" : "Rename";
    if (on && ri) { const n = curClipName(); if (n) ri.value = n.replace(/\.[^.]+$/, ""); ri.focus(); ri.select(); }
  }
  function commitRename() {
    const n = curClipName(), ri = $("#clipRename"); if (!n || !ri) return;
    const nn = (ri.value || "").trim(); if (!nn || nn + ".mp4" === n) { setRenaming(false); return; }
    migrateClipState(n, nn + ".mp4");
    clipsNative.renameClip(n, nn + ".mp4"); closeClipPage(); setTimeout(() => clipsNative.requestClips(), 250); showToast("Renamed", nn);
  }
  { const b = $("#clipRenameBtn"); if (b) b.addEventListener("click", () => { const ri = $("#clipRename"); if (ri && !ri.hidden) commitRename(); else setRenaming(true); }); }
  { const ri = $("#clipRename"); if (ri) ri.addEventListener("keydown", e => {
      if (e.key === "Enter") { e.preventDefault(); commitRename(); }
      else if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); setRenaming(false); }   // cancel the edit, not the editor
  }); }
  document.addEventListener("keydown", e => {
    if (e.key !== "Escape") return;
    // Nested UI owns Escape: a modal overlay (Export dialog / onboarding / confirm) or a focused text edit (bookmark
    // label, rename box) — this capture handler registered FIRST, so without the bail it closed the whole editor
    // underneath them.
    if (document.querySelector(".confirm-ov")) return;
    const ae = document.activeElement;
    if (ae && /^(INPUT|TEXTAREA)$/.test(ae.tagName) && (ae.type || "").toLowerCase() !== "range") return;
    const pg = $("#clipPage"); if (pg && !pg.hidden) { e.preventDefault(); e.stopPropagation(); closeClipPage(); }
  }, true);
  // ── Theme (splash reads sc_theme; app applies data-theme) ──
  function applyTheme() { document.body.setAttribute("data-theme", state.settings.theme || "spartan"); }

  // ══════════════════════════════════════════════════════════════════════════════════════════════════
  //  BOOT — runs last. Lean: no launcher pollers; only the recording-elapsed timer ticks (while recording).
  // ══════════════════════════════════════════════════════════════════════════════════════════════════
  async function boot() {
    { const b = $("#nativeBadge"); if (b) b.textContent = clipsNative.has() ? "Native bridge active" : "Browser preview mode"; }
    applyTheme();

    // 1. Recover from the on-disk config mirror if localStorage was empty/reset (same no-wipe rule as load()).
    if (!lsState && clipsNative.has()) {
      let raw = null; try { raw = await clipsNative.loadConfig(); } catch {}
      if (raw) {
        let parsed = null;
        try { parsed = JSON.parse(raw); }
        catch (e) {
          console.error("config.json parse failed", e);
          try { await clipsNative.quarantineConfig(); } catch {}   // rename the bad file BEFORE flushDisk overwrites it with defaults
          showToast("Settings couldn't be read", "Your saved config was unreadable and has been set aside — starting with defaults.");
        }
        if (parsed) {
          let disk = null;
          try { disk = normalize(parsed); } catch (e) { console.error("normalize() failed on the disk config — using it raw", e); disk = parsed; }
          if (disk) { state = disk; try { localStorage.setItem(STORE_KEY, JSON.stringify(state)); } catch {} applyTheme(); showToast("Settings restored", "Loaded your saved configuration from disk"); }
        }
      }
    }
    _allowDisk = true; flushDisk();   // every change now mirrors to the %APPDATA% file

    // 2. Push the saved settings + hotkeys to C# (fire-and-forget bridge calls — nothing below waits on them).
    pushCaptureSettings();
    pushClipHotkeys();
    clipsNative.setClipsDir(state.settings.clipFolder || "");
    clipsNative.setAutoHighlight(state.settings.autoHighlight === true);
    clipsNative.setMicDevice(state.settings.captureMicDevice || "");
    clipsNative.setChatDevice(state.settings.captureChatDevice || "");
    clipsNative.setArmPolicy(state.settings.armPolicy || "always");

    // 3. FIRST CONTENT: request the clips + show the gallery NOW. before: this waited behind 4 serialized bridge
    // round-trips + codec probes (AV1/HEVC/autostart/armed) that the gallery doesn't need — they run in parallel below.
    clipsNative.requestClips();
    showView("gallery");
    maybeShowOnboarding();   // first-run welcome (once), over the gallery

    // 4. Capability probes + OS state — concurrently; each updates its own bit of UI as it lands.
    //    (Device pickers populate inside syncCapturePane — they were ALSO called directly here before, double-probing
    //    the audio endpoints on every boot.)
    if (clipsNative.has()) {
      await Promise.all([
        clipsNative.hasHardwareAv1().then(hw => { _hwAv1 = !!hw; applyAv1Availability(); }).catch(() => {}),
        detectHevcNative().then(native => {
          // If an HEVC clip was opened before the probe resolved, it went direct — reroute it through the proxy now.
          const info = infoMem[_clipName];
          if (!native && info && info.codec === "hevc" && _clipSrcMode === "direct") setClipSource(_clipName);
        }).catch(() => {}),
        Promise.resolve(clipsNative.requestAutostartState()).catch(() => {}),   // → onAutostartState reconciles autostart + elevated
        clipsNative.isReplayArmed().then(a => { _replayArmed = !!a; updateClipRecUI(); }).catch(() => {}),
        // A hotkey recording started with NO window open pushed no "state" event this page could have seen — sync it,
        // then correct the elapsed epoch to the true start (updateClipRecUI's timer starts counting from window-open).
        clipsNative.isClipRecording().then(async r => {
          if (!r) return;
          _recording = true; updateClipRecUI();
          try { const el = +(await clipsNative.recElapsedSec()) || 0; if (el > 0) { _recStart = Date.now() - el * 1000; paintRecElapsed(); } } catch {}
        }).catch(() => {}),
      ]);
    } else { try { await detectHevcNative(); } catch {} }
    syncShellSettings();
    syncCapturePane();

    // Sync the instant-replay enabled flag to C# on boot (TrayCore already applied the arm policy at startup; this
    // keeps it in step with the loaded settings). C# does the actual arming per policy.
    setTimeout(() => { try { applyReplayArm(); } catch {} }, 1200);

    // Reflect the LIVE replay-armed state in the HUD once more after the arm-policy settles — TrayCore arms at
    // startup, before this window existed, so the onCapture("replay") arm event was missed.
    setTimeout(async () => { try { _replayArmed = !!(await clipsNative.isReplayArmed()); updateClipRecUI(); } catch {} }, 1600);
  }
  // ── Command palette (Ctrl/⌘+K) — quick actions + jump to a recent clip. Self-contained: its window-capture key
  //    handling stops the editor/Esc handlers from also firing while it's open (clipEditorKey already bails on an
  //    input focus, and stopImmediatePropagation blocks the document-capture Esc handlers). ──
  (function cmdPalette() {
    const pal = $("#cmdPalette"), input = $("#cmdInput"), listEl = $("#cmdList");
    if (!pal || !input || !listEl) return;
    let open = false, cmds = [], shown = [], sel = 0;
    function build() {
      const a = [];
      if (_recording) a.push({ t: "Stop recording", run: toggleClipRecord });
      else if (_replayArmed) a.push({ t: "Save replay", run: () => clipsNative.saveReplay() });
      else a.push({ t: "Start recording", run: toggleClipRecord });
      a.push({ t: "Go to Clips", run: () => { const pg = $("#clipPage"); if (pg && !pg.hidden) closeClipPage(); showView("gallery"); } });
      a.push({ t: "Go to Settings", run: () => { const pg = $("#clipPage"); if (pg && !pg.hidden) closeClipPage(); showView("settings"); } });
      a.push({ t: "Open clips folder", run: () => clipsNative.openClipsFolder() });
      a.push({ t: "Open logs folder", run: () => clipsNative.openLogsFolder() });
      (_clips || []).slice().sort((x, y) => (y.modified || 0) - (x.modified || 0)).slice(0, 8)
        .forEach(c => a.push({ t: "Open clip · " + (parseClip(c.name).game || c.name), run: () => { showView("gallery"); openClipPage(c.name); } }));
      return a;
    }
    function render() {
      const q = input.value.trim().toLowerCase();
      shown = q ? cmds.filter(c => c.t.toLowerCase().includes(q)) : cmds;
      if (sel >= shown.length) sel = Math.max(0, shown.length - 1);
      listEl.innerHTML = shown.length
        ? shown.map((c, i) => `<button class="cmdp-item${i === sel ? " sel" : ""}" data-i="${i}">${esc(c.t)}</button>`).join("")
        : `<div class="cmdp-empty">No matching command.</div>`;
      const s = listEl.querySelector(".cmdp-item.sel"); if (s) s.scrollIntoView({ block: "nearest" });
    }
    function show() { open = true; cmds = build(); sel = 0; input.value = ""; pal.hidden = false; render(); setTimeout(() => input.focus(), 0); }
    function hide() { open = false; pal.hidden = true; }
    function runSel() { const c = shown[sel]; hide(); if (c) try { c.run(); } catch {} }
    input.addEventListener("input", () => { sel = 0; render(); });
    listEl.addEventListener("click", e => { const b = e.target.closest(".cmdp-item"); if (b) { sel = b.dataset.i | 0; runSel(); } });
    pal.addEventListener("pointerdown", e => { if (e.target === pal) hide(); });
    window.addEventListener("keydown", e => {
      if ((e.ctrlKey || e.metaKey) && (e.key === "k" || e.key === "K")) { e.preventDefault(); e.stopImmediatePropagation(); open ? hide() : show(); return; }
      if (!open) return;
      if (e.key === "Escape") { e.preventDefault(); e.stopImmediatePropagation(); hide(); }
      else if (e.key === "ArrowDown") { e.preventDefault(); e.stopImmediatePropagation(); sel = Math.min(shown.length - 1, sel + 1); render(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); e.stopImmediatePropagation(); sel = Math.max(0, sel - 1); render(); }
      else if (e.key === "Enter") { e.preventDefault(); e.stopImmediatePropagation(); runSel(); }
    }, true);
  })();

  boot();
})();
