# Vertical App Switcher Design

**Status:** Implemented current-state design<br>
**Last updated:** 2026-10-15<br>
**Platform:** Windows 10/11<br>
**UI framework:** C# Windows Forms on .NET 10 LTS<br>
**Runtime dependencies:** Windows and the .NET 10 Desktop Runtime; no
third-party packages

This document describes the application as it exists on the date above. It is
the architectural reference for future changes; [README.md](../README.md)
remains the short-form build and usage guide.

## 1. Product intent

Vertical App Switcher is a lightweight, controller-friendly Windows overlay
inspired by Xbox Game Bar. It provides a vertical list in five tabs, in this order:

- Open: taskbar windows on the current virtual desktop, including minimized
  windows.
- Pinned: persistently saved applications.
- Recent: recently closed applications and File Explorer folders.
- Tray: running notification-area applications, including overflow icons.
- Desktops: windows on other virtual desktops.

The app is designed for handheld PCs and normal Windows desktops. Every primary
operation has both an Xbox/XInput control and a keyboard or mouse equivalent.

### Goals

- Open quickly from a global shortcut.
- Navigate reliably without depending on overlay focus.
- Keep Open windows in recent-use order, including minimized windows.
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
| Configured global shortcut | Toggle the overlay |
| `Win + Shift + Backtick` | Layout-compatible alias when the default shortcut is selected |
| Tray icon double-click | Open the overlay |
| Launch executable again | Signal the existing instance to open |

First-run setup defaults to `Win + Backtick`. Its large, US-layout on-screen
keyboard lets the user toggle modifiers and one key, including a two-key chord
such as `Ctrl + K`. Clicking the selected key again clears it, and **Clear
selection** clears all keys so the user can start over. The user can also use a
preset or record a chord while the setup window has focus. Custom
chords must include Ctrl, Alt, or Win; known system combinations are rejected.
A temporary `RegisterHotKey` probe reports a conflict in the page, with
**Check again** and another-choice paths. Saving repeats registration and
persists the selected chord only after Windows accepts it. The probe cannot
identify shortcuts that work only inside another app. If the saved shortcut
becomes unavailable, setup opens so the user can choose another. The setup page
can also be reopened from the tray menu. The overlay header shows the
configured shortcut; clicking its top-right chip opens the same setup page and
returns to the overlay after Save or Cancel.

The invocation shortcut toggles the overlay: the first press opens it and the
next press closes it.

Manual launch opens the overlay after the message loop starts. Windows startup
uses `--background` and does not interrupt sign-in.

### Navigation and actions

The keyboard controls below apply while browsing the app list, not while
search has focus. Search input has separate rules in the next section.

| Action | Controller | Keyboard/mouse |
|---|---|---|
| Previous/next tab | D-pad or left stick Left/Right | Left/Right or tab click |
| Previous/next entry | D-pad or left stick Up/Down | Up/Down, Tab, mouse wheel |
| Open/switch/reopen | A | Enter, Space, left-click |
| Open numbered entry | - | Ctrl+1 through Ctrl+9 |
| Show and focus search | - | S; click the field after it is shown |
| Dismiss overlay | B | Escape or invocation shortcut |
| Request normal close | X | D or right-click |
| Remove closed Recent entry | X | D, Delete, or right-click |
| Request force-stop warning | - | Separate right-click menu action |
| Pin/unpin | Y | P |
| First/last entry | - | Home/End |

Controller X and keyboard D request a normal close for a live selected window,
or remove a Recent entry when its app is already closed. Any saved pin stays.
Delete removes only closed Recent entries. The footer changes its
D/X label from Close to Remove for closed Recent entries. Repeated actions never
request force-stop. Right-click exposes open/switch, pin/unpin, normal close,
a separate force-stop action with a Yes/No warning, and recent-removal actions.

### Search

Search is a keyboard and mouse feature.

The app list has focus when the overlay opens. Search starts hidden and does
not reserve header space. S expands the header and shows and focuses search;
the first S is not added to the query. Once shown, search stays visible through
tab and focus changes until the next invocation hides it again. A click in the
visible field resumes typing. The query matches part
of `Title` or `ProcessName` in the current tab and ignores letter case. The
filter preserves row order. The query stays across tab changes while the
overlay is open and clears on the next invocation. It is not saved to disk.
An empty query restores all entries in that tab. Tab counts show filtered
counts. With no matches, the list shows **No matching apps** and **Change or
clear the search to see more apps.** Use Ctrl+A then Backspace or Delete to
clear the query.

