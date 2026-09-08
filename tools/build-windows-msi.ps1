[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PortableZipPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputMsiPath,

    [Parameter(Mandatory = $true)]
    [string]$VpkToolsPath,

    [Parameter(Mandatory = $true)]
    [string]$PackId,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("x86", "x64", "arm64")]
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"

function ConvertTo-XmlAttribute([string]$Value) {
    return [System.Security.SecurityElement]::Escape($Value)
}

function Find-File([string]$Root, [string]$Name) {
    $file = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Name | Select-Object -First 1
    if ($null -eq $file) {
        throw "Could not find '$Name' below '$Root'."
    }

    return $file.FullName
}

function Export-EmbeddedResource([System.Reflection.Assembly]$Assembly, [string]$FileName, [string]$OutputPath) {
    $resourceName = $Assembly.GetManifestResourceNames() |
        Where-Object { $_.EndsWith("." + $FileName, [System.StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($resourceName)) {
        throw "Could not find embedded Velopack resource '$FileName'."
    }

    $stream = $Assembly.GetManifestResourceStream($resourceName)
    if ($null -eq $stream) {
        throw "Could not open embedded Velopack resource '$resourceName'."
    }

    try {
        $output = [System.IO.File]::Create($OutputPath)
        try {
            $stream.CopyTo($output)
        }
        finally {
            $output.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-MsiDatabaseContract([string]$MsiPath) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null

    try {
        $database = $installer.OpenDatabase($MsiPath, 0)

        $checks = [ordered]@{
            "Custom directory survives elevation" = @'
SELECT Property, Value
FROM Property
WHERE Property = 'SecureCustomProperties'
'@
            "Install hook is scheduled" = @'
SELECT Action
FROM InstallExecuteSequence
WHERE Action = 'InstallHookDeferred' AND Condition = 'NOT REMOVE'
'@
            "Uninstall hook is scheduled" = @'
SELECT Action
FROM InstallExecuteSequence
WHERE Action = 'UninstallHookDeferred' AND Condition = 'REMOVE="ALL"'
'@
            "Velopack cleanup is scheduled" = @'
SELECT Action
FROM InstallExecuteSequence
WHERE Action = 'RustCleanup' AND Condition = 'REMOVE="ALL"'
'@
            "Channel patch is scheduled" = @'
SELECT Action
FROM InstallExecuteSequence
WHERE Action = 'PatchChannelDeferred' AND Condition = 'NOT REMOVE'
'@
        }

        foreach ($check in $checks.GetEnumerator()) {
            $view = $null
            try {
                $sql = [System.Text.RegularExpressions.Regex]::Replace($check.Value, '\s+', ' ').Trim()
                $view = $database.OpenView($sql)
                if ($null -eq $view) {
                    throw "Could not open MSI query."
                }

                $view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record) {
                    throw "Required MSI row was not found."
                }
                if ($check.Key -eq "Custom directory survives elevation" -and
                    $record.StringData(2) -notmatch '(^|;)VELOPACK_INSTALLDIR(;|$)') {
                    throw "VELOPACK_INSTALLDIR is not listed in SecureCustomProperties."
                }
            }
            catch {
                throw "MSI contract check failed: $($check.Key) $($_.Exception.Message)"
            }
            finally {
                if ($null -ne $view) {
                    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
                }
            }
        }

        $controlEventChecks = @(
            @{ Name = "ExitDialog Finish exits the wizard"; Dialog = "ExitDialog"; Control = "Finish"; Event = "EndDialog"; Argument = "Return" }
            @{ Name = "ExitDialog Finish launches the app"; Dialog = "ExitDialog"; Control = "Finish"; Event = "DoAction"; Argument = "RustLaunchApplication" }
            @{ Name = "InstallDirDlg validates the selected path"; Dialog = "InstallDirDlg"; Control = "Next"; Event = "DoAction"; Argument = "RustValidatePath" }
            @{ Name = "InstallDirDlg continues after a valid path"; Dialog = "InstallDirDlg"; Control = "Next"; Event = "NewDialog"; Argument = "VerifyReadyDlg" }
            @{ Name = "InstallDirDlg handles an invalid path"; Dialog = "InstallDirDlg"; Control = "Next"; Event = "SpawnDialog"; Argument = "InvalidDirDlg" }
        )

        $eventOrders = @{}
        foreach ($check in $controlEventChecks) {
            $view = $null
            try {
                $sql = "SELECT Event, Argument, Ordering FROM ControlEvent WHERE Dialog_ = '$($check.Dialog)' AND Control_ = '$($check.Control)'"
                $view = $database.OpenView($sql)
                $view.Execute()
                $found = $false
                while ($record = $view.Fetch()) {
                    if ($record.StringData(1) -eq $check.Event -and $record.StringData(2) -eq $check.Argument) {
                        $eventOrders[$check.Name] = [int]$record.StringData(3)
                        $found = $true
                        break
                    }
                }

                if (!$found) {
                    throw "Required control event was not found."
                }
            }
            catch {
                throw "MSI contract check failed: $($check.Name) $($_.Exception.Message)"
            }
            finally {
                if ($null -ne $view) {
                    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
                }
            }
        }

        if ($eventOrders["ExitDialog Finish launches the app"] -ge $eventOrders["ExitDialog Finish exits the wizard"]) {
            throw "MSI contract check failed: app launch must run before ExitDialog closes."
        }

        Write-Host "MSI contract checks passed."
    }
    finally {
        if ($null -ne $database) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
        }
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function New-StableGuidFromHash([string]$Text) {
    $namespace = [Guid]"6ba7b812-9dad-11d1-80b4-00c04fd430c8"
    $namespaceBytes = $namespace.ToByteArray()

    function Swap-Bytes([byte[]]$Bytes, [int]$Left, [int]$Right) {
        $temporary = $Bytes[$Left]
        $Bytes[$Left] = $Bytes[$Right]
        $Bytes[$Right] = $temporary
    }

    Swap-Bytes $namespaceBytes 0 3
    Swap-Bytes $namespaceBytes 1 2
    Swap-Bytes $namespaceBytes 4 5
    Swap-Bytes $namespaceBytes 6 7

    $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [System.Security.Cryptography.SHA1]::Create().ComputeHash($namespaceBytes + $nameBytes)
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($hash, 0, $guidBytes, 0, 16)
    $guidBytes[6] = ($guidBytes[6] -band 0x0F) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
    Swap-Bytes $guidBytes 0 3
    Swap-Bytes $guidBytes 1 2
    Swap-Bytes $guidBytes 4 5
    Swap-Bytes $guidBytes 6 7
    return [Guid]::new($guidBytes).ToString()
}

if (!(Test-Path -LiteralPath $PortableZipPath -PathType Leaf)) {
    throw "Portable package was not found: $PortableZipPath"
}

$vpkWindowsAssemblyPath = Find-File $VpkToolsPath "Velopack.Packaging.Windows.dll"
$wixC7Path = Find-File $VpkToolsPath "wixc7.exe"
$velopackWixFileName = switch ($Architecture) {
    "x86" { "velopack_wix_x86.dll" }
    "x64" { "velopack_wix_x64.dll" }
    "arm64" { "velopack_wix_arm64.dll" }
    default { throw "Unsupported Windows MSI architecture: $Architecture" }
}
$velopackWixPath = Find-File $VpkToolsPath $velopackWixFileName
$iconPath = Join-Path $PSScriptRoot "..\Assets\CxShellLogo.ico"

if (!(Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "CxShell icon was not found: $iconPath"
}

$versionObject = [System.Version]::Parse($Version)
$msiVersion = "$($versionObject.Major).$($versionObject.Minor).$($versionObject.Build).0"
$packageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("CxShell-Msi-" + [Guid]::NewGuid().ToString("N"))
$stagingRoot = Join-Path $packageRoot "payload"
$resourceRoot = Join-Path $packageRoot "resources"
$sourcePath = Join-Path $packageRoot "CxShell.wxs"
$generatedMsiPath = Join-Path $packageRoot "CxShell.custom.msi"

New-Item -ItemType Directory -Force -Path $stagingRoot, $resourceRoot | Out-Null

try {
    Expand-Archive -LiteralPath $PortableZipPath -DestinationPath $stagingRoot

    $portableMarker = Join-Path $stagingRoot ".portable"
    $msiMarker = Join-Path $stagingRoot ".msi-installed"
    if (Test-Path -LiteralPath $portableMarker -PathType Leaf) {
        Move-Item -LiteralPath $portableMarker -Destination $msiMarker
    }
    elseif (!(Test-Path -LiteralPath $msiMarker -PathType Leaf)) {
        New-Item -ItemType File -Path $msiMarker | Out-Null
    }

    $velopackWindowsAssembly = [System.Reflection.Assembly]::LoadFrom($vpkWindowsAssemblyPath)
    $dialogNames = @(
        "BrowseDlg.wxs",
        "CancelDlg.wxs",
        "DiskCostDlg.wxs",
        "ErrorDlg.wxs",
        "ExitDialog.wxs",
        "FatalError.wxs",
        "FilesInUse.wxs",
        "InvalidDirDlg.wxs",
        "MaintenanceTypeDlg.wxs",
        "MaintenanceWelcomeDlg.wxs",
        "MsiRMFilesInUse.wxs",
        "OutOfDiskDlg.wxs",
        "OutOfRbDiskDlg.wxs",
        "PrepareDlg.wxs",
        "ProgressDlg.wxs",
        "ResumeDlg.wxs",
        "UserExit.wxs",
        "VerifyReadyDlg.wxs",
        "WelcomeDlg.wxs"
    )

    foreach ($dialogName in $dialogNames) {
        Export-EmbeddedResource $velopackWindowsAssembly $dialogName (Join-Path $resourceRoot $dialogName)
    }

    foreach ($bitmapName in @("banner.bmp", "dialog.bmp", "exclam.ico", "new.ico", "up.ico")) {
        Export-EmbeddedResource $velopackWindowsAssembly $bitmapName (Join-Path $resourceRoot $bitmapName)
    }

    $architectureName = $Architecture.ToUpperInvariant()
    $programFilesDirectory = if ($Architecture -eq "x86") { "ProgramFilesFolder" } else { "ProgramFiles64Folder" }
    $sourceAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath($stagingRoot))
    $iconAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath($iconPath))
    $rustNativeAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath($velopackWixPath))
    $bannerAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath((Join-Path $resourceRoot "banner.bmp")))
    $dialogAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath((Join-Path $resourceRoot "dialog.bmp")))
    $exclamAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath((Join-Path $resourceRoot "exclam.ico")))
    $newAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath((Join-Path $resourceRoot "new.ico")))
    $upAttribute = ConvertTo-XmlAttribute ([System.IO.Path]::GetFullPath((Join-Path $resourceRoot "up.ico")))
    $packIdAttribute = ConvertTo-XmlAttribute $PackId

    # Keep these values compatible with Velopack's UUID v5-style MSI identifiers.
    # The stable upgrade code lets this MSI upgrade the previous v0.1.49 package.
    $upgradeCode = New-StableGuidFromHash "$PackId`:UpgradeCode"
    $componentSeed = New-StableGuidFromHash "$PackId`:INSTALLFOLDER"

    $dialogRefs = ($dialogNames | ForEach-Object {
        "            <DialogRef Id=""$([System.IO.Path]::GetFileNameWithoutExtension($_))"" />"
    }) -join "`r`n"

    $wix = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
    <Package Name="CxShell"
             Manufacturer="xiaochengzjc"
             Version="$msiVersion"
             Language="1033"
             Scope="perMachine"
             UpgradeCode="$upgradeCode">

        <Media Id="1" Cabinet="app.cab" EmbedCab="yes" />
        <StandardDirectory Id="$programFilesDirectory">
            <Directory Id="INSTALLFOLDER" Name="CxShell" ComponentGuidGenerationSeed="$componentSeed">
                <Directory Name="current" />
                <Directory Id="PACKAGES_DIR" Name="packages" />
            </Directory>
        </StandardDirectory>

        <Icon Id="appicon" SourceFile="$iconAttribute" />
        <DirectoryRef Id="INSTALLFOLDER">
            <Component Id="ApplicationArpRegistration">
                <RegistryKey Root="HKLM" Key="Software\Microsoft\Windows\CurrentVersion\Uninstall\MSI:$packIdAttribute">
                    <RegistryValue Name="DisplayName" Value="CxShell" Type="string" />
                    <RegistryValue Name="DisplayVersion" Value="$Version" Type="string" />
                    <RegistryValue Name="DisplayIcon" Value="[INSTALLFOLDER]CxShell.exe" Type="string" />
                    <RegistryValue Name="Publisher" Value="xiaochengzjc" Type="string" />
                    <RegistryValue Name="UninstallString" Value="msiexec.exe /x [ProductCode]" Type="string" />
                    <RegistryValue Name="InstallLocation" Value="[INSTALLFOLDER]" Type="string" />
                    <RegistryValue Name="NoModify" Value="1" Type="integer" />
                    <RegistryValue Name="NoRepair" Value="1" Type="integer" KeyPath="yes" />
                </RegistryKey>
            </Component>
        </DirectoryRef>

        <StandardDirectory Id="DesktopFolder">
            <Component Id="ApplicationDesktopShortcut">
                <Shortcut Id="ApplicationDesktopShortcut" Name="CxShell" Description="CxShell" Target="[INSTALLFOLDER]CxShell.exe" WorkingDirectory="INSTALLFOLDER" Icon="appicon" />
                <RemoveFolder Id="CleanUpDesktopShortcut" Directory="INSTALLFOLDER" On="uninstall" />
                <RegistryValue Root="HKLM" Key="Software\xiaochengzjc\CxShell.DesktopShortcut" Name="installed" Type="integer" Value="1" KeyPath="yes" />
            </Component>
        </StandardDirectory>

        <StandardDirectory Id="ProgramMenuFolder">
            <Directory Id="ApplicationProgramMenuDir" Name="CxShell">
                <Component Id="ApplicationStartMenuShortcut">
                    <Shortcut Id="ApplicationStartMenuShortcut" Name="CxShell" Description="CxShell" Target="[INSTALLFOLDER]CxShell.exe" WorkingDirectory="INSTALLFOLDER" Icon="appicon" />
                    <RemoveFolder Id="CleanUpStartMenuShortcut" Directory="ApplicationProgramMenuDir" On="uninstall" />
                    <RegistryValue Root="HKLM" Key="Software\xiaochengzjc\CxShell.StartMenuShortcut" Name="installed" Type="integer" Value="1" KeyPath="yes" />
                </Component>
            </Directory>
        </StandardDirectory>

        <Files Include="$sourceAttribute\**" />

        <Property Id="ApplicationFolderName" Value="CxShell" />
        <Property Id="WIXUI_INSTALLDIR" Value="INSTALLFOLDER" />
        <Property Id="_BrowseProperty" Value="INSTALLFOLDER" />
        <Property Id="WixAppFolder" Value="WixPerMachineFolder" />
        <Property Id="WIXUI_EXITDIALOGOPTIONALCHECKBOX" Value="1" />
        <Property Id="VELOPACK_INSTALLDIR" Secure="yes" />
        <SetProperty Action="SetVelopackInstallFolder" Id="INSTALLFOLDER" Value="[VELOPACK_INSTALLDIR]" Before="CostFinalize" Sequence="execute" Condition="VELOPACK_INSTALLDIR" />
        <SetProperty Action="SetQuietDefaultInstallFolder" Id="INSTALLFOLDER" Value="[$programFilesDirectory]CxShell" Before="CostFinalize" Sequence="execute"
                     Condition="NOT Installed AND NOT VELOPACK_INSTALLDIR AND UILevel &lt; 5" />

        <Binary Id="RustDll" SourceFile="$rustNativeAttribute" />
        <Binary Id="WixUI_Bmp_Banner" SourceFile="$bannerAttribute" />
        <Binary Id="WixUI_Bmp_Dialog" SourceFile="$dialogAttribute" />
        <Binary Id="WixUI_Ico_Exclam" SourceFile="$exclamAttribute" />
        <Binary Id="WixUI_Bmp_Up" SourceFile="$upAttribute" />
        <Binary Id="WixUI_Bmp_New" SourceFile="$newAttribute" />

        <Property Id="RustAppId" Value="CxShell" />
        <Property Id="RustAppTitle" Value="CxShell" />
        <Property Id="RustAppVersion" Value="$Version" />
        <Property Id="RustStubFileName" Value="CxShell.exe" />
        <Property Id="RustMainExeFileName" Value="CxShell.exe" />

        <CustomAction Id="RustSetLocaleStrings" BinaryRef="RustDll" DllEntry="RustSetLocaleStrings" Execute="immediate" Return="check" />
        <CustomAction Id="RustValidatePath" BinaryRef="RustDll" DllEntry="ValidatePath" Execute="immediate" Return="check" />
        <CustomAction Id="RustLaunchApplication" BinaryRef="RustDll" DllEntry="LaunchApplication" Impersonate="yes" Execute="immediate" Return="ignore" />

        <CustomAction Id="SetInstallHookData" Property="InstallHookDeferred"
                      Value="[INSTALLFOLDER]&quot;[RustMainExeFileName]&quot;[RustAppVersion]" Execute="immediate" Return="check" />
        <CustomAction Id="InstallHookDeferred" BinaryRef="RustDll" DllEntry="InstallHookDeferred"
                      Execute="deferred" Impersonate="yes" Return="ignore" />
        <CustomAction Id="SetUninstallHookData" Property="UninstallHookDeferred"
                      Value="[INSTALLFOLDER]&quot;[RustMainExeFileName]&quot;[RustAppVersion]" Execute="immediate" Return="check" />
        <CustomAction Id="UninstallHookDeferred" BinaryRef="RustDll" DllEntry="UninstallHookDeferred"
                      Execute="deferred" Impersonate="yes" Return="ignore" />
        <CustomAction Id="SetRustCleanupData" Property="RustCleanup"
                      Value="[INSTALLFOLDER]&quot;[RustAppId]&quot;[TempFolder]" Execute="immediate" Return="check" />
        <CustomAction Id="RustCleanup" BinaryRef="RustDll" DllEntry="CleanupDeferred"
                      Execute="deferred" Impersonate="no" Return="ignore" />
        <CustomAction Id="SetPatchChannelData" Property="PatchChannelDeferred"
                      Value="[OriginalDatabase]&quot;[INSTALLFOLDER]" Execute="immediate" Return="check" />
        <CustomAction Id="PatchChannelDeferred" BinaryRef="RustDll" DllEntry="PatchChannelDeferred"
                      Execute="deferred" Impersonate="no" Return="ignore" />

        <InstallUISequence>
            <Custom Action="RustSetLocaleStrings" Before="AppSearch" />
        </InstallUISequence>

        <InstallExecuteSequence>
            <Custom Action="SetPatchChannelData" Before="PatchChannelDeferred" Condition="NOT REMOVE" />
            <Custom Action="PatchChannelDeferred" After="InstallFiles" Condition="NOT REMOVE" />
            <Custom Action="SetInstallHookData" Before="InstallHookDeferred" Condition="NOT REMOVE" />
            <Custom Action="InstallHookDeferred" After="PatchChannelDeferred" Condition="NOT REMOVE" />
            <Custom Action="SetUninstallHookData" Before="UninstallHookDeferred" Condition="REMOVE=&quot;ALL&quot;" />
            <Custom Action="UninstallHookDeferred" Before="RemoveFiles" Condition="REMOVE=&quot;ALL&quot;" />
            <Custom Action="SetRustCleanupData" Before="RustCleanup" Condition="REMOVE=&quot;ALL&quot;" />
            <Custom Action="RustCleanup" Before="RemoveFolders" Condition="REMOVE=&quot;ALL&quot;" />
        </InstallExecuteSequence>

        <UI>
            <TextStyle Id="WixUI_Font_Normal" FaceName="Segoe UI" Size="8" />
            <TextStyle Id="WixUI_Font_Bigger" FaceName="Segoe UI" Size="12" />
            <TextStyle Id="WixUI_Font_Title" FaceName="Segoe UI" Size="9" Bold="yes" />
            <TextStyle Id="WixUI_Font_Emphasized" FaceName="Segoe UI" Size="8" Bold="yes" />
            <Property Id="DefaultUIFont" Value="WixUI_Font_Normal" />

