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

function Get-WindowsForbiddenRuntimeDependencies {
    return @(
        "libgcc_s_seh-1.dll",
        "libstdc++-6.dll",
        "libwinpthread-1.dll"
    )
}

function Get-WindowsMsvcRuntimeNames {
    return @(
        "concrt140.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "msvcp140_atomic_wait.dll",
        "msvcp140_codecvt_ids.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll"
    )
}

function Resolve-DumpbinPath {
    $dumpbinCommand = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($dumpbinCommand) {
        return $dumpbinCommand.Source
    }

    $vswhereCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    )

    foreach ($vswhere in $vswhereCandidates) {
        if (!(Test-Path $vswhere)) {
            continue
        }

        $installations = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        foreach ($installation in $installations) {
            if ([string]::IsNullOrWhiteSpace($installation)) {
                continue
            }

            $dumpbinPath = Get-ChildItem -Path $installation -Recurse -Filter dumpbin.exe -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
            if (![string]::IsNullOrWhiteSpace($dumpbinPath) -and (Test-Path $dumpbinPath)) {
                return $dumpbinPath
            }
        }
    }

    return $null
}

function Get-WindowsDllDependents {
    param([string]$BinaryPath)

    if (!(Test-Path $BinaryPath)) {
        return @()
    }

    $dumpbinPath = Resolve-DumpbinPath
    if ([string]::IsNullOrWhiteSpace($dumpbinPath)) {
        throw "dumpbin.exe was not found. Install Visual Studio Build Tools so Windows native dependencies can be validated."
    }

    $output = & $dumpbinPath /dependents $BinaryPath
    $dependents = @()
    foreach ($line in $output) {
        $trimmed = $line.Trim()
        if ($trimmed -match "^[A-Za-z0-9_.+\-]+\.dll$") {
            $dependents += $trimmed
        }
    }

    return $dependents
}

function Write-CMakeCacheDiagnostics {
    param([string]$BuildDirectory)

    $cachePath = Join-Path $BuildDirectory "CMakeCache.txt"
    if (!(Test-Path $cachePath)) {
        Write-Host "CMake cache was not found at $cachePath"
        return
    }

    Write-Host "CMake cache diagnostics:"
    $keys = @(
        "CMAKE_GENERATOR",
        "CMAKE_GENERATOR_PLATFORM",
        "CMAKE_GENERATOR_INSTANCE",
        "CMAKE_C_COMPILER",
        "CMAKE_CXX_COMPILER",
        "CMAKE_LINKER",
        "CMAKE_VS_PLATFORM_TOOLSET",
        "FreeRDP_DIR",
        "WinPR_DIR",
        "OpenSSL_DIR",
        "CMAKE_PREFIX_PATH"
    )

    foreach ($key in $keys) {
        $value = Get-CMakeCacheValue -CachePath $cachePath -Key $key
        if (![string]::IsNullOrWhiteSpace($value)) {
            Write-Host "  ${key}=$value"
        }
    }
}

