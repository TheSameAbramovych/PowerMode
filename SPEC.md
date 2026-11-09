# PowerMode — spec (v1)

Windows 11 tray app that switches this PC between three power-consumption modes so the owner can
work from a portable power station during blackouts. Written 2026-11-09. UI language: English.

## 1. Why / situation

The PC is a **desktop** (no battery, no OS battery awareness), fed from a portable power station when
the grid is down. The owner wants three one-click modes: everything as usual on the grid, comfortable
savings on the station, maximum savings when the station is nearly empty. The app must be convenient
(tray icon, one click / one hotkey, visible effect) and fully reversible (Grid restores exactly what
was there before).

## 2. Machine facts (measured 2026-11-09; defaults are tuned to them, everything configurable)

| Fact | Value |
|---|---|
| OS | Windows 11 26H2, build 26300 |
| CPU | AMD Ryzen 7 2700, 8c/16t, base 3.2 GHz, Precision Boost to ~4.1 GHz |
| GPU | NVIDIA GeForce GTX 1660 Ti; `nvidia-smi` in `C:\Windows\System32`; `nvml.dll` ships with the driver. Idle at 199 Hz: P5, **20 W**. Power limit default 120 W, min 60 W, max 132 W |
| Monitor | one: Gigabyte **M27Q2**, 2560×1440 @ **199 Hz** now. DDC/CI works via `dxva2.dll`: brightness 0..100 (currently 80), contrast 50, VCP 0xD6 readable. `EnumDisplaySettings` must be called with the real device name from `EnumDisplayDevices` (e.g. `\\.\DISPLAY1`), a null name returned nothing |
| Power schemes | `381b4222-f694-41f0-9685-ff5bb260df2e` Balanced, `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` High performance, `9897998c-92de-4669-853f-b7cd3ecb2790` **AMD Ryzen Balanced (active)**, `a1841308-3541-4fab-bc81-f71556f20b4a` Power saver. Scheme names are localized (Ukrainian) — never match by name, only by GUID |
| powercfg | `/setacvalueindex` and `/change` work **without admin** (verified). `PROCTHROTTLEMAX` AC = 100. `PERFBOOSTMODE` (`be337238-0d82-4146-a960-4f3749d470c7`) is a hidden attribute: not listed by `/q`, but `/setacvalueindex` still sets it. No Energy Saver subgroup exposed. Display-off / sleep / hibernate timeouts on AC are all 0 (never). Hibernate is available (`powercfg /a`) |
| Docker | Docker Desktop 29.7.2 with WSL2 backend (`vmmemWSL` ≈ 8.3 GB). CLI plugin `docker desktop start|stop|restart|status` exists (v0.4.3). Running containers: `p365-kafka`, `p365-zookeeper`, `p365-redis`, `qm-sonar`, `qm-sonar-db`, `gitlab-runner` (the owner's local GitLab CI runner) |
| Heavy background apps | **Wallpaper Engine** (`C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe`, GPU process, supports `wallpaper64.exe -control pause|play`), Chrome (32 procs), Claude, ChatGPT desktop, Slack, Telegram, Postman Agent, Steam (autostart, not running now), OneDrive (autostart), Viber, Teams, EPSDNMON (Epson), Phone Link |
| Local DB services | `MongoDB`, `MySQL80`, `postgresql-x64-18` (all Automatic, running) |
| Other services | `WSearch` running, `SysMain` running, `DiagTrack` running, `Spooler` running, `com.docker.service` stopped/manual |
| Shell user | not elevated. Admin is needed only for: NVML power limit, start/stop of Windows services |
| Toolchain | no system-wide dotnet. .NET 10 SDK is installed **per-user** at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (not on PATH). Set `DOTNET_ROOT` and prepend that dir to `PATH` in every shell call |

## 3. Modes and levers (defaults)

Mode names are situations, not speeds: **Grid** (grid power, nothing changed), **Station** (working from the
power station, comfortable savings), **Reserve** (station nearly empty, maximum savings).

| Lever | Grid | Station | Reserve |
|---|---|---|---|
| Power scheme | restore baseline scheme GUID (currently AMD Ryzen Balanced) | activate scheme **"PowerMode Station"** (duplicate of Balanced `381b4222…`, fixed GUID `7d0a5e01-0000-4000-8000-00504d535441`): boost off (`PERFBOOSTMODE`=0), `PROCTHROTTLEMAX` 80, `PROCTHROTTLEMIN` 5, display off 5 min (`VIDEOIDLE` 300), sleep never (`STANDBYIDLE` 0), hibernate 30 min (`HIBERNATEIDLE` 1800) | activate **"PowerMode Reserve"** (duplicate of Power saver `a1841308…`, fixed GUID `7d0a5e02-0000-4000-8000-00504d525356`): boost off, max 50, min 5, display off 2 min, sleep never, hibernate 10 min |
| Monitor brightness (DDC/CI, every physical monitor) | restore snapshot (80) | 35 | 10 |
| Refresh rate (every display that offers 60 Hz at its current resolution) | restore snapshot mode (199 Hz) | 60 Hz | 60 Hz |
| GPU power limit (NVML, **admin**) | restore default limit (120 W) | 90 W | 60 W (= min limit; always clamp to `[min_limit, max_limit]`) |
| Wallpaper Engine | `-control play` if it was playing | `-control pause` | `-control pause` |
| Docker Desktop | `docker desktop start`, wait for the engine (poll `docker info`, up to 120 s), then `docker start` every container that was running in the snapshot and is not running now | `docker desktop stop` — **unless the per-mode option "Keep Docker running" is on** (the owner needs it for local CI sometimes; default off) | `docker desktop stop` |
| Apps closed (process names; exe path + args captured so Grid can relaunch) | relaunch what the snapshot closed (Steam with `-silent`, OneDrive with `/background`) | `steam`, `EPSDNMON` | Station list + `ChatGPT`, `OneDrive`, `Viber`, `ms-teams`, `Postman Agent` |
| Efficiency mode (EcoQoS via `SetProcessInformation(ProcessPowerThrottling)` + `IDLE_PRIORITY_CLASS`) | clear on every process it was set on | — | `Slack`, `Telegram`, `msedgewebview2`, `PhoneExperienceHost`, `CrossDeviceService` |
| Windows services stopped (**admin**; start type untouched; restarted by Grid/Station if the snapshot had them running) | restore | `WSearch` | `WSearch`, `SysMain`, `DiagTrack`, `Spooler`, `MongoDB`, `MySQL80`, `postgresql-x64-18` |

Semantics:
- **Target-state model.** Each mode is a desired state per lever. Applying a mode computes the diff from
  the current state and applies it, levers in parallel where independent (Docker is slow: 10–30 s).
- **Snapshot ("baseline")** is captured once, when leaving Grid (Grid→Station or Grid→Reserve): active
  scheme GUID, brightness/contrast per monitor, display mode per display, GPU power limit + default
  limit, Wallpaper Engine playing?, running container names, running processes from the union of the
  kill lists with exe paths + args, service states for the union of the service lists. Persisted to
  `state.json` immediately. Station↔Reserve does **not** re-snapshot. Grid restores the snapshot lever by
  lever, then clears it. "Restore" levers (Docker when kept, services, apps) in Station mean "as in the
  snapshot".
- **Idempotent re-apply**: on app start, if the persisted mode is not Grid, re-apply it (survives a
  reboot or crash during a blackout — the power scheme survives anyway, the GPU limit does not). Also
  re-apply on resume from hibernate/sleep (`SystemEvents.PowerModeChanged` Resume) and on
  `SystemEvents.DisplaySettingsChanged` only for brightness/refresh (debounced, and only if the value
  drifted from the target; never fight the user more than once per minute). Refresh rates within 1 Hz
  are the same rate (60 Hz reads back as 59 Hz), for "already set", drift and the live column.
- **Brightness after a mode change.** The monitor re-syncs for 1–3 s after a new display mode and does
  not answer DDC/CI: brightness runs after the refresh rate, waits 2 s when the refresh rate changed in
  the same apply, then retries up to 6 times 1 s apart (re-enumerating the monitors every time) before it
  reports the failure. A switch that still left brightness failed (also an unfinished Grid restore) gets
  one more display check 2 s after the switch.
- **Partial failure** is normal (not elevated, monitor asleep, Docker not installed): the mode is still
  "applied"; the failed lever's row shows ⚠ with the reason; a "Restart as administrator" button appears
  when the only reason is elevation.
- Never change settings the mode does not own (e.g. do not touch the user's own schemes; the two
  PowerMode schemes are created once with `powercfg /duplicatescheme <src> <guid>` and then re-configured
  on every apply so config edits take effect).

## 4. UI

Tray-only app, no taskbar window. Follow the Windows theme (light/dark) with WPF's Fluent theme
(`ThemeMode="System"` in .NET 10); define the palette for both themes and keep contrast >= 4.5:1.

**Tray icon** — crisp at 100/125/150/200 % DPI, drawn in code (Segoe Fluent Icons glyph or geometry →
`System.Drawing.Icon`; no bitmap assets): Grid = plug glyph, neutral; Station = battery glyph, amber
`#F2B83C`; Reserve = low-battery glyph, red `#FF6B6B`. While applying: same glyph with a small spinner
dot. Tooltip: `PowerMode · Station · GPU 9 W`.
Left click toggles the flyout. Right click: `Grid` / `Station` / `Reserve` (radio, current checked),
separator, `Open`, `Start with Windows` (check), `Open config`, separator, `Switch to Grid and quit`,
`Quit (keep mode)`.

**Flyout** — borderless window 380 px wide, height to content (max 600), 8 px DWM rounded corners,
1 px border, anchored at the bottom-right of the work area (12 px margin), hidden on `Deactivated`/Esc,
shown by tray left click, hotkey, or `PowerMode.exe --open`. Layout top→bottom:

1. Header row: title `PowerMode` (20 px semibold), right: gear button (opens the Apps settings page), close ×.
2. Segmented mode selector: three equal buttons 44 px tall, icon + label (`Grid`, `Station`, `Reserve`).
   Selected: filled with the mode color at 18 % + 1 px border in the mode color + semibold label;
   others: transparent, hover fill. Disabled (greyed) while a switch is in progress. Keyboard 1/2/3.
3. Status line (13 px, muted): `Station · since 14:32` or `Applying Station…` or
   `Station · 2 levers failed` (amber). Nothing fabricated: no total-watts estimate (the station's own
   display shows total draw; say so in README).
4. Lever table — one named row per lever, in this fixed order: `CPU`, `GPU`, `Monitor`, `Display`,
   `Docker`, `Apps`, `Efficiency`, `Services`. Row = 3 columns: lever name (muted, 12 px, 84 px wide) ·
   what this mode set, one fact per line, full names, never "+N" (e.g. `Boost off · max 80 %`,
   `Limit 90 W`, `35 % · 60 Hz`, `Off after 5 min · Hibernate 30 min`, `Stopped · was 6 containers`,
   `Closed: Steam, EPSDNMON`, `Slack, Telegram, …` listed in full and wrapped, `Search paused`) · right:
   live value when measurable — CPU watts are NOT measurable without a kernel driver, so CPU shows
   `3.1 GHz · 12 %` from performance counters (`Processor Information(_Total)\% Processor Performance`
   × max MHz, `% Processor Utility`), GPU `9 W` from NVML, Monitor `35 % · 60 Hz` as read back, Docker
   `engine stopped` / `6 running` — and a 16 px status glyph: ✓ (green) this row's fact holds now (set,
   already set, or not used in this mode; no glyph when the row has no result yet), ⟳ spinning while
   applying, ⚠ (amber) failed with the reason as tooltip **and** as a second line in the row (the owner
   judges by look — the reason must be visible). Rows 40 px min, 1 px dividers, no hover-only controls.
   The Docker row carries one toggle on the right (`Keep running` switch), visible only in Station and
   only when the config stops Docker there; with `stopDocker: false` the row says `Kept running (config)`.
