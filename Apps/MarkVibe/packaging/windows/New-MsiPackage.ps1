# VBWR B
#
# Project: MarkVibe
# Repository: https://github.com/umbertotechnopreneur/MarkVibe
# Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
#
# VibeWare: Human intent, AI execution, and plenty of tokens
# Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
#
# Created with AI: OpenAI Codex; native Windows Installer packaging; 2026-10-16
# Modified with AI: OpenAI Codex; branded MSI progress and shared .NET runtime prerequisite; 2026-11-22
# Human guidance: Umberto Giacobbi; real MSI and portable Windows distribution
# Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
#
# Copyright (c) 2026 Umberto Giacobbi
# License: MIT (see LICENSE)
#
# VBWR E

<#
.SYNOPSIS
Create a per-user MSI from an existing framework-dependent publish directory without installing build tools.
.DESCRIPTION
Uses the Windows Installer COM database API and Windows makecab.exe. It never runs
the application, installs the result, or changes certificate trust. The caller signs
the completed MSI alongside the other distribution files.
.NOTES
MsiSchema.json uses the standard MSI text-archive column types. Lowercase types
are required, uppercase types are nullable: s=string, l=localized string,
i=integer and v=binary stream. MsiPackage.json upgrade codes are permanent product
identities for each channel and architecture, not identifiers to regenerate.
.LINK
https://learn.microsoft.com/windows/win32/msi/archive-file-format
.LINK
https://learn.microsoft.com/windows/win32/msi/suggested-installexecutesequence
.LINK
https://learn.microsoft.com/windows/win32/msi/upgrade-table
#>
[CmdletBinding(DefaultParameterSetName = 'Package')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Package')][string]$PayloadDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Package')][string]$OutputPath,
    [Parameter(Mandatory, ParameterSetName = 'Package')][string]$WorkDirectory,
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory, ParameterSetName = 'Package')][ValidateSet('Debug', 'Release')][string]$Configuration,
    [Parameter(Mandatory, ParameterSetName = 'Package')][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory, ParameterSetName = 'Package')][string]$IconPath,
    [Parameter(Mandatory, ParameterSetName = 'Validate')][switch]$ValidateVersionOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<#
.SYNOPSIS
Reject destinations outside repository artifacts before creating any installer intermediates.
#>
function Assert-ArtifactPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $artifactsRoot = Join-Path $repositoryRoot 'artifacts'
    $artifactsPrefix = $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI outputs and intermediates must remain inside $artifactsRoot."
    }

    $existingAncestor = $fullPath
    while ($existingAncestor.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $existingAncestor) {
            $ancestor = Get-Item -LiteralPath $existingAncestor -Force
            if ($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "MSI artifact paths must not traverse links: $existingAncestor."
            }
        }

        $existingAncestor = [System.IO.Path]::GetDirectoryName($existingAncestor)
    }

    return $fullPath
}

<#
.SYNOPSIS
Map the four-part application version to the three-part version that MSI actually compares.
#>
function Get-MsiProductVersion([version]$ApplicationVersion) {
    $installerBuild = ($ApplicationVersion.Build * 1000L) + $ApplicationVersion.Revision

    # MSI ignores a fourth field. Reserve three decimal digits for each revision
    # so adjacent Debug packages upgrade correctly instead of installing side by side.
    if ($ApplicationVersion.Major -gt 255 -or
        $ApplicationVersion.Minor -gt 255 -or
        $ApplicationVersion.Build -lt 0 -or
        $ApplicationVersion.Revision -lt 0 -or
        $ApplicationVersion.Revision -gt 999 -or
        $installerBuild -gt 65535) {
        throw 'MSI requires major/minor <=255, revision <=999 and build * 1000 + revision <=65535.'
    }

    return '{0}.{1}.{2}' -f $ApplicationVersion.Major, $ApplicationVersion.Minor, $installerBuild
}

<#
.SYNOPSIS
Return a stable GUID derived from the channel, architecture and installed resource identity.
#>
function Get-ResourceGuid([string]$Identity) {
    $hash = [System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Identity))
    $guidBytes = [byte[]]$hash[0..15]

    # UUID version 8 denotes an application-defined derivation; do not imply a
    # randomly generated identity for resources that must remain stable on upgrade.
    $guidBytes[7] = ($guidBytes[7] -band 0x0f) -bor 0x80
    $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80
    return ([Guid]::new($guidBytes)).ToString('B').ToUpperInvariant()
}

