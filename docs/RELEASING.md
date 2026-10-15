# Releases

GitHub Actions builds release packages for Windows x64 and Arm64. Each package
contains a framework-dependent, single-file executable, `LICENSE`, `NOTICE`,
and `README.txt`. The target PC needs Windows 10 or 11 and the matching
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
The executable is not code-signed. Follow your organization's security policy
when Windows or a security product shows a warning.

## Pipelines

| Workflow | Trigger | Result |
| --- | --- | --- |
| `ci.yml` — CI | Pull request to `main`, push to `main`, or manual run | Builds both architectures and saves ZIP files and SHA-256 checksums as workflow artifacts for 14 days. |
| `build-packages.yml` — Build packages | Called by CI or Release | Uses `global.json` and `scripts/Publish-Release.ps1` to build the packages. |
| `release.yml` — Release | Push of a tag that starts with `v` | Checks the tag and commit, builds both architectures, verifies checksums, and creates a **draft** GitHub Release with all four assets. |

CI uses the version `0.0.0-ci.<run-number>`. These are check builds, not published
releases. A failed build prevents release creation. There is no automated app
test suite; compilation and package checks do not replace the manual
[UI checks](UI-TESTING.md).

The workflows use GitHub-hosted runners and the built-in `GITHUB_TOKEN`.
No repository secrets are required. Build jobs have read-only repository
access. Only the draft-release job has `contents: write`. Action versions are
pinned to full commit hashes.

## Prepare a release

1. Merge the intended changes into `main`. Confirm that CI passed for that
   commit.
2. Choose a new version tag: `vMAJOR.MINOR.PATCH`, for example `v1.2.3`.
   A prerelease can use a suffix such as `v1.2.3-rc.1`. Version components must
   be between 0 and 65534 for Windows file metadata. Do not use leading zeros
   in numeric components or build metadata (`+...`).
3. Check the app on the target architectures. Complete the applicable
   [UI checks](UI-TESTING.md), including startup and shortcut setup.
4. From an up-to-date `main`, create and push the chosen tag. Replace the
   example version before running these commands:

   ```powershell
   git switch main
   git pull --ff-only origin main
   git tag -a v1.2.3 -m "Release v1.2.3"
   git push origin v1.2.3
   ```

The tag must point to a commit in `main` history. The tag supplies the version
for the executable metadata and package filenames; the project file does not
need a version edit.

## Review and publish

1. Open the [Release workflow](https://github.com/ness001/vertical-app-switcher/actions/workflows/release.yml)
   and wait for all jobs to pass.
2. Open the draft in [GitHub Releases](https://github.com/ness001/vertical-app-switcher/releases).
   Confirm that the generated notes describe the release. Add the Desktop
   Runtime requirement and the unsigned-executable notice from this guide.
3. Confirm that these four assets are present, with the chosen version:

   ```text
   VerticalAppSwitcher-1.2.3-win-x64.zip
   VerticalAppSwitcher-1.2.3-win-x64.zip.sha256
   VerticalAppSwitcher-1.2.3-win-arm64.zip
   VerticalAppSwitcher-1.2.3-win-arm64.zip.sha256
   ```

4. Download and extract the ZIP for each target PC. Confirm that the app starts
   with the matching Desktop Runtime installed. Keep the license and notice
   files with the executable.
5. Review the prerelease setting. A version with a suffix is marked as a
   prerelease by the workflow. Select **Publish release** only when the assets
   and notes are ready.

The pipeline does not publish a release automatically. It does not replace
assets on an existing release. If a run fails before draft creation, fix the
cause and rerun the failed jobs. If the draft already exists, review it before
retrying. For changed source, use a new version tag rather than moving a tag or
overwriting a published package.

## Build packages locally

Use Windows, PowerShell, and the .NET SDK selected by `global.json`:

```powershell
pwsh -NoProfile -File .\scripts\Publish-Release.ps1 `
    -RuntimeIdentifier win-x64 -Version 1.2.3
pwsh -NoProfile -File .\scripts\Publish-Release.ps1 `
    -RuntimeIdentifier win-arm64 -Version 1.2.3
```

Packages and individual SHA-256 files are saved in `bin\release`. Use
`-OutputDirectory <path>` to select another output folder. The script refuses
to replace an existing ZIP or checksum file. Its temporary staging folder is
removed after the build. It does not replace the repository-root executable
or stop or restart a running app.

To check a downloaded package, compare its hash with its `.sha256` file:

```powershell
Get-FileHash .\VerticalAppSwitcher-1.2.3-win-x64.zip -Algorithm SHA256
Get-Content .\VerticalAppSwitcher-1.2.3-win-x64.zip.sha256
```

The two hash values must match; letter case does not matter. A matching hash
checks file integrity. It does not replace executable signing or confirm the
publisher's identity.
