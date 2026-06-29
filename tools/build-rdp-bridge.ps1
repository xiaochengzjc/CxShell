param(
    [string]$VcpkgRoot = $env:VCPKG_ROOT,
    [string]$CMakePath = "",
    [string]$NinjaPath = "",
    [string]$VsDevCmdPath = "",
    [string]$Triplet = "x64-windows",
    [string]$Configuration = "Release",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

function Find-VsDevCmd {
    param([string[]]$FallbackPaths)

    $paths = @()
    $vswhereCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    )

    foreach ($vswhere in $vswhereCandidates) {
        if (!(Test-Path $vswhere)) {
            continue
        }

        $installations = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installations.Count -eq 0) {
            $installations = & $vswhere -products * -property installationPath
        }

        foreach ($installation in $installations) {
            if (![string]::IsNullOrWhiteSpace($installation)) {
                $paths += (Join-Path $installation "Common7\Tools\VsDevCmd.bat")
            }
        }
    }

    $paths += $FallbackPaths
    return $paths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($VcpkgRoot)) {
    $VcpkgRoot = "D:\develop\vcpkg"
}

$vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"
$toolchain = Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake"

if (!(Test-Path $vcpkgExe)) {
    throw "vcpkg.exe was not found at $vcpkgExe. Set VCPKG_ROOT or pass -VcpkgRoot."
}

if (!(Test-Path $toolchain)) {
    throw "vcpkg toolchain file was not found at $toolchain."
}

if ($Triplet -like "*windows*") {
    if ([string]::IsNullOrWhiteSpace($VsDevCmdPath)) {
        $candidates = @(
            "D:\develop\BuildTools\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2026\Community\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2026\Professional\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2026\Enterprise\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2026\BuildTools\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat",
            "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
        )
        $VsDevCmdPath = Find-VsDevCmd -FallbackPaths $candidates
    }

    if ([string]::IsNullOrWhiteSpace($VsDevCmdPath) -or !(Test-Path $VsDevCmdPath)) {
        throw "VsDevCmd.bat was not found. Install Visual Studio Build Tools or pass -VsDevCmdPath."
    }

    $vsArch = if ($Triplet -like "arm64-*") {
        "arm64"
    }
    elseif ($Triplet -like "x86-*") {
        "x86"
    }
    else {
        "x64"
    }

    $devEnv = cmd /c "`"call `"$VsDevCmdPath`" -arch=$vsArch -host_arch=x64 >nul && set`""
    foreach ($line in $devEnv) {
        $index = $line.IndexOf("=")
        if ($index -gt 0) {
            [Environment]::SetEnvironmentVariable($line.Substring(0, $index), $line.Substring($index + 1), "Process")
        }
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceDir = Join-Path $repoRoot "native\CxRdpBridge"
$buildDir = Join-Path $sourceDir "build\$Triplet"

if ([string]::IsNullOrWhiteSpace($CMakePath)) {
    $cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmakeCommand) {
        $CMakePath = $cmakeCommand.Source
    }
    else {
        $CMakePath = Get-ChildItem "D:\develop\cx-build-tools\cmake" -Recurse -Filter "cmake.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

if ([string]::IsNullOrWhiteSpace($NinjaPath)) {
    $ninjaCommand = Get-Command ninja -ErrorAction SilentlyContinue
    if ($ninjaCommand) {
        $NinjaPath = $ninjaCommand.Source
    }
    else {
        $NinjaPath = Get-ChildItem "D:\develop\cx-build-tools\ninja" -Recurse -Filter "ninja.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

if ([string]::IsNullOrWhiteSpace($CMakePath) -or !(Test-Path $CMakePath)) {
    throw "cmake was not found. Install CMake or pass -CMakePath."
}

if ([string]::IsNullOrWhiteSpace($NinjaPath) -or !(Test-Path $NinjaPath)) {
    throw "ninja was not found. Install Ninja or pass -NinjaPath."
}

$env:PATH = "$(Split-Path $NinjaPath);$env:PATH"

Write-Host "Using vcpkg: $vcpkgExe"
Write-Host "Using CMake: $CMakePath"
Write-Host "Using Ninja: $NinjaPath"
if ($Triplet -like "*windows*") {
    Write-Host "Using VsDevCmd: $VsDevCmdPath"
}
& $CMakePath --version
& $NinjaPath --version

& $vcpkgExe install "freerdp:$Triplet"
if ($LASTEXITCODE -ne 0) {
    throw "vcpkg install freerdp:$Triplet failed."
}

& $CMakePath -S $sourceDir -B $buildDir -G Ninja `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
    "-DCMAKE_BUILD_TYPE=$Configuration" `
    "-DVCPKG_TARGET_TRIPLET=$Triplet" `
    "-DVCPKG_APPLOCAL_DEPS=ON"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed."
}

& $CMakePath --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed."
}

if (![string]::IsNullOrWhiteSpace($OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    Get-ChildItem $buildDir -Filter "*.dll" | Copy-Item -Destination $OutputDir -Force

    $vcpkgBin = Join-Path $VcpkgRoot "installed\$Triplet\bin"
    if (Test-Path $vcpkgBin) {
        Get-ChildItem $vcpkgBin -Filter "*.dll" | Copy-Item -Destination $OutputDir -Force
    }

    $vcpkgInstalled = Join-Path $VcpkgRoot "installed\$Triplet"
    if (Test-Path $vcpkgInstalled) {
        Get-ChildItem $vcpkgInstalled -Recurse -Filter "*.dll" |
            Where-Object { $_.FullName -notmatch "\\debug\\" } |
            Copy-Item -Destination $OutputDir -Force
    }

    $legacyProvider = Join-Path $vcpkgBin "legacy.dll"
    if (Test-Path $legacyProvider) {
        Copy-Item $legacyProvider -Destination $OutputDir -Force
        $providerDir = Join-Path $OutputDir "ossl-modules"
        New-Item -ItemType Directory -Force -Path $providerDir | Out-Null
        Copy-Item $legacyProvider -Destination $providerDir -Force
        Get-ChildItem $vcpkgBin -Filter "libcrypto-3*.dll" -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName -Destination $providerDir -Force
        }
    }
}

Write-Host "CxRdpBridge build completed: $buildDir"