5. Footer: `Ctrl+Alt+1 · 2 · 3 switch modes` (muted) · right: `Start with Windows` toggle.
6. When not elevated and a lever needed it: a one-line banner under the status line:
   `GPU limit and services need administrator rights.` with a `Restart as administrator` button
   (relaunch self with the `runas` verb, same arguments, then exit).

**Apps settings page** — the gear replaces the lever table with the page `Apps` inside the same flyout
(header: back arrow · `Apps` · close ×; Esc goes back first; keys 1/2/3 do not switch modes here). One row
per app, three columns: the app (friendly name, process file in small muted text below, e.g.
`Postman Agent` / `Postman Agent.exe`), **Station**, **Reserve**. Each mode cell is a compact choice box
`Keep` / `Efficiency` / `Close` (same control everywhere, keyboard accessible, nothing hover-only).
`Efficiency` = the mode's `efficiencyApps`, `Close` = its `closeApps`; an app is in at most one of the two
per mode. A first fixed row `Wallpaper Engine` offers `Keep` / `Pause` (= `pauseWallpaperEngine`).
- Rows: every name in any `closeApps`/`efficiencyApps`, plus processes of the user's session that have a
  visible top-level window or are in a catalogue of common background apps (Steam, Discord, Spotify,
  Telegram, Slack, Viber, Teams, ChatGPT, OneDrive, Postman Agent, EPSDNMON, Epic Games Launcher,
  Battle.net, Chrome, Edge, Edge WebView2, Phone Link, Cross Device, Claude, VS Code, IntelliJ IDEA,
  Rider, WebStorm, Zoom), never system processes (image under the Windows folder, shell processes,
  PowerMode itself); plus names typed into `Add process…` (no `.exe`, no path; `.exe` is dropped).
  Order: rows with a non-Keep choice first, then alphabetical; the order stays while the page is open.
