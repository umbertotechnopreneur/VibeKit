[CmdletBinding()]
param([Parameter(Mandatory)][psobject]$Imports)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$packages = Join-Path $root '.imports\packages'
$tools = [IO.Path]::GetFullPath($Imports.ToolRoot)
$toolBase = Join-Path $root '.imports\wix-5.0.2'
if (-not $Imports.Prepared -or $Imports.ProjectRoot -ne $root -or $Imports.PackageRoot -ne $packages -or ($tools -ne $toolBase -and -not $tools.StartsWith($toolBase + '-', [StringComparison]::OrdinalIgnoreCase))) { throw 'Prepare Windows packaging imports in this session first.' }
foreach ($group in @(@{Root=$packages; Entries=@($Imports.Packages)}, @{Root=$tools; Entries=@($Imports.Tools)})) {
    if (-not $group.Entries.Count -or @(Get-ChildItem -LiteralPath $group.Root -Recurse -File).Count -ne $group.Entries.Count) { throw 'Import staging changed since preparation.' }
    foreach ($entry in $group.Entries) {
        $file = [IO.Path]::GetFullPath((Join-Path $group.Root $entry.path))
        if (-not $file.StartsWith($group.Root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.sha256) { throw "Unverified import: $file" }
    }
}
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$run = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$stage = Join-Path $root "artifacts\windows-distribution-$run"
$payload = Join-Path $stage "OpenAIUsage-$version-windows-x64"
$output = Join-Path $root 'artifacts\installers'
$msi = Join-Path $output "OpenAIUsage-$version-windows-x64-$run.msi"
New-Item -ItemType Directory -Path $payload,$output -Force | Out-Null
Push-Location $root
try {
    & dotnet restore Windows/OpenAIUsage.Windows.csproj -r win-x64 --packages $packages --source $packages -p:NuGetAudit=false -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed.' }
    & dotnet build Windows/OpenAIUsage.Windows.csproj -t:Rebuild -c Release -r win-x64 --self-contained true --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Windows source build failed.' }
    & dotnet publish Windows/OpenAIUsage.Windows.csproj -c Release -r win-x64 --self-contained true --no-build --no-restore -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    foreach ($file in @('OpenAIUsage.Windows.exe','OpenAIUsage.Windows.pri','App.xbf','hostfxr.dll','coreclr.dll')) { if (-not (Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Missing $file" } }
    if ((Get-Item (Join-Path $payload 'OpenAIUsage.Windows.dll')).VersionInfo.FileVersion -ne "$version.0") { throw 'Published version mismatch.' }
    $notices = Join-Path $payload 'licenses'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Get-ChildItem -LiteralPath $packages -Directory | ForEach-Object { $id = $_.Name; Get-ChildItem -LiteralPath $_.FullName -Directory | ForEach-Object { $v = $_.Name; Get-ChildItem -LiteralPath $_.FullName -File | Where-Object Name -Match '(?i)^(license|third[-_]?party|notice)' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices "$id-$v-$($_.Name)") } } }
    $wxs = Join-Path $stage 'OpenAIUsage.wxs'
    & python Packaging/package.py wix $payload $wxs
    if ($LASTEXITCODE -ne 0) { throw 'WiX source generation failed.' }
    & dotnet (Join-Path $tools 'wix\tools\net6.0\any\wix.dll') build $wxs -arch x64 -o $msi -pdbtype none -intermediatefolder (Join-Path $stage 'wix-intermediate')
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    & (Join-Path $PSScriptRoot 'Verify-WindowsRequirement.ps1') -PackagePath $msi
    Write-Output "MSI: $msi"
} finally { Pop-Location }
