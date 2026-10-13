# Vertical App Switcher Design

**Status:** Implemented current-state design<br>
**Last updated:** 2026-10-12<br>
**Platform:** Windows 10/11<br>
**UI framework:** C# Windows Forms on .NET 10 LTS<br>
**Runtime dependencies:** Windows and the .NET 10 Desktop Runtime; no
third-party packages

This document describes the application as it exists on the date above. It is
the architectural reference for future changes; `README.md` remains the
short-form build and usage guide.

## 1. Product intent

Vertical App Switcher is a lightweight, controller-friendly Windows overlay
inspired by Xbox Game Bar. It provides one vertical list that combines:

- Normal taskbar application windows across all virtual desktops.
- Minimized windows.
- Running notification-area applications, including overflow icons.
- Persistently pinned applications.
- Recently closed applications and File Explorer folders.

The app is designed for handheld PCs and normal Windows desktops. Every primary
operation has both an Xbox/XInput control and a keyboard or mouse equivalent.

### Goals

- Open quickly from a global shortcut.
- Navigate reliably without depending on overlay focus.
- Preserve Windows latest-used ordering for unpinned visible windows.
- Include running tray-only applications without listing arbitrary background
  processes.
- Allow safe close, explicit force-stop, pinning, and recent-app reopening.
- Remain lightweight and build with the standard .NET SDK without NuGet.
- Fail visibly and record diagnostics instead of silently degrading.

### Non-goals

- Replacing the Windows shell, taskbar, or Task Manager.
- Capturing XInput exclusively; games may receive the same controller input.
- Enumerating every background service or process.
- Simulating direct clicks on notification-area icons.
- Providing a general executable browser or launcher.
- Hiding or virtualizing the physical controller.

## 2. User-visible behavior

### Invocation

| Input | Behavior |
|---|---|
| `Win + Backtick` | Toggle the overlay |
| `Win + Shift + Backtick` | Layout-compatible toggle alias |
| `Ctrl + Shift + Space` | Fallback when the Windows key is locked |
| Tray icon double-click | Open the overlay |
| Launch executable again | Signal the existing instance to open |

The invocation shortcuts are true toggles: the first press opens the overlay
and the next press closes it. They never advance the selection.

Manual launch opens the overlay after the message loop starts. Windows startup
uses `--background` and does not interrupt sign-in.

### Navigation and actions

| Action | Controller | Keyboard/mouse |
|---|---|---|
| Previous/next | D-pad or left stick Up/Down | Up/Down, Tab, mouse wheel |
| Open/switch/reopen | A | Enter, Space, left-click |
| Dismiss overlay | B | Escape or invocation shortcut |
| Request close | X | Delete |
| Pin/unpin | Y | P |
| First/last row | - | Home/End |
| Additional actions | - | Right-click |

Right-click exposes open/switch, pin/unpin, close/remove-recent, and
force-stop actions.

## 3. High-level architecture

```text
Program / named mutex
        |
        v
SwitcherApplicationContext
  |         |             |              |
  v         v             v              v
Global   XInput       ClosedApp      AppStateStore
HotKey   Navigation   Tracker         state.json
  |         |             |
  +---------+-------------+
            |
            v
      SwitcherOverlay
            |
            v
    AppWindowCollection
       /           \
      v             v
WindowCatalog   TrayAppCatalog
EnumWindows     NotifyIconSettings
```

### Component responsibilities

| Component | Responsibility |
|---|---|
| `Program` | DPI setup, single-instance mutex, fatal error boundary |
| `SwitcherApplicationContext` | Application lifetime, service wiring, tray menu, startup setting, action orchestration |
| `GlobalHotKey` | Invocation shortcuts, second-instance message, overlay-scoped navigation hotkeys |
| `XInputNavigationService` | Poll four XInput slots and emit intent-level actions |
| `SwitcherOverlay` | Drawing, selection, mouse/keyboard interaction, action events |
| `WindowCatalog` | Discover and describe eligible visible/minimized windows |
| `TrayAppCatalog` | Discover running notification-area applications |
| `AppWindow` | Entry state, identity, activation, close, and force-stop |
| `AppStateStore` | Persist pins and recent entries and compose the displayed list |
| `ClosedAppTracker` | Detect closed windows or changed identities and publish list updates |
| `VirtualDesktopWindowManager` | Distinguish inactive-desktop windows from other DWM-cloaked windows |
| `ExplorerFolderResolver` | Resolve a File Explorer HWND to its folder path |
| `StartupRegistration` | Manage the current-user Run registry value |
| `DiagnosticLog` | Append timestamped runtime diagnostics |

