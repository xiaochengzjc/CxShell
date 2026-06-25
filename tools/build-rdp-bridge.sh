#!/usr/bin/env bash
set -euo pipefail

VCPKG_ROOT="${VCPKG_ROOT:-$HOME/vcpkg}"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_DIR="${OUTPUT_DIR:-}"

case "$(uname -s)" in
  Darwin)
    ARCH="$(uname -m)"
    if [ "$ARCH" = "arm64" ]; then
      TRIPLET="${TRIPLET:-arm64-osx}"
    else
      TRIPLET="${TRIPLET:-x64-osx}"
    fi
    ;;
  Linux)
    TRIPLET="${TRIPLET:-x64-linux}"
    ;;
  *)
    echo "Unsupported OS. Use build-rdp-bridge.ps1 on Windows." >&2
    exit 1
    ;;
esac

VCPKG_EXE="$VCPKG_ROOT/vcpkg"
TOOLCHAIN="$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="$REPO_ROOT/native/CxRdpBridge"
BUILD_DIR="$SOURCE_DIR/build/$TRIPLET"

if [ ! -x "$VCPKG_EXE" ]; then
  echo "vcpkg was not found at $VCPKG_EXE. Set VCPKG_ROOT." >&2
  exit 1
fi

"$VCPKG_EXE" install "freerdp:$TRIPLET"

cmake -S "$SOURCE_DIR" -B "$BUILD_DIR" -G Ninja \
  "-DCMAKE_TOOLCHAIN_FILE=$TOOLCHAIN" \
  "-DCMAKE_BUILD_TYPE=$CONFIGURATION" \
  "-DVCPKG_TARGET_TRIPLET=$TRIPLET"

cmake --build "$BUILD_DIR" --config "$CONFIGURATION"

if [ -n "$OUTPUT_DIR" ]; then
  mkdir -p "$OUTPUT_DIR"
  find "$BUILD_DIR" -maxdepth 1 \( -name "*.so*" -o -name "*.dylib" \) -exec cp -f {} "$OUTPUT_DIR" \;
fi

echo "CxRdpBridge build completed: $BUILD_DIR"
