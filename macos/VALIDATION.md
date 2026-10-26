# Native macOS parity validation — 2026-10-25

Scope: Windows feature areas adapted to macOS, excluding controller/XInput.
This records evidence, not a claim that every platform configuration was tested.

## Twenty focused source-review passes

Four reviewers each completed five distinct read-only passes. Main integration
also reviewed the fixes and ran the checks below.

| Pass | Subject | Outcome / correction |
| --- | --- | --- |
| 1 | Shortcut registration and persistence rollback | Preserve old registration on failure; unchanged shortcut now still registers/saves first-run choice. |
| 2 | Search and key focus | Live testing additionally found and fixed initial field focus and native Command-A/C/V/X/Z handling. |
| 3 | Panel/menu lifecycle | Local monitor scoped to panel; outside clicks/deactivation dismiss; modal destructive actions guarded. |
| 4 | Single instance and login startup | Added login Apple-event detection so login launches stay in background. |
| 5 | Accessibility and visual state | Reviewed permission card, selected traits, Reduce Motion and empty states; inspected rendered fixture UI. |
| 6 | AX activation/minimize | Target timeouts, live minimized state, application identity and restore/raise handling reviewed. |
| 7 | Spaces membership/switching | Dynamic APIs, membership and destination selection reviewed; unknown membership never guessed. |
| 8 | Menu-bar ownership | AX extras and status-window evidence, geometry and live-window dedup reviewed. |
| 9 | Closed history and restore | Transient omissions retained; invalid/terminated windows recorded; file URL restoration reviewed. |
| 10 | Observation lifetime/timeouts | Failed observer registration is not cached; periodic synchronization retries. |
| 11 | Release/asset trust | Exact repository asset URLs, stable version checks and filename-bound SHA-256 reviewed. |
| 12 | Updater resource bounds | Preparation extraction removed after verification; helper payloads pruned after completion; prelaunch failures cleaned. |
| 13 | ZIP validation/extraction | Paths, types, local/central consistency, expansion and CRC reviewed. |
| 14 | Update approval/normal exit | Separate installation guard prevents check completion from re-enabling another installation. |
| 15 | Rollback/restart | Failed restart now checks for another running instance before replacing its bundle. |
| 16 | State compatibility | Legacy shortcuts/history migrate; unreadable state is preserved; atomic writes retained. |
| 17 | Tabs/order/pins | Current/other Spaces, Tray, offline pins, recent identities and filtered selection reviewed. |
| 18 | Release integration | Both Windows architectures retained; macOS required before release publication; six exact assets checked. |
| 19 | Package/version/signature | Universal ZIP naming and updater contract aligned; full prerelease metadata retained; ad-hoc limits disclosed. |
| 20 | Coverage/docs | Standalone model/Spaces regression checks added to macOS CI alongside XCTest and updater checks. |

## Final feature-gap comparison

The final bounded Windows README-to-macOS comparison identified two remaining
implementation gaps: per-tab scroll restoration and diagnostic coverage.
Both were implemented: native scroll offsets are retained per tab, tab restores
do not recenter selection, and shortcut/history/activation/close/persistence
branches emit diagnostics. Pinned recent-document and cross-tab removal fixes
also remain covered by regression fixtures. No controller code is included.

## Executed checks

- Universal Intel/Apple Silicon app build and ZIP packaging succeeded using
  installed macOS 26.5 SDK (the default SDK 27 mismatches the installed compiler).
- Standalone model checks passed: pin independence, bounded/deduplicated history,
  migration, corrupt-state preservation, search/Tray/Desktops/Pinned filtering,
  empty selection and stale-focus selection regression. Pinned preserves closed
  document/folder identities alongside live windows of the same app; Recent
  removal and footer actions also work from Pinned without removing the pin.
- Spaces fixtures passed: current/other/sticky/unknown memberships and destination.
- Offline updater tests passed against the actual universal ZIP: versions,
  trusted URLs, checksum binding, ZIP structure, sizes, CRC and symlink rejection.
- PowerShell release tests passed with mocked Git/GitHub I/O: version selection,
  complete rerun, missing assets and bad checksum rejection. Nothing published.
- Read-only live lookup found 15 menu-bar owning apps; dedup/exclusion checks
  passed. Managed Spaces query succeeded with one display and one Space.
- A disposable native app with two test windows passed actual discovery,
  specific-window focus, minimized restoration, and normal-close checks. Its
  process was removed and original foreground application restored. No user
  windows were closed.
- A separate demo app passed native UI inspection: five tabs, populated/empty
  search, Command-F focus, Command-A replacement, letters D/P/S as search text,
  Down returning to list, shortcut guide/availability controls and Cancel.
  Fixture processes were stopped; normal profile was not used for mutation.
- `git diff --check` passed.

## Remaining validation limits

- Only one preexisting Space/display was available. Cross-Space switching and
  multi-display/full-screen combinations are implemented but not exercised live.
- Live installation/rollback of an update and login-session relaunch were not
  exercised; updater structure/archive checks and lifecycle source reviews ran.
- The installed SwiftPM crashes with a BuildServerProtocol missing symbol, so
  XCTest was not run locally. Standalone checks ran; XCTest remains in CI.
- Builds use ad-hoc signing unless a Developer ID identity is supplied, and are
  not notarized. macOS may require Accessibility reapproval after rebuilding.
- Private Spaces interfaces may change across macOS releases. Hosted menu-bar
  items not attributed/exposed by macOS cannot reliably identify their origin.

Final package: `bin/release-macos/VerticalAppSwitcher-0.2.0-osx-universal.zip`.
The existing running MVP was not replaced. No commits, pushes or releases made.
