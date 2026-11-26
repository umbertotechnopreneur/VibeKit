param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$IntermediateDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$msvcPath = $env:VCToolsInstallDir
if (-not $msvcPath -or -not (Test-Path -LiteralPath (Join-Path $msvcPath 'bin\Hostx64\x64\cl.exe'))) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio or Build Tools with the C++ x64 toolchain.' }
    $installation = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'No Visual Studio installation with the C++ x64 build tools was found.' }
    $vsTools = Join-Path $installation 'VC\Tools\MSVC'
    $msvcPath = (Get-ChildItem -LiteralPath $vsTools -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe') } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
}
if (-not $msvcPath) { throw 'The selected Visual Studio installation has no usable x64 compiler.' }

$sdk = $env:WindowsSdkDir
if (-not $sdk -or -not (Test-Path -LiteralPath $sdk)) { $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10' }
$minimumSdkVersion = [version]'10.0.26100.0'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory |
    Where-Object {
        $version = $null
        [version]::TryParse($_.Name, [ref]$version) -and $version -ge $minimumSdkVersion -and
        (Test-Path -LiteralPath (Join-Path $sdk "bin\$($_.Name)\x64\fxc.exe")) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'cppwinrt')) -and
        (Test-Path -LiteralPath (Join-Path $sdk "Lib\$($_.Name)\um\x64")) -and
        (Test-Path -LiteralPath (Join-Path $sdk "Lib\$($_.Name)\ucrt\x64"))
    } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $sdkVersion) { throw "Install Windows SDK $minimumSdkVersion or newer, including C++/WinRT and fxc." }
$compiler = Join-Path $msvcPath 'bin\Hostx64\x64\cl.exe'
$shaderCompiler = Join-Path $sdk "bin\$sdkVersion\x64\fxc.exe"
foreach ($tool in @($compiler, $shaderCompiler)) { if (-not (Test-Path -LiteralPath $tool)) { throw "Missing official build tool: $tool" } }
$outPath = [IO.Path]::GetFullPath($OutputDirectory)
$intermediatePath = if ([IO.Path]::IsPathRooted($IntermediateDirectory)) {
    $IntermediateDirectory
} else {
    Join-Path $projectRoot $IntermediateDirectory
}
$objPath = [IO.Path]::GetFullPath($intermediatePath)
New-Item -ItemType Directory -Path $outPath,$objPath,(Join-Path $outPath 'Shaders') -Force | Out-Null
$env:INCLUDE = @((Join-Path $msvcPath 'include'), (Join-Path $sdk "Include\$sdkVersion\ucrt"), (Join-Path $sdk "Include\$sdkVersion\shared"), (Join-Path $sdk "Include\$sdkVersion\um"), (Join-Path $sdk "Include\$sdkVersion\winrt"), (Join-Path $sdk "Include\$sdkVersion\cppwinrt")) -join ';'
$env:LIB = @((Join-Path $msvcPath 'lib\x64'), (Join-Path $sdk "Lib\$sdkVersion\ucrt\x64"), (Join-Path $sdk "Lib\$sdkVersion\um\x64")) -join ';'
Write-Host "Building Ripple with MSVC $msvcPath and Windows SDK $sdkVersion"
$shaderSource = Join-Path $projectRoot 'Shaders\Ripple.hlsl'
& $shaderCompiler /nologo /T vs_5_0 /E VSMain /O3 /Fo (Join-Path $outPath 'Shaders\RippleVS.cso') $shaderSource
if ($LASTEXITCODE -ne 0) { throw 'Vertex shader compilation failed.' }
& $shaderCompiler /nologo /T ps_5_0 /E PSMain /O3 /Fo (Join-Path $outPath 'Shaders\RipplePS.cso') $shaderSource
if ($LASTEXITCODE -ne 0) { throw 'Pixel shader compilation failed.' }
$optimization = if ($Configuration -eq 'Debug') { '/Od' } else { '/O2' }
& $compiler /nologo /std:c++20 /EHsc /W4 /MD /Zi $optimization /LD /DUNICODE /D_UNICODE /DNOMINMAX (Join-Path $projectRoot 'Native\RippleRenderer.cpp') "/Fo$(Join-Path $objPath 'RippleRenderer.obj')" "/Fd$(Join-Path $objPath 'compiler.pdb')" /link /MACHINE:X64 "/OUT:$(Join-Path $outPath 'RippleRenderer.dll')" "/IMPLIB:$(Join-Path $objPath 'RippleRenderer.lib')" "/PDB:$(Join-Path $outPath 'RippleRenderer.pdb')" d3d11.lib dxgi.lib user32.lib windowsapp.lib
if ($LASTEXITCODE -ne 0) { throw 'Native renderer compilation failed.' }
