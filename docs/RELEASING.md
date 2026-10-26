# Releases

Every push to `main`, including a merged pull request, starts the Release
workflow. It selects a version, runs the update checks, builds Windows x64 and
Arm64 packages plus a universal macOS app, verifies checksums, and publishes the release automatically.
Starting the desktop app or editing an uncommitted file does not publish code.

## Pipelines

| Workflow | Trigger | Result |
| --- | --- | --- |
| `ci.yml` — CI | Pull request to `main`, push to `main`, or manual run | Builds both architectures and saves check-build artifacts for 14 days. |
| `build-packages.yml` — Build packages | Called by CI or Release | Runs update checks on x64, then builds both architecture packages. |
| `macos.yml` — macOS packages | macOS pull requests/main changes, manual run, or Release call | Runs core/offline updater checks and builds universal macOS ZIP plus checksum. |
| `release.yml` — Release | Push to `main`, version-tag push, or manual run | Selects the version, builds and checks packages, then publishes a GitHub Release. |

CI uses `0.0.0-ci.<run-number>` for its check artifacts. Release builds use the
selected release version. Publication depends on the Release workflow's own
successful checks and builds, not the timing of the separate CI run.

Release runs share a queue so version selection and publication cannot overlap.
Up to 100 pending runs can wait; GitHub cancels additional arrivals beyond that
limit. Active release runs are not canceled by new pushes.

Only the publication job has `contents: write`. Other jobs have read-only
repository access. Action versions are pinned to full commit hashes. No custom
secrets are needed; publication uses `GITHUB_TOKEN`.

## Automatic version selection

`scripts/Get-ReleaseVersion.ps1` reads the checked-out project `Version` and
remote version tags:

- If this commit already has a stable version tag, reuse that version on rerun.
- If the project version is newer than every stable tag, use the project version.
- Otherwise, increase the highest stable tag's patch number by one.

For example, project version `0.2.0` with latest tag `v0.1.0` produces `v0.2.0`.
The next change produces `v0.2.1`, then `v0.2.2`. Set a higher project `Version`
when a new minor or major version is appropriate. Automatic patch releases do
not write version-bump commits back to the repository. Local source builds
therefore show the project's base version, not necessarily the latest release.

The version is applied to executable metadata, package names, and `update.json`.
Components must be between 0 and 65534. If the patch component reaches that
limit, raise the project minor or major version.

`scripts/Publish-GitHubRelease.ps1` publishes against the exact built commit.
GitHub creates its tag as part of release creation if needed. The same workflow
publishes the assets; it does not depend on a token-created tag starting another
workflow. A rerun accepts an existing published release only if its tag points
to the same commit and all six assets are present. It does not replace assets.

## Check a release

1. Open the [Release workflow](https://github.com/ness001/vertical-app-switcher/actions/workflows/release.yml)
   for the merged commit and wait for all jobs to pass.
2. Open [GitHub Releases](https://github.com/ness001/vertical-app-switcher/releases).
   Confirm that the version and generated notes match the intended change.
3. Confirm the Windows x64/Arm64 and `osx-universal` ZIP files and their individual
   `.sha256` files are present. Windows ZIPs contain `VerticalAppSwitcher.exe`,
   `LICENSE`, `NOTICE`, `README.txt`, and `update.json` inside a versioned folder.
   The macOS ZIP contains only `Vertical App Switcher.app`, with LICENSE/NOTICE
   inside Contents/Resources. See [macOS build/update instructions](../macos/README.md).

### Install the macOS download

1. Download `VerticalAppSwitcher-<version>-osx-universal.zip` from the release.
2. Extract it and move **Vertical App Switcher.app** to **Applications**.
3. Launch the app, then enable it in **System Settings → Privacy & Security →
   Accessibility**.

The universal app requires macOS 13 or newer and supports Apple Silicon and Intel
without .NET or developer tools. In-app updates need write access to its installed
location; no administrator elevation is attempted.

CI uses an ad-hoc signature, not a Developer ID certificate, and does not notarize
packages. Gatekeeper may block a downloaded app. Follow organizational security
policy; a checksum verifies integrity, not publisher identity. The persistent
local development certificate used for rebuilds does not establish public trust.
A Gatekeeper-trusted public distribution requires a Developer ID Application
certificate and Apple notarization; these are not configured by this workflow.
Do not describe these artifacts as notarized. Accessibility may need reapproval
after an ad-hoc update.

The release notes include installation instructions and the runtime requirement.
Target PCs need Windows 10 or 11 and the matching
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
Packages are framework-dependent and unsigned. Follow your organization's
security policy when a security product shows a warning.

A failed check or build blocks publication. Fix the cause and rerun the failed
workflow. If publication failed, inspect GitHub Releases first: a partially
created release requires review. Do not move tags, replace published assets,
or bypass a failed check. Commit a fix to `main` for a new version when needed.

## Explicit versions and prereleases

A pushed version tag still supports an explicit release. The tagged commit
must be in `main` history. Use `vMAJOR.MINOR.PATCH`, or a prerelease such as
`v0.3.0-rc.1`, with no numeric leading zeros or build metadata (`+...`).
Prerelease tags publish prereleases; they do not increase the stable patch
sequence. A manual workflow run on `main` uses automatic version selection.

## In-app update contract

Starting with version 0.2.0, packages include `update.json` with schema version 1,
the release version, runtime identifier, and minimum .NET Desktop Runtime.
Keep the manifest aligned with project framework and runtime requirements.
The updater permits only the five documented files, verifies executable version
and architecture, and rejects unknown package layouts. A future contract change
must preserve compatibility or require manual installation.

Stable releases use GitHub's latest-release API. Prereleases are not offered
automatically. Older versions are not installed over newer versions. Version
0.1.0 has no updater and needs one manual upgrade. Installation of later updates
still requires the user's **Update and restart** action in the app; automatic
GitHub publication does not remove that choice.

## Build packages locally

Use Windows, PowerShell, and the .NET SDK selected by `global.json`:

```powershell
pwsh -NoProfile -File .\scripts\Publish-Release.ps1 `
    -RuntimeIdentifier win-x64 -Version 0.2.0
pwsh -NoProfile -File .\scripts\Publish-Release.ps1 `
    -RuntimeIdentifier win-arm64 -Version 0.2.0
```

Packages and checksums are saved in `bin\release`. Use `-OutputDirectory <path>`
for another folder. The script refuses to overwrite existing output. It does
not replace or restart the normal app.

On macOS, build a universal ZIP without replacing a running development app:

```bash
VERSION=0.2.0 bash macos/package.sh
```

The ZIP and checksum are saved in `bin/release-macos`; set `PACKAGE_OUTPUT_DIR`
for another folder. Packaging defaults to ad-hoc signing even when a local
persistent development certificate exists, and refuses to overwrite the same
version's artifacts. `CODE_SIGN_IDENTITY` can explicitly select a Developer ID
Application certificate already installed in the Keychain; notarization is still
a separate distribution step.

Compare a downloaded ZIP's hash with its checksum file:

```powershell
Get-FileHash .\VerticalAppSwitcher-0.2.0-win-x64.zip -Algorithm SHA256
Get-Content .\VerticalAppSwitcher-0.2.0-win-x64.zip.sha256
```

The hash values must match; letter case does not matter. This checks file
integrity, not publisher identity. Run applicable [UI checks](UI-TESTING.md)
before merging app changes; automated checks do not replace them.