## 4. Entry model

`AppWindow` is the common model for every displayed row.

Important fields:

- `Handle` and `ProcessId`
- `Title` and `ProcessName`
- `ExecutablePath`
- Optional `LaunchTarget`, primarily an Explorer folder path
- Optional `AppUserModelId` for packaged apps
- `Icon`
- `IsMinimized`
- `IsOnAnotherDesktop`
- `IsPinned`
- `IsRecent`
- `IsTray`
- Optional `TrayTooltip`

### Entry states

| State | Handle | Process | Badge | Activation |
|---|---:|---:|---|---|
| Visible window | HWND | Running | None | Restore/show and foreground HWND |
| Minimized window | HWND | Running | `MINIMIZED` | Restore and foreground HWND |
| Other-desktop window | HWND | Running | `DESKTOP` | Switch desktop and foreground HWND |
| Tray-only app | None | Running | `TRAY` | AUMID shell activation or registered executable |
| Recently closed app | None | Not running | `RECENT` | Relaunch executable/target |
| Pinned running app | HWND or none | Running | `PINNED` | Same as its underlying state |
| Pinned offline app | None | Not running | `PINNED` | Relaunch persisted executable/target |

Badge precedence is:

1. `PINNED`
2. `RECENT`
3. `TRAY`
4. `DESKTOP`
5. `MINIMIZED`

### Identity

Identity describes what can be reopened:

```text
ExecutablePath + newline + LaunchTarget
```

- Ordinary desktop apps normally use executable path only.
- Explorer folders use `explorer.exe` plus the exact folder path.
- Multiple visible windows from the same ordinary executable can coexist in
  the active list, but pins and recents deduplicate by identity.
- Tray entries deduplicate against visible windows by normalized executable
  path.

## 5. List composition and ordering

The list is composed in `AppStateStore.Compose`:

1. Resolve persisted pins in saved pin order.
2. Attach currently running entries that match each pin.
3. Add remaining active visible/minimized windows in `EnumWindows` Z-order.
4. Add running tray-only applications after normal active windows.
5. Add persisted recent entries whose identity is not currently open.

For unpinned visible windows, Windows Z-order provides practical
latest-used-first ordering. This is recency, not usage frequency.

The initial selected row is index `1` when more than one entry exists, otherwise
index `0`. This favors switching away from the first/current Z-order entry.
Pins can alter what occupies those indexes.

The overlay header reports:

- Open count: entries whose HWND exists or whose tray process is running.
- Saved count: remaining offline pinned/recent entries.

## 6. Normal window discovery

`WindowCatalog` enumerates top-level windows using `EnumWindows`.

It excludes:

- The switcher's own process.
- The shell window.
- Invisible windows.
- Windows cloaked by their application or inherited from an owner.
- Shell-cloaked windows that Windows does not identify as belonging to another
  virtual desktop.
- Tool windows unless explicitly marked `WS_EX_APPWINDOW`.
- `WS_EX_NOACTIVATE` windows unless explicitly marked as app windows.
- Owned windows unless explicitly marked as app windows.
- Windows with no title.

It includes minimized windows and shell-cloaked windows that the public
`IVirtualDesktopManager` API confirms are on an inactive virtual desktop.
Those entries receive a `DESKTOP` badge. Presentation metadata is taken from
the owning process, associated executable icon, and, if needed, the window
icon. Activating an inactive-desktop entry uses normal foreground activation;
Windows switches to the window's desktop as part of that explicit action.

## 7. Notification-area application discovery

Windows 11 uses a XAML notification area, so legacy
`ToolbarWindow32` enumeration is not reliable. `TrayAppCatalog` instead uses
Windows' registered notification-icon records:

```text
HKCU\Control Panel\NotifyIconSettings
```

Discovery steps:

