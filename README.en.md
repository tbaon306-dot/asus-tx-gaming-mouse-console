# ASUS TX GAMING MOUSE Console

[![build](https://github.com/tbaon306-dot/asus-tx-gaming-mouse-console/actions/workflows/build.yml/badge.svg)](https://github.com/tbaon306-dot/asus-tx-gaming-mouse-console/actions/workflows/build.yml)

A native 85 KB Windows utility that controls the ASUS **TX GAMING MOUSE (P310, `VID_0B05&PID_1A8D`)** — DPI, button remapping and onboard profiles — **without running Armoury Crate**.

> Not affiliated with ASUS. This repository contains no ASUS binaries or artwork.
> At runtime the app loads `ArmouryMsSDK.dll` from *your own* Armoury Crate installation.

## Why

Armoury Crate spins up SYSTEM services and a stack of daemons just to change a DPI setting. The mouse itself only needs a handful of HID commands.

This project extracts that one feature into a window you can open, use and close.

## The interesting part: the SDK cannot coexist with a message pump

`ArmouryMsSDK.dll` exposes `startService` / `ExecuteFunction` / `freeBuffer`. If the calling process has a Win32 message loop — even a hidden WinForms window — `ExecuteFunction` dies with `0xC0000409` (fail-fast) often enough to be unusable: silent process death, no stack.

The same calls from a console process never crash. Measured side by side: console 6/6 OK, GUI 3/6 crashed.

So the architecture splits in two:

- the **UI process** never loads the DLL;
- a **`--host` child process** (no window, no pump) owns the SDK and is driven over stdin/stdout.

Child crash → respawn + one retry, plus a 25 s watchdog. Cold-start crash rate went from ~30% to **0/10**.

## Features

- Overview: model, firmware, receiver version, active profile and DPI; detects the mouse's power-save sleep (which makes the SDK return fake `firmware=0` readings)
- Performance: 4 DPI levels (100–12000), polling rate, angle snapping, optional onboard save
- Buttons: remap all 8 physical buttons — mouse function / key combo (recordable) / media key
- Profiles: switch, restore to factory
- Logging of every SDK call

## Requirements

- Windows + .NET Framework 4.x
- Armoury Crate (or at least its ArmouryDevice component) installed **once**, to obtain `ArmouryMsSDK.dll`.
  Usually `C:\Program Files (x86)\ASUS\ArmouryDevice\dll\MouseSDK` — the app finds it automatically.

```powershell
./tools/build.ps1                 # -> build/ASUSMouseConsole.exe
./tools/fetch-sdk.ps1 -Destination ./MouseSDK   # optional: copy the SDK next to the app
```

> If PowerShell refuses to run the scripts (`execution policy`), use:
> `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1`

## Known limitations (all measured)

- **Polling rate is effectively fixed at ~125 Hz** over 2.4G (656 reports / 7.000 s, median interval 8.00 ms). The device accepts writes and ignores them; Armoury Crate can't change it either.
- The mouse sleeps after a few idle minutes and the SDK then reports garbage; the UI detects and explains this.
- **Onboard save only persists when `asus_framework` is not running** — otherwise the call returns success and nothing is stored. Run `tools/stop-ac.cmd` (self-elevating) first.
- Only the P310 is supported.

## Docs

- [docs/PROTOCOL.md](docs/PROTOCOL.md) — the XML protocol
- [docs/FINDINGS.md](docs/FINDINGS.md) — reverse-engineering notes and measurements

## License

MIT. No warranty. Remapping every button away from left-click will leave you with a mouse you cannot click with — the UI guards against that once, but you have been warned.