[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$Verify,
    [switch]$Package,
    [string]$WixPath,
    [ValidateSet('local','public')][string]$Channel = $(if ($env:GITHUB_ACTIONS -eq 'true') { 'public' } else { 'local' })
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Windows-Package.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Windows/StudioControls.Windows.csproj'
$artifactRoot = Join-Path $root 'artifacts'
$relativeOutput = if ($Configuration -eq 'Debug') { 'debug/windows-x64' }
    elseif ($Channel -eq 'public') { 'public/windows-x64' }
    else { 'windows-x64' }
$managedOutput = Join-Path $artifactRoot $relativeOutput
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { $managedOutput }
$managed = $output -eq $managedOutput
if (-not $IsWindows) { throw 'The native WinUI build requires Windows 11 and its Windows SDK.' }
if ($Package -and -not $WixPath) { throw 'Supply -WixPath when building an MSI.' }
if ($Configuration -eq 'Debug' -and $Channel -eq 'public') { throw 'Publish public packages in Release configuration.' }
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw "Publish destination is not a directory: $output" }
    if (-not $managed -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
        throw 'Choose a new or empty custom OutputDirectory.'
    }
}
if ($managed) { Assert-StudioArtifactPath -Path $output -ArtifactRoot $artifactRoot }
$parent = Split-Path $output -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staged = Join-Path $parent ('.studio-build-' + [Guid]::NewGuid().ToString('N'))
try {
    & dotnet restore $project -r win-x64 -p:Platform=x64 --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Studio Controls locked dependency restore failed.' }
    & dotnet publish $project -c $Configuration -r win-x64 -p:Platform=x64 --self-contained false --no-restore -o $staged
    if ($LASTEXITCODE -ne 0) { throw 'Studio Controls Windows publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'Windows/README.txt') -Destination (Join-Path $staged 'README.txt')
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $staged $name)
    }
    Copy-DependencyNotices -AssetsFile (Join-Path $root 'Windows/obj/project.assets.json') -Destination $staged
    if ($Channel -eq 'local') {
        & (Join-Path $root '../../scripts/packaging/Sign-VibeWareDevelopmentFile.ps1') -Path (Join-Path $staged 'StudioControls.Windows.exe') -Publisher 'CN=Umberto Giacobbi'
    }
    if ($Verify) {
        $exe = Join-Path $staged 'StudioControls.Windows.exe'
        $process = Start-Process -FilePath $exe -ArgumentList '--verify' -WindowStyle Hidden -PassThru
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
    Write-PublishManifest -Directory $staged -Version (Get-StudioVersion -ProjectRoot $root)
    if ($Package) {
        $package = @{ WixPath = $WixPath; PublishDirectory = $staged; Channel = $Channel; Configuration = $Configuration }
        if (-not $managed) { $package.OutputDirectory = $parent }
        & (Join-Path $PSScriptRoot 'Package-Windows.ps1') @package
    }
    if ($managed) {
        Move-StudioManagedPublish -Staged $staged -Destination $output -ArtifactRoot $artifactRoot
    } else {
        if (Test-Path -LiteralPath $output) { [IO.Directory]::Delete($output, $false) }
        [IO.Directory]::Move($staged, $output)
    }
}
finally {
    if (Test-Path -LiteralPath $staged) { Write-Output "Build staging retained: $staged" }
}
Write-Output "Windows output: $output"