1. Read registered executable paths and initial tooltips.
2. Expand environment variables and known-folder GUID prefixes.
3. Match registrations against currently running process names.
4. Confirm exact process paths when Windows permits access.
5. If an elevated process denies path access, correlate its exact process name
   with the registered executable path.
6. Exclude the switcher, visible-window duplicates, and selected system shell
   entries.
7. Add one tray row per represented executable.

Current explicit system exclusions:

- `explorer.exe`
- `MoNotificationUx.exe`
- `SecurityHealthSystray.exe`

These are shell/status components rather than user application switch targets.

### Tray activation

- Packaged apps use:

  ```text
  explorer.exe shell:AppsFolder\<AUMID>
  ```

- Ordinary tray apps execute their registered path. Single-instance
  applications typically signal their existing process to show its UI.

This is best-effort. The app does not simulate a notification-icon click, so a
tray utility that ignores repeat launches may remain hidden.

## 8. Explorer folder identity

Explorer commonly hosts many windows or tabs in one `explorer.exe` process and
can reuse the same top-level HWND after a folder/tab changes.

`ExplorerFolderResolver` uses the `Shell.Application` COM API:

```text
Window -> Document -> Folder -> Self -> Path
```

The path is resolved while the window is alive and cached by HWND plus title.
The tracker treats either of these as a close:

- The HWND disappears.
- The same HWND now has a different identity.

This allows one Explorer folder to become recent while other Explorer windows
remain open. Reopening launches:

```text
explorer.exe "<original folder path>"
```

## 9. Activation

Activation has three paths:

### Existing HWND

1. Validate the HWND.
2. Restore it if minimized; otherwise show it.
3. Call `SetForegroundWindow`.
4. Fall back to `BringWindowToTop` and another foreground request.

### Offline desktop/recent entry

Launch `ExecutablePath` with `UseShellExecute = true`. If `LaunchTarget` exists,
pass it as a quoted argument.

### Packaged tray entry

Launch its `AppUserModelId` through `shell:AppsFolder`.

Activation errors are logged and surfaced through a tray notification.

## 10. Input architecture

### Global keyboard shortcuts

`GlobalHotKey` owns a hidden native window and registers:

- Required: `Win + Backtick`
- Optional: `Win + Shift + Backtick`
- Optional: `Ctrl + Shift + Space`

If the required shortcut cannot register, startup fails explicitly. Optional
conflicts are logged while the remaining shortcuts continue to work.

While the overlay is visible, the app temporarily registers unmodified:

- Up
- Down
- Enter
- Escape
- Delete
- P
- Home
- End

They are unregistered when the overlay hides. `ProcessCmdKey` remains the local
fallback. This design handles the case where the overlay is visible but
Windows refuses to transfer foreground focus.

### XInput

`XInputNavigationService` polls `xinput1_4.dll` every 16 ms across slots 0-3.

- Button edges trigger A/B/X/Y once.
- D-pad and left-stick vertical input navigate.
- Stick threshold: `16000`.
- Held navigation repeat delay: 400 ms initially, then 120 ms.
- Previous state is updated even when overlay input is disabled, preventing a
  held button from firing when the overlay opens.

The service emits semantic actions instead of exposing button masks to the UI.

## 11. Overlay UI and DPI

The overlay is a borderless, topmost, custom-drawn WinForms window.

Logical dimensions:

- Width: 390
- Header: 78
- Row: 68
- Footer: 68
- Maximum visible rows: 9
- Work-area edge margin: 24

The overlay appears on the foreground window's monitor, centered vertically in
that monitor's working area.

Rendering uses one logical coordinate system and a manual DPI scale. Icons are
converted to bitmaps and drawn with `Graphics.DrawImage`; `DrawIcon` is avoided
because it did not follow the same transform and caused row misalignment at
handheld display scaling.

The current implementation is system-DPI aware through `SetProcessDPIAware`
and `GetDpiForSystem`; it is not Per-Monitor-V2 aware.

The overlay hides when:

- Escape/B is pressed.
- An invocation shortcut is pressed again.
- Focus moves elsewhere, unless a context menu or protected initial-show
  interval is active.
- An entry is activated.

## 12. Pinning

Y/P toggles a persisted pin by application identity.