function Test-IsMsvcCompilerPath {
    param([string]$Compiler)

    if ([string]::IsNullOrWhiteSpace($Compiler)) {
        return $false
    }

    $compilerName = [IO.Path]::GetFileName($Compiler)
    return [string]::Equals($compilerName, "cl.exe", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($compilerName, "cl", [StringComparison]::OrdinalIgnoreCase)
}

function Assert-WindowsMsvcCompiler {
    param([string]$BuildDirectory)

    $cachePath = Join-Path $BuildDirectory "CMakeCache.txt"
    $generator = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_GENERATOR"
    if (![string]::IsNullOrWhiteSpace($generator) -and
        $generator.StartsWith("Visual Studio", [StringComparison]::OrdinalIgnoreCase)) {
        $linker = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_LINKER"
        if (![string]::IsNullOrWhiteSpace($linker) -and
            [string]::Equals([IO.Path]::GetFileName($linker), "link.exe", [StringComparison]::OrdinalIgnoreCase) -and
            ($linker.IndexOf("\VC\Tools\MSVC\", [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
             $linker.IndexOf("/VC/Tools/MSVC/", [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            Write-Host "CMake generator is $generator with MSVC linker: $linker"
            return
        }

        Write-Host "CMake generator is $generator; Visual Studio generators use the MSVC toolchain."
        return
    }

    $cCompiler = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_C_COMPILER"
    $cxxCompiler = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_CXX_COMPILER"
    foreach ($compiler in @($cCompiler, $cxxCompiler)) {
        if (!(Test-IsMsvcCompilerPath -Compiler $compiler)) {
            throw "CMake did not record an MSVC compiler in $cachePath."
        }
    }
}

function Assert-WindowsMsvcBridge {
    param(
        [string]$BridgePath,
        [string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($BridgePath) -or !(Test-Path $BridgePath)) {
        throw "CxRdpBridge.dll was not found for validation: $BridgePath"
    }

    $dependents = @(Get-WindowsDllDependents -BinaryPath $BridgePath)
    $forbiddenDependencies = Get-WindowsForbiddenRuntimeDependencies
    $forbiddenFound = @($dependents | Where-Object {
        $dependency = $_
        $forbiddenDependencies | Where-Object { [string]::Equals($_, $dependency, [StringComparison]::OrdinalIgnoreCase) }
    })

    if ($forbiddenFound.Count -gt 0) {
        throw "Windows RDP bridge must be pure MSVC. $Context depends on MinGW runtime DLLs: $($forbiddenFound -join ', ')"
    }

    Write-Host "$Context uses MSVC-compatible native dependencies."
}

function Get-WindowsBridgeBinaryPath {
    param(
        [string]$BuildDirectory,
        [string]$ConfigurationName
    )

    $candidates = @(
        (Join-Path $BuildDirectory "$ConfigurationName\CxRdpBridge.dll"),
        (Join-Path $BuildDirectory "CxRdpBridge.dll")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $found = Get-ChildItem $BuildDirectory -Recurse -Filter "CxRdpBridge.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch "\\debug\\" } |
        Select-Object -First 1
    if ($found) {
        return $found.FullName
    }

    return $null
}

function Remove-WindowsForbiddenRuntimeFiles {
    param([string]$Directory)

    foreach ($fileName in Get-WindowsForbiddenRuntimeDependencies) {
        $path = Join-Path $Directory $fileName
        if (Test-Path $path) {
            Remove-Item -LiteralPath $path -Force
            Write-Host "Removed forbidden MinGW runtime from output: $path"
        }
    }
}

function Get-WindowsMsvcRuntimeDirectory {
    param([string]$TripletName)

    $architecture = if ($TripletName -like "arm64-*") {
        "arm64"
    }
    elseif ($TripletName -like "x86-*") {
        "x86"
    }
    else {
        "x64"
    }

    $candidateRoots = @()
    if (![string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) {
        $candidateRoots += $env:VCToolsRedistDir
    }
    if (![string]::IsNullOrWhiteSpace($env:VCINSTALLDIR)) {
        $candidateRoots += (Join-Path $env:VCINSTALLDIR "Redist\MSVC")
    }

    $vswhereCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    )

    foreach ($vswhere in $vswhereCandidates) {
        if (!(Test-Path $vswhere)) {
            continue
        }

        $installations = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        foreach ($installation in $installations) {
            if (![string]::IsNullOrWhiteSpace($installation)) {
                $candidateRoots += (Join-Path $installation "VC\Redist\MSVC")
            }
        }
    }

    foreach ($root in $candidateRoots | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
        if (!(Test-Path $root)) {
            continue
        }

        $crtDirectory = Get-ChildItem -Path $root -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -match "\\$([regex]::Escape($architecture))\\Microsoft\.VC\d+\.CRT$" -or
                $_.FullName -match "/$([regex]::Escape($architecture))/Microsoft\.VC\d+\.CRT$"
            } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($crtDirectory) {
            return $crtDirectory.FullName
        }
    }

    return $null
}

function Copy-WindowsMsvcRuntimeFiles {
    param(
        [string]$TripletName,
        [string]$DestinationDirectory
    )

    $runtimeDirectory = Get-WindowsMsvcRuntimeDirectory -TripletName $TripletName
    if ([string]::IsNullOrWhiteSpace($runtimeDirectory) -or !(Test-Path $runtimeDirectory)) {
        Write-Host "MSVC redistributable CRT directory was not found; package may require the Visual C++ Redistributable."
        return
    }

    $runtimeNames = Get-WindowsMsvcRuntimeNames
    foreach ($runtimeName in $runtimeNames) {
        $source = Join-Path $runtimeDirectory $runtimeName
        if (Test-Path $source) {
            Copy-Item $source -Destination $DestinationDirectory -Force
            Write-Host "Copied MSVC runtime: $runtimeName"
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
    if (!$clCommand) {
        throw "cl.exe was not found after loading VsDevCmd.bat. Windows RDP bridge must be built with MSVC."
    }

    $env:CC = $clCommand.Source
    $env:CXX = $clCommand.Source
    Write-Host "Using MSVC compiler: $($clCommand.Source)"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$sourceDir = Join-Path $repoRoot "native\CxRdpBridge"
$buildDir = Join-Path $sourceDir "build\$Triplet"

if ($Triplet -like "*windows*") {
    $cachePath = Join-Path $buildDir "CMakeCache.txt"
    $clCommand = Get-Command cl.exe -ErrorAction SilentlyContinue
    if ((Test-Path $cachePath) -and $clCommand) {
        $cachedGenerator = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_GENERATOR"
        $cachedGeneratorPlatform = Get-CMakeCacheValue -CachePath $cachePath -Key "CMAKE_GENERATOR_PLATFORM"
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
            Where-Object { ![string]::Equals($_, $expectedCompiler, [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1

        $packagePathMissing = ![string]::IsNullOrWhiteSpace($cachedFreeRdpDir) -and
            $cachedFreeRdpDir.EndsWith("-NOTFOUND", [StringComparison]::OrdinalIgnoreCase)

        $generatorMismatch = ![string]::IsNullOrWhiteSpace($cachedGenerator) -and
            !$cachedGenerator.StartsWith("Visual Studio", [StringComparison]::OrdinalIgnoreCase)
        $expectedGeneratorPlatform = if ($Triplet -like "arm64-*") {
            "ARM64"
        }
        elseif ($Triplet -like "x86-*") {
            "Win32"
        }
        else {
            "x64"
        }
        $generatorPlatformMismatch = ![string]::IsNullOrWhiteSpace($cachedGeneratorPlatform) -and
            ![string]::Equals($cachedGeneratorPlatform, $expectedGeneratorPlatform, [StringComparison]::OrdinalIgnoreCase)

        if ($compilerMismatch -or $packagePathMissing -or $generatorMismatch -or $generatorPlatformMismatch) {
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

if (($Triplet -notlike "*windows*") -and [string]::IsNullOrWhiteSpace($NinjaPath)) {
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

if (($Triplet -notlike "*windows*") -and ([string]::IsNullOrWhiteSpace($NinjaPath) -or !(Test-Path $NinjaPath))) {
    throw "ninja was not found. Install Ninja or pass -NinjaPath."
}

if (($Triplet -notlike "*windows*") -and ![string]::IsNullOrWhiteSpace($NinjaPath) -and (Test-Path $NinjaPath)) {
    $env:PATH = "$(Split-Path $NinjaPath);$env:PATH"
}

Write-Host "Using vcpkg: $vcpkgExe"
Write-Host "Using CMake: $CMakePath"
if (($Triplet -notlike "*windows*") -and ![string]::IsNullOrWhiteSpace($NinjaPath)) {
    Write-Host "Using Ninja: $NinjaPath"
}
if ($Triplet -like "*windows*") {
    Write-Host "Using VsDevCmd: $VsDevCmdPath"
}
& $CMakePath --version
if (($Triplet -notlike "*windows*") -and ![string]::IsNullOrWhiteSpace($NinjaPath) -and (Test-Path $NinjaPath)) {
    & $NinjaPath --version
}

& $vcpkgExe install "freerdp:$Triplet"
if ($LASTEXITCODE -ne 0) {
    throw "vcpkg install freerdp:$Triplet failed."
}

$cmakeArgs = @(
    "-S", $sourceDir,
    "-B", $buildDir,
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DVCPKG_TARGET_TRIPLET=$Triplet",
    "-DVCPKG_APPLOCAL_DEPS=ON"
)

if ($Triplet -like "*windows*") {
    $clCommand = Get-Command cl.exe -ErrorAction SilentlyContinue
    if (!$clCommand) {
        throw "cl.exe was not found. Windows RDP bridge must be built with MSVC."
    }

    $env:CC = $clCommand.Source
    $env:CXX = $clCommand.Source

    $cmakePlatform = if ($Triplet -like "arm64-*") {
        "ARM64"
    }
    elseif ($Triplet -like "x86-*") {
        "Win32"
    }
    else {
        "x64"
    }

    $cmakeArgs += @("-G", "Visual Studio 17 2022", "-A", $cmakePlatform)
}
else {
    $cmakeArgs += @("-G", "Ninja")
}

& $CMakePath @cmakeArgs
if ($LASTEXITCODE -ne 0) {
    Write-CMakeCacheDiagnostics -BuildDirectory $buildDir
    throw "CMake configure failed."
}

Write-CMakeCacheDiagnostics -BuildDirectory $buildDir

if ($Triplet -like "*windows*") {
    Assert-WindowsMsvcCompiler -BuildDirectory $buildDir
}

& $CMakePath --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed."
}

if ($Triplet -like "*windows*") {
    $builtBridgePath = Get-WindowsBridgeBinaryPath -BuildDirectory $buildDir -ConfigurationName $Configuration
    Assert-WindowsMsvcBridge -BridgePath $builtBridgePath -Context "Built CxRdpBridge.dll"
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
        Remove-WindowsForbiddenRuntimeFiles -Directory $OutputDir
        Copy-WindowsMsvcRuntimeFiles -TripletName $Triplet -DestinationDirectory $OutputDir
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

    if ($Triplet -like "*windows*") {
        Assert-WindowsMsvcBridge -BridgePath (Join-Path $OutputDir "CxRdpBridge.dll") -Context "Packaged CxRdpBridge.dll"
    }
}

Write-Host "CxRdpBridge build completed: $buildDir"
