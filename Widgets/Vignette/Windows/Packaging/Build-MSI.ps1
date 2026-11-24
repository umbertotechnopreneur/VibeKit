[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../dist')
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot -Parent
$project = Join-Path $windows 'Vignette.csproj'
$shared = Join-Path $windows '../../Shared'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).SelectSingleNode('//Version').InnerText
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output ('.vignette-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$msi = Join-Path $output "Vignette-$version-windows-x64.msi"
if (Test-Path -LiteralPath $msi) { throw "Choose a new output directory: $msi" }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Vignette source publish failed.' }

$wxs = Join-Path $stage 'Vignette.wxs'
& python (Join-Path $shared 'create-widget-wix.py') vignette $version $payload $wxs
if ($LASTEXITCODE -ne 0) { throw 'Vignette WiX source generation failed.' }
& (Join-Path $shared 'Build-WindowsMsi.ps1') -WixPath $WixPath -Source $wxs -Output $msi -IntermediateDirectory (Join-Path $stage 'wix')
