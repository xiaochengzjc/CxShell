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
