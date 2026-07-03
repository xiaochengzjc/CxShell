#!/usr/bin/env bash
set -euo pipefail

APP_NAME="${APP_NAME:-CxShell}"
BUNDLE_IDENTIFIER="${BUNDLE_IDENTIFIER:-com.cxshell.app}"
BUNDLE_VERSION="${BUNDLE_VERSION:-1.0.0}"
BUNDLE_SHORT_VERSION="${BUNDLE_SHORT_VERSION:-$BUNDLE_VERSION}"
MIN_MACOS_VERSION="${MIN_MACOS_VERSION:-11.0}"
PUBLISH_DIR="${PUBLISH_DIR:-}"
ARTIFACT_DIR="${ARTIFACT_DIR:-}"
ARCH="${ARCH:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ICON_NAME="${ICON_NAME:-CxShell}"
ICON_SOURCE="${ICON_SOURCE:-$REPO_ROOT/Assets/CxShellLogo.png}"
ICON_FILE="$ICON_NAME.icns"

if [ -z "$PUBLISH_DIR" ]; then
  echo "PUBLISH_DIR is required." >&2
  exit 1
fi

if [ -z "$ARTIFACT_DIR" ]; then
  echo "ARTIFACT_DIR is required." >&2
  exit 1
fi

if [ ! -d "$PUBLISH_DIR" ]; then
  echo "Publish directory was not found: $PUBLISH_DIR" >&2
  exit 1
fi

if [ ! -f "$PUBLISH_DIR/$APP_NAME" ]; then
  echo "macOS executable was not found: $PUBLISH_DIR/$APP_NAME" >&2
  exit 1
fi

APP_ROOT="$ARTIFACT_DIR/$APP_NAME.app"
CONTENTS_DIR="$APP_ROOT/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

rm -rf "$APP_ROOT"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

cp -R "$PUBLISH_DIR"/. "$MACOS_DIR"/
chmod +x "$MACOS_DIR/$APP_NAME"

if [ -f "$ICON_SOURCE" ]; then
  if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    ICON_WORK_DIR="$(mktemp -d)"
    ICONSET_DIR="$ICON_WORK_DIR/$ICON_NAME.iconset"
    mkdir -p "$ICONSET_DIR"

    sips -z 16 16 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
    sips -z 32 32 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
    sips -z 32 32 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
    sips -z 64 64 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
    sips -z 128 128 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
    sips -z 256 256 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
    sips -z 256 256 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
    sips -z 512 512 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
    sips -z 512 512 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
    sips -z 1024 1024 "$ICON_SOURCE" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null
    iconutil -c icns "$ICONSET_DIR" -o "$RESOURCES_DIR/$ICON_FILE"
    rm -rf "$ICON_WORK_DIR"
  else
    echo "Warning: sips/iconutil not found; macOS app icon was not generated." >&2
  fi
else
  echo "Warning: icon source was not found: $ICON_SOURCE" >&2
fi

cat > "$CONTENTS_DIR/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_IDENTIFIER</string>
    <key>CFBundleIconFile</key>
    <string>$ICON_FILE</string>
    <key>CFBundleIconName</key>
    <string>$ICON_NAME</string>
    <key>CFBundleVersion</key>
    <string>$BUNDLE_VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$BUNDLE_SHORT_VERSION</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>MacOSX</string>
    </array>
    <key>LSMinimumSystemVersion</key>
    <string>$MIN_MACOS_VERSION</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

printf 'APPL????' > "$CONTENTS_DIR/PkgInfo"

mkdir -p "$ARTIFACT_DIR"
cat > "$ARTIFACT_DIR/README-macos.txt" <<README
$APP_NAME macOS package${ARCH:+ ($ARCH)}

This package is not notarized. If macOS blocks the app after download, run:

chmod +x $APP_NAME.app/Contents/MacOS/$APP_NAME
xattr -dr com.apple.quarantine $APP_NAME.app

The RDP native bridge is included when libCxRdpBridge.dylib is present in:

$APP_NAME.app/Contents/MacOS/
README

echo "Created macOS app bundle: $APP_ROOT"
