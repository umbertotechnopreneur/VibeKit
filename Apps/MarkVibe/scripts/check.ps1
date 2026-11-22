# VBWR B
# Project: MarkVibe | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; explicit local verification profiles; 2026-10-16
# Human guidance: simple checks, isolated fixtures, no API calls or packaging
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E
<# .SYNOPSIS
Run the selected local checks without packaging, installation, AI requests or retained reports.
#>
[CmdletBinding()]
param([ValidateSet('Rapida', 'InterfacciaWindows', 'Completa')][string]$Profile = 'Rapida')
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$selectedNode = @(Get-Command node -CommandType Application -ErrorAction Stop)[0].Source
if ([version]((& $selectedNode --version).TrimStart('v')) -lt [version]'22.12.0') {
    throw 'Select an existing Node >=22.12 on PATH.'
}
<# .SYNOPSIS
Execute a check and stop immediately when its native exit code reports failure.
#>
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}
Push-Location -LiteralPath $repositoryRoot
try {
    & (Join-Path $PSScriptRoot 'prepare-client.ps1') -DependenciesOnly
    if ($Profile -ne 'InterfacciaWindows') {
        foreach ($projectName in @('MarkVibe.Core.Checks', 'MarkVibe.Native.Checks')) {
            $project = "tests/$projectName/$projectName.csproj"
            Invoke-Checked 'dotnet' @('restore', $project, '--locked-mode')
            Invoke-Checked 'dotnet' @('run', '--project', $project, '-c', 'Release', '--no-restore')
        }
        Invoke-Checked $selectedNode @('--test', 'tests/client/path.test.mjs', 'tests/client/rendering.test.mjs', 'tests/client/workspaces.test.mjs')
    }
    if ($Profile -ne 'Rapida') {
        if (-not $IsWindows) { throw 'Native UI checks require Windows.' }
        # The owner chooses the current source-built executable; no stale Debug build is launched.
        & (Join-Path $repositoryRoot 'tests/windows/smoke.ps1')
    }
} finally {
    Pop-Location
}
