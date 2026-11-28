[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/windows-x64'),
    [string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('local','public')][string]$Channel = $(if ($env:GITHUB_ACTIONS -eq 'true') { 'public' } else { 'local' })
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$version = Get-StudioVersion -ProjectRoot $root
if ($Channel -eq 'public' -and $Configuration -ne 'Release') { throw 'Public packages require Release configuration.' }
$artifactRoot = Join-Path $root 'artifacts'
$defaultOutput = Join-Path $artifactRoot "installers/$Channel/windows-x64"
if ($Configuration -eq 'Debug') { $defaultOutput = Join-Path $artifactRoot 'installers/local/debug/windows-x64' }
if (-not $PSBoundParameters.ContainsKey('PublishDirectory')) {
    if ($Configuration -eq 'Debug') { $PublishDirectory = Join-Path $artifactRoot 'debug/windows-x64' }
    elseif ($Channel -eq 'public') { $PublishDirectory = Join-Path $artifactRoot 'public/windows-x64' }
}
if (-not $OutputDirectory) { $OutputDirectory = $defaultOutput }
$source = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$managed = $output -eq [IO.Path]::GetFullPath($defaultOutput)
$name = "StudioControls-$version-windows-x64"
$msi = Join-Path $output "$name.msi"
if ($managed) { Assert-StudioArtifactPath -Path $output -ArtifactRoot $artifactRoot }
elseif (Test-Path -LiteralPath $msi) { throw "Choose a new output directory: $msi" }
Assert-PublishLayout -Directory $source
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $source 'StudioControls.Windows.exe')
if ($Channel -eq 'local') {
    $certificate = & (Join-Path $root '../../scripts/packaging/Get-VibeWareDevelopmentCertificate.ps1') -Publisher 'CN=Umberto Giacobbi'
    try {
        if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw 'The local MSI requires a publish signed with the shared VibeWare development certificate.'
        }
    } finally { $certificate.Dispose() }
} elseif ($signature.Status -ne 'NotSigned') { throw 'Use an unsigned public publish for the public MSI.' }
$stage = Join-Path $output ('.studio-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $wxs = Join-Path $stage "$name.wxs"
    $stagedMsi = Join-Path $stage "$name.msi"
    & python (Join-Path $PSScriptRoot 'create-wix.py') $source $wxs
    if ($LASTEXITCODE -ne 0) { throw 'Studio Controls WiX source generation failed.' }
    & (Join-Path $root '../Shared/Build-WindowsMsi.ps1') -WixPath $WixPath -Source $wxs -Output $stagedMsi -IntermediateDirectory (Join-Path $stage 'wix') -SkipSigning
    if ($Channel -eq 'local') {
        & (Join-Path $root '../../scripts/packaging/Sign-VibeWareDevelopmentFile.ps1') -Path $stagedMsi -Publisher 'CN=Umberto Giacobbi'
    }
    if ((Get-Item -LiteralPath $stagedMsi).Length -eq 0) { throw 'The staged MSI is empty.' }
    if ($managed) {
        $lockPath = Join-Path $output '.publish.lock'
        Assert-StudioArtifactPath -Path $lockPath -ArtifactRoot $artifactRoot
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            Assert-StudioArtifactPath -Path $msi -ArtifactRoot $artifactRoot
            [IO.File]::Move($stagedMsi, $msi, $true)
            Get-ChildItem -LiteralPath $output -File -Filter 'StudioControls-*-windows-x64.msi' |
                Where-Object { $_.Name -match '^StudioControls-[0-9]+(\.[0-9]+){2,3}-windows-x64\.msi$' -and
                    $_.FullName -ne $msi -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        } finally { $lock.Dispose() }
    } else {
        [IO.File]::Move($stagedMsi, $msi)
    }
    Write-Output "MSI: $msi"
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    if ((Split-Path $resolved -Parent) -ne $output -or
        (Split-Path $resolved -Leaf) -notmatch '^\.studio-package-[0-9a-f]{32}$' -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected package staging directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
