[CmdletBinding()]
param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/windows-x64'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$version = Get-StudioVersion -ProjectRoot $root
$source = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "StudioControls-$version-windows-x64"
$manifestPath = Join-Path $source '.publish-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Use Build-Windows.ps1 to create a fresh, inventoried publish output first.' }
Assert-PublishLayout -Directory $source
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.format -ne 1 -or $manifest.application -ne 'StudioControls' -or $manifest.version -ne $version -or $manifest.runtime -ne 'win-x64') {
    throw 'Publish manifest does not match this Windows release.'
}
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
        $relative.Contains('\') -or $relative.Contains(':') -or $relative.Split('/').Where({ $_ -in @('', '.', '..') }).Count -or
        -not $known.Add($relative)) { throw 'Publish manifest contains an invalid or duplicate relative path.' }
    if ($relative -match '(?i)(^|/)(panel\.json|project\.json|local\.settings\.json|credentials[^/]*|\.env[^/]*|Logs|Backups)(/|$)' -or
        $relative -match '(?i)\.(log|pdb)$|\.runtimeconfig\.dev\.json$') { throw "User data or development output cannot be packaged: $relative" }
    $path = Join-Path $source $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Published file is missing: $relative" }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Published file changed after its clean build: $relative"
    }
}
foreach ($file in Get-PublishFiles -Directory $source) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
    if (-not $known.Contains($relative)) { throw "Unexpected file in publish output; it will not be silently included: $relative" }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$temporary = Join-Path $output ('.studio-package-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $temporary $name
$temporaryZip = Join-Path $temporary ($name + '.zip')
New-Item -ItemType Directory -Path $package -Force | Out-Null
try {
    foreach ($entry in $manifest.files) {
        $destination = Join-Path $package $entry.path
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $entry.path) -Destination $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "A published file changed while packaging: $($entry.path)"
        }
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $package 'package-manifest.json')
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $destinationZip = Join-Path $output ($name + '.zip')
    [IO.File]::Move($temporaryZip, $destinationZip, $false)
    $hash = (Get-FileHash -LiteralPath $destinationZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath ($destinationZip + '.sha256') -Encoding utf8NoBOM
    Write-Output "Windows ZIP: $destinationZip"
    Write-Output "SHA-256: $hash"
}
finally {
    if (Test-Path -LiteralPath $temporary) { Write-Output "Package staging retained: $temporary" }
}