<#
.SYNOPSIS
Create a table definition with typed columns and an ordered collection of installer rows.
#>
function New-InstallerTable(
    [string]$Name,
    [string[]]$Columns,
    [string[]]$Types,
    [string[]]$Keys
) {
    return [pscustomobject]@{
        Name = $Name
        Columns = $Columns
        Types = $Types
        Keys = $Keys
        Rows = [System.Collections.Generic.List[object]]::new()
    }
}

<#
.SYNOPSIS
Add a row while rejecting malformed table cells before Windows Installer imports them.
#>
function Add-InstallerRow($Table, [object[]]$Values) {
    if ($Values.Count -ne $Table.Columns.Count) {
        throw "Incorrect column count in MSI table $($Table.Name): expected $($Table.Columns.Count), received $($Values.Count)."
    }

    foreach ($value in $Values) {
        if ([string]$value -match '[\x00-\x1f]') {
            throw "Control characters cannot be represented in MSI table $($Table.Name)."
        }
    }

    $Table.Rows.Add($Values)
}

<#
.SYNOPSIS
Write the documented MSI text archive format and import one table into the new database.
#>
function Import-InstallerTable($Database, $Table, [string]$Directory) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add($Table.Columns -join "`t")
    $lines.Add($Table.Types -join "`t")
    $lines.Add((@('1252', $Table.Name) + $Table.Keys) -join "`t")

    foreach ($row in $Table.Rows) {
        $lines.Add($row -join "`t")
    }

    # MSI archives use the declared ANSI code page, not PowerShell's UTF-8
    # default. Fail on unrepresentable names instead of silently corrupting them.
    [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
    $archiveEncoding = [Text.Encoding]::GetEncoding(
        1252,
        [Text.EncoderExceptionFallback]::new(),
        [Text.DecoderExceptionFallback]::new())
    $archiveName = $Table.Name + '.idt'
    [System.IO.File]::WriteAllLines((Join-Path $Directory $archiveName), $lines, $archiveEncoding)
    $Database.Import($Directory, $archiveName)
}

<#
.SYNOPSIS
Embed a cabinet or icon stream through the Windows Installer automation record API.
#>
function Add-InstallerStream($Installer, $Database, [string]$Table, [string]$Name, [string]$FilePath) {
    $record = $null
    $view = $null

    try {
        $record = $Installer.CreateRecord(2)
        $record.GetType().InvokeMember(
            'StringData',
            [Reflection.BindingFlags]::SetProperty,
            $null,
            $record,
            @(1, $Name)) | Out-Null
        $record.SetStream(2, $FilePath)
        $view = $Database.OpenView("INSERT INTO ``$Table`` (``Name``, ``Data``) VALUES (?, ?)")
        $view.Execute($record)
    } finally {
        if ($view) {
            $view.Close()
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
        }

        if ($record) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        }
    }
}

<#
.SYNOPSIS
Set a summary property, including indexed COM properties that PowerShell cannot assign directly.
#>
function Set-InstallerSummaryProperty($Summary, [int]$PropertyId, $Value) {
    $Summary.GetType().InvokeMember(
        'Property',
        [Reflection.BindingFlags]::SetProperty,
        $null,
        $Summary,
        @($PropertyId, $Value)) | Out-Null
}

