# VBWR B
#
# Project: MarkVibe
# Repository: https://github.com/umbertotechnopreneur/MarkVibe
# Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
#
# VibeWare: Human intent, AI execution, and plenty of tokens
# Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
#
# Modified with AI: OpenAI Codex; readability revision; 2026-10-11
# Human guidance: Umberto Giacobbi; repository readability rules and MIT licensing
# Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
#
# Copyright (c) 2026 Umberto Giacobbi
# License: MIT (see LICENSE)
#
# VBWR E

<#
.SYNOPSIS
Install the adjacent MSIX bundle; trust its development certificate only when the explicit switch is supplied.
#>

param(
    [switch]$TrustDevelopmentCertificate,
    [switch]$SkipFileAssociationPrompt
)
$ErrorActionPreference = 'Stop'
$msixBundles = @(
    Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.msixbundle' -File)
if ($msixBundles.Count -ne 1) {

    throw 'Keep this script beside exactly one MarkVibe MSIX bundle.'
}
# .NET is shared with other applications; installing MarkVibe never installs a runtime implicitly.
$buildInfo = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BUILD-INFO.json') -Raw | ConvertFrom-Json
$nativeArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$runtimeArchitecture = if ($nativeArchitecture -in $buildInfo.architectures) { $nativeArchitecture }
    elseif ($nativeArchitecture -eq 'arm64' -and 'x64' -in $buildInfo.architectures) { 'x64' }
    else { throw "This bundle does not support the current Windows architecture: $nativeArchitecture." }
$runtimeRegistry = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\$runtimeArchitecture"
$runtimeRoot = (Get-ItemProperty -LiteralPath $runtimeRegistry -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
$nativeProgramFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$defaultRuntimeRoot = Join-Path $nativeProgramFiles 'dotnet'
if ($nativeArchitecture -eq 'arm64' -and $runtimeArchitecture -eq 'x64') {
    $defaultRuntimeRoot = Join-Path $defaultRuntimeRoot 'x64'
}
$runtimeRoots = @($runtimeRoot,
    [Environment]::GetEnvironmentVariable('DOTNET_ROOT_' + $runtimeArchitecture.ToUpperInvariant()),
    $env:DOTNET_ROOT, $defaultRuntimeRoot) | Where-Object { $_ } | Select-Object -Unique
$compatibleRuntime = $false
foreach ($candidateRoot in $runtimeRoots) {
    $dotnetHost = Join-Path $candidateRoot 'dotnet.exe'
    $runtimeDirectory = Join-Path $candidateRoot 'shared/Microsoft.NETCore.App'
    if (-not (Test-Path -LiteralPath $dotnetHost -PathType Leaf) -or
        -not (Test-Path -LiteralPath $runtimeDirectory -PathType Container)) { continue }
    # A manually provisioned SDK/runtime need not have registry metadata. Check its
    # PE machine field so an x64 runtime cannot satisfy an ARM64 prerequisite.
    $hostReader = [IO.BinaryReader]::new([IO.File]::OpenRead($dotnetHost))
    try {
        if ($hostReader.ReadUInt16() -ne 0x5A4D) { continue }
        $hostReader.BaseStream.Position = 0x3C
        $peOffset = $hostReader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt $hostReader.BaseStream.Length - 6) { continue }
        $hostReader.BaseStream.Position = $peOffset
        if ($hostReader.ReadUInt32() -ne 0x00004550) { continue }
        $machine = $hostReader.ReadUInt16()
        $expectedMachine = if ($runtimeArchitecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
        if ($machine -ne $expectedMachine) { continue }
    } finally {
        $hostReader.Dispose()
    }
    $compatibleRuntime = @(Get-ChildItem -LiteralPath $runtimeDirectory -Directory | Where-Object {
        $_.Name -match '^10\.0\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'coreclr.dll'))
    }).Count -gt 0
    if ($compatibleRuntime) { break }
}
if (-not $compatibleRuntime) {
    throw "Install Microsoft .NET Runtime 10.0 ($runtimeArchitecture) from https://dotnet.microsoft.com/en-us/download/dotnet/10.0/runtime, then run this helper again. MarkVibe does not include .NET."
}
if ($TrustDevelopmentCertificate) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell as administrator to trust the development certificate, then run this script again.'
    }
    Import-Certificate -FilePath (Join-Path $PSScriptRoot 'MarkVibe-Development.cer') -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}
Add-AppxPackage -Path $msixBundles[0].FullName
Write-Host 'MarkVibe installed for the current Windows user. Open it from Start.'

if (-not $SkipFileAssociationPrompt -and [Environment]::UserInteractive) {
    try {
        # Read the installed bundle's own identity, including custom publisher/name builds.
        $bundleArchive = [System.IO.Compression.ZipFile]::OpenRead($msixBundles[0].FullName)
        try {
            $manifestEntry = $bundleArchive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
            if ($null -eq $manifestEntry) {
                throw 'The installed bundle has no identity manifest.'
            }

            $manifestReader = [System.IO.StreamReader]::new($manifestEntry.Open())
            try {
                [xml]$bundleManifest = $manifestReader.ReadToEnd()
            }
            finally {
                $manifestReader.Dispose()
            }
        }
        finally {
            $bundleArchive.Dispose()
        }

        $bundleIdentity = $bundleManifest.Bundle.Identity
        $installedPackages = @(
            Get-AppxPackage -Name $bundleIdentity.Name | Where-Object {
                $_.Publisher -eq $bundleIdentity.Publisher -and
                $_.Version -eq [version]$bundleIdentity.Version
            })
        if ($installedPackages.Count -ne 1) {
            throw 'Could not identify the installed MarkVibe application.'
        }

        $installedExecutable = Join-Path $installedPackages[0].InstallLocation 'MarkVibe.Windows.exe'
        Start-Process -FilePath $installedExecutable -ArgumentList '--configure-file-associations' -Wait
    }
    catch {
        Write-Warning 'MarkVibe is installed, but default-app setup could not be shown. Open MarkVibe from Start to choose Markdown defaults.'
    }
}
