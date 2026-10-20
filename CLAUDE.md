# Sparta Clips — Project Rules (read this first, every session)

Sparta Clips is a **standalone, tray-first Windows game clipper** — record / instant-replay / edit clips. It was
**extracted from the Sparta Dock launcher** (a separate project); the capture engine there was built
to be liftable and is now the core of this app. Same tech shape as Sparta Dock: a **C# .NET 8 WinForms host** that
wraps a **WebView2 frontend** (`web/`). C# does the real OS work; the web side is the UI.

The three principles that override everything: **Performance · Scalability · Maintainability.** Prefer simple,
efficient, readable solutions; don't over-engineer; don't take messy shortcuts.

User-facing overview, build and test commands: **`README.md`**.

---

## Architecture

- **`TrayCore`** (`Ui/TrayCore.cs`) — the **always-on** core. Runs as the `ApplicationContext` on the message-pump
  thread with **NO WebView2 resident**. Owns the `ClipRecorder` engine, the `ClipHotkeyHook` (global hotkeys), the
  `ClipRecorder.Notify`→toast/WebSink wiring, the **instant-replay arming policy**, the NotifyIcon + menu, and the
  lazy `MainForm` lifecycle. This is what's alive when the app sits in the tray.
- **`MainForm`** (`Ui/MainForm.cs` + `.Chrome.cs` + `.DragShare.cs`) — the WebView2 host window (gallery /
  editor / settings). **Created lazily** by TrayCore on "Open" and, by default, **disposed on close** so idle carries
  no Chromium render/GPU processes. Partials add the frameless window chrome + the drag-to-Discord proxy. **Clip
  playback is an in-page HTML5 `<video>`** in the WebView (served from `clips.local`) — LibVLC was removed; H.264/AV1
  decode natively, HEVC plays natively when the OS "HEVC Video Extensions" is present else via a cached ffmpeg H.264
  proxy (`ClipProxy` → `clipproxy.local`). C# only probes keyframes (`GetClipKeyframes`).
- **`BridgeApi`** (`Bridge/BridgeApi*.cs`) — the only `[ComVisible]` object exposed to JS, registered as
  **`clipsBridge`** (wrapped as `clipsNative` in JS). Split into partials by concern (Capture / ClipPlayer / Share /
  Config). Every JS→C# call lands here.
- **`Services/Capture/`** — the self-contained record/replay/ffmpeg/AV1/sidecar engine, lifted verbatim from Sparta
  Dock. **`Services/System/`** — `AppConfig` (config.json + engine-apply + autostart), `DiagLog`,
  `ProcessLoopbackCapture`, `ManagedTools` (slim ffmpeg fetcher). **`Services/Input/ClipHotkeyHook.cs`** — the
  low-level keyboard hook that fires even over fullscreen anti-cheat games.
- Frontend calls C# via `window.chrome.webview.hostObjects.clipsBridge`; C# calls JS via `PostToWeb(...)` →
  `window.on*(...)` callbacks.

---

## DESIGN INVARIANTS — do NOT "fix" these (they're deliberate)

1. **No WebView2 in the idle/tray state.** The tray core is WebView-free; the window is created on demand and disposed
   on close (`releaseUiOnClose`, default on). This is the entire performance premise of the standalone — never make the
   window always-resident or move the recorder/hotkeys into MainForm.
2. **`TrayCore` owns `ClipRecorder.Notify` + the hotkeys**, not the bridge — so recording, instant-replay and the
   over-game "clip saved" toast all work with **no window open**. The bridge only registers a `TrayCore.WebSink` while
   the UI is up (to push `window.onCapture`/`onClips`). See the before/after note in `Bridge/BridgeApi.Capture.cs`.
3. **Bridge calls run on the UI thread.** Never block a `BridgeApi` method with sync WinRT/`.GetResult()`/long work —
   offload to `Task.Run` and return via `PostToWeb`. (Same rule that bit Sparta Dock.)
4. **The bridge JSON shapes are a verbatim copy of Sparta Dock's**, and the frontend is a **faithful lift** of Sparta
   Dock's clip UI. The web `state.settings.capture*` / `replay*` key names must stay in sync with what
   `Services/System/AppConfig.cs` reads at startup. If you rename a setting key, change BOTH sides.
5. **Editor mic/chat audio plays via Web-Audio buffer-source nodes, not `<audio>` elements.** This was a deliberate
   fix (voice doubled on seek otherwise). Never revert.
6. **Frontend is zero-build**: one classic `app.js` sharing global scope (not `type="module"`), served from a virtual
   https host. Assets are relative / data URIs. Keep it that way.
7. **Virtual hosts**: `spartaclips.local` → `web/`, `clips.local` → clips folder, `clipthumb.local` → thumbnails.
8. **Naming is uniform `SpartaClips`** everywhere (namespace/exe/assembly/mutex/appdata). No `SpartaDoc`-vs-`SpartaDock`
   quirk here — don't introduce one.

---

## Folder structure
```
SpartaClips/
  Program.cs                 entry point (single-instance mutex, --tray)
  app.manifest, app.ico
  Ui/        TrayCore.cs, MainForm*.cs, ClipToast.cs
  Bridge/    BridgeApi*.cs                     (clipsBridge; one concern per partial)
  Services/
    Capture/   ClipRecorder/Library/Ffmpeg/Av1Transcoder/AudioSidecar/ClipShare/ClipChime/HardwareAv1 + HighlightObservers/
    Input/     ClipHotkeyHook.cs
    System/    AppConfig.cs, DiagLog.cs, ProcessLoopbackCapture.cs, ManagedTools.cs
  web/       index.html, styles.css, app.js    (zero-build WebView2 UI)
  deploy.ps1
```
A new `BridgeApi` method goes in the matching `BridgeApi.*.cs` partial. Namespace is flat (`SpartaClips`) regardless
of folder, so the SDK globs `**/*.cs` — moving/adding files needs no `.csproj` change.

