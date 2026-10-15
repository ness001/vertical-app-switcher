# Vertical App Switcher

A lightweight Windows 10/11 app switcher with an Xbox Game Bar-inspired
vertical overlay.

The current architecture and behavioral contract are documented in
[DESIGN.md](./DESIGN.md).

## Features

- Choose and check a global keyboard shortcut during first-run setup. The
  default is **Win + Backtick**; use a preset, an on-screen keyboard, or record
  keys in the setup window if another app already uses it.
- Separate tabs show open taskbar windows on this desktop, notification-area
  apps, windows on other virtual desktops, recently closed apps, and pinned
  apps. Minimized windows remain available. Selecting an other-desktop window
  switches to its desktop.
- The Tray tab includes registered notification-area apps and hidden overflow
  icons. Apps with a visible window are shown only once. Activating a tray app
  asks the existing single-instance app to show itself, including packaged
  Windows apps such as Xbox App.
- Uses recorded foreground-use history to keep minimized windows in Open at
  their recent-use positions instead of moving them to the bottom.
- Saves pinned applications across restarts in a separate Pinned tab.
- Tracks up to 12 recently closed desktop applications so they can be reopened.
- Highlights the previously used app when the overlay opens.
- Supports Xbox/XInput controllers, keyboard, mouse, and mouse-wheel
  navigation.
- Runs quietly in the notification area.
- Includes an optional `Start with Windows` setting in the tray menu.
- Uses Windows Forms and native Windows APIs with no third-party dependencies.

## Framework

The app is written in C# using Windows Forms and targets
`net10.0-windows` (.NET 10 LTS). It calls the standard Windows APIs directly
for global hotkeys, app discovery, icons, and window activation. The project
uses the standard .NET SDK and has no NuGet package or third-party runtime
dependencies.

The app supports Windows only. macOS support requires a separate port and is
not included.

## Build

Install the current [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
open Windows PowerShell in this folder, and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

The build defaults to Windows x64. To publish for Windows on Arm:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 `
    -RuntimeIdentifier win-arm64
```

The build creates a framework-dependent, single-file
`VerticalAppSwitcher.exe` in this folder. Running it requires the matching
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Using the installed runtime keeps the executable small and lets installed .NET
security and servicing updates apply without republishing the app.

If the switcher is running, the build requests a normal shutdown immediately
before replacing the executable and restarts it in background mode afterward.

## Launch

The included `VerticalAppSwitcher.exe` is built for Windows x64 and requires the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
You do not need to run it as an administrator.

To launch it from File Explorer, open this folder and double-click
`VerticalAppSwitcher.exe`.

To launch it from Windows PowerShell, open PowerShell in this folder and run:

```powershell
.\VerticalAppSwitcher.exe
```

The app bar opens immediately and the switcher remains available in the
notification area after the bar is dismissed. If the switcher is already
running, launching it again opens the existing app bar instead of starting a
second copy.

To start the switcher quietly in the notification area without opening the app
bar, run:

```powershell
.\VerticalAppSwitcher.exe --background
```

You can also enable **Start with Windows** from the switcher's notification-area
menu.

On first launch, the setup page shows a large keyboard. Click Ctrl, Alt, or
Win and one other key for a two-key shortcut such as **Ctrl + K**. Click a
selected key again to clear it, or select **Clear selection** to start over.
Shift can be added but cannot be the only modifier. You can also select a
preset, or choose **Record keys** and type a
combination while the setup page has focus. The page checks whether Windows can
reserve it and shows a conflict before you save. Choose another combination or
**Check again** if it is not available. Select **Save shortcut** to apply it;
Windows checks again when you save. The keyboard uses a US-layout guide, and a
registration check cannot detect shortcuts used only inside another app. You
can change the shortcut later by clicking the shortcut chip at the top-right of
the app bar or selecting **Set up shortcut...** in the notification-area menu.

## Controls

Each time the app bar opens, the footer shows keyboard hints. A controller
action changes the footer to Xbox-style gamepad hints. A keyboard or mouse
action restores keyboard hints. The footer shows only one set at a time.
All keyboard, mouse, and controller controls remain available with either set.

1. Press your configured global shortcut whenever you want to open the app bar
   again.
2. Select a tab and an app:
   - Controller D-pad or left stick Left/Right changes tabs; Up/Down selects
     an app. Keyboard `Left` / `Right` also changes tabs, while `Up` / `Down`,
     `Tab`, or the mouse wheel selects an app. Click a tab to open it.
   - Controller `A`, `Enter`, `Space`, or a click opens the selected app.
   - Controller `B`, `Escape`, or the invocation shortcut closes the overlay.
   - Keyboard `D` requests a normal close of the selected window on every
     press. Repeated presses never request force-stop.
   - For an open window, keyboard `C` or `Delete` also requests normal close.
     `Delete` removes a Recent entry if its app is closed; `C` does not.
     Neither key requests force-stop.
   - Controller `X` also requests only a normal close of the selected window
     on every press.
   - Controller `Y` or `P` pins or unpins the selected app.
   - Right-click an app for switch, pin, normal close, force-stop, and
     recent-removal actions. Force-stop is a separate menu action with a Yes/No
     warning. It can discard unsaved work and runs only after you select Yes.
3. Right-click the notification-area icon to enable automatic startup or exit.

A tray-only row has no window handle (HWND), so it cannot receive a normal
close request. The app reports this limit and does not request force-stop.

Only one copy runs at a time. Launching the executable again asks the existing
copy to show its app bar. Automatic Windows startup uses background mode, so it
does not interrupt sign-in. If another program takes the configured shortcut,
the switcher opens setup so you can choose a working alternative.

Open uses foreground-use history from the current switcher session, not usage
frequency. A minimized window with recorded use keeps its recent-use position;
minimizing it does not move it to the bottom. Windows with recorded use appear
first. Windows without that history follow in Windows discovery order
(Z-order). History is not saved across switcher restarts. If history tracking
is unavailable, Open uses Windows discovery order for all rows. Tray,
Desktops, Recent, and Pinned keep their separate order rules.

Saved apps are in the Pinned tab, and recently closed apps are in the Recent
tab. Both can be opened with `A`, `Enter`, or a click. When an app quits while
the overlay is visible, the list updates after the next tracker check (normally
within 100 ms). A relaunchable app moves to Recent; a duplicate
or non-relaunchable row is removed, and a window that continues as a tray app
moves to Tray. Reopening the overlay is not required. A close update keeps the
current tab selected, even if the selected row moves to Recent or Tray, remains
only in Pinned, or is removed. If that row leaves the current tab, the overlay
selects the nearest remaining row. If the tab has no rows, it stays selected
and empty.

The tracker also records displayed windows before a normal-close or force-stop
request, so a quick exit can appear in Recent. An app that opens and closes
entirely between checks cannot be recovered.

File Explorer folders are tracked individually, even if other Explorer windows
remain open. Reopening a recent Explorer entry restores its original folder
path.

Pins and recent history are stored in:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\state.json
```

The selected shortcut and first-run setup state are stored in:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\settings.json
```

## Diagnostics

Runtime errors are logged to:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\switcher.log
```
