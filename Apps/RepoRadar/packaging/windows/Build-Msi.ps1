#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$WixBin
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The MSI must be built on Windows.' }

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectFile = Join-Path $projectRoot 'src\RepoRadar.csproj'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0'
}
if (-not $WixBin) {
    $candleCommand = Get-Command candle.exe -ErrorAction SilentlyContinue
    if ($candleCommand) { $WixBin = Split-Path -Parent $candleCommand.Source }
    else { $WixBin = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'WiX Toolset v3.14\bin' }
}
foreach ($tool in 'candle.exe', 'light.exe', 'dark.exe', 'WixUIExtension.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $WixBin $tool) -PathType Leaf)) {
        throw "Missing $tool. Install WiX Toolset 3.14 from https://github.com/wixtoolset/wix3/releases or pass -WixBin."
    }
}

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
}
function Get-FileId([string]$Name) {
    $bytes = [Text.Encoding]::UTF8.GetBytes("RepoRadar/win-$Architecture/" + $Name.ToLowerInvariant())
    return 'F' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 32)
}
function Assert-Child([string]$Parent, [string]$Target) {
    $parentPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent))
    $targetPath = [IO.Path]::GetFullPath($Target)
    if (!$targetPath.StartsWith($parentPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected directory: $targetPath"
    }
    $itemPath = $targetPath
    while ($itemPath -and $itemPath.Length -ge $projectRoot.Length) {
        if ((Test-Path -LiteralPath $itemPath) -and ((Get-Item -LiteralPath $itemPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "The package directory contains a junction or symbolic link: $itemPath"
        }
        $itemPath = [IO.Path]::GetDirectoryName($itemPath)
    }
}

<#
.SYNOPSIS
Write the approved 64-pixel icon frame as a single-frame ICO for the MSI control.
#>
function Export-SetupIcon([string]$Source, [string]$Target) {
    $bytes = [IO.File]::ReadAllBytes($Source)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
        [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw "Invalid setup icon: $Source"
    }
    $frameCount = [BitConverter]::ToUInt16($bytes, 4)
    $tableEnd = 6 + (16 * $frameCount)
    if ($tableEnd -gt $bytes.Length) { throw "Invalid setup icon directory: $Source" }

    for ($index = 6; $index -lt $tableEnd; $index += 16) {
        if ($bytes[$index] -ne 64 -or $bytes[$index + 1] -ne 64) { continue }
        $length = [BitConverter]::ToUInt32($bytes, $index + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $index + 12)
        if ($length -eq 0 -or $offset -lt $tableEnd -or [long]$offset + $length -gt $bytes.Length) {
            throw "Invalid 64-pixel icon frame: $Source"
        }

        $frame = [byte[]]::new(22 + $length)
        $frame[2] = 1 # ICO image type.
        $frame[4] = 1 # One image entry.
        [Array]::Copy($bytes, $index, $frame, 6, 16)
        [BitConverter]::GetBytes([uint32]22).CopyTo($frame, 18)
        [Array]::Copy($bytes, $offset, $frame, 22, $length)
        [IO.File]::WriteAllBytes($Target, $frame)
        return
    }

    throw "Missing 64-pixel icon frame: $Source"
}

$packageDir = Join-Path $projectRoot 'artifacts\msi'
$stage = Join-Path $packageDir ('.stage-' + [Guid]::NewGuid().ToString('N'))
Assert-Child $projectRoot $stage
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$publish = Join-Path $stage 'publish'

# A fresh output directory ensures no files from previous builds enter the installer.
Invoke-Checked dotnet @('publish', $projectFile, '-c', $Configuration, '-r', "win-$Architecture", '--self-contained', 'false',
    '-p:PublishSingleFile=false', '-p:UseAppHost=true', '-p:DebugSymbols=false', '-p:DebugType=None', '-o', $publish)
$configs = @(Get-ChildItem -LiteralPath $publish -File -Filter '*.runtimeconfig.json')
if ($configs.Count -ne 1) { throw 'Expected one runtime configuration for the published application.' }
$runtime = (Get-Content -LiteralPath $configs[0].FullName -Raw | ConvertFrom-Json -AsHashtable)['runtimeOptions']
$framework = if ($null -ne $runtime) { $runtime['framework'] }
if ($null -eq $framework -or $runtime['tfm'] -ne 'net10.0' -or $framework['name'] -ne 'Microsoft.NETCore.App' -or
    $framework['version'] -notmatch '^10\.0\.[0-9]+$' -or $runtime.Contains('includedFrameworks') -or $runtime.Contains('frameworks') -or
    ($runtime.Contains('rollForward') -and $runtime['rollForward'] -notin 'Minor', 'LatestPatch') -or
    ($runtime.Contains('applyPatches') -and -not $runtime['applyPatches'])) {
    throw 'A framework-dependent Microsoft.NETCore.App 10.0 publish with normal servicing roll-forward is required.'
}
$runtimeVersion = $framework['version']
$appName = $configs[0].Name -replace '\.runtimeconfig\.json$', ''
if ($appName -ne 'repo-radar') { throw 'Expected the repo-radar application host.' }
$appExeName = $appName + '.exe'
$appHostPath = Join-Path $publish $appExeName
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($appHostPath))
try {
    $reader.BaseStream.Position = 60
    $reader.BaseStream.Position = $reader.ReadUInt32()
    $machine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $machine) {
        throw "The application host does not match Windows $Architecture."
    }
}
finally { $reader.Dispose() }
$versionInfo = (Get-Item -LiteralPath (Join-Path $publish ($appName + '.dll'))).VersionInfo
$productVersion = '{0}.{1}.{2}' -f $versionInfo.ProductMajorPart, $versionInfo.ProductMinorPart, $versionInfo.ProductBuildPart
$packageName = "RepoRadar-$productVersion-win-$Architecture.msi"

