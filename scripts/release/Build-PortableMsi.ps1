# Build a per-user MSI from a verified portable publish directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][guid]$UpgradeCode,
    [ValidateRange(1, 99999)][int]$MinimumWindowsBuild = 22000,
    [Parameter(Mandatory)][string]$WixPath
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/../..").Path
$payload = (Resolve-Path -LiteralPath $Source).Path
$target = [IO.Path]::GetFullPath($Output)
if ([IO.Path]::GetRelativePath($payload, $target) -notmatch '^\.\.([\\/]|$)' -and
    [IO.Path]::GetPathRoot($payload) -eq [IO.Path]::GetPathRoot($target)) {
    throw 'Place the installer output outside its payload.'
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$work = [IO.Directory]::CreateTempSubdirectory('vibekit-portable-msi-')
try {
    $sourceFile = Join-Path $work.FullName 'Package.wxs'
    $arch = $Architecture.ToLowerInvariant()
    python -B "$repo/scripts/ci/create-app-msi.py" portable $Version.ToString() $payload $sourceFile --architecture $arch --name $Name --executable $Executable --upgrade-code $UpgradeCode.ToString() --minimum-build $MinimumWindowsBuild
    if ($LASTEXITCODE) { throw "Failed to generate installer source for $Name." }
    & "$repo/Widgets/Shared/Build-WindowsMsi.ps1" -Architecture $arch -WixPath $WixPath -Source $sourceFile -Output $target -IntermediateDirectory $work.FullName
} finally {
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())
    if ($work.Parent.FullName -ne $tempRoot -or $work.Name -notlike 'vibekit-portable-msi-*') {
        throw "Unexpected staging directory: $($work.FullName)"
    }
    Remove-Item -LiteralPath $work.FullName -Recurse -Force
}
