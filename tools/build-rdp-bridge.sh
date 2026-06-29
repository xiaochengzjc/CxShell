#!/usr/bin/env bash
set -euo pipefail

VCPKG_ROOT="${VCPKG_ROOT:-$HOME/vcpkg}"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_DIR="${OUTPUT_DIR:-}"
USE_SYSTEM_FREERDP="${USE_SYSTEM_FREERDP:-0}"

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

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="$REPO_ROOT/native/CxRdpBridge"
BUILD_DIR="$SOURCE_DIR/build/$TRIPLET"

echo "Using CMake: $(command -v cmake)"
echo "Using Ninja: $(command -v ninja)"
cmake --version
ninja --version

if [ "$USE_SYSTEM_FREERDP" = "1" ]; then
  BUILD_DIR="$SOURCE_DIR/build/$TRIPLET-system"
  cmake_args=(
    -S "$SOURCE_DIR"
    -B "$BUILD_DIR"
    -G Ninja
    "-DCMAKE_BUILD_TYPE=$CONFIGURATION"
    "-DVCPKG_APPLOCAL_DEPS=ON"
  )
  if [ -n "${CMAKE_PREFIX_PATH:-}" ]; then
    cmake_args+=("-DCMAKE_PREFIX_PATH=$CMAKE_PREFIX_PATH")
  fi
  cmake "${cmake_args[@]}"
else
  VCPKG_EXE="$VCPKG_ROOT/vcpkg"
  TOOLCHAIN="$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake"

  if [ ! -x "$VCPKG_EXE" ]; then
    echo "vcpkg was not found at $VCPKG_EXE. Set VCPKG_ROOT." >&2
    exit 1
  fi

  "$VCPKG_EXE" install "freerdp:$TRIPLET"

  cmake_args=(
    -S "$SOURCE_DIR"
    -B "$BUILD_DIR"
    -G Ninja
    "-DCMAKE_BUILD_TYPE=$CONFIGURATION"
    "-DCMAKE_TOOLCHAIN_FILE=$TOOLCHAIN"
    "-DVCPKG_TARGET_TRIPLET=$TRIPLET"
    "-DVCPKG_APPLOCAL_DEPS=ON"
  )
  if [ -n "${CMAKE_PREFIX_PATH:-}" ]; then
    cmake_args+=("-DCMAKE_PREFIX_PATH=$CMAKE_PREFIX_PATH")
  fi
  cmake "${cmake_args[@]}"
fi

cmake --build "$BUILD_DIR" --config "$CONFIGURATION"

if [ -n "$OUTPUT_DIR" ]; then
  mkdir -p "$OUTPUT_DIR"
  find "$BUILD_DIR" -maxdepth 1 \( -name "*.so*" -o -name "*.dylib" \) -exec cp -f {} "$OUTPUT_DIR" \;
  if [ "$USE_SYSTEM_FREERDP" != "1" ]; then
    VCPKG_INSTALL_DIR="$VCPKG_ROOT/installed/$TRIPLET"
    if [ -d "$VCPKG_INSTALL_DIR/lib" ]; then
      find "$VCPKG_INSTALL_DIR/lib" -maxdepth 1 \( -name "*.so*" -o -name "*.dylib" \) -exec cp -f {} "$OUTPUT_DIR" \;
    fi
    if [ -d "$VCPKG_INSTALL_DIR/bin" ]; then
      find "$VCPKG_INSTALL_DIR/bin" -maxdepth 1 \( -name "*.so*" -o -name "*.dylib" \) -exec cp -f {} "$OUTPUT_DIR" \;
    fi
    for module_dir in "$VCPKG_INSTALL_DIR/lib/ossl-modules" "$VCPKG_INSTALL_DIR/bin/ossl-modules"; do
      if [ -d "$module_dir" ]; then
        mkdir -p "$OUTPUT_DIR/ossl-modules"
        cp -R "$module_dir/." "$OUTPUT_DIR/ossl-modules/"
      fi
    done
  fi
fi

echo "CxRdpBridge build completed: $BUILD_DIR"
