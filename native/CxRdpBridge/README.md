# CxRdpBridge

`CxRdpBridge` is the native RDP bridge used by CxShell. It wraps FreeRDP behind a small C ABI so the .NET/Avalonia application does not need to P/Invoke large FreeRDP structs directly.

## Build outline

The supported dependency path is vcpkg + FreeRDP 3.x. On Windows:

```powershell
tools\build-rdp-bridge.ps1 -VcpkgRoot D:\develop\vcpkg -OutputDir .buildcheck-rdp
```

Windows builds must use the MSVC toolchain from Visual Studio Build Tools. The PowerShell script rejects MinGW runtime dependencies and copies the required Visual C++ runtime DLLs beside the bridge.

On macOS/Linux:

```bash
VCPKG_ROOT="$HOME/vcpkg" OUTPUT_DIR="./publish" ./tools/build-rdp-bridge.sh
```

The native bridge and FreeRDP/WinPR runtime libraries must be copied next to `CxShell.dll`, for example:

```text
.buildcheck-rdp/
  CxShell.dll
  CxRdpBridge.dll
  freerdp*.dll
  winpr*.dll
```

The application also probes the .NET native asset layout:

```text
runtimes/
  win-x64/native/CxRdpBridge.dll
  osx-arm64/native/libCxRdpBridge.dylib
  osx-x64/native/libCxRdpBridge.dylib
  linux-x64/native/libCxRdpBridge.so
```

FreeRDP runtime libraries must live in the same native directory as the bridge. Linux and macOS builds set rpath to the bridge directory (`$ORIGIN` or `@loader_path`) so those adjacent dependencies can be resolved by the OS loader.

## First bridge scope

- Connect with username/password using FreeRDP.
- Publish BGRA framebuffer updates to C#.
- Accept pointer events from Avalonia.
- Accept basic keyboard events from Avalonia.
- Report status and disconnect callbacks.

Clipboard, audio, drive redirection, and full keyboard layout translation should be added after the first framebuffer connection is stable.
