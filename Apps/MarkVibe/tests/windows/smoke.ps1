# VBWR B
# Project: MarkVibe | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; native WinUI Release smoke checks with synthetic local files
# Human guidance: verify the native shell after retiring the browser edition
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E
<# .SYNOPSIS
Verify native WinUI commands using UI Automation and an isolated synthetic profile.
.DESCRIPTION
Requires an explicitly selected source-built executable. Does not call AI, change
real documents, capture the desktop or inspect credentials. GUI acceptance remains
with the owner. Runs locally; never installs or enables Windows Sandbox.
#>
[CmdletBinding()]
param([string]$Executable = $env:MARKVIBE_WINDOWS_EXECUTABLE)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows UI checks require Windows.' }
if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw 'Set MARKVIBE_WINDOWS_EXECUTABLE to the current source-built Release x64 executable.'
}
$null = Get-Command winapp -ErrorAction Stop
$applicationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $applicationRoot ('artifacts/windows-fixture-' + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path $fixture 'workspace'
$profile = Join-Path $fixture 'profile'
[IO.Directory]::CreateDirectory($workspace) | Out-Null
$source = "# Native migration fixture`n`n## Outline fixture heading`n`nUnicode: città, perché.`n`n[Next](next.md)`n"
[IO.File]::WriteAllText((Join-Path $workspace 'README.md'), $source)
[IO.File]::WriteAllText((Join-Path $workspace 'next.md'), "# Next`n`nA second synthetic page.`n")
# Explicit language and disabled remembering keep selectors deterministic and data isolated.
[IO.Directory]::CreateDirectory($profile) | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'desktop-settings.json'), '{"preferences":{"language":"en","rememberLastDocument":false}}')
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$application = $null
$window = $null
<# .SYNOPSIS
Run a native UI Automation command against the fixture window or its launching process.
#>
function Invoke-FixtureUi([string[]]$Arguments) {
    if ($window) {
        & winapp ui @Arguments -w $window
    } else {
        & winapp ui @Arguments -a $application.Id
    }
    if ($LASTEXITCODE -ne 0) { throw "Native UI check failed: $($Arguments -join ' ')." }
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('--data-dir', $profile, '--open', (Join-Path $workspace 'README.md'))) {
        $start.ArgumentList.Add($argument)
    }
    $application = [Diagnostics.Process]::Start($start)
    Invoke-FixtureUi @('wait-for', 'Native migration fixture', '--type', 'Text', '--timeout', '30000')
    $windows = Invoke-FixtureUi @('list-windows', '--json') | ConvertFrom-Json
    $main = $windows | Where-Object title -eq 'Native migration fixture — MarkVibe' | Select-Object -First 1
    if (-not $main.hwnd) { throw 'The synthetic MarkVibe main window was not found.' }
    $window = [string]$main.hwnd
    Write-Host 'PASS native startup opens the synthetic Markdown workspace'
    Invoke-FixtureUi @('wait-for', 'README.md', '--root', 'NavigationTree', '--type', 'Text', '--timeout', '10000')
    Invoke-FixtureUi @('invoke', 'OutlineButton', '--action', 'select')
    Invoke-FixtureUi @('wait-for', 'Outline fixture heading', '--root', 'NavigationTree', '--type', 'Text', '--timeout', '10000')
    Invoke-FixtureUi @('invoke', 'PagesButton', '--action', 'select')
    Invoke-FixtureUi @('wait-for', 'next.md', '--root', 'NavigationTree', '--type', 'Text', '--timeout', '10000')
    Write-Host 'PASS Pages and Outline display document captions'
    Invoke-FixtureUi @('invoke', 'SourceButton', '--action', 'select')
    Invoke-FixtureUi @('wait-for', 'SourceButton', '--property', 'IsSelected', '--value', 'true', '--timeout', '10000')
    Invoke-FixtureUi @('wait-for', 'Native migration fixture', '--type', 'Text', '--gone', '--timeout', '15000')
    Invoke-FixtureUi @('invoke', 'SplitButton', '--action', 'select')
    Invoke-FixtureUi @('wait-for', 'SplitGrip', '--timeout', '10000')
    Invoke-FixtureUi @('wait-for', 'Native migration fixture', '--type', 'Text', '--timeout', '15000')
    Invoke-FixtureUi @('invoke', 'MapButton', '--action', 'select')
    Invoke-FixtureUi @('wait-for', 'Find a document', '--timeout', '15000')
    Invoke-FixtureUi @('wait-for', 'Document map', '--class-name', 'WindowsGraphCanvas', '--timeout', '15000')
    Invoke-FixtureUi @('invoke', 'ReadButton', '--action', 'select')
    Invoke-FixtureUi @('wait-for', 'Native migration fixture', '--type', 'Text', '--timeout', '10000')
    Invoke-FixtureUi @('wait-for', 'SplitGrip', '--gone', '--timeout', '10000')
    Invoke-FixtureUi @('wait-for', 'Document map', '--class-name', 'WindowsGraphCanvas', '--gone', '--timeout', '10000')
    Write-Host 'PASS native Read, Source, Split and Map display their selected surfaces'
    Invoke-FixtureUi @('invoke', 'About MarkVibe', '--type', 'Hyperlink', '--action', 'invoke')
    Invoke-FixtureUi @('wait-for', 'About MarkVibe', '--type', 'Text', '--timeout', '10000')
    Invoke-FixtureUi @('invoke', 'Close', '--type', 'Button', '--class-name', 'Button', '--action', 'invoke')
    Invoke-FixtureUi @('wait-for', 'Close', '--type', 'Button', '--class-name', 'Button', '--gone', '--timeout', '10000')
    Write-Host 'PASS native About dialog opens and closes'
    if ([IO.File]::ReadAllText((Join-Path $workspace 'README.md')) -ne $source) {
        throw 'Native smoke checks changed the original Markdown.'
    }
    Write-Host 'PASS original synthetic Markdown remains unchanged'
} finally {
    if ($application -and -not $application.HasExited) {
        $application.Kill($true)
        $application.WaitForExit()
    }
    & winapp ui yield | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if (-not $resolvedFixture.StartsWith($applicationRoot + [IO.Path]::DirectorySeparatorChar)) {
        throw 'Fixture cleanup escaped the application root.'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
