# VBWR B
# Project: VibeKit | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; shared local Windows development signing
# Human guidance: reuse a portable development certificate across repositories
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E

<#
.SYNOPSIS
Return the imported VibeWare development certificate for the current Windows user.
.PARAMETER Publisher
If supplied, require the certificate subject to match the MSIX publisher.
#>

[CmdletBinding()]
param([string]$Publisher)

$ErrorActionPreference = 'Stop'
$thumbprint = [Environment]::GetEnvironmentVariable('VIBEWARE_DEVELOPMENT_CERTIFICATE_THUMBPRINT', 'User')
if ($thumbprint -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Import the VibeWare development signing kit for this Windows account before signing.'
}
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
$now = Get-Date
if (-not $certificate -or -not $certificate.HasPrivateKey -or $certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) {
    throw 'The configured VibeWare development certificate has no usable private key. Import a valid signing kit.'
}
if ($Publisher) {
    $subject = [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher).Name
    if ($certificate.Subject -cne $subject) {
        throw "MSIX publisher '$Publisher' must match the shared development certificate subject '$($certificate.Subject)'."
    }
}
$certificate
