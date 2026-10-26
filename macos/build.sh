#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTPUT="${BUILD_OUTPUT_DIR:-$ROOT/bin/macos}"
APP="$OUTPUT/Vertical App Switcher.app"
SDK="${SDKROOT:-$(xcrun --sdk macosx --show-sdk-path)}"
ARCH="${ARCH:-$(uname -m)}"
VERSION="${VERSION:-$(/usr/libexec/PlistBuddy -c 'Print :VerticalAppSwitcherReleaseVersion' "$ROOT/macos/Info.plist")}"
IDENTITY="${CODE_SIGN_IDENTITY:-}"
LOCAL_IDENTITY="Vertical App Switcher Local Development"
if [[ -z "$IDENTITY" ]]; then
    if /usr/bin/security find-identity -v -p codesigning 2>/dev/null | /usr/bin/grep -F "\"$LOCAL_IDENTITY\"" >/dev/null; then
        IDENTITY="$LOCAL_IDENTITY"
    else
        IDENTITY="-"
    fi
fi

# Match the release version format, without a leading v or build metadata.
NUMBER='(0|[1-9][0-9]*)'
IDENTIFIER='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
if [[ ! "$VERSION" =~ ^$NUMBER\.$NUMBER\.$NUMBER(-$IDENTIFIER(\.$IDENTIFIER)*)?$ ]]; then
    printf 'VERSION must be a three-part version such as 1.2.3 or 1.2.3-rc.1.\n' >&2
    exit 1
fi
if [[ "$ARCH" != "arm64" && "$ARCH" != "x86_64" && "$ARCH" != "universal" ]]; then
    printf 'ARCH must be arm64, x86_64, or universal.\n' >&2
    exit 1
fi
if [[ "$IDENTITY" != "-" && "$IDENTITY" != "$LOCAL_IDENTITY" && "$IDENTITY" != "Apple Development: "* && "$IDENTITY" != "Developer ID Application: "* ]]; then
    printf 'CODE_SIGN_IDENTITY must be the local development identity, an Apple Development/Developer ID identity, or -.\n' >&2
    exit 1
fi

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd -P)"
APP="$OUTPUT/Vertical App Switcher.app"
check_stopped() {
    if /bin/ps -axo command= | /usr/bin/grep -F "$APP/Contents/MacOS/VerticalAppSwitcher" | /usr/bin/grep -v grep >/dev/null; then
        printf 'Quit the app at %s before rebuilding. No process was stopped.\n' "$APP" >&2
        exit 1
    fi
}
check_stopped
TEMP="$(mktemp -d "$OUTPUT/build.XXXXXX")"
trap 'rm -rf "$TEMP"' EXIT

build_arch() {
    xcrun swiftc -O -sdk "$SDK" -target "$1-apple-macosx13.0" \
        "$ROOT"/macos/Sources/VerticalAppSwitcher/*.swift -o "$TEMP/VerticalAppSwitcher-$1"
}

if [[ "$ARCH" == "universal" ]]; then
    build_arch arm64
    build_arch x86_64
    xcrun lipo -create "$TEMP/VerticalAppSwitcher-arm64" "$TEMP/VerticalAppSwitcher-x86_64" -output "$TEMP/VerticalAppSwitcher"
else
    build_arch "$ARCH"
    mv "$TEMP/VerticalAppSwitcher-$ARCH" "$TEMP/VerticalAppSwitcher"
fi

STAGED_APP="$TEMP/Vertical App Switcher.app"
mkdir -p "$STAGED_APP/Contents/MacOS" "$STAGED_APP/Contents/Resources"
cp "$TEMP/VerticalAppSwitcher" "$STAGED_APP/Contents/MacOS/VerticalAppSwitcher"
cp "$ROOT/macos/Info.plist" "$STAGED_APP/Contents/Info.plist"
BASE_VERSION="${VERSION%%-*}"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $BASE_VERSION" "$STAGED_APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BASE_VERSION" "$STAGED_APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :VerticalAppSwitcherReleaseVersion $VERSION" "$STAGED_APP/Contents/Info.plist"
cp "$ROOT/LICENSE" "$ROOT/NOTICE" "$STAGED_APP/Contents/Resources/"
if [[ "$IDENTITY" == "-" ]]; then
    /usr/bin/codesign --force --sign - "$STAGED_APP"
elif [[ "$IDENTITY" == "Developer ID Application: "* ]]; then
    /usr/bin/codesign --force --options runtime --timestamp --sign "$IDENTITY" "$STAGED_APP"
else
    /usr/bin/codesign --force --sign "$IDENTITY" "$STAGED_APP"
fi
/usr/bin/codesign --verify --strict "$STAGED_APP"
check_stopped
rm -rf "$APP"
mv "$STAGED_APP" "$APP"
if [[ "$IDENTITY" == "-" ]]; then
    printf 'Built %s (%s, version %s, local ad-hoc signature; NOT notarized).\n' "$APP" "$ARCH" "$VERSION"
else
    printf 'Built %s (%s, version %s, signed with %s; NOT notarized).\n' "$APP" "$ARCH" "$VERSION" "$IDENTITY"
fi
