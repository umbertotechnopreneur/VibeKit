param(
    [string]$Version = '1.0.1.0',
    [string]$PackageName = 'VibeWare.WaterRipple',
    [string]$Publisher = 'CN=VibeWare',
    [string]$CertificateThumbprint,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist')
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $project ("obj\msix-stage-" + $Version)
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$makeappx = Join-Path $sdk 'makeappx.exe'
$signtool = Join-Path $sdk 'signtool.exe'
foreach ($tool in @($makeappx, $signtool)) { if (-not (Test-Path -LiteralPath $tool)) { throw "Missing official Windows SDK tool: $tool" } }
if (Test-Path -LiteralPath $stage) { throw "Staging already exists. Clean this project-local directory before a new package build: $stage" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$package = Join-Path $output "WaterRipple_${Version}_x64.msix"
if (Test-Path -LiteralPath $package) { throw "The output already exists; choose a new version or clean it first: $package" }
# Stage and explicitly scan any new imports before running this source build.
# Restore is restricted to the pre-staged cache; builds never download or scan dependencies.
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
& dotnet publish (Join-Path $project 'WaterRipple.csproj') -c Release -r win-x64 --self-contained true -p:TargetLatestRuntimePatch=true -p:NuGetAudit=false -p:DebugType=None --source $cache -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Source publish failed.' }
$nativeOutput = Join-Path $project 'bin\Release\net10.0-windows10.0.26100.0\win-x64'
Copy-Item -LiteralPath (Join-Path $nativeOutput 'RippleRenderer.dll') -Destination $stage
New-Item -ItemType Directory -Path (Join-Path $stage 'Shaders') -Force | Out-Null
foreach ($shader in @('RippleVS.cso','RipplePS.cso')) { Copy-Item -LiteralPath (Join-Path $nativeOutput "Shaders\$shader") -Destination (Join-Path $stage 'Shaders') }
Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Image]::FromFile((Join-Path $project 'Assets\WaterRipple.png'))
try {
    foreach ($asset in @(@('Square44x44Logo.png',44), @('Square150x150Logo.png',150), @('StoreLogo.png',50))) {
        $size = [int]$asset[1]
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($icon, 0, 0, $size, $size)
            $bitmap.Save((Join-Path $stage ('Assets\' + $asset[0])), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $icon.Dispose() }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Package.Identity.Name = $PackageName
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
& $makeappx pack /d $stage /p $package /o | Select-Object -Last 6
if ($LASTEXITCODE -ne 0) { throw 'MSIX schema/content validation or packing failed.' }
if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne $Publisher) { throw 'Signing certificate must have a private key and match the manifest publisher.' }
    & $signtool sign /fd SHA256 /sha1 $CertificateThumbprint /s My $package
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
    & $signtool verify /pa $package
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signature verification failed.' }
}
Write-Output "MSIX package: $package"
Write-Output "Temporary staging to clean after handoff: $stage"