<#
.SYNOPSIS
Define the standard per-user setup window and its progress or prerequisite content.
#>
function Add-SetupDialog($Tables, [string]$Name, [bool]$IsPrerequisite) {
    $button = if ($IsPrerequisite) { 'Close' } else { 'Cancel' }
    $buttonEvent = if ($IsPrerequisite) { 'EndDialog' } else { 'SpawnDialog' }
    $buttonArgument = if ($IsPrerequisite) { 'Exit' } else { 'MarkVibeConfirmCancel' }
    $dialogStyle = if ($IsPrerequisite) { 3 } else { 5 } # Visible modal or visible modeless with minimize.
    $defaultButton = if ($IsPrerequisite) { $button } else { $null }
    $nextContactControl = if ($IsPrerequisite) { 'Download' } else { $button }

    Add-InstallerRow $Tables.Dialog @($Name, 50, 50, 370, 270, $dialogStyle, '[MarkVibeSetupName] Setup', $button, $defaultButton, $button)
    Add-InstallerRow $Tables.Control @($Name, 'Logo', 'Icon', 20, 24, 44, 44, 1, $null, 'MarkVibeSetupIcon', $null, 'MarkVibe logo|')
    Add-InstallerRow $Tables.Control @($Name, 'Heading', 'Text', 82, 24, 268, 23, 3, $null, '{\InstallerHeading}[MarkVibeSetupName]', $null, $null)
    Add-InstallerRow $Tables.Control @($Name, 'Version', 'Text', 82, 49, 268, 14, 3, $null, 'Version [MarkVibeDisplayVersion] · Per-user installation', $null, $null)
    Add-InstallerRow $Tables.Control @($Name, 'Welcome', 'Text', 82, 70, 268, 16, 3, $null, 'Welcome to the [MarkVibeSetupName] installer.', $null, $null)
    Add-InstallerRow $Tables.Control @($Name, 'Requirement', 'Text', 82, 92, 268, 26, 3, $null, '[MarkVibeRuntimeDisplayName] is required.', $null, $null)
    Add-InstallerRow $Tables.Control @($Name, 'Product', 'Hyperlink', 82, 124, 268, 14, 3, $null,
        '<a href="[ARPURLINFOABOUT]">MarkVibe home</a> • <a href="[MarkVibeManifesto]">Member of VibeWare movement</a>', 'Contact', $null)
    Add-InstallerRow $Tables.Control @($Name, 'Contact', 'Hyperlink', 82, 142, 268, 14, 3, $null, 'Need help? <a href="[ARPHELPLINK]">Contact us</a>', $nextContactControl, $null)
    Add-InstallerRow $Tables.Control @($Name, 'Divider', 'Line', 0, 234, 370, 0, 1, $null, $null, $null, $null)
    Add-InstallerRow $Tables.Control @($Name, $button, 'PushButton', 294, 243, 56, 17, 3, $null, $button, 'Product', $null)
    Add-InstallerRow $Tables.ControlEvent @($Name, $button, $buttonEvent, $buttonArgument, '1', 1)

    if ($IsPrerequisite) {
        Add-InstallerRow $Tables.Control @($Name, 'Missing', 'Text', 82, 170, 268, 30, 3, $null,
            'Setup needs a compatible runtime. Open the Microsoft download page, install .NET, then run setup again.', $null, $null)
        Add-InstallerRow $Tables.Control @($Name, 'Download', 'Hyperlink', 82, 204, 268, 16, 3, $null,
            '<a href="[MarkVibeRuntimeDownload]">Open the Microsoft download page</a>', $button, $null)
    } else {
        Add-InstallerRow $Tables.Control @($Name, 'Status', 'Text', 20, 201, 330, 16, 3, $null, 'Installation progress', $null, $null)
        Add-InstallerRow $Tables.Control @($Name, 'Meter', 'ProgressBar', 20, 220, 330, 10, 1, $null, 'Installation progress', $null, $null)
        Add-InstallerRow $Tables.EventMapping @($Name, 'Meter', 'SetProgress', 'Progress')
    }
}

$installerVersion = Get-MsiProductVersion $Version

if ($ValidateVersionOnly) {
    return $installerVersion
}

$outputFile = Assert-ArtifactPath $OutputPath
$workRoot = Assert-ArtifactPath $WorkDirectory
$payloadRoot = [System.IO.Path]::GetFullPath($PayloadDirectory)
$packageMetadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MsiPackage.json') -Raw | ConvertFrom-Json
$upgradeCode = $packageMetadata.upgradeCodes.$Configuration.$Architecture
$identity = 'MarkVibe/' + $Configuration + '/' + $Architecture
$channelName = if ($Configuration -eq 'Debug') { 'MarkVibe (Debug)' } else { 'MarkVibe' }
$productName = "$channelName ($Architecture, MSI)"
$installFolderName = "MarkVibe-$($Configuration.ToLowerInvariant())-$Architecture"
$registryKey = "Software\VibeWare\MarkVibe\MSI\$Configuration\$Architecture"
$registrationName = "MarkVibe.$Configuration.$Architecture"
$markdownProgId = "$registrationName.Markdown"
$makeCab = (Get-Command makecab.exe -ErrorAction Stop).Source

if (Test-Path -LiteralPath $outputFile) {
    throw "Refusing to overwrite an existing MSI: $outputFile. Clean artifacts before packaging."
}

if (Test-Path -LiteralPath (Join-Path $payloadRoot 'portable.flag')) {
    throw 'Remove portable.flag before building the installed MSI payload.'
}

if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot 'MarkVibe.Windows.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $payloadRoot 'app/index.html'))) {
    throw 'MSI requires the complete existing framework-dependent desktop publish output.'
}

