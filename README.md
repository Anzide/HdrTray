<h1 align="center">
  <img src="docs/logo.png" width="96" alt="HDR Tray icon"><br>
  HDR Tray
</h1>
<p align="center"><b>Toggle Windows HDR with a single click on tray icon.</b></p>

<p align="center">
  <a href="https://github.com/Anzide/HdrTray/releases/latest"><img src="https://img.shields.io/github/v/release/Anzide/HdrTray?label=download&color=ff8a1f" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Anzide/HdrTray" alt="MIT license"></a>
</p>

<div align="center">

| <img src="docs/state-on.png" width="102" alt="Solid amber HDR icon"> | <img src="docs/state-off.png" width="102" alt="Outlined HDR icon"> | <img src="docs/state-paused.png" width="102" alt="Amber outlined HDR icon"> | <img src="docs/state-unavailable.png" width="102" alt="Faded outlined HDR icon"> |
| :---: | :---: | :---: | :---: |
| On | Off | Paused by Windows | No HDR display |

</div>

## Usage

- **Left-click** the icon to turn HDR on or off.
- **Right-click** for per-display switches, display settings and start with Windows.

## Command line

```
HdrTray.exe --toggle
HdrTray.exe --on
HdrTray.exe --off
```

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```
