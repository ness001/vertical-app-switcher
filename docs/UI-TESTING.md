# UI testing

Use this procedure for focused checks of Vertical App Switcher. It describes
expected behavior, not completed test results. Read the current source when a
change affects this contract. See [README.md](../README.md) for controls and
[DESIGN.md](../DESIGN.md) for the design.

## Scope and safety

- Select only checks that cover the change and its main failure paths. Do not
  run the full matrix for each change.
- Use the normal app and user profile for visual checks. Keep the existing
  startup setting, shortcut, pins, and history. Do not replace the normal app
  with a preview build or reset its data to make a test pass.
- Use disposable test windows with no user work for activation and close
  checks. Identify the selected row before each action. Never close an actual
  user app to test a binding.
- Use fake entries and a temporary state store in a test harness for pin and
  history mutations. Do not change the user's pins or remove their Recent
  entries. Closing even a disposable window in the normal app can add Recent
  history and remove its oldest entry. Use a test overlay with temporary state
  for close checks when this would affect user history; record that limit.
  A callback-only test does not prove a real state change or close.
- Do not test force-stop on user apps. A disposable target is required. Check
  the warning and its No path first. A Yes-path test needs explicit approval.
- Preserve unrelated processes and unsaved work. Do not stop processes by name
  alone. Do not delete or overwrite profile files.

The normal profile files are under `%LOCALAPPDATA%\VerticalAppSwitcher`:
`state.json` holds pins and recent history, `settings.json` holds shortcut
settings, and `switcher.log` holds diagnostics.

## Build and app lifecycle

Run commands from the repository root in Windows PowerShell. The project is
native Windows Forms, targets `net10.0-windows`, and needs the .NET 10 SDK to
build. The default target is Windows x64. The root executable is
framework-dependent and needs the matching .NET 10 Desktop Runtime.

Build only if the changed code has not already been built:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

`Build.ps1` publishes Release output under `bin\publish\win-x64` and copies the
single-file executable to `VerticalAppSwitcher.exe` in the repository root.
Before replacement, it finds a running instance by its full executable path,
requests a normal shutdown, and waits for exit. If that instance was running,
it restarts the root executable with `--background`. If it was stopped, the
script does not start it. A shutdown failure stops the build replacement; use
the switcher's tray-menu Exit command instead of force-stopping it.

Do not rerun a completed build just to open the overlay. If the app is stopped,
launch it explicitly. If it is already running, the same command opens the
existing overlay rather than starting a second instance:

```powershell
.\VerticalAppSwitcher.exe
```

The supported lifecycle options are:

| Command | Behavior |
| --- | --- |
| `.\VerticalAppSwitcher.exe` | Start and show the overlay, or show the existing instance. |
| `.\VerticalAppSwitcher.exe --background` | Start quietly if no instance is running. First-run setup or a shortcut conflict can still show setup. If an instance is running, this also asks it to show. |
| `.\VerticalAppSwitcher.exe --shutdown` | Ask the existing instance to exit normally. Do nothing if none is running. |

Use `--shutdown` only when a shutdown is needed for the task and the existing
instance has been identified. The app uses a session-local single-instance
mutex; a launch from another folder does not create an isolated profile.
Automatic startup uses the executable path with `--background`. Leave the
user's startup setting unchanged.

## Choose focused checks

Use native Windows UI Automation (UIA) for available control names and focus
state, plus a direct desktop or window screenshot for rendered appearance.
The app paints its own rows; do not assume every row is a separate UIA element.
The search text box is named **Search current tab**. The overlay's accessible
description includes the current tab, result count, match, and input mode.
Do not install an unrelated browser or Electron test stack for this app.

| Change | Small check set | Evidence or limit to record |
| --- | --- | --- |
| Layout, color, or text | Inspect the changed state, one populated list, and a relevant empty state. | Direct screenshot; record screen size and Windows scale. |
| Search or keyboard input | Run the physical keyboard flow below with disposable windows. Check mouse focus transfer if affected. | Record the actual keys, focus, selected row, and outcome. |
| Font or layout lifetime | Repeat search opening and overlay reopening; check the font regression below. | Rendered text, new log entries, and any message-loop exception. |
| Tabs or state updates | Check the affected tab and one transition with controlled test data. | Record query, row order, count, and selection before and after. |
| Controller input | Use a connected XInput controller for the affected actions. | Record controller availability and actions used. A direct method call is not a device test. |
| DPI or screen placement | Check the current display. Add a second scale or display only if available and relevant. | Record each actual configuration. State unavailable configurations as not tested. |
| Launch or startup | Check only the changed lifecycle path against the root executable. | Record executable path and whether the instance was already running. Do not change startup settings without approval. |

For visual checks, inspect tab labels, row text and icons, number labels,
selection highlight, search border, empty-state text, and footer hints where
relevant. Capture the overlay without unrelated private content. A screenshot
shows one state; it does not prove keyboard delivery or later behavior.

## Search and keyboard flow

Prepare disposable windows with distinct titles. Use a query with at least two
known matches, then one with no matches. For shortcuts that activate a result,
ensure every possible target is a test window; otherwise use fake entries and
record that real activation was not tested.

1. Open the overlay. Search is hidden, with no blank search area. Tabs are
   **Open, Pinned, Recent, Tray, Desktops**, in that order.
2. Press `S`. Search appears and receives focus. This first `S` is consumed;
   it is not part of the query. Type part of a known title or process name.
   Matching ignores letter case and keeps the current tab's existing order.
   Counts show matching entries. Clearing the query restores the full list.
