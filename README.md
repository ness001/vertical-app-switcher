# Vertical App Switcher

A lightweight Windows 10/11 app switcher with an Xbox Game Bar-inspired
vertical overlay.

The current architecture and behavioral contract are documented in
[DESIGN.md](./DESIGN.md).

## Features

- Press **Win + Backtick** from anywhere to open the overlay.
- **Ctrl + Shift + Space** is a fallback when a keyboard layout, gaming mode, or
  Windows-key lock prevents the primary shortcut. **Win + Shift + Backtick**
  is also accepted for layouts that require Shift to type a backtick.
- Shows normal taskbar applications across all Windows virtual desktops,
  including minimized windows. Entries on an inactive desktop display a
  `DESKTOP` badge, and selecting one switches to its desktop.
- Shows currently running applications registered in the Windows notification
  area, including hidden overflow icons. Apps that already have a normal
  visible window are shown only once. Notification-area entries display a
  `TRAY` badge; activating one asks the existing single-instance app to show
  itself, including packaged Windows apps such as Xbox App.
- Uses the current Windows Z-order so recently used apps appear first.
- Pins selected applications above the latest-used list and saves pins across
  restarts.
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

## Use

1. Run `VerticalAppSwitcher.exe`. The vertical app bar opens immediately.
2. After dismissing it, press **Win + Backtick** whenever you want to open it
   again. Use **Ctrl + Shift + Space** if the Windows key is locked.
3. Select an app:
   - Controller D-pad or left stick Up/Down navigates.
   - Controller `A` switches to the selected app.
   - Controller `B` closes the overlay.
   - Controller `X` requests a normal app close. Press `X` again within
     15 seconds to force-stop an app that refuses or is unable to close
     normally.
   - Controller `Y` pins or unpins the selected application.
   - `Up` / `Down`, `Tab`, or the mouse wheel navigates.
   - Pressing an invocation shortcut again closes the overlay.
   - `Enter`, `Space`, or a mouse click switches to the selected app.
   - `Delete` closes an app and `P` pins or unpins it.
   - `Escape` or clicking elsewhere closes the overlay.
   - Right-click an app for switch, pin, close, force-stop, and recent-removal
     actions.
4. Right-click the notification-area icon to enable automatic startup or exit.

Only one copy runs at a time. Launching the executable again asks the existing
copy to show its app bar. Automatic Windows startup uses background mode, so it
does not interrupt sign-in. If another program already owns the required
**Win + Backtick** shortcut, the switcher reports the conflict instead of
running without a usable hotkey. Optional shortcut conflicts are logged while
the remaining shortcuts continue to work.

Pinned applications appear first. All unpinned open applications remain ordered
by latest use, not by usage frequency. Closed applications display a `RECENT`
badge and can be reopened with `A`, `Enter`, or a click. When an application
quits while the overlay is visible, the list updates immediately. A
relaunchable app changes to `RECENT`; a duplicate or non-relaunchable row is
removed, and a window that continues as a tray app changes to `TRAY`.
Reopening the overlay is not required.

File Explorer folders are tracked individually, even if other Explorer windows
remain open. Reopening a recent Explorer entry restores its original folder
path.

Pins and recent history are stored in:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\state.json
```

## Diagnostics

Runtime errors are logged to:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\switcher.log
```
