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

function Get-CMakeCacheValue {
    param(
        [string]$CachePath,
        [string]$Key
    )

    if (!(Test-Path $CachePath)) {
        return $null
    }

    $line = Select-String -Path $CachePath -Pattern "^$([regex]::Escape($Key)):" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $line) {
        return $null
    }

    $index = $line.Line.IndexOf("=")
    if ($index -lt 0) {
        return $null
    }

    return $line.Line.Substring($index + 1)
}

function Copy-FirstExistingFile {
    param(
        [string]$FileName,
        [string[]]$SearchDirectories,
        [string]$DestinationDirectory
    )

    foreach ($directory in $SearchDirectories | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
        $candidate = Join-Path $directory $FileName
        if (Test-Path $candidate) {
            Copy-Item $candidate -Destination $DestinationDirectory -Force
            Write-Host "Copied native dependency: $candidate"
            return $true
        }
    }

    $whereOutput = & where.exe $FileName 2>$null
    foreach ($candidate in $whereOutput) {
        if (Test-Path $candidate) {
            Copy-Item $candidate -Destination $DestinationDirectory -Force
            Write-Host "Copied native dependency: $candidate"
            return $true
        }
    }

    return $false
}

function Get-WindowsDllDependents {
    param([string]$BinaryPath)

    if (!(Test-Path $BinaryPath)) {
        return @()
    }

    $dumpbinCommand = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if (!$dumpbinCommand) {
        Write-Host "dumpbin.exe was not found; skipping dependency inspection for $BinaryPath"
        return @()
    }

    $output = & $dumpbinCommand.Source /dependents $BinaryPath
    $dependents = @()
    foreach ($line in $output) {
        $trimmed = $line.Trim()
        if ($trimmed -match "^[A-Za-z0-9_.+\-]+\.dll$") {
            $dependents += $trimmed
        }
    }

    return $dependents
}

