param(
    [string]$Version = '1.0.0.0',
    [string]$CertificateThumbprint,
    [string]$Publisher = 'CN=VibeWare'
)
$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot -Parent
$project = Split-Path $windows -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 tools are required.' }
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$makeappx = Join-Path $sdk 'makeappx.exe'
$signtool = Join-Path $sdk 'signtool.exe'
foreach ($tool in @($cmake, $makeappx, $signtool)) { if (-not (Test-Path -LiteralPath $tool)) { throw "Missing build tool: $tool" } }
$build = Join-Path $windows "build-msix-$Version"
$stage = Join-Path $windows "msix-stage-$Version"
$out = Join-Path $project "dist\RetroTerminal_${Version}_x64.msix"
foreach ($path in @($build, $stage, $out)) { if (Test-Path -LiteralPath $path) { throw "Output already exists: $path" } }
& $cmake -S $windows -B $build -A x64 '-DCMAKE_SYSTEM_VERSION=10.0.26100.0' -DBUILD_TESTING=OFF
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'Source build failed.' }
New-Item -ItemType Directory -Path $stage,(Join-Path $stage 'Shaders'),(Join-Path $stage 'Assets'),(Split-Path $out -Parent) -Force | Out-Null
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
& dotnet publish (Join-Path $windows 'Studio\RetroStudio.csproj') -c Release -r win-x64 --self-contained true '-p:NuGetAudit=false' --source $cache -o $stage
if ($LASTEXITCODE -ne 0) { throw 'WinUI source publish failed.' }
Copy-Item -LiteralPath (Join-Path $windows 'Studio\bin\Release\net10.0-windows10.0.26100.0\win-x64\RetroStudio.pri') -Destination (Join-Path $stage 'resources.pri')
Copy-Item -LiteralPath (Join-Path $build 'Release\RetroEngine.dll') -Destination $stage
Copy-Item -LiteralPath (Join-Path $build 'Release\Shaders\Retro.hlsl') -Destination (Join-Path $stage 'Shaders')
Copy-Item -LiteralPath (Join-Path $project 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $project 'Linux\extension\retro-terminal@vibeware.local\VibeWare-LICENSE.txt') -Destination $stage
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Image]::FromFile((Join-Path $project 'Linux\extension\retro-terminal@vibeware.local\VibeWare.png'))
try {
    foreach ($asset in @(@('Square44x44Logo.png',44), @('Square150x150Logo.png',150), @('StoreLogo.png',50))) {
        $size = [int]$asset[1]; $bitmap = [Drawing.Bitmap]::new($size,$size); $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.Clear([Drawing.Color]::Transparent); $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $graphics.DrawImage($icon, 0, 0, $size, $size); $bitmap.Save((Join-Path $stage ('Assets\' + $asset[0])), [Drawing.Imaging.ImageFormat]::Png) }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $icon.Dispose() }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
& $makeappx pack /d $stage /p $out
if ($LASTEXITCODE -ne 0) { throw 'MSIX packing failed.' }
if ($CertificateThumbprint) {
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $cert.HasPrivateKey -or $cert.Subject -ne $Publisher) { throw 'Certificate must match publisher and have a private key.' }
    & $signtool sign /fd SHA256 /sha1 $CertificateThumbprint /s My $out
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
    & $signtool verify /pa $out
    if ($LASTEXITCODE -ne 0) { throw 'Signature verification failed.' }
}
Write-Output "MSIX: $out"
