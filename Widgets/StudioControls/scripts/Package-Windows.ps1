[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/windows-x64'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$version = Get-StudioVersion -ProjectRoot $root
$source = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "StudioControls-$version-windows-x64"
$msi = Join-Path $output "$name.msi"
if (Test-Path -LiteralPath $msi) { throw "Choose a new output directory: $msi" }
Assert-PublishLayout -Directory $source
$stage = Join-Path $output ('.studio-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$wxs = Join-Path $stage "$name.wxs"
& python (Join-Path $PSScriptRoot 'create-wix.py') $source $wxs
if ($LASTEXITCODE -ne 0) { throw 'Studio Controls WiX source generation failed.' }
& (Join-Path $root '../Shared/Build-WindowsMsi.ps1') -WixPath $WixPath -Source $wxs -Output $msi -IntermediateDirectory (Join-Path $stage 'wix')
