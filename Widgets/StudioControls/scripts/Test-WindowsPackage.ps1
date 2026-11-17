[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('studio-package-test-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $temporary 'publish'
$output = Join-Path $temporary 'release'
$nuget = Join-Path $temporary 'synthetic-nuget'
$version = Get-StudioVersion -ProjectRoot $root
$name = "StudioControls-$version-windows-x64"
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected $Message
}
try {
    # Plain text placeholders, not executable binaries. Nothing is ever launched.
    foreach ($relative in @(
        'StudioControls.Windows.exe', 'StudioControls.Windows.dll', 'StudioControls.Windows.deps.json',
        'StudioControls.Windows.pri', 'App.xbf', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.WinUI.dll',
        'Assets/CREDITS.txt', 'Assets/CRT-test.mp4', 'Assets/OBS-logo.png', 'Assets/VibeWare-logo.png',
        'Scenes/studio-windows.json', 'README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
    )) {
        $path = Join-Path $publish $relative
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        Set-Content -LiteralPath $path -Value "Synthetic packaging fixture: $relative" -Encoding utf8NoBOM
    }
    '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.14"}]}}' |
        Set-Content -LiteralPath (Join-Path $publish 'StudioControls.Windows.runtimeconfig.json') -Encoding utf8NoBOM
    foreach ($package in @('microsoft.windowsappsdk.winui/2.6.4', 'microsoft.netcore.app.runtime.win-x64/10.0.14')) {
        $path = Join-Path $nuget $package
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $path 'LICENSE.txt') -Value 'Synthetic licence text.' -Encoding utf8NoBOM
    }
    $assets = @{
        libraries = @{ 'microsoft.windowsappsdk.winui/2.6.4' = @{ type = 'package'; path = 'microsoft.windowsappsdk.winui/2.6.4' } }
        project = @{ frameworks = @{ 'net10.0-windows' = @{ downloadDependencies = @(@{ name = 'Microsoft.NETCore.App.Runtime.win-x64'; version = '[10.0.14, 10.0.14]' }) } } }
        packageFolders = @{ $nuget = @{} }
    }
    $assetsFile = Join-Path $temporary 'project.assets.json'
    $assets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $assetsFile -Encoding utf8NoBOM
    Copy-DependencyNotices -AssetsFile $assetsFile -Destination $publish
    $dependencyText = Get-Content -LiteralPath (Join-Path $publish 'Licenses/dependencies.json') -Raw
    Assert-True (-not $dependencyText.Contains($temporary)) 'Dependency notices leaked a local path.'
    Set-Content -LiteralPath (Join-Path $publish 'StudioControls.Windows.pdb') -Value 'Excluded symbols.'
    Write-PublishManifest -Directory $publish -Version $version
    # CI adds these reports after publishing. Neither belongs in the download.
    Set-Content -LiteralPath (Join-Path $publish 'windows-verification.txt') -Value 'PASS: synthetic check.'
    Set-Content -LiteralPath (Join-Path $publish 'ui-smoke.txt') -Value 'PASS: synthetic native startup.'
    & (Join-Path $PSScriptRoot 'Package-Windows.ps1') -PublishDirectory $publish -OutputDirectory $output | Out-Null
    $zip = Join-Path $output ($name + '.zip')
    Assert-True (Test-Path -LiteralPath $zip) 'Versioned ZIP was not created.'
    Assert-True (Test-Path -LiteralPath ($zip + '.sha256')) 'SHA-256 sidecar was not created.'
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ((Get-Content -LiteralPath ($zip + '.sha256') -Raw).StartsWith($hash)) 'SHA-256 sidecar does not match the ZIP.'
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        Assert-True ($entries -contains "$name/StudioControls.Windows.exe") 'The ZIP does not have its versioned application folder.'
        Assert-True ($entries -contains "$name/package-manifest.json") 'The ZIP does not include its relative-path manifest.'
        Assert-True ($entries -contains "$name/Licenses/microsoft.netcore.app.runtime.win-x64/10.0.14/LICENSE.txt") 'Runtime licence missing.'
        Assert-True (-not @($entries | Where-Object { $_ -match '\.pdb$|ui-smoke|windows-verification|\.publish-manifest|panel\.json|project\.json' }).Count) 'A report, symbol file or setting leaked into the ZIP.'
    }
    finally { $archive.Dispose() }
    Set-Content -LiteralPath (Join-Path $publish 'panel.json') -Value '{"private":"must not ship"}'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Package-Windows.ps1') -PublishDirectory $publish -OutputDirectory $output } 'Unexpected settings were accepted.'
    Write-PublishManifest -Directory $publish -Version $version
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Package-Windows.ps1') -PublishDirectory $publish -OutputDirectory $output } 'Settings present in a manifest were accepted.'
    Remove-Item -LiteralPath (Join-Path $publish 'panel.json')
    Write-PublishManifest -Directory $publish -Version $version
    Assert-True ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -eq $hash) 'A rejected package replaced the previous release.'
    Add-Content -LiteralPath (Join-Path $publish 'Assets/CREDITS.txt') -Value 'Changed after build.'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Package-Windows.ps1') -PublishDirectory $publish -OutputDirectory $output } 'Tampered assets were accepted.'
    Write-PublishManifest -Directory $publish -Version $version
    $manifestPath = Join-Path $publish '.publish-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $manifest.files[0].path = '../outside.txt'
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Package-Windows.ps1') -PublishDirectory $publish -OutputDirectory $output } 'Path traversal in a manifest was accepted.'
    Write-Output 'PASS: synthetic ZIP layout, runtime notices, SHA-256, report/PDB exclusions, unexpected settings, tamper detection and traversal rejection. No application or device was started.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
