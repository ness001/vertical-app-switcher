#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT="${PACKAGE_OUTPUT_DIR:-$ROOT/bin/release-macos}"
VERSION="${VERSION:-$(/usr/libexec/PlistBuddy -c 'Print :VerticalAppSwitcherReleaseVersion' "$ROOT/macos/Info.plist")}"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd -P)"
TEMP="$(mktemp -d "$OUTPUT/package.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT

ASSET="VerticalAppSwitcher-$VERSION-osx-universal.zip"
if [[ -e "$OUTPUT/$ASSET" || -e "$OUTPUT/$ASSET.sha256" ]]; then
    printf 'Package output already exists for %s; use a new version or PACKAGE_OUTPUT_DIR.\n' "$VERSION" >&2
    exit 1
fi

# Release packages never inherit the machine's local development identity.
# Build in a private staging directory, never over a locally running app.
VERSION="$VERSION" ARCH=universal CODE_SIGN_IDENTITY="${CODE_SIGN_IDENTITY:--}" BUILD_OUTPUT_DIR="$TEMP/build" bash "$ROOT/macos/build.sh"
mkdir -p "$TEMP/archive"
mv "$TEMP/build/Vertical App Switcher.app" "$TEMP/archive/"
(cd "$TEMP/archive" && COPYFILE_DISABLE=1 /usr/bin/zip -X -q -r "$TEMP/$ASSET" 'Vertical App Switcher.app')
(cd "$TEMP" && /usr/bin/shasum -a 256 "$ASSET" > "$ASSET.sha256")
mv "$TEMP/$ASSET" "$TEMP/$ASSET.sha256" "$OUTPUT/"
printf 'Packaged %s and %s.sha256 (not notarized).\n' "$OUTPUT/$ASSET" "$OUTPUT/$ASSET"