While search has focus, letters, including D, P, and S, are text. Left, Right,
Home, End, and Delete retain their text-editing actions. With matches present,
the first Up or Down moves focus to the current result without advancing it.
Later arrow presses navigate the list. With no matches, Up/Down stays in search.
The current result has no selection highlight while search has focus; moving
to the list shows its highlight and enables D/P actions. S returns to search.
Enter opens the current match. Tab or Shift+Tab returns focus to the app list
without clearing the query. Clicking a tab or using the controller also
returns to list controls.
Ctrl+1 through Ctrl+9 opens the first through ninth filtered entry from either
search or list focus. Row
numbers show these targets; they are not relative to the current scroll
position.

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

### Source layout

The app has one Windows Forms project. Files are grouped by responsibility;
these folders are not separate projects or services.

```text
Build.ps1
LICENSE
NOTICE
README.md
docs/
  DESIGN.md
  UI-TESTING.md
  assets/
scripts/
  GenerateIcon.ps1
src/
  VerticalAppSwitcher/
    VerticalAppSwitcher.csproj
    Program.cs
    Application/
    UI/
    Input/
    Windows/
    Persistence/
    Models/
    Assets/
```

| Path under `src/VerticalAppSwitcher` | Contents |
|---|---|
| `Program.cs` | Entry point, DPI setup, and single-instance startup |
| `Application/` | App lifetime, action orchestration, recent-window tracking, startup registration, and diagnostics |
| `UI/` | The switcher overlay and shortcut setup form |
| `Input/` | Global hotkeys, shortcut definitions, and XInput navigation |
| `Windows/` | Window and tray discovery, `AppWindow` activation and close actions, foreground order, Explorer folders, virtual desktops, and native APIs |
| `Persistence/` | App state and shortcut settings stores |
| `Models/` | Window collections, snapshots, updates, and saved-data records |
| `Assets/` | Generated app icon |

Run `Build.ps1` from the repository root to build `VerticalAppSwitcher.exe`
in that folder. Project-local `bin/` and `obj/` folders hold intermediate build
output. The build script publishes to the root `bin/publish/<RID>` folder;
see section 18.

### Component responsibilities

| Component | Responsibility |
|---|---|
| `Program` | DPI setup, single-instance mutex, fatal error boundary |
| `SwitcherApplicationContext` | Application lifetime, service wiring, tray menu, startup setting, action orchestration |
| `GlobalHotKey` | Configurable invocation shortcut, second-instance message, overlay-scoped navigation hotkeys |
| `ShortcutSettingsStore` | Persist the selected shortcut and first-run setup state |
| `ShortcutSetupForm` | Guide shortcut selection and report registration conflicts |
| `XInputNavigationService` | Poll four XInput slots and emit intent-level actions |
| `SwitcherOverlay` | Drawing, search filtering, selection, mouse/keyboard interaction, action events |
| `WindowCatalog` | Discover and describe eligible visible/minimized windows |
| `ForegroundWindowOrder` | Track foreground use during the session and order Open rows |
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

One entry can be in its state tab and the Pinned tab. A recently closed pin can
also be in the Recent tab. Badge precedence is:

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

`AppStateStore.Compose` joins discovered windows and tray apps with saved pins
and recents. The overlay then assigns an entry to Open, Tray, Desktops, or
Recent by state and also to Pinned when it is saved.

`ForegroundWindowOrder` records foreground-use events during the switcher
session. `SwitcherOverlay.RebuildTabs` applies this history to Open only.
Windows with valid history are sorted by their latest foreground use, not by
usage frequency. Minimized windows use the same rule as other Open windows;
Windows Z-order does not push a tracked minimized window to the bottom.
Windows without valid history follow the tracked windows in their original
`EnumWindows` discovery order. If tracking is unavailable, all Open rows keep
discovery order.

History is held in memory, is limited to 256 windows, and resets when the
switcher stops. It cannot recover foreground use from before the session.
History checks the HWND, process ID, thread ID, and process start time. Window
create/destroy events clear history for a reused HWND. Windows whose identity
cannot be checked use the discovery-order fallback.

Foreground-use ordering does not change Tray, Desktops, Recent, or Pinned.
Pin status does not affect the order in Open, Tray, Desktops, or Recent.
Pinning changes the badge and membership in the Pinned tab, not an item's
priority in its state tab.

Pinned is a filter across all app states, not a separate state. It includes
pinned open windows, tray apps, recent apps, other-desktop windows, and saved
apps that are not running. Entries remain in their matching state tab too.
The Pinned tab uses saved pin order; recent identities are limited to 12.

At opening, the overlay selects the entry after the foreground entry in Open
when that entry is found; otherwise it selects the first available entry.
When Open is empty, it shows the first nonempty tab. Each tab keeps its own
selection and scroll position when the user changes tabs. A query change keeps
the selected row if it still matches; otherwise it selects the first match.
Scrolling adjusts to keep that selection visible.

A closed-window update does not change the active tab or the search query.
The selected row stays selected if it is still in that tab's filtered list.
If it leaves the list, the overlay selects the row at its former index, or the
last row if that index no longer exists. If no matches remain, the active tab
stays selected and empty. This applies when a row moves to Recent or Tray,
remains only in Pinned, or is removed.

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

