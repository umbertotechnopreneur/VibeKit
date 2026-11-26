[CmdletBinding()]
param([string]$Source, [string]$Build, [string]$Configuration, [string]$Target)
$ErrorActionPreference = 'Stop'
$cmake = (Get-Command cmake.exe -ErrorAction SilentlyContinue).Source
if (!$cmake) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $cmake = & $vswhere -latest -products '*' -find 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' | Select-Object -First 1
}
if (!$cmake) { throw 'Install Visual Studio C++ CMake tools or add CMake to PATH.' }
$configureOptions = @()
if ($Target -eq 'RetroEngine') { $configureOptions += '-DBUILD_TESTING=OFF' }
& $cmake -S $Source -B $Build -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.26100.0' @configureOptions
if ($LASTEXITCODE) { throw 'Native engine configuration failed.' }
$buildOptions = @()
if ($Target) { $buildOptions = @('--target', $Target) }
& $cmake --build $Build --config $Configuration @buildOptions
if ($LASTEXITCODE) { throw 'Native engine build failed.' }
if ($Target) { return }
& $cmake --install $Build --config $Configuration --prefix (Join-Path $Build 'payload')
if ($LASTEXITCODE) { throw 'Native engine staging failed.' }
