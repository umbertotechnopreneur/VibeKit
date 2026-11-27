[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
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
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$msi = Join-Path $output "WaterRipple-$version-windows-$Architecture.msi"
$zip = Join-Path $output "WaterRipple-$version-windows-$Architecture-portable.zip"
if ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $msi)) { throw "Choose a new output directory: $msi" }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

& dotnet publish $project -c Release -r "win-$Architecture" --self-contained false "-p:Platform=$platform" -p:NuGetAudit=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Water Ripple source publish failed.' }
$native = Join-Path $root "bin/$platform/Release/net10.0-windows10.0.26100.0/win-$Architecture"
Copy-Item -LiteralPath (Join-Path $native 'RippleRenderer.dll') -Destination $payload
Copy-Item -LiteralPath (Join-Path $native 'WaterRipple.pri') -Destination (Join-Path $payload 'resources.pri')
$shaders = New-Item -ItemType Directory -Path (Join-Path $payload 'Shaders') -Force
foreach ($shader in @('RippleVS.cso', 'RipplePS.cso')) {
    Copy-Item -LiteralPath (Join-Path $native "Shaders/$shader") -Destination $shaders.FullName
}

& python (Join-Path $shared 'package-portable.py') --payload $payload --executable WaterRipple.exe --native RippleRenderer.dll --architecture $Architecture --output $zip
if ($LASTEXITCODE -ne 0) { throw 'Portable ZIP creation failed.' }

$wxs = Join-Path $stage 'WaterRipple.wxs'
& python (Join-Path $shared 'create-widget-wix.py') water-ripple $version $payload $wxs --architecture $Architecture
if ($LASTEXITCODE -ne 0) { throw 'Water Ripple WiX source generation failed.' }
& (Join-Path $shared 'Build-WindowsMsi.ps1') -WixPath $WixPath -Architecture $Architecture -Source $wxs -Output $msi -IntermediateDirectory (Join-Path $stage 'wix')
