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
Generate the transparent package logos from the 256-pixel frame of the native MarkVibe icon.
#>

param(
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$IconPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$icon = [System.IO.File]::ReadAllBytes($IconPath)
if ($icon.Length -lt 6 -or [BitConverter]::ToUInt16($icon, 0) -ne 0 -or
    [BitConverter]::ToUInt16($icon, 2) -ne 1) {
    throw 'Expected a Windows icon file for the package logos.'
}
$entryCount = [BitConverter]::ToUInt16($icon, 4)
$directoryEnd = 6 + (16 * $entryCount)
$frameOffset = 0
$frameLength = 0
if ($directoryEnd -le $icon.Length) {
    for ($entry = 6; $entry -lt $directoryEnd; $entry += 16) {
        # A zero width and height in an ICO directory entry represent 256 pixels.
        if ($icon[$entry] -eq 0 -and $icon[$entry + 1] -eq 0) {
            $frameLength = [BitConverter]::ToUInt32($icon, $entry + 8)
            $frameOffset = [BitConverter]::ToUInt32($icon, $entry + 12)
            break
        }
    }
}
if ($frameOffset -lt $directoryEnd -or $frameLength -eq 0 -or
    ([long]$frameOffset + $frameLength) -gt $icon.Length) {
    throw 'The MarkVibe icon is missing its 256-pixel frame.'
}

$stream = [System.IO.MemoryStream]::new($icon, [int]$frameOffset, [int]$frameLength, $false)
try {
    $source = [System.Drawing.Image]::FromStream($stream)
    try {
        [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
        foreach ($asset in @(
                @{ Name = 'Square44x44Logo.png'; Size = 44 },
                @{ Name = 'Square150x150Logo.png'; Size = 150 },
                @{ Name = 'StoreLogo.png'; Size = 50 })) {
            $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.DrawImage($source, 0, 0, $asset.Size, $asset.Size)
                } finally {
                    $graphics.Dispose()
                }
                $bitmap.Save((Join-Path $Destination $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $bitmap.Dispose()
            }
        }
    } finally {
        $source.Dispose()
    }
} finally {
    $stream.Dispose()
}
