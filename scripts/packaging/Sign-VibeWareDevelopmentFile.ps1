# VBWR B
# Project: VibeKit | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; Windows SDK signing for local development artifacts
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E

<#
.SYNOPSIS
Sign application EXEs, MSI installers or MSIX packages by thumbprint in CurrentUser/My.
.DESCRIPTION
Use the portable signing kit to register the certificate before running this command.
For MSIX, set the manifest Publisher to the certificate subject before packaging.
#>

[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Path, [string]$Publisher, [switch]$WhenConfigured)

$ErrorActionPreference = 'Stop'
if ($WhenConfigured -and -not [Environment]::GetEnvironmentVariable('VIBEWARE_DEVELOPMENT_CERTIFICATE_THUMBPRINT', 'User')) {
    return
}
$certificate = & (Join-Path $PSScriptRoot 'Get-VibeWareDevelopmentCertificate.ps1') -Publisher $Publisher
try {
    $signTool = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty Source
    if (-not $signTool) {
        $processor = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $architectures = if ($processor -eq 'arm64') { @('arm64', 'x64') } else { @($processor) }
        $kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
        $buildTools = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'
        $versions = @(Get-ChildItem -LiteralPath $kitRoot -Directory -ErrorAction SilentlyContinue)
        foreach ($package in (Get-ChildItem -LiteralPath $buildTools -Directory -ErrorAction SilentlyContinue)) {
            $versions += Get-ChildItem -LiteralPath (Join-Path $package.FullName 'bin') -Directory -ErrorAction SilentlyContinue
        }
        $versions = $versions | Where-Object { $_.Name -as [version] } |
            Sort-Object { [version]$_.Name } -Descending
        $signTool = foreach ($directory in $versions) {
            foreach ($architecture in $architectures) {
                $candidate = Join-Path $directory.FullName "$architecture/signtool.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $candidate
                }
            }
        }
        $signTool = $signTool | Select-Object -First 1
    }
    if (-not $signTool) {
        throw 'Windows SDK SignTool is unavailable. Add the existing SDK tool to PATH or restore Microsoft.Windows.SDK.BuildTools.'
    }
    foreach ($artifact in $Path) {
        $file = Get-Item -LiteralPath $artifact
        if ($file.PSIsContainer -or $file.Extension -notin '.exe', '.msi', '.msix', '.msixbundle') {
            throw "Unsupported development signing target: $artifact"
        }
        & $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $file.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed for '$($file.FullName)' with exit code $LASTEXITCODE."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "The artifact's signature does not match the VibeWare development certificate: $artifact"
        }
    }
} finally {
    $certificate.Dispose()
}
