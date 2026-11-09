# PowerMode 1.0.0

The first public release of PowerMode, a Windows 11 tray app that switches a desktop PC between three power modes
for running from a portable power station or a UPS: **Grid** (everything as you left it), **Station** (comfortable
savings) and **Reserve** (maximum savings).

## Features

- One click in the flyout, `Ctrl+Alt+1/2/3`, the tray menu or `PowerMode.exe --mode <grid|station|reserve>`.
- Levers: PowerMode's own power schemes (boost off, CPU maximum, display and hibernate timeouts), monitor brightness
  over DDC/CI (laptop panels through Windows), refresh rate, secondary displays off, NVIDIA GPU power limit,
  Wallpaper Engine pause, Docker Desktop stop and start, closing or throttling (efficiency mode) background apps,
  stopping heavy Windows services.
- Exact restore: Grid puts back the recorded baseline lever by lever and retries what failed; apps PowerMode closed
  are relaunched, apps you quit yourself are not.
- Power estimate card: total watts, the saving against Grid, a 10-minute line and the CPU, GPU, monitor and other
  parts (the parts add up to the total), calibrated once against the station's display.
- Settings pages in the flyout for every lever and every app per mode; everything also in `config.json`.
- Several monitors, safe shutdown (Grid's scheme, displays and GPU limit are restored at Windows shutdown) and a
  start without limits.
- Tray icon coloured by mode for dark and light taskbars; the flyout can stay pinned.
- Command line: `--status` (lever table, estimate, active power scheme), `--dry-run`, `--open`, `--quit`,
  `--install-autostart`, `--uninstall-autostart`, `--version`.

## Requirements

- Windows 11, 64-bit. The exe is self-contained: no .NET install is needed.
- Optional: an NVIDIA GPU (GPU limit and measured GPU watts), monitors with DDC/CI enabled, Docker Desktop,
  Wallpaper Engine.
- Administrator rights only for the GPU limit and Windows services (**Start with Windows** sets that up once).

## Download

| File | Size | SHA-256 |
|---|---|---|
| `PowerMode.exe` | 76,278,364 bytes | `BCAA18A00F93A39D7845B8887E3E022273C6C0961BA0A0C186B9C719EDF5402E` |

Check it in PowerShell:

```powershell
Get-FileHash .\PowerMode.exe -Algorithm SHA256
```

## Known limitations

- Total watts are an estimate (CPU power is not measurable without a kernel driver); calibrate against the station.
- Wallpaper Engine's pause state cannot be read, so a saving mode sends `pause` on every apply.
- A monitor with DDC/CI disabled or in standby cannot be dimmed.