- Only the app list scrolls (8 rows), so the page stays within 600 px. At the bottom: `Open config.json`
  for everything the page does not cover (services, levers, hotkeys).
- Every change is saved at once to config.json: only `closeApps`, `efficiencyApps` and
  `pauseWallpaperEngine` of that mode are rewritten (other keys stay; comments do not survive an edit),
  through a temp file and a rename; the watcher does not reload our own write. A file that is invalid on
  disk is never overwritten: the choice goes back and the page says so.
- A change for the **current** saving mode applies at once through the switcher (after any running
  switch): to `Keep` → relaunch it if PowerMode closed it, clear efficiency mode if PowerMode set it;
  to `Close` → close it now and record it, so Grid relaunches it; to `Efficiency` → efficiency mode on it
  now (an app PowerMode closed stays closed until the next apply). Wallpaper Engine likewise. A change for
  the other mode takes effect on its next apply. The lever table shows the new lists immediately.

**Application icon** — `src/PowerMode.App/Assets/PowerMode.ico` (16–256 px): the plug glyph in the accent
colour on a rounded dark tile; exe icon and flyout window icon. Generated by `tools/make-icon.ps1`
(`PowerMode.exe --make-icon <file>`) and checked in.

**Toast** on every switch (tray `ShowBalloonTip`, 4 s): `Station mode` / `Boost off · max 80 % · monitor
35 % @ 60 Hz · GPU 90 W · Docker stopped`.

