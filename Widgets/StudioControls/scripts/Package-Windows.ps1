[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [string]$PublishDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts'),
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$version = Get-StudioVersion -ProjectRoot $root
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $root "artifacts/windows-$Architecture" }
$source = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "StudioControls-$version-windows-$Architecture"
$msi = Join-Path $output "$name.msi"
if (Test-Path -LiteralPath $msi) { throw "Choose a new output directory: $msi" }
Assert-PublishLayout -Directory $source
$stage = Join-Path $output ('.studio-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$wxs = Join-Path $stage "$name.wxs"
& python (Join-Path $PSScriptRoot 'create-wix.py') $source $wxs --architecture $Architecture
if ($LASTEXITCODE -ne 0) { throw 'Studio Controls WiX source generation failed.' }
& (Join-Path $root '../Shared/Build-WindowsMsi.ps1') -WixPath $WixPath -Source $wxs -Output $msi -IntermediateDirectory (Join-Path $stage 'wix') -Architecture $Architecture

# Include exactly the files already verified against the Studio publish manifest.
$portable = Join-Path $stage 'portable'
foreach ($file in Get-PublishFiles -Directory $source) {
    $target = Join-Path $portable ([IO.Path]::GetRelativePath($source, $file.FullName))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
& python (Join-Path $root '../Shared/package-portable.py') --payload $portable --executable StudioControls.Windows.exe --architecture $Architecture --native Microsoft.UI.Xaml.dll --output (Join-Path $output "$name-portable.zip")
if ($LASTEXITCODE -ne 0) { throw 'Studio Controls portable packaging failed.' }
