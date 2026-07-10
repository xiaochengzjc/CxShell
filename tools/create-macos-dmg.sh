#!/usr/bin/env bash
set -euo pipefail

APP_PATH="${APP_PATH:-}"
OUTPUT_DMG="${OUTPUT_DMG:-}"
VOLUME_NAME="${VOLUME_NAME:-CxShell}"
README_SOURCE="${README_SOURCE:-}"

if [ -z "$APP_PATH" ]; then
  echo "APP_PATH is required." >&2
  exit 1
fi

if [ -z "$OUTPUT_DMG" ]; then
  echo "OUTPUT_DMG is required." >&2
  exit 1
fi

if [ ! -d "$APP_PATH" ]; then
  echo "App bundle was not found: $APP_PATH" >&2
  exit 1
fi

if ! command -v hdiutil >/dev/null 2>&1; then
  echo "hdiutil is required to create a macOS DMG." >&2
  exit 1
fi

staging_dir="$(mktemp -d)"
trap 'rm -rf "$staging_dir"' EXIT

app_name="$(basename "$APP_PATH")"
cp -R "$APP_PATH" "$staging_dir/$app_name"
ln -s /Applications "$staging_dir/Applications"

cat > "$staging_dir/Install Guide.txt" <<'GUIDE'
CxShell macOS install guide

Recommended no-admin install:
1. Create ~/Applications if it does not exist.
2. Move CxShell.app into ~/Applications.
3. Start CxShell from ~/Applications.

Dragging CxShell.app to the system /Applications folder may ask for an administrator password. Touch ID availability for that prompt is controlled by macOS, not by CxShell.

Automatic updates are smoother when CxShell.app is installed in a folder your user account can write to, such as ~/Applications.
GUIDE

if [ -n "$README_SOURCE" ] && [ -f "$README_SOURCE" ]; then
  cp "$README_SOURCE" "$staging_dir/README-macos.txt"
fi

mkdir -p "$(dirname "$OUTPUT_DMG")"
hdiutil create \
  -volname "$VOLUME_NAME" \
  -srcfolder "$staging_dir" \
  -ov \
  -format UDZO \
  "$OUTPUT_DMG"

echo "Created macOS DMG: $OUTPUT_DMG"