`GlobalHotKey` owns a hidden native window and registers the selected
shortcut. Five presets are available, and a validated custom chord is stored
as a modifier-and-key ID. The default keeps `Win + Shift + Backtick` as an
optional layout-compatible alias.

The setup page checks a candidate with a temporary registration and releases
it at once. A chord already held by this app is treated as available. Changing
the shortcut unregisters the current combination, registers the new one, and
restores the previous combination if registration fails. Settings are written
atomically to `settings.json` only after Windows accepts the shortcut. The
setup page reports a conflict and lets the user choose or check another chord.
A pressed registered shortcut is captured during setup and does not open the
overlay.

While the overlay is visible and search does not have focus, the app
registers temporary navigation and action hotkeys. These let the user control
the list. D requests normal close or removal of a closed Recent entry.
Delete only removes a closed Recent entry. S focuses search, and Ctrl+1 through
Ctrl+9 opens a numbered entry. D uses no-repeat registration to prevent repeated
global close requests while held.

Search focus releases the overlay navigation hotkeys but keeps the invocation
shortcut active. The app checks queued global navigation actions again before
it runs them and ignores them if search has focus. Local search input handles
text editing, Up/Down, Enter, and Ctrl+1 through Ctrl+9. Leaving search restores
browsing hotkeys if the overlay is still visible. Hiding the overlay releases
them. `ProcessCmdKey` remains the local fallback.

### XInput

`XInputNavigationService` polls `xinput1_4.dll` every 16 ms across slots 0-3.

- Button edges trigger A/B/X/Y once.
- D-pad and left-stick vertical input select entries; horizontal input changes
  tabs.
- Stick threshold: `16000`.
- Held navigation repeat delay: 400 ms initially, then 120 ms.
- Previous state is updated even when overlay input is disabled, preventing a
  held button from firing when the overlay opens.

The service emits semantic actions instead of exposing button masks to the UI.

### Input hints

The footer shows one set of input hints at a time:

- Each time the overlay opens, it shows keyboard hints.
- A controller action changes the footer to Xbox-style gamepad hints.
- A keyboard or mouse action restores keyboard hints.
- Browsing hints include S Search and D Close. Search-focus hints show Up/Down
  Focus list, Enter Open, Tab List, and Ctrl+1..9 Open numbered app. They do not
  show letter-key close or pin commands while search has focus.

The input-device hint mode does not disable controls. Search focus changes
keyboard actions as described above. The footer does not combine keyboard keys
and controller buttons in one label.

## 11. Overlay UI and DPI

The overlay is a borderless, topmost, fully opaque WinForms window so text
from background apps does not show through it. The header, tabs, rows,
and footer are custom-drawn. Search uses a native text box for text input,
with the label **Search** and placeholder **Title or app name**.

Logical dimensions:

- Width: 390
- Header: 124 by default; 184 when search is shown, including five tab targets
  and the search field
- Row: 68
- Footer: 88
- Maximum visible rows: 9
- Work-area edge margin: 24

The overlay appears on the foreground window's monitor, centered vertically in
that monitor's working area. While visible, it recalculates its bounds and row
capacity after display, work-area, or DPI-change notifications. This keeps the
footer within the working area after a resolution change.

Rendering uses one logical coordinate system and a manual DPI scale. Icons are
converted to bitmaps and drawn with `Graphics.DrawImage` to use the same scale
as the text and rows.

The current implementation is system-DPI aware through `SetProcessDPIAware`
and `GetDpiForSystem`; it is not Per-Monitor-V2 aware. Search reuses its font
until the scaled size changes. WinForms can retain the previous font when an
equal font is assigned, so repeated layout must not dispose that active font.

The overlay hides when:

- Escape/B is pressed.
- An invocation shortcut is pressed again.
- Focus moves elsewhere, unless a context menu or protected initial-show
  interval is active.
- An entry is activated.

## 12. Pinning

Controller Y or keyboard P while browsing toggles a persisted pin by
application identity. P types text when search has focus.

- Pins appear in the separate Pinned tab immediately, in persisted pin order.
- An active pinned entry also remains in its state tab.
- All live rows sharing the pinned identity receive the pinned state.
- An offline pin remains launchable using its persisted executable, Explorer
  path, or packaged app identifier.

## 13. Closing and force-stop safety

Controller X and keyboard D request a normal close of a live selected window.
For a closed Recent entry, they remove its saved history and update the visible
list without removing its pin or closing a process. A saved offline pin without
Recent membership is left unchanged. D works only while browsing the app list;
it types text when search has focus. Delete removes only closed Recent entries
while browsing and edits text in search.

Close controls never request force-stop, including after a failed or repeated
close request.

