# VBWR B
#
# Project: UlanziDeck
# Repository: https://github.com/umbertotechnopreneur/ulanzi-dock-vibeware
# Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
#
# VibeWare: Human intent, AI execution, and plenty of tokens
# Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
#
# Created with AI: OpenAI Codex; cross-platform release packaging, 2026-10-09.
# Modified with AI: OpenAI Codex; comments and readability; 2026-10-27
# Human guidance: Umberto Giacobbi; AGENTS.md; readability/documentation request
# Evidence: docs/CODE-REVIEW.md and current working-tree changes
#
# Copyright (c) 2026 Umberto Giacobbi
# SPDX-License-Identifier: MIT
# License: MIT - see LICENSE
#
# VBWR E

<#
.SYNOPSIS
Package a target already compiled from this checkout into its distribution files.
.DESCRIPTION
Packages source-built binaries and dependency notices into a ZIP and per-user MSI on
Windows, or a tar.gz on Linux. Does not compile the app, test it, install it or publish it.
.PARAMETER Target
Rust target triple matching the selected Label and its source-build output directory.
.PARAMETER Label
Supported platform label used in filenames and archive-format selection.
.PARAMETER Tag
Version label used in the output archive name; this does not create a Git tag.
.PARAMETER WixPath
Path to WiX 5.0.2, or the command name if it is on PATH. Required for Windows packages.
#>

[CmdletBinding()]
param(
    [string]$Target,
    [ValidateSet('windows-x64', 'windows-arm64', 'linux-x64')]
    [string]$Label,
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Tag,
    [string]$WixPath = 'wix'
)

if ($PSBoundParameters.Count -eq 0) {
    & (Join-Path $PSScriptRoot 'ulanzi.ps1')
    return
}
foreach ($name in 'Target', 'Label', 'Tag') {
    if (-not $PSBoundParameters.ContainsKey($name)) { throw "-$name is required. Run without parameters for the menu." }
}

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$targets = @{
    'windows-x64' = @('x86_64-pc-windows-msvc', 'x86_64-pc-windows-gnu')
    'windows-arm64' = 'aarch64-pc-windows-msvc'
    'linux-x64' = 'x86_64-unknown-linux-gnu'
}
if ($Target -notin $targets[$Label]) { throw "Target $Target does not match platform $Label." }
$isWindowsPackage = $Label.StartsWith('windows-')
if ($isWindowsPackage) {
    if (-not $IsWindows) { throw 'Windows installer packaging requires Windows.' }
    $wix = (Get-Command $WixPath -CommandType Application -ErrorAction Stop).Source
    $signer = Join-Path $root '../../scripts/packaging/Sign-VibeWareDevelopmentFile.ps1'
    $publicCi = $env:GITHUB_ACTIONS -eq 'true'
}
$archiveRoot = Join-Path $root 'dist'
$stem = "ulanzi-dock-vibeware-$Tag-$Label"
$suffix = if ($isWindowsPackage) { 'zip' } else { 'tar.gz' }
$outputs = @("$stem.$suffix")
if ($isWindowsPackage) { $outputs += "$stem.msi" }
foreach ($name in $outputs) {
    foreach ($file in $name, "$name.sha256") {
        if (Test-Path -LiteralPath (Join-Path $archiveRoot $file)) { throw "Output already exists: $file" }
    }
}

