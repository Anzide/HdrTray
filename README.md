<h1 align="center">
  <img src="docs/logo.png" width="96" alt="HDR Tray icon"><br>
  HDR Tray
</h1>

<p align="center"><b>Toggle Windows HDR with a single click on a tray icon.</b></p>

<p align="center">
  <a href="https://github.com/Anzide/HdrTray/releases/latest"><img src="https://img.shields.io/github/v/release/Anzide/HdrTray?label=download&color=ff8a1f" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4" alt="Windows 10 | 11">
  <img src="https://img.shields.io/badge/size-55%20KB-success" alt="55 KB">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Anzide/HdrTray" alt="MIT license"></a>
</p>

<p align="center">
  <img src="docs/toggle.png" width="260" alt="Outlined HDR icon (off) becomes a solid amber HDR icon (on)">
</p>

## ✨ Features

- 🖱️ **One click** — left-click the tray icon to switch HDR on or off.
- 🖥️ **Multi-monitor** — toggle every HDR display at once, or each one separately from the menu.
- 🔄 **Always in sync** — follows changes made in Settings or with <kbd>Win</kbd> + <kbd>Alt</kbd> + <kbd>B</kbd> within about 2 seconds.
- 🎨 **Fits the taskbar** — the icon is drawn at your DPI and follows the light / dark taskbar.
- ⌨️ **Scriptable** — `--toggle`, `--on` and `--off` for shortcuts and hotkeys.
- 🪶 **Tiny and portable** — one 55 KB exe. No installer, no admin rights, no background service.
- 🌐 **English and Chinese** — follows the Windows display language.

## 🚦 Icon states

Each icon is shown on a dark and on a light taskbar.

| Icon | Meaning |
| :---: | --- |
| <img src="docs/state-on.png" width="102" alt="Solid amber HDR icon"> | HDR is **on** |
| <img src="docs/state-off.png" width="102" alt="Outlined HDR icon"> | HDR is **off** |
| <img src="docs/state-paused.png" width="102" alt="Amber outlined HDR icon"> | HDR is set to on, but **paused** by Windows (for example on battery) |
| <img src="docs/state-unavailable.png" width="102" alt="Faded outlined HDR icon"> | **No HDR-capable display** is connected |

## 📦 Download

Get `HdrTray.exe` from the [latest release](https://github.com/Anzide/HdrTray/releases/latest) and run it. That is all.

It needs Windows 10 or 11 and .NET Framework 4.8, which Windows already includes.

> [!NOTE]
> Windows 11 puts new tray icons in the `^` overflow area. Drag the HDR icon onto the taskbar to keep it visible.

## 🖱️ Usage

| Action | Result |
| --- | --- |
| **Left-click** | Toggle HDR. With several displays: if any has HDR on, all are turned off; otherwise all are turned on. |
| **Right-click** | Open the menu. |

The menu contains:

- **Turn HDR on / off**
- **One entry per display**, when more than one is connected
- **Display settings…**
- **Start with Windows**
- **Exit**

## ⌨️ Command line

```
HdrTray.exe --toggle   Toggle HDR
HdrTray.exe --on       Turn HDR on
HdrTray.exe --off      Turn HDR off
```

With an argument, HDR Tray does the action once and exits without starting the tray icon. Use it for a desktop shortcut or a hotkey.

| Exit code | Meaning |
| :---: | --- |
| `0` | Success |
| `1` | No HDR-capable display |
| `2` | Invalid argument |
| `3` | The switch failed, or the HDR state could not be read |

`HdrStatus.exe`, also in the release, is an optional read-only diagnostic tool. It prints the HDR state of every display, which is useful in a bug report.

## 🛠️ Build

The build uses the C# compiler that ships with Windows. You do not need an SDK or Visual Studio.

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The output is in `build\`.
