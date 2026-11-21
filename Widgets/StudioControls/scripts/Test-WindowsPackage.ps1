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
New-Item -ItemType Directory -Path $output -Force | Out-Null
function New-FixtureWix {
    $file = Join-Path $output ([Guid]::NewGuid().ToString('N') + '.wxs')
    & python (Join-Path $PSScriptRoot 'create-wix.py') $publish $file | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Fixture WiX source generation failed.' }
    $file
}
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
        'StudioControls.Windows.pri', 'App.xbf', 'Microsoft.WinUI.dll',
        'Assets/CREDITS.txt', 'Assets/CRT-test.mp4', 'Assets/OBS-logo.png', 'Assets/VibeWare-logo.png',
        'Scenes/studio-windows.json', 'README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
    )) {
        $path = Join-Path $publish $relative
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        Set-Content -LiteralPath $path -Value "Synthetic packaging fixture: $relative" -Encoding utf8NoBOM
    }
    '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' |
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
    $wxs = New-FixtureWix
    $source = Get-Content -LiteralPath $wxs -Raw
    [xml]$xml = $source
    Assert-True ($xml.Wix.Package.Scope -eq 'perUser') 'Installer does not have per-user scope.'
    Assert-True ($xml.Wix.Package.Version -eq $version) 'Installer version does not match the application.'
    Assert-True ($source.Contains('StudioControls.Windows.exe')) 'The application is missing from the installer.'
    Assert-True ($source.Contains('LICENSE.txt')) 'Runtime licence missing.'
    Assert-True ($source -notmatch '\.pdb|ui-smoke|windows-verification|\.publish-manifest|panel\.json|project\.json') 'A report, symbol file or setting leaked into the installer.'
    Set-Content -LiteralPath (Join-Path $publish 'panel.json') -Value '{"private":"must not ship"}'
    Assert-Rejected { New-FixtureWix } 'Unexpected settings were accepted.'
    Write-PublishManifest -Directory $publish -Version $version
    Assert-Rejected { New-FixtureWix } 'Settings present in a manifest were accepted.'
    Remove-Item -LiteralPath (Join-Path $publish 'panel.json')
    Write-PublishManifest -Directory $publish -Version $version
    Assert-True ((Get-Content -LiteralPath $wxs -Raw) -ceq $source) 'A rejected package changed existing WiX source.'
    Add-Content -LiteralPath (Join-Path $publish 'Assets/CREDITS.txt') -Value 'Changed after build.'
    Assert-Rejected { New-FixtureWix } 'Tampered assets were accepted.'
    Write-PublishManifest -Directory $publish -Version $version
    $manifestPath = Join-Path $publish '.publish-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $manifest.files[0].path = '../outside.txt'
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Assert-Rejected { New-FixtureWix } 'Path traversal in a manifest was accepted.'
    Write-Output 'PASS: synthetic MSI layout, runtime notices, report/PDB exclusions, unexpected settings, tamper detection and traversal rejection. No application or device was started.'
}
finally {
    Write-Output "Synthetic fixture retained: $temporary"
}