- Pins move ahead of unpinned entries immediately.
- Pin order is persisted.
- All live rows sharing the pinned identity receive the pinned state.
- An offline pin remains launchable using its persisted executable, Explorer
  path, or packaged app identifier.

## 13. Closing and force-stop safety

The first X/Delete action always requests a normal close.

### HWND-backed entry

1. Post `WM_CLOSE`.
2. If posting fails, retry with `SendMessageTimeout`.
3. Timeout is 500 ms with `SMTO_ABORTIFHUNG`.
4. Preserve and report the Win32 error.

### Tray-only entry

A tray row has no HWND that can receive `WM_CLOSE`, so normal close reports
that limitation and arms force-stop.

### Explicit force-stop

After a close request or close failure, the app arms a 15-second confirmation
for the exact close key:

- HWND for normal windows.
- Process ID plus identity for tray-only entries.

A second X/Delete during that interval calls `Process.Kill`. Selecting another
tray app cannot reuse the previous confirmation. The context-menu force-stop
action additionally shows a Yes/No warning dialog.

## 14. Recently closed tracking

`ClosedAppTracker` polls every 500 ms in the background and every 100 ms while
the overlay is visible.

It snapshots:

- Eligible normal windows.
- Running tray entries.
- Explorer folder identity.

A close is detected when:

- A tracked HWND no longer exists.
- A reused HWND changes identity.
- A tracked tray process disappears and no currently open entry has the same
  identity.

Every detected closure is published to the visible overlay. Relaunchable
entries are persisted first and change to `RECENT` in place. Rows that cannot
be relaunched, or duplicate rows whose application identity remains open, are
removed immediately. If closing a normal window leaves the same application
running only in the notification area, the existing row changes to `TRAY`.
Selection is preserved when possible, and the list does not need to be closed
and reopened.

Recent history is capped at 12 identities.

## 15. Persistence