$runtimeConfigPath = Join-Path $payloadRoot 'MarkVibe.Windows.runtimeconfig.json'
$runtimeOptions = (Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json -AsHashtable)['runtimeOptions']
$framework = if ($null -ne $runtimeOptions) { $runtimeOptions['framework'] }
if ($null -eq $framework -or $framework['name'] -ne 'Microsoft.NETCore.App' -or $framework['version'] -notmatch '^10\.0\.\d+$' -or
    $runtimeOptions.Contains('frameworks') -or $runtimeOptions.Contains('includedFrameworks') -or
    ($runtimeOptions.Contains('rollForward') -and $runtimeOptions['rollForward'] -ne 'Minor') -or
    ($runtimeOptions.Contains('applyPatches') -and -not $runtimeOptions['applyPatches'])) {
    throw 'MSI requires a framework-dependent Microsoft.NETCore.App 10.0 publish with default servicing roll-forward.'
}
$runtimeVersion = $framework['version']
$runtimeDisplayVersion = if ($runtimeVersion -eq '10.0.0') { '10.0' } else { $runtimeVersion }
$runtimeDisplayName = "Microsoft .NET Runtime $runtimeDisplayVersion ($Architecture)"
$runtimeDownload = 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0/runtime'

[System.IO.Directory]::CreateDirectory($workRoot) | Out-Null
$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Sort-Object FullName)
$payloadBytes = ($payloadFiles | Measure-Object Length -Sum).Sum

# A single embedded cabinet keeps this installer self-contained. Reject larger
# payloads deliberately rather than accidentally generating split external media.
if ($payloadFiles.Count -gt 32767 -or $payloadBytes -ge 2GB) {
    throw 'The native MSI author supports at most 32767 files and a payload smaller than 2 GiB.'
}

$tableSchema = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MsiSchema.json') -Raw | ConvertFrom-Json
$tables = [ordered]@{}