### HWND-backed entry

1. Capture the process start time when the row is first observed.
2. Before closing, check that the process start time and HWND owner still match.
   For File Explorer, also resolve the current folder and check its path.
3. Post `WM_CLOSE`.
4. If posting fails, repeat the identity checks and retry with
   `SendMessageTimeout` (500 ms, `SMTO_ABORTIFHUNG`).
5. Preserve and report the Win32 error if the close request fails.

### Tray-only entry

A tray-only row has no window handle (HWND) that can receive `WM_CLOSE`.
It cannot receive a normal close request. The app reports this limit and
does not request force-stop.

### Explicit force-stop

Force-stop is available only as a separate right-click menu action. It shows
a Yes/No warning before it stops the selected process. The warning is owned
by the overlay. Overlay navigation pauses while it is open, and the overlay
stays visible. No process is stopped if the user selects No. Force-stop can
discard unsaved work.

When the user selects Yes, the app opens one process handle for both the
process-start-time check and termination. It also checks the HWND owner and,
for File Explorer, the folder path. If identity has changed or cannot be
verified, it reports the problem and does not terminate the process. These
checks narrow the gap between selection and action. They cannot make a
`WM_CLOSE` request atomic with identity validation; an Explorer folder can
change after validation.

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

When the overlay opens or a close action begins, it also copies eligible
entry metadata before the app can exit. The next tracker check confirms the
closure. This covers a quick exit after the entry is observed, but not an app
that both opens and closes between all observations.

Every detected closure is published to the visible overlay. Relaunchable
entries are persisted first and move to Recent; a saved pin stays in Pinned.
Rows that cannot be relaunched, or duplicate rows whose application identity
remains open, are removed. If closing a normal window leaves the same
application running only in the notification area, the row moves to Tray.
Each tab updates without closing and reopening the overlay. The active tab
does not change when a row moves or is removed; selection follows the rules in
section 5. Tracker checks normally occur every 100 ms while the overlay is
visible and every 500 ms in the background. Recent history is capped at 12
identities.

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

The repository-root `Build.ps1`:

1. Regenerates `src/VerticalAppSwitcher/Assets/AppIcon.ico` through
   `scripts/GenerateIcon.ps1`.
2. Publishes `src/VerticalAppSwitcher/VerticalAppSwitcher.csproj` in Release
   mode through the .NET 10 SDK.
3. Targets `win-x64` by default or the requested supported RID.
4. Produces a framework-dependent, single-file Windows executable under
   `bin/publish/<RID>` at the repository root.
5. Requests a normal shutdown if that exact root executable is running.
6. Copies the published executable to `VerticalAppSwitcher.exe` at the
   repository root.
7. Restarts it in background mode when it was running before the build.

`src/VerticalAppSwitcher/VerticalAppSwitcher.csproj` targets `net10.0-windows`,
enables Windows Forms, and embeds the generated icon. It copies the root
`LICENSE` and `NOTICE` files beside the published executable, outside the
single-file bundle. The root `global.json` requires a stable .NET 10 SDK and
rolls forward to the latest installed .NET 10 feature band.

The framework-dependent publish is intentional. It keeps the executable small
and allows installed .NET 10 security and servicing patches to apply without
republishing the app. The target PC therefore needs the matching .NET 10
Desktop Runtime; building additionally requires the .NET 10 SDK.

## 19. Safety and privacy

- The app performs no network communication.
- It does not elevate itself.
- Force-stop requires the separate right-click menu action and an explicit
  Yes response to its warning. D and X never request force-stop.
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
4. **Shortcut checks are scoped to Windows registration.** They do not detect
   combinations handled only inside another application, and the keyboard
   drawing uses a US layout.
5. **The foreground app can remain in Open.** Selection normally starts with
   the next Open entry.
6. **State has no schema version.** Current JSON evolution relies on additive,
   optional fields.
7. **Tray registration is Windows implementation-specific.** It relies on
   `NotifyIconSettings`, which should be revalidated after major Windows shell
   changes.
8. **Desktop tray apps must support repeat launch** to surface their existing
   UI; otherwise the row can identify and manage the process but may not show a
   window.
9. **Windows only.** Windows Forms, window discovery, global shortcuts, and
   XInput depend on Windows. macOS support requires a separate port and is not
   included in this implementation.
10. **Window permissions apply.** Windows can block close requests to elevated
    or protected windows. The reported Task Manager permission issue is
    unresolved. The app does not elevate itself.

## 21. Documentation maintenance

See [docs/UI-TESTING.md](./UI-TESTING.md) for focused manual checks.

Update this design document when architecture, behavior, input, discovery,
identity, persistence, safety, build, diagnostics, or known limits change.
Update the README when controls or build and use instructions change. Record
only verified results, and remove task-owned temporary diagnostics after checks.
