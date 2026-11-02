# Run explicitly when introducing dependencies. Builds never invoke this script.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$packageRoot = Join-Path $projectRoot '.imports\packages'
$manifestPath = Join-Path $projectRoot '.imports\verified-files.json'
$scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (-not (Test-Path -LiteralPath $scanner)) { throw 'Microsoft Defender command-line scanner is unavailable. Imports cannot proceed.' }
$defender = Get-MpComputerStatus
if (-not $defender.AntivirusEnabled -or -not $defender.AMServiceEnabled) { throw 'Microsoft Defender is not available for the required scan.' }
$old = @{}
if (Test-Path -LiteralPath $manifestPath) {
    foreach ($entry in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) { $old[$entry.path] = $entry.sha256 }
}
Push-Location $projectRoot
try {
    # Restore stages archives and extracted dependencies; no compilation is performed here.
    foreach ($project in @('Windows/OpenAIUsage.Windows.csproj','Backend/OpenAIUsage.Backend.csproj','Tests/OpenAIUsage.Tests.csproj')) {
        & dotnet restore $project --packages $packageRoot --source 'https://api.nuget.org/v3/index.json' -p:NuGetAudit=false --disable-parallel
        if ($LASTEXITCODE -ne 0) { throw "Dependency staging failed for $project." }
    }
    $records = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
        [pscustomobject]@{ path=[IO.Path]::GetRelativePath($packageRoot,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $changedPackages = @($records | Where-Object { $old[$_.path] -ne $_.sha256 } | ForEach-Object {
        $parts = $_.path -split '[/\\]'
        Join-Path (Join-Path $packageRoot $parts[0]) $parts[1]
    } | Sort-Object -Unique)
    foreach ($target in $changedPackages) {
        & $scanner -Scan -ScanType 3 -File $target -DisableRemediation
        if ($LASTEXITCODE -ne 0) { throw "Import scan failed or detected a threat: $target. Do not build or use this import." }
    }
    $records | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    if ($changedPackages.Count -gt 0) {
        $evidence = [pscustomobject]@{
            source='https://api.nuget.org/v3/index.json'
            scannedUtc=[DateTime]::UtcNow.ToString('o')
            command='MpCmdRun.exe -Scan -ScanType 3 -File <changed package directory> -DisableRemediation'
            result='All changed imports completed with exit 0; unchanged imports reuse verified-files.json evidence'
            scannedPackages=$changedPackages | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot,$_) }
            archives=@($records | Where-Object { $_.path.EndsWith('.nupkg') })
        }
        $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot 'dependency-scan.json') -Encoding utf8
    }
    Write-Output "Prepared imports. Scanned $($changedPackages.Count) new or changed package directories; reused unchanged evidence."
}
finally { Pop-Location }