---

## Contracts (keep C# and JS in sync)

- **Bridge host object**: `clipsBridge` (JS `clipsNative`). Method access is case-insensitive (COM IDispatch), so JS
  camelCase resolves to the C# PascalCase method.
- **C#→JS callbacks**: `window.on*` — `onClips`, `onClipThumb`, `onClipInfo`, `onCapture`, `onClipKeyframes`,
  `onClipMarks(name, arr)` (mark-a-moment sidecar import; RequestClipMarks reads+deletes it off-thread),
  `onFolderPicked(path)` / `onSaveFilePicked(path)` (deferred-modal pickers — the JS wrappers re-wrap these in
  promises, so call sites still `await clipsNative.pickFolder/pickSaveFile`),
  `onAutostartState({runKey, task})` (observable autostart truth — the UI reconciles both toggles to it, so a
  cancelled UAC prompt snaps the switch back),
  `onClipTime`, `onShareRendered`, `onShare`, `onExport(ok,path)`, `onManagedInstall`, `onFolderWritable(ok)`,
  `showNativeToast`, `onCaptureWindows(list)` (open-window list + preview thumbs for the title-bar capture-source
  picker; the picked source is a session-only override via `SetCaptureSource` — never persisted), and
  **`onRouteView(view)`** (`""`→gallery, `"settings"`→settings; called by TrayCore when opening the window). Grep
  `window.on` before renaming/removing. `onCapture` events include `marked`/`marknorec` (mark-a-moment hotkey).
- **Settings schema** lives in `state.settings.*` (web) and is read by `AppConfig.cs` (C#). Capture keys:
  `capturePreset, captureFps, captureBitrate, captureResH, captureMode, captureEncoder, captureMic, captureSepMic,
  captureChat, captureCapGB, captureMicDevice, captureChatDevice ("" / device id / "app:<process>" per-app),
  capturePin (pinned program, "" = auto-follow), replayEnabled, replayLen, replayChime, autoHighlight,
  clipFolder, recHotkey{ctrl,alt,shift,win,vk}, repHotkey{…}, markHotkey{…}` (mark-a-moment, default Ctrl+Alt+F8),
  and `gameProfiles[]` ({match, fps, mbps, resH} — per-game capture overrides, read by `AppConfig.ProfileFor` at
  record start). Standalone-only keys: `armPolicy` ("always"|"game"), `autostart` (bool), `autostartElevated` (bool — Task Scheduler
  /RL HIGHEST logon task instead of the Run key; set via the atomic `SetAutostartMode("off"|"on"|"elevated")`, truth
  reported back via `onAutostartState({runKey, task})`), `releaseUiOnClose` (bool),
  `overlayEnabled` (bool — in-game ● REC overlay), `seenOnboarding` (bool — first-run welcome shown). `save()`
  mirrors the whole `state` to C# via `clipsNative.saveConfig`.

---

## Code style & comments
- C# = PascalCase members, `_camelCase` private fields. JS = `camelCase`, the `$`/`$$` DOM helpers, `state` for
  persisted config. Match the surrounding file's style.
- Comments explain **why**, not what. Short (≤4–5 lines). Every file starts with a one-line purpose comment.
- **Document meaningful behavioral changes before/after** — inline (`// before: … / after: …`) and in the commit message.

## Deploy & verify
- Deploy with **`./deploy.ps1`** (builds Release x64, stages to `SpartaClips-final/`, relaunches). **Close the
  app first** — it locks `SpartaClips.dll`. `SpartaClips.exe --tray` starts hidden to the tray.
- After any `web/` change, run the app and smoke-test: tray menu, hotkey record with no window, save-replay + toast,
  open UI → gallery thumbnails, open a clip → editor (timeline, mic/chat lanes, bookmarks, segments, export, share),
  close window (WebView2 disposed, recording still works), autostart toggle. Watch the WebView console for errors.
- After C# changes: `dotnet build SpartaClips.csproj -c Release` must be 0 errors.
- **UI smoke-test driver**: deploy a test build (`./deploy.ps1 -TestHooks`, or any Debug build), then relaunch it
  with the env var `SPARTACLIPS_CDP=9333` to expose the WebView2 DevTools Protocol on localhost (the hook in
  `MainForm.InitWebViewAsync` is compiled ONLY under `CDP_HOOK` — absent from normal Release builds), then
  drive the REAL UI: `python tools/uidrive.py eval "<js>"` (awaits promises; bridge reachable as
  `chrome.webview.hostObjects.clipsBridge`), `shot out.png` (screenshot — LOOK at it), `click "<css>"`, `key <code>`.
  Use it to verify fixes end-to-end; it already caught one plausible-but-wrong "fix" (maximize inset).
- **Diagnostic suite** (all in `tools/`, all self-cleaning): `smoke.py` (6-check UI regression, run after every
  deploy), `stress.py` (replay-engine concurrency: arm/disarm/save races, source-switch storms, pinned-window death
  self-heal — asserts against the `GetEngineDiag` bridge snapshot; found the lost-disarm `_wantArmed` race),
  `mediatest.py` (synthetic-clip truth tests: trimmed-share mic tone, >4 GiB largesize moov via sparse file,
  keyboard-selection export mix, HEVC→H.264 proxy), `verify_elevated.py` (elevated-autostart task config), and
  `tools/enginetest` (C# engine tests vs the built DLL: encoder pick, per-app game audio level, share mix/resolution).
- Build artifacts (`bin/`, `obj/`) and `SpartaClips-final/` are git-ignored — don't re-add them.
