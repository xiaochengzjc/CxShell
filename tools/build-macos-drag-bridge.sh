#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_DIR="$REPO_ROOT/native/CxMacDragBridge"
OUTPUT_DIR="${OUTPUT_DIR:-}"
ARCH="${ARCH:-$(uname -m)}"
MIN_MACOS_VERSION="${MIN_MACOS_VERSION:-11.0}"

if [ -z "$OUTPUT_DIR" ]; then
  echo "OUTPUT_DIR is required." >&2
  exit 1
fi

case "$ARCH" in
  arm64|x86_64)
    ;;
  x64)
    ARCH="x86_64"
    ;;
  *)
    echo "Unsupported macOS architecture: $ARCH" >&2
    exit 1
    ;;
esac

mkdir -p "$OUTPUT_DIR"

clang \
  -dynamiclib \
  -fobjc-arc \
  -fblocks \
  -arch "$ARCH" \
  -mmacosx-version-min="$MIN_MACOS_VERSION" \
  -framework AppKit \
  -framework Foundation \
  "$SOURCE_DIR/cxmac_drag_bridge.m" \
  -o "$OUTPUT_DIR/libCxMacDragBridge.dylib"

echo "CxMacDragBridge build completed: $OUTPUT_DIR/libCxMacDragBridge.dylib"
