[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$Verify,
    [switch]$Package,
    [string]$WixPath,
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Windows/StudioControls.Windows.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/windows-$Architecture" }
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $IsWindows) { throw 'The native WinUI build requires Windows 11 and its Windows SDK.' }
if ($Package -and -not $WixPath) { throw 'Supply -WixPath when building an MSI.' }
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or @(Get-ChildItem -LiteralPath $output -Force).Count) {
        throw 'Choose a new or empty OutputDirectory. Build output is never mixed with older files or user data.'
    }
}
$parent = Split-Path $output -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staged = Join-Path $parent ('.studio-build-' + [Guid]::NewGuid().ToString('N'))
try {
    & dotnet restore $project "-p:Platform=$platform" --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Studio Controls locked dependency restore failed.' }
    & dotnet publish $project -c $Configuration -r "win-$Architecture" "-p:Platform=$platform" --self-contained false --no-restore -o $staged
    if ($LASTEXITCODE -ne 0) { throw 'Studio Controls Windows publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'Windows/README.txt') -Destination (Join-Path $staged 'README.txt')
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $staged $name)
    }
    Copy-DependencyNotices -AssetsFile (Join-Path $root 'Windows/obj/project.assets.json') -Destination $staged -Architecture $Architecture
    if ($Verify) {
        $exe = Join-Path $staged 'StudioControls.Windows.exe'
        $process = Start-Process -FilePath $exe -ArgumentList '--verify' -PassThru
        if (-not $process.WaitForExit(120000)) {
            Stop-Process -Id $process.Id
            throw 'Synthetic verification timed out. Only the verification process was stopped.'
        }
        $report = Join-Path $staged 'windows-verification.txt'
        if ($process.ExitCode -ne 0) {
            if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
            throw "Synthetic verification failed: $($process.ExitCode)"
        }
        if (-not (Test-Path -LiteralPath $report) -or (Get-Content -LiteralPath $report -Raw) -notmatch '^PASS:') {
            throw 'Synthetic verification did not produce a passing report.'
        }
        Get-Content -LiteralPath $report
    }
    Write-PublishManifest -Directory $staged -Version (Get-StudioVersion -ProjectRoot $root) -Architecture $Architecture
    if (Test-Path -LiteralPath $output) { [IO.Directory]::Delete($output, $false) }
    [IO.Directory]::Move($staged, $output)
}
finally {
    if (Test-Path -LiteralPath $staged) { Write-Output "Build staging retained: $staged" }
}
Write-Output "Windows output: $output"
if ($Package) {
    & (Join-Path $PSScriptRoot 'Package-Windows.ps1') -WixPath $WixPath -PublishDirectory $output -OutputDirectory $parent -Architecture $Architecture
}