function Copy-WindowsToolchainRuntimeDependencies {
    param(
        [string]$BuildDirectory,
        [string]$VcpkgRootPath,
        [string]$TripletName,
        [string]$DestinationDirectory
    )

    $searchDirectories = New-Object System.Collections.Generic.List[string]
    foreach ($pathEntry in $env:PATH.Split([IO.Path]::PathSeparator, [StringSplitOptions]::RemoveEmptyEntries)) {
        $searchDirectories.Add($pathEntry)
    }

    $cachePath = Join-Path $BuildDirectory "CMakeCache.txt"
    foreach ($key in @("CMAKE_C_COMPILER", "CMAKE_CXX_COMPILER")) {
        $compilerPath = Get-CMakeCacheValue -CachePath $cachePath -Key $key
        if (![string]::IsNullOrWhiteSpace($compilerPath)) {
            $compilerDirectory = Split-Path $compilerPath -Parent
            if (![string]::IsNullOrWhiteSpace($compilerDirectory)) {
                $searchDirectories.Add($compilerDirectory)
            }
        }
    }

    $searchDirectories.Add((Join-Path $VcpkgRootPath "installed\$TripletName\bin"))
    $searchDirectories.Add((Join-Path $VcpkgRootPath "downloads\tools\msys2\mingw64\bin"))
    $searchDirectories.Add((Join-Path $VcpkgRootPath "downloads\tools\msys2\ucrt64\bin"))
    $searchDirectories.Add((Join-Path $VcpkgRootPath "downloads\tools\perl\5.42.2.1\c\bin"))
    $searchDirectories.Add("C:\msys64\mingw64\bin")
    $searchDirectories.Add("C:\msys64\ucrt64\bin")

    $dependencyNames = @(
        "libgcc_s_seh-1.dll",
        "libstdc++-6.dll",
        "libwinpthread-1.dll"
    )

    $bridgePath = Join-Path $DestinationDirectory "CxRdpBridge.dll"
    if (!(Test-Path $bridgePath)) {
        $bridgePath = Join-Path $DestinationDirectory "libCxRdpBridge.dll"
    }

    $detectedDependents = @(Get-WindowsDllDependents -BinaryPath $bridgePath)
    if ($detectedDependents.Count -eq 0) {
        Write-Host "No optional MinGW runtime dependency inspection result for $bridgePath."
        return
    }

    $dependencyNames = @($dependencyNames | Where-Object {
        $dependencyName = $_
        $detectedDependents | Where-Object { [string]::Equals($_, $dependencyName, [StringComparison]::OrdinalIgnoreCase) }
    })

    if ($dependencyNames.Count -eq 0) {
        Write-Host "No MinGW runtime dependencies detected for $bridgePath."
        return
    }

    foreach ($dependencyName in $dependencyNames) {
        if (Test-Path (Join-Path $DestinationDirectory $dependencyName)) {
            continue
        }

        if (Copy-FirstExistingFile -FileName $dependencyName -SearchDirectories $searchDirectories.ToArray() -DestinationDirectory $DestinationDirectory) {
            continue
        }

        $found = Get-ChildItem $VcpkgRootPath -Recurse -Filter $dependencyName -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch "\\debug\\" } |
            Select-Object -First 1
        if ($found) {
            Copy-Item $found.FullName -Destination $DestinationDirectory -Force
            Write-Host "Copied native dependency: $($found.FullName)"
        }
        else {
            Write-Host "Optional native dependency was not found: $dependencyName"
        }
    }
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

    $clCommand = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ($clCommand) {
        Write-Host "Using MSVC compiler: $($clCommand.Source)"
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceDir = Join-Path $repoRoot "native\CxRdpBridge"
$buildDir = Join-Path $sourceDir "build\$Triplet"

if ($Triplet -like "*windows*") {
    $cachePath = Join-Path $buildDir "CMakeCache.txt"
    $clCommand = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ((Test-Path $cachePath) -and $clCommand) {
        $cachedCCompiler = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_C_COMPILER"
        $cachedCxxCompiler = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_CXX_COMPILER"
        $cachedFreeRdpDir = Get-CMakeCacheValue -CachePath $cachePath -Key "FreeRDP_DIR"
        $expectedCompiler = [IO.Path]::GetFullPath($clCommand.Source)
        $cachedCompilers = @($cachedCCompiler, $cachedCxxCompiler) |
            Where-Object { ![string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object {
                try {
                    [IO.Path]::GetFullPath($_)
                }
                catch {
                    $_
                }
            }

        $compilerMismatch = $cachedCompilers |
            Where-Object { ![string]::Equals($_, $expectedCompiler, [StringComparison]::Ordinal) } |
            Select-Object -First 1

        $packagePathMissing = ![string]::IsNullOrWhiteSpace($cachedFreeRdpDir) -and
            $cachedFreeRdpDir.EndsWith("-NOTFOUND", [StringComparison]::OrdinalIgnoreCase)

        if ($compilerMismatch -or $packagePathMissing) {
            $resolvedBuildDir = [IO.Path]::GetFullPath($buildDir)
            $resolvedSourceDir = [IO.Path]::GetFullPath($sourceDir)
            if (!$resolvedBuildDir.StartsWith($resolvedSourceDir, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove build directory outside native source: $resolvedBuildDir"
            }

            Write-Host "Removing stale CMake build directory: $buildDir"
            Remove-Item -LiteralPath $buildDir -Recurse -Force
        }
    }
}

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

$cmakeArgs = @(
    "-S", $sourceDir,
    "-B", $buildDir,
    "-G", "Ninja",
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DVCPKG_TARGET_TRIPLET=$Triplet",
    "-DVCPKG_APPLOCAL_DEPS=ON"
)

if ($Triplet -like "*windows*") {
    $clCommand = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ($clCommand) {
        $cmakeArgs += "-DCMAKE_C_COMPILER=$($clCommand.Source)"
        $cmakeArgs += "-DCMAKE_CXX_COMPILER=$($clCommand.Source)"
    }
}

& $CMakePath @cmakeArgs
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
    Get-ChildItem $buildDir -Recurse -Filter "*.dll" |
        Where-Object { $_.FullName -notmatch "\\debug\\" } |
        Copy-Item -Destination $OutputDir -Force

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

    if ($Triplet -like "*windows*") {
        Copy-WindowsToolchainRuntimeDependencies -BuildDirectory $buildDir -VcpkgRootPath $VcpkgRoot -TripletName $Triplet -DestinationDirectory $OutputDir
    }

    $prefixedBridge = Join-Path $OutputDir "libCxRdpBridge.dll"
    $expectedBridge = Join-Path $OutputDir "CxRdpBridge.dll"
    if ((Test-Path $prefixedBridge) -and !(Test-Path $expectedBridge)) {
        Copy-Item $prefixedBridge -Destination $expectedBridge -Force
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