$outputDirectory = New-Item -ItemType Directory -Path $archiveRoot -Force
if ($outputDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked dist directory: $archiveRoot" }
$work = New-Item -ItemType Directory -Path (Join-Path $archiveRoot ".package-$([Guid]::NewGuid().ToString('N'))")
$package = Join-Path $work.FullName 'archive'
$archive = Join-Path $work.FullName "$stem.$suffix"
try {
    New-Item -ItemType Directory -Path "$package/docs/licenses", "$package/assets/fonts", "$package/assets/banners" -Force | Out-Null
    $binaryName = if ($isWindowsPackage) { 'ulanzi-dock-vibeware.exe' } else { 'ulanzi-dock-vibeware' }
    Copy-Item -LiteralPath (Join-Path $root "target/$Target/release/$binaryName") -Destination $package
    if ($isWindowsPackage) {
        Copy-Item -LiteralPath (Join-Path $root "target/$Target/release/ulanzi-dock-launcher.exe") -Destination $package
        & $signer -Path @("$package/ulanzi-dock-vibeware.exe", "$package/ulanzi-dock-launcher.exe") -WhenConfigured:$publicCi
    }
    Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') -Destination $package
    $docs = 'ARTWORK.md', 'THIRD-PARTY.md', 'RELEASING.md', 'INSTALLATION.md', 'PLATFORMS.md', 'README.md', 'BRANDING.md', 'DEVELOPMENT.md', 'ulanzi-dock-vibeware-guide-v2.pdf', 'obs-creator-quick-start.pdf'
    Copy-Item -LiteralPath ($docs | ForEach-Object { Join-Path $root "docs/$_" }) -Destination "$package/docs"
    Copy-Item -LiteralPath (Join-Path $root 'docs/design'), (Join-Path $root 'docs/photos'), (Join-Path $root 'docs/screenshots') -Destination "$package/docs" -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'presets') -Destination $package -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'assets/banners/ulanzi-dock-light.png') -Destination "$package/assets/banners"
    Copy-Item -Path (Join-Path $root 'docs/licenses/*') -Destination "$package/docs/licenses"
    Copy-Item -LiteralPath (Join-Path $root 'assets/fonts/bitstream-vera-license.txt') -Destination "$package/assets/fonts"

    Push-Location -LiteralPath $root
    try {
        & python (Join-Path $PSScriptRoot 'tools.py') notices --mode cargo --target $Target --output "$package/third-party-notices"
        if ($LASTEXITCODE -ne 0) { throw 'Dependency notice collection failed.' }
    } finally { Pop-Location }

    if ($isWindowsPackage) {
        Compress-Archive -Path "$package/*" -DestinationPath $archive
        $payload = Join-Path $work.FullName 'installer'
        New-Item -ItemType Directory -Path "$payload/notices" -Force | Out-Null
        Copy-Item -LiteralPath "$package/ulanzi-dock-vibeware.exe", "$package/ulanzi-dock-launcher.exe", "$package/LICENSE" -Destination $payload
        Copy-Item -LiteralPath "$package/docs/THIRD-PARTY.md", "$package/docs/licenses", "$package/assets/fonts/bitstream-vera-license.txt", "$package/third-party-notices" -Destination "$payload/notices" -Recurse
        $versionLine = Select-String -LiteralPath (Join-Path $root 'Cargo.toml') -Pattern '^version\s*=\s*"(\d+\.\d+\.\d+)"' | Select-Object -First 1
        if (-not $versionLine) { throw 'A three-part package version is required in Cargo.toml.' }
        $source = Join-Path $work.FullName 'installer.wxs'
        & python (Join-Path $PSScriptRoot 'create-msi.py') $versionLine.Matches[0].Groups[1].Value $payload $source
        if ($LASTEXITCODE -ne 0) { throw 'MSI authoring failed.' }
        $msi = Join-Path $work.FullName "$stem.msi"
        $architecture = $Label.Substring('windows-'.Length)
        $uiExtension = 'WixToolset.UI.wixext/5.0.2'
        Push-Location -LiteralPath $work.FullName
        try {
            & $wix extension add $uiExtension
            if ($LASTEXITCODE -ne 0) { throw 'WiX UI extension setup failed.' }
            & $wix build $source -arch $architecture -ext $uiExtension -pdbtype none -o $msi
            if ($LASTEXITCODE -ne 0) { throw 'Windows Installer compilation failed.' }
        } finally { Pop-Location }
        & $signer -Path $msi -WhenConfigured:$publicCi
    } else {
        # tar preserves the executable permission on Linux.
        & tar -czf $archive -C $package .
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
    }

    foreach ($name in $outputs) {
        $file = Join-Path $work.FullName $name
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name" | Set-Content -LiteralPath "$file.sha256" -Encoding ascii
    }
    foreach ($name in $outputs) {
        Move-Item -LiteralPath (Join-Path $work.FullName $name), (Join-Path $work.FullName "$name.sha256") -Destination $archiveRoot
        Write-Host "Package ready: $(Join-Path $archiveRoot $name)"
    }
} finally {
    $resolved = Get-Item -LiteralPath $work.FullName -Force
    $insideDist = $resolved.FullName.StartsWith($archiveRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    if ($insideDist -and -not ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
    }
}