**Hotkeys**: `Ctrl+Alt+1/2/3` → Grid/Station/Reserve (RegisterHotKey, configurable strings in config).

## 5. CLI (same exe, single instance)

- `PowerMode.exe` — start the tray app (second instance forwards to the first over a named pipe and exits).
- `--quit` — ask the running instance to quit (keeps the mode; a running switch finishes first) and wait until it has gone.
- `--mode grid|station|reserve` — apply (through the running instance if any, else apply and exit).
- `--status` — print the lever table as text.
- `--dry-run --mode X` — print what would change, change nothing.
- `--render-states <dir>` — render the flyout to PNG for review: `grid.png`, `grid-restored.png` (Grid
  after a blackout, restoring the snapshot), `station.png`, `reserve.png`, `station-applying.png` (CPU
  and Docker rows ⟳), `station-failed.png` and `reserve-failed.png` (GPU + Services ⚠ "needs
  administrator", banner visible), `reserve-light.png` (light theme), `settings-apps.png` and
  `settings-apps-light.png` (the Apps settings page), plus `tray-*.png` at 32 px for the
  three modes. Uses fake lever data; touches nothing. Exits with an error naming any state whose content
  is taller than the 600 px flyout. Exit when done.
- `--install-autostart` / `--uninstall-autostart` — create/delete the scheduled task `PowerMode`
  (trigger: at logon of this user, run level Highest, no battery conditions, no time limit, start in
  the exe folder). Self-elevates with `runas` if needed.
- `--make-icon <file.ico>` — developer tool: write the application icon (see `tools/make-icon.ps1`).

## 6. Storage, config, logging

`%LOCALAPPDATA%\PowerMode\config.json` (created with the defaults above on first run; `FileSystemWatcher`
reload, invalid JSON → keep last good + log), `state.json` (current mode, snapshot, per-mode options
like Keep Docker, timestamps), `logs\powermode-YYYYMMDD.log` (plain text, every lever action with
result and duration, 14 days kept). Config schema is documented in README (hotkeys, per-mode lever
values, kill/efficiency/service lists, docker container exclusions, flyout anchor).

## 7. Tech

- .NET 10, C# 14, `net10.0-windows`, WPF + `UseWindowsForms` (for `NotifyIcon`). No third-party runtime
  dependencies except optionally `CommunityToolkit.Mvvm`. Tests: xUnit.
- Solution layout: `src/PowerMode.Core` (models, config, snapshot, planner, parsers, lever interfaces —
  no Windows API), `src/PowerMode.App` (WPF app, tray, flyout, CLI, lever implementations under
  `Levers/`: `PowercfgLever`, `DdcBrightnessLever`, `DisplayModeLever`, `NvmlGpuLever`, `DockerLever`,
  `ProcessLever`, `EcoQosLever`, `ServiceLever`, `WallpaperEngineLever`, `AutostartTask`),
  `tests/PowerMode.Core.Tests`. `build.ps1` = restore, build, test, publish → `publish\PowerMode.exe`
  (self-contained single file, win-x64, `IncludeNativeLibrariesForSelfExtract`, ReadyToRun), prints the
  exe size. Manifest `asInvoker` (never `requireAdministrator` — no UAC on every start; elevation comes
  from the scheduled task or the button).
- `install.ps1` = `build.ps1`, then copy the exe to `%LOCALAPPDATA%\Programs\PowerMode\PowerMode.exe` (asking a PowerMode
  that runs from there to quit with `--quit`), create/update the desktop shortcut `PowerMode.lnk` in the real Desktop folder
  (`[Environment]::GetFolderPath('Desktop')`; OneDrive-redirected here) and point an existing `PowerMode` task
  at the installed exe. Starts the app only with `-Start`.
- Windows API via P/Invoke: `dxva2` (physical monitors, `Get/SetMonitorBrightness`), `user32`
  (`EnumDisplayDevices`, `EnumDisplaySettingsEx`, `ChangeDisplaySettingsEx` with `CDS_UPDATEREGISTRY`,
  `RegisterHotKey`), `nvml.dll` (`nvmlInit_v2`, `nvmlDeviceGetHandleByIndex_v2`, `nvmlDeviceGetPowerUsage`,
  `nvmlDeviceGetPowerManagementLimit`, `nvmlDeviceGetPowerManagementLimitConstraints`,
  `nvmlDeviceGetPowerManagementDefaultLimit`, `nvmlDeviceSetPowerManagementLimit`), `kernel32`
  (`SetProcessInformation` with `PROCESS_POWER_THROTTLING_STATE`), `ServiceController`, `dwmapi`
  (corner preference). `powercfg.exe` and `docker.exe` via `Process` with timeouts (powercfg 10 s,
  docker 120 s) and captured output; parse GUIDs, never localized names.
- Unit tests (Core, target >= 80 % lines): planner target state per mode; transition semantics
  (Grid→Station snapshots, Station→Reserve does not, →Grid restores and clears, re-apply is idempotent);
  snapshot JSON round trip; config defaults + partial config merge + invalid config kept-last-good;
  powercfg output parsing (`/getactivescheme`, `/list`, `/q` index lines); docker `ps` parsing; hotkey
  string parsing; needs-admin gating; render-state fixtures.
- Code style: nullable on, warnings as errors, no dead code, no repeated magic strings (constants),
  every lever logs start/result/duration, every external call has a timeout and never throws out of
  `ApplyAsync` (returns `LeverResult { Ok | Failed(reason) | Skipped(reason) }`).

## 8. Rules for whoever implements this on this machine

- **Do not change this PC's state while developing.** Allowed: read-only probes (DDC read, NVML read,
  `powercfg /q`, `/getactivescheme`, `docker ps`, `docker desktop status`, process/service listing,
  `EnumDisplaySettings`), creating the two PowerMode schemes with `/duplicatescheme` and setting values
  **on those two schemes only**, and `--render-states`. **Not allowed**: `powercfg /setactive`, any
  write to the active/other schemes, brightness/contrast writes, `ChangeDisplaySettingsEx`, NVML set,
  `docker stop/start/desktop stop`, killing or relaunching processes, EcoQoS on real processes, service
  start/stop, creating the scheduled task. The owner is working at this PC right now; the real switch
  test happens after review, announced.
- Commits: English, imperative, small; branch `master`; end each message with
  `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. No remote yet.
- Build with the exact tree you commit; `build.ps1` must pass (restore, build, tests, publish).
