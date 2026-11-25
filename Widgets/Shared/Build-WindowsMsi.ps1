[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$IntermediateDirectory
)
$ErrorActionPreference = 'Stop'
$compiler = (Resolve-Path -LiteralPath $WixPath).Path
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [IO.Path]::GetFullPath($Output)
$buildDirectory = [IO.Path]::GetFullPath($IntermediateDirectory)
if (Test-Path -LiteralPath $outputPath) { throw "Installer already exists: $outputPath" }
$extensions = 'WixToolset.Netfx.wixext/5.0.2', 'WixToolset.UI.wixext/5.0.2'

<#
.SYNOPSIS
Run the selected pinned WiX tool and surface tool failures to the caller.
#>
function Invoke-MsiCompiler([string[]]$Arguments) {
    if ([IO.Path]::GetExtension($compiler) -eq '.dll') {
        & dotnet $compiler @Arguments
    } else {
        & $compiler @Arguments
    }
    if ($LASTEXITCODE -ne 0) { throw 'Windows Installer compilation or validation failed.' }
}

# Keep the WiX extension cache inside this package's existing staging directory.
[IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
Push-Location -LiteralPath $buildDirectory
try {
    foreach ($extension in $extensions) {
        Invoke-MsiCompiler @('extension', 'add', $extension)
    }
    $arguments = @('build', $sourcePath, '-arch', 'x64', '-o', $outputPath, '-pdbtype', 'none', '-intermediatefolder', $buildDirectory)
    foreach ($extension in $extensions) {
        $arguments += '-ext', $extension
    }
    Invoke-MsiCompiler $arguments
} finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw 'Windows Installer compilation did not produce a package.'
}
& (Join-Path $PSScriptRoot '../../scripts/packaging/Sign-VibeWareDevelopmentFile.ps1') -Path $outputPath -WhenConfigured
Write-Output "MSI: $outputPath"