State path:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\state.json
```

Stored data:

- Ordered pinned applications.
- Ordered recent applications.
- Executable path.
- Optional launch target.
- Optional App User Model ID.
- Display title and process name.
- Recent close timestamp.

Writes use a temporary file followed by `File.Replace` or `File.Move`. Invalid
JSON is reported as an explicit error; the app does not silently discard it.

Startup registration path:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

Startup command:

```text
"<VerticalAppSwitcher.exe>" --background
```

## 16. Single-instance lifecycle

- A local named mutex (`Local\VerticalAppSwitcher`) permits one instance.
- `GlobalHotKey` owns a hidden native window titled
  `VerticalAppSwitcherHotKey`.
- A second process locates that window and posts `WM_SHOW_SWITCHER`.
- The existing process composes a fresh list and opens its overlay.
- An internal `--shutdown` launch posts `WM_EXIT_SWITCHER` so the build can
  request a normal exit of the exact running instance.
- Dispose order disables the tray icon, unregisters input, stops trackers, and
  disposes UI resources.

## 17. Diagnostics and error handling

Log path:

```text
%LOCALAPPDATA%\VerticalAppSwitcher\switcher.log
```

The log records:

- Application start/stop.
- Global shortcut registration and invocation.
- XInput connection and controller actions.
- Recent-history updates.
- Optional shortcut conflicts.
- Input, persistence, activation, and cleanup errors.

Fatal startup exceptions display a message box and are written to the log.
Recoverable runtime failures use a tray notification or a confirmation dialog.

## 18. Build and generated assets

`Build.ps1`:

1. Regenerates `AppIcon.ico` through `GenerateIcon.ps1`.
2. Publishes `VerticalAppSwitcher.csproj` in Release mode through the .NET 10
   SDK.
3. Targets `win-x64` by default or the requested supported RID.
4. Produces a framework-dependent, single-file Windows executable.
5. Requests a normal shutdown if that exact project executable is running.
6. Copies the published executable to the project directory.
7. Restarts it in background mode when it was running before the build.

`VerticalAppSwitcher.csproj` targets `net10.0-windows`, enables Windows Forms,
and embeds the generated icon. `global.json` requires a stable .NET 10 SDK and
rolls forward to the latest installed .NET 10 feature band.

The framework-dependent publish is intentional. It keeps the executable small
and allows installed .NET 10 security and servicing patches to apply without
republishing the app. The target PC therefore needs the matching .NET 10
Desktop Runtime; building additionally requires the .NET 10 SDK.

## 19. Safety and privacy

- The app performs no network communication.
- It does not modify MSI firmware or controller configuration.
- It does not elevate itself.
- Force-stop is never the first close action.
- State and diagnostics stay under the current user's local application data.
- Tray discovery reads current-user notification registrations and correlates
  only matching running processes.

## 20. Known constraints and future design considerations

1. **Tray activation is best-effort.** There is no stable public API to invoke
   every arbitrary notification icon. The current design uses AUMID activation
   or a repeat executable launch.
2. **XInput is shared.** A foreground game can react to the same controller
   input used by the overlay.
3. **DPI awareness is system-wide.** Moving the overlay between monitors with
   different scale factors may require Per-Monitor-V2 support.
4. **Shortcuts are fixed.** There is no user-facing shortcut configuration.
5. **Current foreground inclusion is unchanged.** The foreground app can
   remain in the source list; default index `1` normally selects the next Z-order
   app.
6. **State has no schema version.** Current JSON evolution relies on additive,
   optional fields.
7. **Tray registration is Windows implementation-specific.** It relies on
   `NotifyIconSettings`, which should be revalidated after major Windows shell
   changes.
8. **Desktop tray apps must support repeat launch** to surface their existing
   UI; otherwise the row can identify and manage the process but may not show a
   window.

## 21. Continuous documentation Stop hook

The workspace contains a repository-level Copilot hook:

```text
.github/hooks/continuous-documentation.json
.github/hooks/Review-Stop.ps1
```

The VS Code-compatible `Stop` event runs when the main agent attempts to finish
a turn. Its purpose is to prevent implementation and debugging knowledge from
drifting away from the project documentation.

On the first stop attempt, the hook returns:

```json
{
  "decision": "block",
  "reason": "Perform the project close-out review..."
}
```

This forces one additional agent turn that must:

1. Review the work for significant reusable learnings.
2. Update the appropriate existing skill or instruction when a durable
   non-obvious learning exists.
3. Update this design document when architecture, behavior, input, discovery,
   identity, persistence, safety, build, diagnostics, limitations, or verified
   behavior changed.
4. Update the README when user-facing behavior or build/use instructions
   changed.
5. Verify work and clean temporary diagnostics before completion.

The hook deliberately does not require meaningless documentation churn. If the
turn contains no applicable learning or design change, the agent records
nothing and completes after the review.

`stop_hook_active` is the loop guard. When Copilot invokes the hook again after
the forced continuation, the script returns:

```json
{
  "decision": "allow"
}
```

This guarantees at most one documentation-review continuation per turn and
avoids the platform's repeated-block runaway limit. Invalid hook input is
reported to stderr and allowed rather than trapping the session.

The hook is Windows-oriented and runs through Windows PowerShell with a
10-second timeout. Its configuration is project-local under `.github/hooks`,
so future Copilot sessions opened from this workspace inherit the maintenance
contract.

## 22. Verified baseline as of 2026-10-12

The current implementation has been validated for:

- Successful .NET 10 SDK build, single-file publish, and embedded application
  icon.
- Running-app rebuild with normal shutdown, executable replacement, and
  background restart.
- Single-instance launch and second-launch surfacing.
- Shortcut toggle behavior for all three invocation chords.
- Keyboard navigation without reliable foreground focus.
- XInput slot discovery and D-pad/left-stick/A/B/X/Y behavior.
- DPI-aligned icons, text, selection cards, and badges.
- Persistent pin ordering.
- Normal close and explicit second-action force-stop.
- Live conversion from open row to recent row.
- Visible-overlay quit refresh: duplicate rows are removed and the last
  relaunchable row converts to recent without reopening the overlay.
- File Explorer per-folder tracking and exact-path reopening.
- Notification-area discovery with visible-window deduplication.
- Elevated tray discovery (Sunshine).
- Packaged app metadata and shell activation.
- Live tray rows for OneDrive, MSI Center M, Xbox App, Doubao, and Sunshine.

When behavior changes, update this document in the same change as the relevant
source and README sections.