$dialogRefs

            <Dialog Id="InstallDirDlg" Width="370" Height="270" Title="[MsiDlgTitle]" TrackDiskSpace="yes">
                <Control Id="BannerBitmap" Type="Bitmap" X="0" Y="0" Width="370" Height="44" TabSkip="no" Text="WixUI_Bmp_Banner" />
                <Control Id="BannerLine" Type="Line" X="0" Y="44" Width="370" Height="0" />
                <Control Id="BottomLine" Type="Line" X="0" Y="234" Width="370" Height="0" />
                <Control Id="Title" Type="Text" X="15" Y="6" Width="300" Height="15" Transparent="yes" NoPrefix="yes" Text="Install location" />
                <Control Id="Description" Type="Text" X="25" Y="35" Width="320" Height="35" Transparent="yes" NoPrefix="yes" Text="Choose the folder where CxShell will be installed." />
                <Control Id="PathLabel" Type="Text" X="25" Y="82" Width="320" Height="15" NoPrefix="yes" Text="Installation folder:" />
                <Control Id="PathEdit" Type="PathEdit" X="25" Y="101" Width="250" Height="18" Property="INSTALLFOLDER" />
                <Control Id="Browse" Type="PushButton" X="282" Y="101" Width="63" Height="18" Text="Browse...">
                    <Publish Event="SpawnDialog" Value="BrowseDlg" />
                </Control>
                <Control Id="Hint" Type="Text" X="25" Y="133" Width="320" Height="35" NoPrefix="yes" Text="The folder can be changed before continuing. Administrator permission may be required." />
                <Control Id="Back" Type="PushButton" X="156" Y="243" Width="56" Height="17" Text="[MsiBtnBack]">
                    <Publish Event="NewDialog" Value="WelcomeDlg" />
                </Control>
                <Control Id="Next" Type="PushButton" X="236" Y="243" Width="56" Height="17" Default="yes" Text="[MsiBtnNext]">
                    <Publish Event="SetTargetPath" Value="INSTALLFOLDER" Order="1" />
                    <Publish Property="WIXUI_INSTALLDIR" Value="[INSTALLFOLDER]" Order="2" Condition="NOT WIXUI_DONTVALIDATEPATH" />
                    <Publish Event="DoAction" Value="RustValidatePath" Order="3" Condition="NOT WIXUI_DONTVALIDATEPATH" />
                    <Publish Event="SpawnDialog" Value="InvalidDirDlg" Order="4" Condition="NOT WIXUI_DONTVALIDATEPATH AND WIXUI_INSTALLDIR_VALID &lt;&gt; &quot;1&quot;" />
                    <Publish Event="NewDialog" Value="VerifyReadyDlg" Order="5" Condition="WIXUI_DONTVALIDATEPATH OR WIXUI_INSTALLDIR_VALID = &quot;1&quot;" />
                </Control>
                <Control Id="Cancel" Type="PushButton" X="304" Y="243" Width="56" Height="17" Cancel="yes" Text="[MsiBtnCancel]">
                    <Publish Event="SpawnDialog" Value="CancelDlg" />
                </Control>
            </Dialog>


            <Publish Dialog="WelcomeDlg" Control="Next" Property="INSTALLFOLDER" Value="[$programFilesDirectory]CxShell" Order="1" Condition="NOT INSTALLFOLDER" />
            <Publish Dialog="WelcomeDlg" Control="Next" Event="SetTargetPath" Value="INSTALLFOLDER" Order="2" />
            <Publish Dialog="WelcomeDlg" Control="Next" Event="NewDialog" Value="InstallDirDlg" Order="3" Condition="NOT Installed" />
            <Publish Dialog="WelcomeDlg" Control="Next" Event="NewDialog" Value="VerifyReadyDlg" Order="4" Condition="Installed AND PATCH" />

            <Publish Dialog="VerifyReadyDlg" Control="Back" Event="NewDialog" Value="InstallDirDlg" Order="1" Condition="NOT Installed" />
            <Publish Dialog="VerifyReadyDlg" Control="Back" Event="NewDialog" Value="MaintenanceTypeDlg" Order="2" Condition="Installed AND NOT PATCH" />
            <Publish Dialog="VerifyReadyDlg" Control="Back" Event="NewDialog" Value="WelcomeDlg" Order="3" Condition="Installed AND PATCH" />

            <Publish Dialog="ExitDialog" Control="Finish" Event="EndDialog" Value="Return" Order="999" />
            <Publish Dialog="ExitDialog" Control="Finish" Event="DoAction" Value="RustLaunchApplication"
                     Order="1" Condition="WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 AND NOT Installed" />

            <Property Id="ARPSYSTEMCOMPONENT" Value="1" />
        </UI>
    </Package>
</Wix>
"@

    [System.IO.File]::WriteAllText($sourcePath, $wix, [System.Text.UTF8Encoding]::new($false))

    $dialogPaths = $dialogNames | ForEach-Object { Join-Path $resourceRoot $_ }
    $sourceFiles = @($sourcePath) + $dialogPaths
    & $wixC7Path build $sourceFiles -arch $architectureName -outputType Package -pdbType none -out $generatedMsiPath
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $generatedMsiPath -PathType Leaf)) {
        throw "wixc7 failed to create the custom MSI."
    }

    $outputDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputMsiPath))
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    Move-Item -LiteralPath $generatedMsiPath -Destination ([System.IO.Path]::GetFullPath($OutputMsiPath)) -Force
    Assert-MsiDatabaseContract ([System.IO.Path]::GetFullPath($OutputMsiPath))
    Write-Host "Created selectable-location MSI: $OutputMsiPath"
}
finally {
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
