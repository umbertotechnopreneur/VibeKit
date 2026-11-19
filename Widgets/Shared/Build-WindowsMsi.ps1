[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WixPath,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$IntermediateDirectory
)
$ErrorActionPreference = 'Stop'
$compiler = (Resolve-Path -LiteralPath $WixPath).Path
if (Test-Path -LiteralPath $Output) { throw "Installer already exists: $Output" }
$arguments = @('build', $Source, '-arch', 'x64', '-o', $Output, '-pdbtype', 'none', '-intermediatefolder', $IntermediateDirectory)
if ([IO.Path]::GetExtension($compiler) -eq '.dll') {
    & dotnet $compiler @arguments
} else {
    & $compiler @arguments
}
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Output -PathType Leaf)) {
    throw 'Windows Installer compilation or validation failed.'
}
Write-Output "MSI: $Output"
