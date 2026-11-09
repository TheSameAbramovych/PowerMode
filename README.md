# PowerMode

A Windows 11 tray app that switches this PC between three power-consumption modes, for working from a
portable power station during blackouts. One click, one hotkey or one command switches mode. Grid puts back
exactly what was there before.

| Mode | When |
|---|---|
| **Grid** | On grid power. Nothing is reduced; everything PowerMode changed is restored. |
| **Station** | On the power station. Comfortable savings. |
| **Reserve** | The station is nearly empty. Maximum savings. |

## What each mode does (defaults)

| Lever | Grid | Station | Reserve |
|---|---|---|---|
| Power scheme | your scheme before the switch (by GUID) | **PowerMode Station** (copy of Balanced, `7d0a5e01-…`): boost off, max CPU 80 %, min 5 %, display off 5 min, sleep never, hibernate 30 min | **PowerMode Reserve** (copy of Power saver, `7d0a5e02-…`): boost off, max 50 %, min 5 %, display off 2 min, sleep never, hibernate 10 min |
| Monitor brightness (DDC/CI on every monitor; a laptop panel through WMI) | restored | 35 % | 10 % |
| Other displays (all but the primary) | turned back on | on | **off** (only the primary display stays on); skipped with one display |
| Refresh rate (each display; one without the rate gets its lowest rate from 50 Hz) | restored | 60 Hz | 60 Hz |
| GPU power limit (NVML, **admin**) | restored | 90 W | 60 W (clamped to the card's min/max) |
| Wallpaper Engine | play again if it was running | pause | pause |
| Docker Desktop | started again, then every container that was running | stopped, unless **Keep running** is on | stopped |
| Apps closed | relaunched (Steam `-silent`, OneDrive `/background`) | Steam, EPSDNMON | Station + ChatGPT, OneDrive, Viber, Teams, Postman Agent |
| Efficiency mode (EcoQoS + idle priority) | cleared | — | Slack, Telegram, Edge WebView2, Phone Link, Cross Device |
| Windows services (**admin**; start type never changed) | restarted if they ran before | Windows Search | Search, SysMain, DiagTrack, Spooler, MongoDB, MySQL80, postgresql-x64-18 |

How it works:

- **Snapshot.** Leaving Grid records the baseline (active scheme, brightness/contrast, display modes, GPU
  limit, Wallpaper Engine running, running containers, running apps from the close lists with path and
  arguments, state of the listed services) in `state.json` before anything changes. Station ↔ Reserve does not
  take a new snapshot. Grid restores it lever by lever and then deletes it; if a restore fails (for example the
  GPU limit without admin rights) the failed parts are kept so Grid can be retried, and a retry touches only
  those. A failure that cannot go away (Docker uninstalled, your scheme deleted) is not retried.
- **A baseline is never guessed.** If a lever cannot be read when the snapshot is taken (a DDC/CI read fails,
  Docker does not answer), it is read again right before the mode applies; if it still cannot be read, that lever
  is left as it is ("Baseline not captured, not reducing"), so Grid has nothing to undo. The power scheme is the
  exception: the mode's scheme is still activated, and Grid then activates `fallbackScheme`. A PowerMode scheme
  is never recorded as the baseline.
- **Lists edited in a saving mode.** An app or service added to a close or service list while in Station or
  Reserve is recorded on the next apply, so Grid brings it back too.
- **Target state.** Every apply computes the difference between the machine and the mode's target and only
  changes what differs, levers in parallel. Re-applying a mode is harmless.
- **Re-apply.** On start (a reboot loses the GPU limit), on resume from sleep/hibernate, and when Windows
  changes display settings (brightness/refresh only, at most once a minute).
- **Partial failure is normal.** The mode still counts as applied; the failed row shows ⚠ and the reason. When
  the reason is missing administrator rights a **Restart as administrator** button appears.
- Only the two PowerMode schemes are ever written; your own schemes are only re-activated.
- Apps are relaunched only if PowerMode itself closed them; an app you quit yourself stays closed.

## Using it

- **Tray icon**: always in colour, so you can see that PowerMode runs and in which mode: blue plug (Grid),
  amber battery with a dot (Station), red low battery with a dot (Reserve); a small dot circles while a switch
  runs. The tooltip shows the mode and the estimated total (`≈ 142 W`), also while a switch runs. Left click opens or closes the flyout;
  right click has the mode menu, Open, Start with Windows, Open config, Create desktop shortcut, Switch to Grid
  and quit, Quit (keep mode). Windows 11 puts new tray icons into the hidden overflow; on its **first start from a folder**
  PowerMode moves its own icon onto the taskbar, once (you can hide it again and it stays hidden). Where
  Windows has no such setting, a one-time balloon says to drag the icon onto the taskbar.
- **Flyout**: mode selector (keys `1` `2` `3`), status line, one row per lever with what the mode set, the live
  value and ✓ / ⟳ / ⚠. The pin in the header keeps it open when you click elsewhere (on by default,
  remembered); it then closes with ×, `Esc` or the tray icon. Unpinned, it hides as soon as it loses focus.
- **Apps settings** (gear in the flyout header): one row per app with a choice for **Station** and
  **Reserve**: `Keep`, `Efficiency` (efficiency mode, the mode's `efficiencyApps`) or `Close` (its
  `closeApps`); plus `Wallpaper Engine`: `Keep` / `Pause`. The list holds every app in those lists, the apps
  running now that have a window or are common background apps (Steam, Discord, Spotify, Telegram, Slack,
  Teams, OneDrive, …; never Windows' own processes), and any process you add by name (`Add process…`,
  without `.exe`). Apps with a setting are listed first. Each change is saved to `config.json` at once; when
  it is for the mode you are in, it also applies at once (an app set to `Close` closes now and Grid starts
  it again; `Keep` starts again what PowerMode closed and clears efficiency mode). `Open config.json` at
  the bottom covers the rest (services, levers, hotkeys). Saving rewrites `config.json` in the app's own
  format: other keys stay as they are, comments do not survive. Each saved change shows `Saved 10:31` next
  to the title. `Esc` (or the back arrow) goes back to the lever table, which already shows the new lists.
- **Levers settings** (the `Levers` tab next to `Apps`): per mode the CPU max state (20–100 %, with the
  estimated CPU watts beside it: Windows caps the CPU by % of its max state, not by watts), CPU boost, the GPU
  limit in watts (clamped to what the card accepts; greyed without an NVIDIA GPU), brightness, refresh rate,
  other displays on/off, display off after, hibernate after, Docker Desktop and Wallpaper Engine. Saved and
  applied like the Apps page. Below: **This PC** (CPU, GPU and its limit range, monitors, Docker, Wallpaper
  Engine, rights, version).
- **Several monitors**: the Monitor row has a line per monitor (`M27Q2 · 35 % · 60 Hz`,
  `T24i-30 · Off`); brightness and refresh rate apply to each, and Reserve turns the other displays off.
- **Shutdown and start**: shutting Windows down in Station or Reserve restores Grid's scheme, displays,
  brightness and GPU limit first (`gridOnShutdown`), and a new start begins in Grid (`startInGrid`).
- **Hotkeys**: `Ctrl+Alt+1` Grid, `Ctrl+Alt+2` Station, `Ctrl+Alt+3` Reserve (configurable).

### Command line

```powershell
.\PowerMode.exe                            # start the tray app (a second start opens the flyout of the first)
.\PowerMode.exe --open                     # open the flyout of the running app
.\PowerMode.exe --quit                     # quit the running app (keeps the mode; waits for a running switch)
.\PowerMode.exe --mode station             # apply through the running app, or apply here and exit
.\PowerMode.exe --dry-run --mode reserve   # print what would change, change nothing
.\PowerMode.exe --status                   # print the lever table
.\PowerMode.exe --render-states .\review   # render the flyout states to PNG with fake data
.\PowerMode.exe --install-autostart        # start at logon with admin rights (asks for UAC once)
.\PowerMode.exe --uninstall-autostart
.\PowerMode.exe --make-icon <file.ico>     # developer tool: write the application icon
```

`PowerMode.exe` is a Windows (GUI) program: in PowerShell its output is complete when piped
(`.\PowerMode.exe --status | Out-String`), in `cmd` use `start /wait PowerMode.exe --status`.

### Build

```powershell
.\build.ps1      # restore, build (warnings as errors), unit tests with coverage, publish
```

Produces `publish\PowerMode.exe` (self-contained single file, win-x64, ReadyToRun, compressed; about 72 MB). It
uses the per-user .NET SDK in `%LOCALAPPDATA%\Microsoft\dotnet` when no `dotnet` is on PATH.

The exe and the flyout use `src\PowerMode.App\Assets\PowerMode.ico` (the plug on a dark tile, 16–256 px). It
is checked in; `tools\make-icon.ps1` regenerates it after a change to the drawing code.

### Install

```powershell
.\install.ps1              # build.ps1, then install, desktop shortcut, autostart task path
.\install.ps1 -NoBuild     # install the existing publish\PowerMode.exe
.\install.ps1 -Start       # ... and start PowerMode afterwards
```

`install.ps1` copies the exe to `%LOCALAPPDATA%\Programs\PowerMode\PowerMode.exe` (a PowerMode running from
there is asked to quit with `--quit` first and stopped only if it does not end; the script says so; the PC
keeps its mode), creates or updates `PowerMode.lnk` on
your desktop (the real Desktop folder, also when OneDrive redirects it) with the app icon, and, when **Start
with Windows** is on, points the `PowerMode` task at the installed exe (one UAC prompt when that needs
administrator rights). It does not start the app unless you pass `-Start`; config, state and logs in
`%LOCALAPPDATA%\PowerMode` are not touched.

### Sharing with friends

Copy `PowerMode.exe` anywhere (it is one self-contained file), run it, and use the tray menu: **Create
desktop shortcut** and **Start with Windows**. Nothing else is needed. On its first start on a PC, PowerMode
takes the power scheme active then as the scheme Grid falls back to, tunes the CPU part of the estimate to that
CPU, and adapts to what the PC has: without an NVIDIA GPU the GPU lever is skipped (`No NVIDIA GPU`), without
Docker Desktop the Docker lever is skipped, without Wallpaper Engine its lines are hidden, a laptop's built-in
panel is dimmed through WMI. The defaults of the close and service lists are this PC's; they only act on what
exists, and the Apps and Levers pages change them.

### Development notes

- **Claude desktop sessions see other files.** The Claude desktop app is an MSIX package: every process started
  from it (its shell, `dotnet`, `PowerMode.exe` runs) has its writes to `%LOCALAPPDATA%` redirected to
  `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\`. So `%LOCALAPPDATA%\PowerMode` seen from
  such a shell is a private copy (its own config, state and log), never the one of a PowerMode started from
  Explorer; the per-user .NET SDK used for building lives only in that copy too. To read the real files, start
  a process outside the package (`Invoke-CimMethod -ClassName Win32_Process -MethodName Create`) and let it
  write its output somewhere outside AppData. The start-up log line names the data folder each process uses.
- Check `Get-Process PowerMode` before `--quit`, `--open`, `--mode` or `install.ps1`: a running instance you
  did not start is someone else's.
- Run only `publish\PowerMode.exe` (self-contained); the framework-dependent `bin\` build needs .NET where the
  shell cannot see it and shows an error dialog.

### Autostart

The GPU limit and Windows services need administrator rights. The app never asks for them on every start
(`asInvoker` manifest). Instead, **Start with Windows** (flyout footer, tray menu or `--install-autostart`)
creates the scheduled task `PowerMode`: at logon of this user, *run with highest privileges*, no battery
conditions, no time limit, started in the exe folder. Creating it asks for UAC once. Without the task, use
**Restart as administrator** when a lever needs it. The task runs PowerMode at normal priority (task priority 4);
a task installed by an earlier build ran it below normal (priority 7) and keeps that until you run
`PowerMode.exe --install-autostart` again (it replaces the task).

When PowerMode runs elevated, apps it relaunches (Steam, OneDrive, …) and Docker Desktop are started through
the desktop shell, so they run as you, not as administrator.

## How much is the PC drawing?

The card at the top of the flyout shows the **estimated** total, e.g. `≈ 142 W`, with `▼ 61 W vs Grid` in
Station or Reserve, a 10-minute line, and the parts CPU, GPU, Monitor and Other. The tray tooltip shows it too
(`PowerMode · Station · ≈ 142 W`), and `--status` prints it.

**Why an estimate.** The PC's total draw cannot be measured on this desktop: there is no kernel driver and no
CPU package power. The **power station's own display is the reference**. PowerMode measures what it can and
estimates the rest, every 2 s:

- **GPU**: board power from NVML (measured, shown without `≈`).
- **CPU**: `cpuIdleW + cpuSpanW · min(utility, 1.25) · (GHz / cpuBaseGhz)^cpuExponent`, from % Processor
  Utility and the effective clock.
- **Monitor**: `monitorBaseW + monitorPerPercentW · brightness`, plus `highRefreshExtraW` above 120 Hz.
- **Other**: board, RAM, drives, fans, USB (`otherW`), the power supply's losses, and the calibration.
- Total = `(CPU + GPU + otherW) / psuEfficiency + Monitor + calibration offset`.

**Calibrate** once (link in the card): type what the station's display shows right now; the difference
becomes an offset (−100…+250 W) added to every estimate, saved in state.json, shown as `Calibrated 10:31`.
Recalibrate replaces it, Reset removes it. Do it in a steady moment (not during a switch or a game loading).

**vs Grid**: when you leave Grid, the mean of the last minute is kept; in Station or Reserve the card shows the
difference of the last minute's mean once 30 s have passed since the switch.

## Files

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\PowerMode\config.json` | settings; created with the defaults on first run, reloaded when saved; an invalid file is ignored (the last good config stays, the log says why). The Apps settings page writes the app lists here (atomically; a file that is invalid on disk is never overwritten) |
| `%LOCALAPPDATA%\PowerMode\state.json` | current mode, since when, snapshot, per-mode options (Keep Docker), last lever results, apps closed by PowerMode, processes in efficiency mode. Written through a flushed temp file; the previous copy is kept as `state.json.bak`. A file that cannot be read is renamed to `state.json.corrupt-<time>` (never deleted) and the backup is used. If both are unusable while a PowerMode scheme is active, PowerMode takes that mode back and Grid later activates `fallbackScheme` |
| `%LOCALAPPDATA%\PowerMode\logs\powermode-YYYYMMDD.log` | every lever action with result and duration; 14 days kept |

## Config reference

Every key is optional: what you leave out keeps its default. Objects merge key by key; lists replace the default
list. `null` for a number means "leave this lever alone in this mode"; `null` for an object, list, text or list
entry makes the file invalid (the last good config stays). Comments and trailing commas are allowed.

| Key | Default | Meaning |
|---|---|---|
| `hotkeys.grid` / `.station` / `.reserve` | `Ctrl+Alt+1` / `2` / `3` | Global hotkeys: modifiers `Ctrl`, `Alt`, `Shift`, `Win` + a key `0`–`9`, `A`–`Z`, `F1`–`F24`, `Num0`–`Num9`. Empty string = no hotkey |
| `flyout.anchor` | `bottomRight` | Corner of the work area: `bottomRight`, `bottomLeft`, `topRight`, `topLeft` |
| `flyout.margin` | `12` | Distance from the work-area edges, px |
| `modes.<station\|reserve>.scheme.id` | `7d0a5e01-…` / `7d0a5e02-…` | GUID of the PowerMode scheme |
| `….scheme.sourceGuid` | Balanced / Power saver | Scheme it is duplicated from (only when it does not exist yet) |
| `….scheme.name` | `PowerMode Station` / `PowerMode Reserve` | Name given when the scheme is created |
| `….scheme.boost` | `false` | Processor boost (`PERFBOOSTMODE` 0 = off, 2 = aggressive) |
| `….scheme.maxProcessorPercent` | `80` / `50` | `PROCTHROTTLEMAX` |
| `….scheme.minProcessorPercent` | `5` | `PROCTHROTTLEMIN` |
| `….scheme.displayOffSeconds` | `300` / `120` | `VIDEOIDLE`, 0 = never |
| `….scheme.sleepSeconds` | `0` | `STANDBYIDLE`, 0 = never |
| `….scheme.hibernateSeconds` | `1800` / `600` | `HIBERNATEIDLE`, 0 = never |
| `modes.<mode>.brightnessPercent` | `35` / `10` | Monitor brightness, 0–100 (`null` = leave) |
| `modes.<mode>.refreshRateHz` | `60` | Refresh rate for displays that offer it (`null` = leave) |
| `modes.<mode>.gpuLimitWatts` | `90` / `60` | GPU power limit, clamped to the card's range (`null` = leave) |
| `modes.<mode>.pauseWallpaperEngine` | `true` | Pause Wallpaper Engine |
| `modes.<mode>.stopDocker` | `true` | Stop Docker Desktop (Station's flyout toggle **Keep running** overrides it) |
| `modes.<mode>.secondaryDisplaysOff` | `false` / `true` | Turn every display but the primary off (Station / Reserve); Grid turns them back on |
| `modes.<mode>.closeApps` | see table above | Process names (without `.exe`) to close |
| `modes.<mode>.efficiencyApps` | `[]` / five names | Process names to put into efficiency mode |
| `modes.<mode>.stopServices` | `["WSearch"]` / seven names | Service names (not display names) to stop |
| `relaunchArguments` | `{"steam": "-silent", "OneDrive": "/background"}` | Arguments used when relaunching an app instead of its captured ones |
| `docker.excludedContainers` | `[]` | Containers Grid never starts again |
| `docker.engineWaitSeconds` | `120` | How long Grid waits for the Docker engine |
| `wallpaperEngine.path` | Steam's `wallpaper64.exe` | Used for `-control pause\|play` (falls back to the running process) |
| `reapplyOnDisplayChange` | `true` | Correct brightness/refresh when Windows changes display settings (at most once a minute) |
| `gridOnShutdown` | `true` | When Windows shuts down or signs out in Station/Reserve, restore Grid's scheme, displays, brightness and GPU limit first (within 5 s); Docker, apps and services stay owed for the next start |
| `startInGrid` | `true` | A start that finds Station or Reserve saved restores Grid instead of applying that mode again (every new start begins without limits); `false` applies the saved mode again |
| `power.cpuIdleW` / `power.cpuSpanW` | `15` / `45` | CPU estimate: watts at idle, and the span up to all cores busy at `cpuBaseGhz` |
| `power.cpuBaseGhz` / `power.cpuExponent` | `3.2` / `2.0` | Base clock and how steeply watts rise with the clock |
| `power.monitorBaseW` / `power.monitorPerPercentW` | `9` / `0.30` | Monitor estimate: watts at 0 % brightness and per brightness percent |
| `power.highRefreshExtraW` | `3` | Monitor extra above 120 Hz |
| `power.otherW` | `40` | Board, RAM, drives, fans, USB |
| `power.psuEfficiency` | `0.88` | Power supply efficiency (0.5–1); everything but the monitor is divided by it |
| `fallbackScheme` | Balanced `381b4222-…` | Scheme Grid activates when the one active before is unknown (its read failed, or the state was lost). Set it to your own scheme, e.g. AMD Ryzen Balanced `9897998c-92de-4669-853f-b7cd3ecb2790`; must not be a PowerMode scheme |

Example — dimmer Station, keep Postman open in Reserve:

```jsonc
{
  "modes": {
    "station": { "brightnessPercent": 25 },
    "reserve": { "closeApps": ["steam", "EPSDNMON", "ChatGPT", "OneDrive", "Viber", "ms-teams"] }
  }
}
```

## Known limitations

- **Total watts are an estimate** (see above): CPU power is not measurable without a kernel driver; calibrate against the station's display.
- **Wallpaper Engine's pause state cannot be read**, so "playing" means "was running" and a saving mode sends
  `pause` on every apply.
- **Efficiency mode** is set on the processes running at apply time; a process started later (a new WebView2
  window) is covered on the next apply (resume, re-select the mode). Some processes refuse it without admin.
- **Docker**: `docker desktop stop/start` take 10–30 s; the Docker row spins meanwhile.
- **DDC/CI**: a monitor in standby, or one with DDC/CI disabled in its menu, cannot be dimmed; the Monitor row
  then shows the reason. Brightness is applied after the refresh-rate change.
- **Config edits** take effect on the next apply (select the mode again); the scheme `name` is only used when
  the scheme is created.
- Closing an app first asks its window to close, then ends it after 3 s (Steam is ended this way, not via its
  own shutdown command).
- The CPU and Display rows both come from the power-scheme lever, so they spin and fail together.
