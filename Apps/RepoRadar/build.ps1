#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('Portable','Debian','All')][string]$Channel = 'Portable',
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
$repoProject = Join-Path $PSScriptRoot 'src\RepoRadar.csproj'
$repoArtifacts = Join-Path $PSScriptRoot 'artifacts'
$repoDistributions = Join-Path $repoArtifacts 'dist'
$repoPython = (Get-Command python -ErrorAction Stop).Source
$repoPackager = Join-Path $PSScriptRoot 'scripts\package.py'
if ($Channel -in @('Portable','All')) {
    $repoOut = Join-Path $repoArtifacts "publish\repo-radar-win-$Architecture"
    & dotnet publish $repoProject -c $Configuration -r "win-$Architecture" --self-contained false -o $repoOut
    if ($LASTEXITCODE) { throw 'Windows source build failed.' }
    & $repoPython $repoPackager windows $repoOut $repoDistributions --architecture $Architecture
    if ($LASTEXITCODE) { throw 'Portable ZIP creation failed.' }
    @{ relativeDll = "artifacts\publish\repo-radar-win-$Architecture\repo-radar.dll" } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $repoArtifacts 'current-build.json') -Encoding UTF8
}
if ($Channel -in @('Debian','All')) {
    $repoOut = Join-Path $repoArtifacts 'publish\repo-radar-linux-x64'
    & dotnet publish $repoProject -c $Configuration -r linux-x64 --self-contained false -p:UseAppHost=false -o $repoOut
    if ($LASTEXITCODE) { throw 'Linux x64 source build failed.' }
    & $repoPython $repoPackager debian $repoOut $repoDistributions
    if ($LASTEXITCODE) { throw 'Debian package creation failed.' }
}
if ($Channel -eq 'All') {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'packaging\windows\Build-Msi.ps1') -Configuration $Configuration -Architecture $Architecture
    if ($LASTEXITCODE) { throw 'Windows MSI creation failed.' }
}
