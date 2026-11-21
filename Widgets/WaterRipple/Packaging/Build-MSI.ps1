[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../dist')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$shared = Join-Path $root '../Shared'
$project = Join-Path $root 'WaterRipple.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).SelectSingleNode('//Version').InnerText
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output ('.water-ripple-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$msi = Join-Path $output "WaterRipple-$version-windows-x64.msi"
if (Test-Path -LiteralPath $msi) { throw "Choose a new output directory: $msi" }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:NuGetAudit=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Water Ripple source publish failed.' }
$native = Join-Path $root 'bin/x64/Release/net10.0-windows10.0.26100.0/win-x64'
Copy-Item -LiteralPath (Join-Path $native 'RippleRenderer.dll') -Destination $payload
Copy-Item -LiteralPath (Join-Path $native 'WaterRipple.pri') -Destination (Join-Path $payload 'resources.pri')
$shaders = New-Item -ItemType Directory -Path (Join-Path $payload 'Shaders') -Force
foreach ($shader in @('RippleVS.cso', 'RipplePS.cso')) {
    Copy-Item -LiteralPath (Join-Path $native "Shaders/$shader") -Destination $shaders.FullName
}

$wxs = Join-Path $stage 'WaterRipple.wxs'
& python (Join-Path $shared 'create-widget-wix.py') water-ripple $version $payload $wxs
if ($LASTEXITCODE -ne 0) { throw 'Water Ripple WiX source generation failed.' }
& (Join-Path $shared 'Build-WindowsMsi.ps1') -WixPath $WixPath -Source $wxs -Output $msi -IntermediateDirectory (Join-Path $stage 'wix')
