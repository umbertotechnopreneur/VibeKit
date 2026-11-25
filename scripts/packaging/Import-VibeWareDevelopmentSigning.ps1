# VBWR B
# Project: VibeKit | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; portable development key import
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E

<#
.SYNOPSIS
Import the adjacent VibeWare signing backup for the current user and trust its public certificate.
.DESCRIPTION
Run from the copied kit on another Windows machine. Only the public certificate is
passed to the elevated trust command. Subsequent runs reuse the same certificate.
.PARAMETER SkipTrust
Configure signing for this account without changing machine-wide certificate trust.
#>

[CmdletBinding()]
param([switch]$SkipTrust)

$ErrorActionPreference = 'Stop'
$certFile = Join-Path $PSScriptRoot 'VibeWare-Development.cer'
$pfxFile = Join-Path $PSScriptRoot 'VibeWare-Development.pfx'
$passwordFile = Join-Path $PSScriptRoot 'Passphrase.txt'
$tools = @('Get-VibeWareDevelopmentCertificate.ps1', 'Sign-VibeWareDevelopmentFile.ps1')
$account = [Security.Principal.WindowsIdentity]::GetCurrent().Name

Write-Host 'VibeWare development signing'
Write-Host "  Kit: $PSScriptRoot"
Write-Host "  Windows account: $account"
Write-Host ''
Write-Host '[1/5] Check the signing kit'
foreach ($requiredFile in @($certFile, $pfxFile, $passwordFile) + @($tools | ForEach-Object { Join-Path $PSScriptRoot $_ })) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Missing kit file: $requiredFile. Copy the complete signing kit folder."
    }
    Write-Host "  Found $([IO.Path]::GetFileName($requiredFile))"
}
$publicCert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certFile)

try {
    $now = Get-Date
    if ($publicCert.Subject -cne 'CN=Umberto Giacobbi' -or $publicCert.NotBefore -gt $now -or $publicCert.NotAfter -le $now) {
        throw 'The kit does not contain a current certificate for CN=Umberto Giacobbi.'
    }
    $thumbprint = $publicCert.Thumbprint
    Write-Host "  Publisher: $($publicCert.Subject)"
    Write-Host "  Thumbprint: $thumbprint"
    Write-Host "  Valid through: $($publicCert.NotAfter.ToString('yyyy-MM-dd HH:mm zzz'))"
    Write-Host '[2/5] Verify the backup and prepare the private key'
    $password = ConvertTo-SecureString ([IO.File]::ReadAllText($passwordFile).Trim()) -AsPlainText -Force
    try {
        $leaves = @((Get-PfxData -FilePath $pfxFile -Password $password).EndEntityCertificates)
        if ($leaves.Count -ne 1 -or $leaves[0].Thumbprint -ne $thumbprint) {
            throw 'The private-key backup does not match the public certificate.'
        }
        Write-Host '  PFX opened successfully and matches the public certificate.'
        $existing = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
        if ($existing -and $existing.HasPrivateKey) {
            Write-Host '  Private key already present in CurrentUser\My. Reusing it.'
        } else {
            $imported = Import-PfxCertificate -FilePath $pfxFile -Password $password -CertStoreLocation 'Cert:\CurrentUser\My' -Exportable
            if (-not ($imported | Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey })) {
                throw 'Windows did not import the signing private key.'
            }
            Write-Host '  Imported the private key into CurrentUser\My.'
        }
    } finally {
        $password.Dispose()
    }

    Write-Host '[3/5] Configure local Windows packaging'
    $toolDirectory = Join-Path $env:LOCALAPPDATA 'VibeWare/DevelopmentSigning'
    [IO.Directory]::CreateDirectory($toolDirectory) | Out-Null
    foreach ($tool in $tools) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $tool) -Destination $toolDirectory -Force
    }
    Write-Host "  Signing tools: $toolDirectory"
    [Environment]::SetEnvironmentVariable('VIBEWARE_DEVELOPMENT_CERTIFICATE_THUMBPRINT', $thumbprint, 'User')
    Write-Host '  Saved VIBEWARE_DEVELOPMENT_CERTIFICATE_THUMBPRINT for this Windows account.'

    Write-Host '[4/5] Check public certificate trust'
    $trustPath = "Cert:\LocalMachine\TrustedPeople\$thumbprint"
    if (Test-Path -LiteralPath $trustPath) {
        Write-Host '  Public certificate already trusted in LocalMachine\TrustedPeople.'
    } elseif ($SkipTrust) {
        Write-Host '  Machine trust was skipped. Run the import without -SkipTrust to enable MSIX installation.'
    } else {
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Import-Certificate -FilePath $certFile -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        } else {
            $certutil = Join-Path $env:WINDIR 'System32\certutil.exe'
            $quotedPath = '"{0}"' -f $certFile
            Write-Host '  Windows will request administrator approval for the public certificate.'
            try {
                $trust = Start-Process -FilePath $certutil -Verb RunAs -ArgumentList @('-addstore', 'TrustedPeople', $quotedPath) -WindowStyle Hidden -Wait -PassThru
            } catch {
                throw "Windows could not start the trust step. Signing is configured. Run this import again to finish MSIX trust. $($_.Exception.Message)"
            }
            if ($trust.ExitCode -ne 0) {
                throw "Windows did not accept the public certificate (exit code $($trust.ExitCode)). Signing is configured. Run this import again to finish MSIX trust."
            }
        }
        if (-not (Test-Path -LiteralPath $trustPath)) {
            throw 'The certificate is missing from LocalMachine/TrustedPeople.'
        }
        Write-Host '  Public certificate trusted in LocalMachine\TrustedPeople.'
    }

    Write-Host '[5/5] Setup summary'
    Write-Host '  Signing: ready for the registered Windows account.'
    if (Test-Path -LiteralPath $trustPath) {
        Write-Host '  MSIX trust: ready. Later builds with the same certificate require no new approval.'
    } else {
        Write-Host '  MSIX trust: pending on this PC.'
    }
} finally {
    $publicCert.Dispose()
}