# Bundle the apphost, runtime manifest and app-specific libraries from the source publish.
# Catalogs, preferences, workspace databases and debug files are personal or local to a build.
$payload = @(Get-ChildItem -LiteralPath $publish -File | Where-Object {
    $_.Extension -eq '.dll' -or $_.Name -in @($appExeName, "$appName.deps.json", "$appName.runtimeconfig.json")
})
if ($payload | Where-Object { $_.Name -in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll') }) {
    throw 'Bundled .NET runtime files found in output.'
}
foreach ($notice in 'LICENSE','THIRD-PARTY-NOTICES') {
    $payload += Get-Item -LiteralPath (Join-Path $projectRoot $notice) -ErrorAction Stop
}
$payload = @($payload | Sort-Object Name)

$payloadWxs = Join-Path $stage 'PublishedApp.wxs'
$xmlSettings = [Xml.XmlWriterSettings]::new()
$xmlSettings.Indent = $true
$writer = [Xml.XmlWriter]::Create($payloadWxs, $xmlSettings)
try {
    $namespace = 'http://schemas.microsoft.com/wix/2006/wi'
    $writer.WriteStartElement('Wix', $namespace)
    $writer.WriteStartElement('Fragment', $namespace)
    $writer.WriteStartElement('ComponentGroup', $namespace)
    $writer.WriteAttributeString('Id', 'PublishedApp')
    $writer.WriteAttributeString('Directory', 'INSTALLFOLDER')
    foreach ($file in $payload) {
        $id = Get-FileId $file.Name
        $writer.WriteStartElement('Component', $namespace)
        $writer.WriteAttributeString('Id', "C$id")
        # Stable HKCU keypaths allow Windows Installer to repair per-user components.
        $writer.WriteAttributeString('Guid', ([guid]::ParseExact($id.Substring(1), 'N')).ToString('B'))
        $writer.WriteStartElement('File', $namespace)
        $writer.WriteAttributeString('Id', $id)
        $writer.WriteAttributeString('Source', $file.FullName)
        $writer.WriteEndElement()
        $writer.WriteStartElement('RegistryValue', $namespace)
        $writer.WriteAttributeString('Root', 'HKCU')
        $writer.WriteAttributeString('Key', 'Software\RepoRadar\Installer\Files')
        $writer.WriteAttributeString('Name', $file.Name)
        $writer.WriteAttributeString('Value', '1')
        $writer.WriteAttributeString('Type', 'integer')
        $writer.WriteAttributeString('KeyPath', 'yes')
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteEndDocument()
}
finally { $writer.Dispose() }

$msi = Join-Path $stage $packageName
$extension = Join-Path $WixBin 'WixUIExtension.dll'
$icon = Join-Path $PSScriptRoot 'RepoRadar.ico'
$dialogIcon = Join-Path $stage 'Setup.ico'
Export-SetupIcon $icon $dialogIcon
$runtimeCheck = Join-Path $PSScriptRoot 'Check-MsiRuntime.js'
Invoke-Checked (Join-Path $WixBin 'candle.exe') @('-nologo', '-arch', $Architecture, "-dArchitecture=$Architecture", "-dAppExeName=$appExeName",
    "-dInstallerIcon=$icon", "-dSetupIcon=$dialogIcon", "-dRuntimeCheck=$runtimeCheck",
    "-dRuntimeVersion=$runtimeVersion", "-dProductVersion=$productVersion", '-out', ($stage + '\'),
    (Join-Path $PSScriptRoot 'Product.wxs'), $payloadWxs)
Invoke-Checked (Join-Path $WixBin 'light.exe') @('-nologo', '-cultures:en-us', '-ext', $extension,
    '-out', $msi, (Join-Path $stage 'Product.wixobj'), (Join-Path $stage 'PublishedApp.wixobj'))

# Dark extracts the cabinet for verification without installing or registering the product.
$extract = Join-Path $stage 'inspect'
Invoke-Checked (Join-Path $WixBin 'dark.exe') @('-nologo', '-x', $extract, $msi, '-o', (Join-Path $stage 'inspect.wxs'))
foreach ($file in $payload) {
    $extracted = Join-Path $extract ('File\' + (Get-FileId $file.Name))
    if (!(Test-Path -LiteralPath $extracted -PathType Leaf) -or
        (Get-FileHash -LiteralPath $extracted).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
        throw "Cabinet verification failed for $($file.Name)"
    }
}
$destination = Join-Path $packageDir $packageName
Assert-Child $packageDir $destination
[IO.File]::Move($msi, $destination, $true)
foreach ($old in Get-ChildItem -LiteralPath $packageDir -Filter "RepoRadar-*-win-$Architecture.msi" -File) {
    if ($old.Name -ne $packageName -and $old.Name -match "^RepoRadar-\d+\.\d+\.\d+-win-$Architecture\.msi$") {
        Assert-Child $packageDir $old.FullName
        Remove-Item -LiteralPath $old.FullName -Force
    }
}
Assert-Child $packageDir $stage
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "Verified Windows $Architecture per-user MSI: $destination"