foreach ($tableDefinition in $tableSchema.PSObject.Properties) {
    $columnDefinitions = @($tableDefinition.Value.columns.PSObject.Properties)
    $columnNames = @($columnDefinitions | ForEach-Object { $_.Name })
    $columnTypes = @($columnDefinitions | ForEach-Object { $_.Value })
    $tables[$tableDefinition.Name] = New-InstallerTable `
        $tableDefinition.Name $columnNames $columnTypes $tableDefinition.Value.keys
}

$productCode = Get-ResourceGuid "$identity/product/$Version"
$properties = [ordered]@{
    ProductCode = $productCode
    ProductName = $productName
    ProductVersion = $installerVersion
    ProductLanguage = '1033'
    Manufacturer = $packageMetadata.manufacturer
    UpgradeCode = $upgradeCode
    DefaultUIFont = 'InstallerBody'
    MarkVibeDisplayVersion = $Version.ToString()
    MarkVibeSetupName = $channelName
    MarkVibeRuntimeVersion = $runtimeVersion
    MarkVibeRuntimeArchitecture = $Architecture
    MarkVibeRuntimeDisplayName = $runtimeDisplayName
    MarkVibeRuntimeDownload = $runtimeDownload
    MarkVibeManifesto = 'https://umbertogiacobbi.biz/vibeware/manifesto/'
    ARPCOMMENTS = "Requires $runtimeDisplayName and WebView2 Runtime. $runtimeDownload"
    ARPHELPLINK = 'https://umbertogiacobbi.biz/contacts/'
    ARPURLINFOABOUT = 'https://github.com/umbertotechnopreneur/VibeKit/tree/main/Apps/MarkVibe'
    ARPPRODUCTICON = 'MarkVibeIcon'
    ARPNOMODIFY = '1'
    INSTALLLEVEL = '1'
    SecureCustomProperties = 'MARKVIBE_OLDER_PRODUCTS;MARKVIBE_NEWER_PRODUCTS;MARKVIBE_WINDOWS_BUILD;MARKVIBE_DOTNET_ROOT_64;MARKVIBE_DOTNET_ROOT_32'
}

foreach ($property in $properties.GetEnumerator()) {
    Add-InstallerRow $tables.Property @($property.Key, $property.Value)
}

Add-InstallerRow $tables.Directory @('TARGETDIR', $null, 'SourceDir')
Add-InstallerRow $tables.Directory @('LocalAppDataFolder', 'TARGETDIR', '.')
Add-InstallerRow $tables.Directory @('ProgramsDirectory', 'LocalAppDataFolder', 'Programs')
Add-InstallerRow $tables.Directory @('INSTALLFOLDER', 'ProgramsDirectory', "MARKVIBE|$installFolderName")
Add-InstallerRow $tables.Directory @('ProgramMenuFolder', 'TARGETDIR', '.')
Add-InstallerRow $tables.Directory @('ApplicationMenuFolder', 'ProgramMenuFolder', "MARKVIBE|$channelName (MSI $Architecture)")
Add-InstallerRow $tables.Feature @('MainApplication', $null, 'MarkVibe', 'Markdown desktop application; requires .NET Runtime 10.0', 0, 1, 'INSTALLFOLDER', 0)

$directoryIds = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
$directoryIds.Add('', 'INSTALLFOLDER')
$directoryIndex = 0

foreach ($directory in Get-ChildItem -LiteralPath $payloadRoot -Recurse -Directory | Sort-Object FullName) {
    if ($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "MSI payload must not contain directory links: $($directory.FullName)."
    }

    $relativePath = [System.IO.Path]::GetRelativePath($payloadRoot, $directory.FullName)
    $parentPath = [System.IO.Path]::GetDirectoryName($relativePath)
    $directoryIndex++
    $directoryId = 'Directory' + $directoryIndex
    $shortName = 'D{0:D7}' -f $directoryIndex
    $directoryIds.Add($relativePath, $directoryId)
    Add-InstallerRow $tables.Directory @($directoryId, $directoryIds[$parentPath], "$shortName|$($directory.Name)")
}

$cabinetDirectives = [System.Collections.Generic.List[string]]::new()
$cabinetDirectives.Add('.OPTION EXPLICIT')
$cabinetDirectives.Add('.Set Cabinet=on')
$cabinetDirectives.Add('.Set Compress=on')
$cabinetDirectives.Add('.Set CompressionType=MSZIP')
$cabinetDirectives.Add('.Set MaxDiskSize=0')
$cabinetDirectives.Add('.Set MaxCabinetSize=0')
$cabinetDirectives.Add('.Set CabinetNameTemplate=MarkVibe.cab')
$cabinetDirectives.Add('.Set DiskDirectoryTemplate="' + $workRoot + '"')
$cabinetDirectives.Add('.Set InfFileName="' + (Join-Path $workRoot 'MarkVibe.inf') + '"')
$cabinetDirectives.Add('.Set RptFileName="' + (Join-Path $workRoot 'MarkVibe.rpt') + '"')

$fileSequence = 0
$applicationFileId = $null
foreach ($file in $payloadFiles) {
    if ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "MSI payload must not contain file links: $($file.FullName)."
    }

    $relativePath = [System.IO.Path]::GetRelativePath($payloadRoot, $file.FullName)
    $parentPath = [System.IO.Path]::GetDirectoryName($relativePath)
    $resourceIdentity = $relativePath.ToLowerInvariant()
    $resourceBytes = [Text.Encoding]::UTF8.GetBytes($resourceIdentity)
    $resourceHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($resourceBytes))
    $fileId = 'File' + $resourceHash.Substring(0, 32)
    if ($relativePath -eq 'MarkVibe.Windows.exe') {
        $applicationFileId = $fileId
    }

    $componentId = 'Component' + $resourceHash.Substring(0, 32)
    $registryId = 'Registry' + $resourceHash.Substring(0, 32)
    $componentGuid = Get-ResourceGuid "$identity/file/$resourceIdentity"
    $fileSequence++
    $shortName = 'F{0:D7}' -f $fileSequence
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
    $numericVersion = $null

    if ($fileVersion.FileVersion -match '^\d+\.\d+\.\d+\.\d+') {
        $numericVersion = '{0}.{1}.{2}.{3}' -f
            $fileVersion.FileMajorPart,
            $fileVersion.FileMinorPart,
            $fileVersion.FileBuildPart,
            $fileVersion.FilePrivatePart
    }

    # Per-user components use HKCU registry key paths. Each file has an independent
    # stable component so removing or renaming a file cannot strand old payloads.
    Add-InstallerRow $tables.Component @($componentId, $componentGuid, $directoryIds[$parentPath], 260, $null, $registryId)
    Add-InstallerRow $tables.FeatureComponents @('MainApplication', $componentId)
    Add-InstallerRow $tables.Registry @($registryId, 1, "$registryKey\Files", $resourceHash, $Version.ToString(), $componentId)
    Add-InstallerRow $tables.File @($fileId, $componentId, "$shortName|$($file.Name)", $file.Length, $numericVersion, $null, 16896, $fileSequence)
    $cabinetDirectives.Add('"' + $file.FullName + '" ' + $fileId)
}

$shortcutComponent = 'StartMenuShortcut'
$shortcutRegistry = 'StartMenuRegistry'
Add-InstallerRow $tables.Component @($shortcutComponent, (Get-ResourceGuid "$identity/shortcut"), 'INSTALLFOLDER', 260, $null, $shortcutRegistry)
Add-InstallerRow $tables.FeatureComponents @('MainApplication', $shortcutComponent)
Add-InstallerRow $tables.Registry @($shortcutRegistry, 1, $registryKey, 'InstalledVersion', $Version.ToString(), $shortcutComponent)

# Registration advertises an available handler without changing the user's default.
# Keep each architecture/channel's ProgID separate so uninstall removes only its own entries.
$associationComponent = 'MarkdownAssociations'
$associationRegistry = 'MarkdownCapabilities'
$capabilitiesKey = "$registryKey\Capabilities"
Add-InstallerRow $tables.Component @(
    $associationComponent, (Get-ResourceGuid "$identity/markdown-associations"),
    'INSTALLFOLDER', 260, $null, $associationRegistry)
Add-InstallerRow $tables.FeatureComponents @('MainApplication', $associationComponent)
Add-InstallerRow $tables.Registry @(
    $associationRegistry, 1, $capabilitiesKey,
    'ApplicationName', $productName, $associationComponent)
Add-InstallerRow $tables.Registry @(
    'MarkdownDescription', 1, $capabilitiesKey, 'ApplicationDescription',
    'Read and explore Markdown documents', $associationComponent)
Add-InstallerRow $tables.Registry @(
    'MarkdownApplicationIcon', 1, $capabilitiesKey, 'ApplicationIcon',
    '"[INSTALLFOLDER]MarkVibe.Windows.exe",0', $associationComponent)
Add-InstallerRow $tables.Registry @(
    'MarkdownRegisteredApplication', 1, 'Software\RegisteredApplications',
    $registrationName, $capabilitiesKey, $associationComponent)
Add-InstallerRow $tables.Registry @(
    'MarkdownProgId', 1, "Software\Classes\$markdownProgId", $null,
    'Markdown document', $associationComponent)
Add-InstallerRow $tables.Registry @(
    'MarkdownIcon', 1, "Software\Classes\$markdownProgId\DefaultIcon", $null,
    '"[INSTALLFOLDER]MarkVibe.Windows.exe",0', $associationComponent)
Add-InstallerRow $tables.Registry @(
    'MarkdownOpenCommand', 1, "Software\Classes\$markdownProgId\shell\open\command", $null,
    '"[INSTALLFOLDER]MarkVibe.Windows.exe" --open "%1"', $associationComponent)

foreach ($extension in @('.md', '.markdown')) {
    $extensionId = $extension.TrimStart('.')
    Add-InstallerRow $tables.Registry @(
        "MarkdownCapability$extensionId", 1, "$capabilitiesKey\FileAssociations",
        $extension, $markdownProgId, $associationComponent)
    Add-InstallerRow $tables.Registry @(
        "MarkdownOpenWith$extensionId", 1, "Software\Classes\$extension\OpenWithProgids",
        $markdownProgId, $null, $associationComponent)
}

Add-InstallerRow $tables.Shortcut @(
    'StartMenu',
    'ApplicationMenuFolder',
    "MARKVIBE|$productName",
    $shortcutComponent,
    '[INSTALLFOLDER]MarkVibe.Windows.exe',
    $null,
    'Read and explore Markdown documents',
    $null,
    'MarkVibeIcon',
    0,
    1,
    'INSTALLFOLDER')

# Null FileName removes an empty directory only. Never use a wildcard to delete
# user-created files, and never register the separate MarkVibe/Windows profile.
foreach ($directoryId in @($directoryIds.Values) + @('ProgramsDirectory', 'ApplicationMenuFolder')) {
    $removalId = 'Remove' + $directoryId
    Add-InstallerRow $tables.RemoveFile @($removalId, $shortcutComponent, $null, $directoryId, 2)
}

Add-InstallerRow $tables.Media @(1, $fileSequence, $null, '#MarkVibe.cab', $null, $null)
Add-InstallerRow $tables.Upgrade @($upgradeCode, $null, $installerVersion, $null, 0, $null, 'MARKVIBE_OLDER_PRODUCTS')
Add-InstallerRow $tables.Upgrade @($upgradeCode, $installerVersion, $null, $null, 2, $null, 'MARKVIBE_NEWER_PRODUCTS')
Add-InstallerRow $tables.LaunchCondition @(
    'Installed OR NOT MARKVIBE_NEWER_PRODUCTS',
    'A newer MarkVibe MSI is already installed. Remove it before installing this version.')
Add-InstallerRow $tables.AppSearch @('MARKVIBE_WINDOWS_BUILD', 'WindowsCurrentBuild')
Add-InstallerRow $tables.RegLocator @('WindowsCurrentBuild', 2, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'CurrentBuildNumber', 18)
Add-InstallerRow $tables.LaunchCondition @(
    'Installed OR (VersionNT64 AND MARKVIBE_WINDOWS_BUILD >= 22000)',
    'MarkVibe requires Windows 11 or newer. Microsoft Edge WebView2 Runtime must also be installed.')

# The Microsoft runtime installers register architecture-specific roots. Search both
# registry views because the runtime and host have used different views over time.
$dotnetRegistryKey = "SOFTWARE\dotnet\Setup\InstalledVersions\$Architecture"
Add-InstallerRow $tables.AppSearch @('MARKVIBE_DOTNET_ROOT_64', 'DotNetInstallLocation64')
Add-InstallerRow $tables.RegLocator @('DotNetInstallLocation64', 2, $dotnetRegistryKey, 'InstallLocation', 18)
Add-InstallerRow $tables.AppSearch @('MARKVIBE_DOTNET_ROOT_32', 'DotNetInstallLocation32')
Add-InstallerRow $tables.RegLocator @('DotNetInstallLocation32', 2, $dotnetRegistryKey, 'InstallLocation', 2)

# UI and execute both check the runtime. Quiet and basic UI run only execute,
# while full UI can offer Microsoft's link before any MSI transaction begins.
# Continue on a script-host failure so the authored prerequisite error still runs.
Add-InstallerRow $tables.CustomAction @('CheckMarkVibeRuntime', 4165, 'MarkVibeRuntimeCheck', 'CheckMarkVibeRuntime')
Add-InstallerRow $tables.CustomAction @('RequireMarkVibeRuntime', 19, $null,
    'Install [MarkVibeRuntimeDisplayName] from [MarkVibeRuntimeDownload], then reopen this installer.')
Add-InstallerRow $tables.TextStyle @('InstallerBody', 'Segoe UI', 9, $null, 0)
Add-InstallerRow $tables.TextStyle @('InstallerHeading', 'Segoe UI', 16, $null, 1)
Add-SetupDialog $tables 'MarkVibeProgress' $false
Add-SetupDialog $tables 'MarkVibeRuntimeRequired' $true

Add-InstallerRow $tables.Dialog @('MarkVibeConfirmCancel', 50, 50, 260, 77, 3, '[MarkVibeSetupName] Setup', 'No', 'No', 'No')
Add-InstallerRow $tables.Control @('MarkVibeConfirmCancel', 'Question', 'Text', 16, 18, 228, 16, 3, $null,
    'Are you sure you want to cancel setup?', $null, $null)
Add-InstallerRow $tables.Control @('MarkVibeConfirmCancel', 'Yes', 'PushButton', 129, 50, 55, 17, 3, $null, '&Yes', 'No', $null)
Add-InstallerRow $tables.Control @('MarkVibeConfirmCancel', 'No', 'PushButton', 189, 50, 55, 17, 3, $null, '&No', 'Yes', $null)
Add-InstallerRow $tables.ControlEvent @('MarkVibeConfirmCancel', 'Yes', 'EndDialog', 'Exit', '1', 1)
Add-InstallerRow $tables.ControlEvent @('MarkVibeConfirmCancel', 'No', 'EndDialog', 'Return', '1', 1)

Add-InstallerRow $tables.InstallUISequence @('FindRelatedProducts', $null, 25)
Add-InstallerRow $tables.InstallUISequence @('AppSearch', $null, 50)
Add-InstallerRow $tables.InstallUISequence @('LaunchConditions', $null, 100)
Add-InstallerRow $tables.InstallUISequence @('CheckMarkVibeRuntime', 'NOT Installed AND UILevel = 5', 110)
Add-InstallerRow $tables.InstallUISequence @('MarkVibeRuntimeRequired', 'NOT Installed AND NOT MarkVibeRuntimeReady AND UILevel = 5', 120)
Add-InstallerRow $tables.InstallUISequence @('CostInitialize', $null, 800)
Add-InstallerRow $tables.InstallUISequence @('FileCost', $null, 900)
Add-InstallerRow $tables.InstallUISequence @('CostFinalize', $null, 1000)
Add-InstallerRow $tables.InstallUISequence @('MarkVibeProgress', 'UILevel = 5', 1280)
Add-InstallerRow $tables.InstallUISequence @('ExecuteAction', $null, 1300)

$executeActions = [ordered]@{
    FindRelatedProducts = 25
    AppSearch = 50
    LaunchConditions = 100
    CostInitialize = 800
    FileCost = 900
    CostFinalize = 1000
    InstallValidate = 1400
    InstallInitialize = 1500
    RemoveExistingProducts = 1501
    ProcessComponents = 1600
    UnpublishFeatures = 1800
    RemoveShortcuts = 3200
    RemoveFiles = 3500
    RemoveFolders = 3600
    CreateFolders = 3700
    InstallFiles = 4000
    CreateShortcuts = 4500
    RemoveRegistryValues = 2600
    WriteRegistryValues = 5000
    RegisterUser = 6000
    RegisterProduct = 6100
    PublishFeatures = 6300
    PublishProduct = 6400
    InstallFinalize = 6600
}

foreach ($action in $executeActions.GetEnumerator()) {
    Add-InstallerRow $tables.InstallExecuteSequence @($action.Key, $null, $action.Value)
}

# Installed products retain their maintenance and uninstall paths even when .NET
# has since been removed. A major upgrade still checks before removing the old app.
Add-InstallerRow $tables.InstallExecuteSequence @('CheckMarkVibeRuntime', 'NOT Installed', 110)
Add-InstallerRow $tables.InstallExecuteSequence @('RequireMarkVibeRuntime', 'NOT Installed AND NOT MarkVibeRuntimeReady', 120)

# A type-18 action starts our installed executable after installation commits.
# Async + Continue leaves installation success independent of the optional dialog.
# Quiet installs, repair and removal never start UI; first normal launch can offer it later.
Add-InstallerRow $tables.CustomAction @(
    'OfferMarkdownAssociation', 210, $applicationFileId, '--configure-file-associations')
Add-InstallerRow $tables.InstallExecuteSequence @(
    'OfferMarkdownAssociation', 'NOT Installed AND NOT REMOVE AND UILevel >= 4', 6700)

$directiveFile = Join-Path $workRoot 'MarkVibe.ddf'
[System.IO.File]::WriteAllLines($directiveFile, $cabinetDirectives, [Text.Encoding]::Default)
& $makeCab /F $directiveFile | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "makecab.exe failed with exit code $LASTEXITCODE. MSI packaging stopped."
}

$cabinetFile = Join-Path $workRoot 'MarkVibe.cab'
if (-not (Test-Path -LiteralPath $cabinetFile)) {
    throw 'makecab.exe did not create the expected embedded cabinet.'
}

$installer = $null
$database = $null
$summary = $null

try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($outputFile, 3)

    foreach ($table in $tables.Values) {
        Import-InstallerTable $database $table $workRoot
    }

    Add-InstallerStream $installer $database '_Streams' 'MarkVibe.cab' $cabinetFile
    Add-InstallerStream $installer $database 'Icon' 'MarkVibeIcon' $IconPath
    Add-InstallerStream $installer $database 'Binary' 'MarkVibeSetupIcon' $IconPath
    Add-InstallerStream $installer $database 'Binary' 'MarkVibeRuntimeCheck' (Join-Path $PSScriptRoot 'Check-MsiRuntime.js')

    $summary = $database.GetType().InvokeMember(
        'SummaryInformation',
        [Reflection.BindingFlags]::GetProperty,
        $null,
        $database,
        @(12))
    Set-InstallerSummaryProperty $summary 1 1252
    Set-InstallerSummaryProperty $summary 2 'Installation Database'
    Set-InstallerSummaryProperty $summary 3 $productName
    Set-InstallerSummaryProperty $summary 4 $packageMetadata.manufacturer
    Set-InstallerSummaryProperty $summary 5 'Installer, MSI, Markdown, VibeWare'
    Set-InstallerSummaryProperty $summary 6 "MarkVibe $Version. MIT. Per-user desktop installer."
    $installerPlatform = if ($Architecture -eq 'arm64') { 'Arm64' } else { 'x64' }
    Set-InstallerSummaryProperty $summary 7 "$installerPlatform;1033"
    Set-InstallerSummaryProperty $summary 9 ([Guid]::NewGuid().ToString('B').ToUpperInvariant())
    Set-InstallerSummaryProperty $summary 14 500
    Set-InstallerSummaryProperty $summary 15 10
    Set-InstallerSummaryProperty $summary 18 'MarkVibe native MSI packaging'
    Set-InstallerSummaryProperty $summary 19 2
    $summary.Persist()
    $database.Commit()
} finally {
    foreach ($comObject in @($summary, $database, $installer)) {
        if ($comObject) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) | Out-Null
        }
    }
}

Write-Host "Created $productName MSI $installerVersion (application $Version), $($payloadFiles.Count) payload files."
