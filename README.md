# Vertical App Switcher

Switch windows, reopen recent apps, and reach pinned apps from one vertical list
on **Windows and macOS**.

<p>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/macOS-13%2B-333333?style=flat-square" alt="macOS 13 or newer">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPL--3.0--only-blue?style=flat-square" alt="GPL-3.0-only"></a>
</p>

[Features](#features) · [Install](#install) · [Controls](#controls) ·
[Updates and privacy](#updates-and-privacy) · [Documentation](#documentation)

<p align="center">
  <img src="docs/assets/macos-switcher.png" alt="Native macOS switcher showing Open, Pinned, Recent, Tray, and Desktops tabs, four sample windows, and keyboard controls." width="520"><br>
  <sub>Actual macOS app, captured in isolated demo mode with sample entries.</sub>
</p>

## Features

- **Five tabs for different app states.** Browse **Open**, **Pinned**,
  **Recent**, **Tray**, and **Desktops** in that order. Open includes minimized
  windows; Tray collects detected notification-area or menu-bar apps; Desktops
  brings together windows on other virtual desktops or Spaces.
- **Pins that stay available.** Save an app once, then switch to it while it is
  running or launch it when it is closed. Pins keep their saved order.
- **Recently closed apps, ready to reopen.** Keep up to 12 recent entries.
  Windows restores tracked File Explorer folders; macOS can restore documents
  and folders when their apps expose a local file URL.
- **Search from the keyboard.** Press **S** to filter the current tab by window
  title or app name. The query follows you between tabs.
- **Choose how to navigate.** Use keyboard, mouse, or mouse wheel on either
  platform. Windows also supports Xbox/XInput controllers.
- **Keep it within reach.** Choose a global shortcut and optional launch at login
  from the notification-area or menu-bar menu. On macOS, choose **System**,
  **Light**, **Dark**, or **Midnight** under **Appearance**.

## Install

### Windows 10 / 11

1. Download the **win-x64** or **win-arm64** ZIP from
   [GitHub Releases](https://github.com/ness001/vertical-app-switcher/releases).
2. Install the matching
   [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
3. Extract the ZIP and run `VerticalAppSwitcher.exe`. Keep `LICENSE` and `NOTICE`
   with the executable.
4. Choose a shortcut in first-run setup, then select **Save shortcut**.
   The default is **Win + Backtick**.

Press the shortcut to open or dismiss the list. The app stays in the notification
area when dismissed. Right-click its icon for **Set up shortcut...**,
**Start with Windows**, update settings, or **Exit**.

Windows packages are unsigned. Follow your organization's security policy if a
security warning appears. Administrator access is not required.

### macOS 13+

One **osx-universal** package supports Apple Silicon and Intel; no developer
tools or .NET runtime are needed to run it.

1. Download `VerticalAppSwitcher-<version>-osx-universal.zip` from
   [GitHub Releases](https://github.com/ness001/vertical-app-switcher/releases).
2. Extract the ZIP and move **Vertical App Switcher.app** to **Applications**.
3. Open the app and enable it in **System Settings → Privacy & Security →
   Accessibility**.
4. Choose a shortcut in setup, then save it. The default is
   **Control + Backtick**.

Screen Recording and Input Monitoring permissions are not required. Use the
menu-bar icon for shortcut setup, **Appearance**, launch at login, updates, or
quitting the switcher. Packages are not notarized; follow your organization's
security policy if macOS blocks opening the app. See the
[macOS guide](macos/README.md) for installation and developer details.

<details>
<summary>Build macOS from source</summary>

With Xcode or Command Line Tools and a matching macOS SDK, run from a checkout:

```bash
bash macos/build.sh
open 'bin/macos/Vertical App Switcher.app'
```

Enable this exact app in Accessibility and choose a shortcut as above. Quit the
output app before rebuilding. A persistent local signing identity preserves
Accessibility approval across development rebuilds; ad-hoc builds can require
reapproval. See [build and signing details](macos/README.md#build-from-source).

</details>

### Build Windows from source

On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and run these commands in **Windows PowerShell** from the repository folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
.\VerticalAppSwitcher.exe
```

The default build targets **win-x64**. For Arm64, add
`-RuntimeIdentifier win-arm64` to the build command. The target PC still needs
the matching .NET 10 Desktop Runtime. Distributable files, including `LICENSE`
and `NOTICE`, are in `bin\publish\<runtime>`.

If the repository's executable is already running, `Build.ps1` requests a normal
exit, replaces that executable, and restarts it in background mode. If normal
exit fails, use the app's **Exit** command before rebuilding.

## Controls

While browsing the list on either platform:

- **Left / Right** changes tabs; **Up / Down** or the mouse wheel selects an app.
- **Enter**, **Space**, or a row click switches to or launches the selected app.
- **S** reveals and focuses search. While editing, letters are query text;
  **Up / Down** returns to the current match without skipping it. **Enter** opens
  the match. Reopening the overlay clears and hides search.
- **P** pins or unpins the selection.
- **D** requests a normal window close, or removes a closed **Recent** entry.
  **Delete** only removes closed Recent entries. Removing history keeps its pin.
- **Escape** or the global shortcut dismisses the overlay.

Open a numbered result with **Ctrl + 1…9** on Windows or **Command + 1…9** on
macOS. These target the first nine results in the filtered list. macOS also
supports **Command + F** for search and **Q** to request normal quit of the
selected running app. **Q quits that app, not the switcher**; the app controls
any save prompts.

On Windows, use the controller's D-pad or left stick to navigate, **A** to open,
**B** to dismiss, **Y** to pin, and **X** to close or remove. Footer hints follow
the input method most recently used. A foreground game may receive the same
controller input. macOS uses keyboard and mouse controls.

Right-click a row for its available actions. **Force-stop / Force quit app…** is
a separate, confirmed action that can discard unsaved work. Repeated close keys
always request normal close; app-only rows without a window cannot be closed
that way.

## Platform details

**Windows** uses native window and notification-area discovery. Tray includes
hidden overflow entries and filters out apps already shown as visible windows.
Activation relaunches the registered app; an app that ignores repeat launches
may stay hidden. Elevated or protected windows can reject close requests.
The overlay is system-DPI aware; mixed-scale monitors can have display issues.

**macOS** uses Accessibility for window access and discovers menu-bar app owners
for **Tray**. Hosted or custom menu-bar items may be unavailable. **Desktops**
uses dynamically loaded private macOS Spaces interfaces; compatibility depends
on the OS version. When membership cannot be determined, windows remain in
**Open** and activation falls back to Accessibility. Restoring a document
requires its app to expose a local file URL; Recent does not restore unsaved
session state. See the [macOS guide](macos/README.md#tabs-and-platform-adaptations).

## Updates and privacy

Both versions check stable GitHub Releases after startup and every 12 hours.
Use **Check for updates…** in the notification-area or menu-bar menu for a
manual check, or turn automatic checks off there. Downloads are verified against
the release's SHA-256 checksum. Installation requires **Update and restart**;
**Later** postpones reminders for one day.

Updates need a writable app location. Installation requests normal exit, backs
up the app's release files or bundle, and preserves settings and history.
A checksum checks integrity, not publisher identity. See the
[release guide](docs/RELEASING.md#in-app-update-contract) and
[macOS update details](macos/README.md#updates-and-local-data) for requirements
and recovery behavior. Windows `v0.1.0` users need one manual upgrade to get the
updater.

Pins, recent history, settings, and diagnostics are stored locally:

- **Windows:** `%LOCALAPPDATA%\VerticalAppSwitcher\`
- **macOS:** `~/Library/Application Support/VerticalAppSwitcher/`, with update
  preferences in the app's UserDefaults domain.

History and diagnostics can contain app names, window titles, or local paths.
Update checks and downloads contact GitHub; window titles, pins, history, and
settings are not sent with those requests.

## Documentation

- [macOS guide](macos/README.md) — native build, permissions, signing, platform
  behavior, and verification.
- [Windows design and architecture](docs/DESIGN.md) — ordering, search details,
  discovery, local files, and source layout.
- [UI testing](docs/UI-TESTING.md) — focused Windows checks and safe test data.
- [Releases](docs/RELEASING.md) — CI, packages, checksums, and publication.

## License

[GNU General Public License version 3 only](LICENSE) (`GPL-3.0-only`).
Copyright 2026 ness001. Attribution is in [NOTICE](NOTICE).

Commercial use, changes, and redistribution are allowed under the license terms.
When distributing covered software, retain required notices and the license;
distribute covered modifications under GPLv3 and provide Corresponding Source
when conveying binaries as required by the license. The software comes without
warranty; the full license controls these terms.

Earlier releases distributed under Apache License 2.0 remain available under
those original terms. This change does not relicense third-party material or
grant permission to copy code from other projects.
