[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../dist')
)
$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot -Parent
$root = Split-Path $windows -Parent
$shared = Join-Path $root '../Shared'
$project = Join-Path $windows 'Studio/RetroStudio.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).SelectSingleNode('//Version').InnerText
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output ('.retro-terminal-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$build = Join-Path $stage 'native'
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$msi = Join-Path $output "RetroTerminal-$version-windows-$Architecture.msi"
$zip = Join-Path $output "RetroTerminal-$version-windows-$Architecture-portable.zip"
if ((Test-Path -LiteralPath $zip) -or (Test-Path -LiteralPath $msi)) { throw "Choose a new output directory: $msi" }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

$cmake = (Get-Command cmake.exe -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'MSVC x64 tools are required.' }
    $cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
}
& $cmake -S $windows -B $build -A $platform '-DCMAKE_SYSTEM_VERSION=10.0.26100.0' -DBUILD_TESTING=OFF
if ($LASTEXITCODE -ne 0) { throw 'Retro Terminal CMake configuration failed.' }
& $cmake --build $build --config Release --target RetroEngine
if ($LASTEXITCODE -ne 0) { throw 'Retro Terminal native source build failed.' }

& dotnet publish $project -c Release -r "win-$Architecture" --self-contained false "-p:Platform=$platform" -p:NuGetAudit=false -o $payload
if ($LASTEXITCODE -ne 0) { throw 'Retro Terminal source publish failed.' }
Copy-Item -LiteralPath (Join-Path $windows "Studio/bin/$platform/Release/net10.0-windows10.0.26100.0/win-$Architecture/RetroStudio.pri") -Destination (Join-Path $payload 'resources.pri')
Copy-Item -LiteralPath (Join-Path $build 'Release/RetroEngine.dll'),(Join-Path $root 'LICENSE') -Destination $payload
$shaders = New-Item -ItemType Directory -Path (Join-Path $payload 'Shaders') -Force
Copy-Item -LiteralPath (Join-Path $windows 'Shaders/Retro.hlsl') -Destination $shaders.FullName

& python (Join-Path $shared 'package-portable.py') --payload $payload --executable RetroStudio.exe --native RetroEngine.dll --architecture $Architecture --output $zip
if ($LASTEXITCODE -ne 0) { throw 'Portable ZIP creation failed.' }

$wxs = Join-Path $stage 'RetroTerminal.wxs'
& python (Join-Path $shared 'create-widget-wix.py') retro-terminal $version $payload $wxs --architecture $Architecture
if ($LASTEXITCODE -ne 0) { throw 'Retro Terminal WiX source generation failed.' }
& (Join-Path $shared 'Build-WindowsMsi.ps1') -WixPath $WixPath -Architecture $Architecture -Source $wxs -Output $msi -IntermediateDirectory (Join-Path $stage 'wix')