3. While search has focus, type `D`, `P`, and `S`. They must be text, not close,
   pin, or search commands. Check `Left`, `Right`, `Home`, `End`, and `Delete`
   as text-editing keys. `Ctrl + A` then `Backspace` or `Delete` clears text.
   No app row has the selection highlight while search has focus.
4. Restore the query. Press `Up` or `Down` once. Focus moves to the current
   match without skipping it, and its selection highlight appears. Later
   arrow presses move through the list. Press `S` to return to the unchanged
   query. `Tab` and `Shift + Tab` also return from search to list controls
   without clearing the query.
5. With a disposable window selected and list focus confirmed, press `D`.
   It must request only a normal close. Repeat this flow with a test window
   that cancels its close request if repeated-close behavior changed. Repeated
   presses must never become force-stop. This physical **S, query, arrow, D**
   flow is required for input changes; a handler-only check is not a substitute.
   If physical input or a safe test target is unavailable, report this check
   as blocked. Do not close user apps or report an unobserved pass.
6. With a live disposable row selected, check that `C` and `Delete` do not
   close it. `D` is the only keyboard close key. On a closed Recent entry,
   `D`, controller `X`, and `Delete` remove its history, not its saved pin.
   Test that mutation with fake data and a temporary state store, not user
   history. Check the Remove footer label and that an offline pin without
   Recent membership is unchanged.
7. Enter a query with no matches. The list shows **No matching apps** and
   **Change or clear the search to see more apps.** `Up` and `Down` keep focus
   in search. `Enter` must not activate an unrelated app.
8. Check `Enter` with a known match. Check `Ctrl + 1` through `Ctrl + 9` as
   needed for the change, including keypad digits. They refer to the first
   nine entries in the filtered list, not the first visible row after scroll.
   A missing numbered result must not activate another row. Both search and
   list focus support these activation shortcuts.
9. Change tabs with a click, or return to the list and use `Left` or `Right`.
   The query remains and filters each tab without changing its base order.
   Search stays visible after focus returns to the list. Dismiss and invoke
   the overlay again: search is hidden and the query is cleared.

The Pinned tab includes pinned entries in any state: open, tray, another
desktop, recent, or not running. Pinning does not remove an entry from its
normal tab or give it priority there. Pinned uses its own saved pin order.
Use fake data to cover these states without changing user pins.

If a close changes the visible list, check that the current tab and query stay
in place. Selection moves to the nearest remaining match when necessary. An
empty result stays in the same tab. A tray-only entry has no window handle;
normal close must report the limit, not force-stop the process.

## Controller and context-menu safety

Run these checks only when the changed behavior needs them:

- With a real XInput controller, check the affected direction or `A`/`B`
  action. `X` requests normal close for live windows or removes closed Recent
  entries. Use disposable windows and controlled history. `Y` toggles pins;
  use a controlled fixture, not a user pin.
- Search is keyboard/mouse only; no controller search binding is required.
  Do not add or test a controller search menu as part of these checks.
- Check that controller input shows gamepad hints and keyboard or mouse input
  restores keyboard hints. Check the search-to-list focus transition if it
  changed.
- Inspect the row context menu with a disposable target. Normal close and
  force-stop are separate actions. Force-stop must show a Yes/No warning with
  No as the default. While the warning is open, navigation and action inputs
  must not act on another row. Cancel the warning.
- Check mouse selection, search focus, tab clicks, and wheel navigation only
  where the change can affect them.

## Search font regression

A repeated layout can assign an equal `Font` value while WinForms retains the
old instance. If code then disposes that retained instance, later text layout
or painting can fail. A successful build does not detect this fault.

For a relevant change, open the overlay, press `S`, type, return to the list,
and return to search. Dismiss and reopen the overlay a few times at the same
scale. Confirm that the search font remains usable and the text still renders.
Include normal closing or disposal of a test overlay. For a real app exit,
use only a task-approved normal shutdown and restore the app as needed.

A focused harness can check repeated layout, `searchBox.Font` measurement,
painting, and disposal on an STA thread with a WinForms message loop. If it
creates fake `AppWindow` entries, supply an owned real icon, for example a
clone of `SystemIcons.Application`; a null icon can fail painting before the
font path is tested. Keep fixture icons and fonts alive for their full use.
Observe message-loop exceptions as well as direct-call exceptions. Reflection
calls alone may miss failures raised later by native control messages.

Do not claim a DPI change was tested by repeating layout at one scale. If no
second display or scale was used, record that limit.

## Evidence and completion

Record the tested executable path, build status, selected checks, observed
results, and any skipped checks. Compare only new diagnostic log entries from
the test period; an old error is not evidence of a new failure.

Keep these evidence types separate:

- Source inspection proves what the code says, not what ran.
- A build proves compilation and publishing, not rendered UI behavior.
- A direct handler call or native window message can check a narrow path but
  bypasses real key delivery. Label these as direct-handler or native-message
  checks, separate from injected input and physical input.
- Injected input that produces no visible response is inconclusive. Do not
  report it as a passed keyboard test or a confirmed app defect without more
  evidence.
- Physical keyboard and controller checks require actual device input.
  Record missing devices or access limits; do not claim those checks passed.
- Screenshots, UIA state, logs, and observed actions support different claims.
  Report only the behavior each check actually verified.

Clean up only test-owned windows and files. Leave user state and unrelated
processes unchanged. Report runtime checks that were not run.
