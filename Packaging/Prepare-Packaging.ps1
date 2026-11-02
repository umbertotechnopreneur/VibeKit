# Explicit first-import preparation. No build or installer invokes antivirus scans.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $projectRoot '.imports\packages'
$manifestPath = Join-Path $projectRoot '.imports\verified-files.json'
$old = @{}
if (Test-Path -LiteralPath $manifestPath) {
    foreach ($entry in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) { $old[$entry.path] = $entry.sha256 }
}
$scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
$status = Get-MpComputerStatus
if (-not $status.AntivirusEnabled -or -not $status.AMServiceEnabled) { throw 'Defender must be available before importing packaging dependencies.' }
Push-Location $projectRoot
try {
    & dotnet restore 'Windows/OpenAIUsage.Windows.csproj' -r win-x64 --packages $packageRoot --source 'https://api.nuget.org/v3/index.json' -p:SelfContained=true -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows runtime staging failed.' }
    & dotnet restore 'Backend/OpenAIUsage.Backend.csproj' -r linux-x64 --packages $packageRoot --source 'https://api.nuget.org/v3/index.json' -p:SelfContained=true -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Linux runtime staging failed.' }
    # Preserve both RID graphs in the shared project's assets after the platform-specific restores.
    & dotnet restore 'Shared/OpenAIUsage.Core.csproj' --packages $packageRoot --source 'https://api.nuget.org/v3/index.json' -p:SelfContained=true -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Shared runtime staging failed.' }
    $records = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($packageRoot,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    $changed = @($records | Where-Object { $old[$_.path] -ne $_.sha256 } | ForEach-Object {
        $parts = $_.path -split '[/\\]'; Join-Path (Join-Path $packageRoot $parts[0]) $parts[1]
    } | Sort-Object -Unique)
    foreach ($directory in $changed) {
        & $scanner -Scan -ScanType 3 -File $directory -DisableRemediation
        if ($LASTEXITCODE -ne 0) { throw "Import scan did not finish cleanly: $directory" }
    }
    $records | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    if ($changed.Count) {
        [pscustomobject]@{
            source='https://api.nuget.org/v3/index.json'; scannedUtc=[DateTime]::UtcNow.ToString('o')
            command='MpCmdRun.exe -Scan -ScanType 3 -File <new runtime package directory> -DisableRemediation'
            result='Completed, no detections, exit 0 for all newly imported runtime directories'
            scannedPackages=@($changed | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot,$_) })
            archives=@($records | Where-Object { $_.path.EndsWith('.nupkg') -and -not $old.ContainsKey($_.path) })
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-scan.json') -Encoding utf8
    }
    $toolRoot = Join-Path $projectRoot '.imports\wix-5.0.2'
    $toolManifest = Join-Path $projectRoot '.imports\wix-verified-files.json'
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    if (-not (Test-Path -LiteralPath $toolManifest)) {
        foreach ($id in @('wix','wixtoolset.ui.wixext')) {
            $archive = Join-Path $toolRoot "$id.5.0.2.nupkg"
            $source = "https://api.nuget.org/v3-flatcontainer/$id/5.0.2/$id.5.0.2.nupkg"
            Invoke-WebRequest -Uri $source -OutFile $archive
            [IO.Compression.ZipFile]::ExtractToDirectory($archive,(Join-Path $toolRoot $id),$true)
        }
        & $scanner -Scan -ScanType 3 -File $toolRoot -DisableRemediation
        if ($LASTEXITCODE -ne 0) { throw 'WiX tool import scan did not finish cleanly.' }
        $toolRecords = @(Get-ChildItem -LiteralPath $toolRoot -Recurse -File | ForEach-Object {
            [pscustomobject]@{path=[IO.Path]::GetRelativePath($toolRoot,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        })
        $toolRecords | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $toolManifest -Encoding utf8
        [pscustomobject]@{
            sources=@('https://api.nuget.org/v3-flatcontainer/wix/5.0.2/wix.5.0.2.nupkg','https://api.nuget.org/v3-flatcontainer/wixtoolset.ui.wixext/5.0.2/wixtoolset.ui.wixext.5.0.2.nupkg')
            version='5.0.2'; scannedUtc=[DateTime]::UtcNow.ToString('o')
            command='MpCmdRun.exe -Scan -ScanType 3 -File .imports\wix-5.0.2 -DisableRemediation'
            result='Archives and extracted files: completed, no detections, exit 0'
            archives=@($toolRecords | Where-Object { $_.path.EndsWith('.nupkg') })
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'tool-scan.json') -Encoding utf8
    }
    Write-Output 'Packaging imports prepared. Application builds and packaging are separate steps.'
}
finally { Pop-Location }
