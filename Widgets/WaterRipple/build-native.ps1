param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$IntermediateDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Tools\MSVC'
if (-not (Test-Path -LiteralPath $vsRoot)) { throw 'Install the Microsoft C++ x64 build tools or set the tool location in this script.' }
$msvc = Get-ChildItem -LiteralPath $vsRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion = '10.0.26100.0'
$compiler = Join-Path $msvc.FullName 'bin\Hostx64\x64\cl.exe'
$shaderCompiler = Join-Path $sdk "bin\$sdkVersion\x64\fxc.exe"
foreach ($tool in @($compiler, $shaderCompiler)) { if (-not (Test-Path -LiteralPath $tool)) { throw "Missing official build tool: $tool" } }
$outPath = [IO.Path]::GetFullPath($OutputDirectory)
$objPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $IntermediateDirectory))
New-Item -ItemType Directory -Path $outPath,$objPath,(Join-Path $outPath 'Shaders') -Force | Out-Null
$env:INCLUDE = @((Join-Path $msvc.FullName 'include'), (Join-Path $sdk "Include\$sdkVersion\ucrt"), (Join-Path $sdk "Include\$sdkVersion\shared"), (Join-Path $sdk "Include\$sdkVersion\um"), (Join-Path $sdk "Include\$sdkVersion\winrt"), (Join-Path $sdk "Include\$sdkVersion\cppwinrt")) -join ';'
$env:LIB = @((Join-Path $msvc.FullName 'lib\x64'), (Join-Path $sdk "Lib\$sdkVersion\ucrt\x64"), (Join-Path $sdk "Lib\$sdkVersion\um\x64")) -join ';'
$shaderSource = Join-Path $projectRoot 'Shaders\Ripple.hlsl'
& $shaderCompiler /nologo /T vs_5_0 /E VSMain /O3 /Fo (Join-Path $outPath 'Shaders\RippleVS.cso') $shaderSource
if ($LASTEXITCODE -ne 0) { throw 'Vertex shader compilation failed.' }
& $shaderCompiler /nologo /T ps_5_0 /E PSMain /O3 /Fo (Join-Path $outPath 'Shaders\RipplePS.cso') $shaderSource
if ($LASTEXITCODE -ne 0) { throw 'Pixel shader compilation failed.' }
$optimization = if ($Configuration -eq 'Debug') { '/Od' } else { '/O2' }
& $compiler /nologo /std:c++20 /EHsc /W4 /MD /Zi $optimization /LD /DUNICODE /D_UNICODE /DNOMINMAX (Join-Path $projectRoot 'Native\RippleRenderer.cpp') "/Fo$(Join-Path $objPath 'RippleRenderer.obj')" "/Fd$(Join-Path $objPath 'compiler.pdb')" /link /MACHINE:X64 "/OUT:$(Join-Path $outPath 'RippleRenderer.dll')" "/IMPLIB:$(Join-Path $objPath 'RippleRenderer.lib')" "/PDB:$(Join-Path $outPath 'RippleRenderer.pdb')" d3d11.lib dxgi.lib user32.lib windowsapp.lib
if ($LASTEXITCODE -ne 0) { throw 'Native renderer compilation failed.' }
