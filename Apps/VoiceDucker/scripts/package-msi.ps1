# VoiceDucker - Copyright (c) 2026 Umberto Giacobbi. MIT license.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = [IO.Path]::GetFullPath((Join-Path $root '../..'))
$payload = Join-Path $root 'artifacts/app/win-x64'
$executable = Join-Path $payload 'VoiceDucker.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Build the x64 Release app before packaging the MSI.'
}

$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'VoiceDucker-windows-x64.msi'
if (Test-Path -LiteralPath $output) { throw "Installer already exists: $output" }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $payload
& python (Join-Path $PSScriptRoot 'collect-notices.py') --mode nuget --target win-x64 --output (Join-Path $payload 'third-party-notices')
if ($LASTEXITCODE -ne 0) { throw 'Dependency notice collection failed.' }

$thumbprint = [Environment]::GetEnvironmentVariable('VIBEWARE_DEVELOPMENT_CERTIFICATE_THUMBPRINT', 'User')
if ($thumbprint) {
    $signer = Join-Path $env:LOCALAPPDATA 'VibeWare/DevelopmentSigning/Sign-VibeWareDevelopmentFile.ps1'
    & $signer -Path $executable
}

[xml]$project = Get-Content -LiteralPath (Join-Path $root 'VoiceDucker.csproj') -Raw
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$work = New-Item -Path (Split-Path -Parent $output) -Name "msi-work-$([guid]::NewGuid())" -ItemType Directory -Force
try {
    $source = Join-Path $work.FullName 'VoiceDucker.wxs'
    & python (Join-Path $repo 'scripts/ci/create-app-msi.py') voiceducker $version $payload $source
    if ($LASTEXITCODE -ne 0) { throw 'WiX source generation failed.' }
    & (Join-Path $repo 'Widgets/Shared/Build-WindowsMsi.ps1') -WixPath $WixPath `
        -Source $source -Output $output -IntermediateDirectory (Join-Path $work.FullName 'build')
    if ($thumbprint -and (Get-AuthenticodeSignature -LiteralPath $output).SignerCertificate.Thumbprint -ne $thumbprint) {
        & $signer -Path $output
    }
} finally {
    if ($work.Parent.FullName -eq (Split-Path -Parent $output)) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
