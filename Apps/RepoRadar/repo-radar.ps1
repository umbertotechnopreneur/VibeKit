#requires -Version 5.1
# Repo Radar launcher. Application arguments other than the Preflight commands are forwarded unchanged.
$ErrorActionPreference = 'Stop'
$repoForwardedArguments = $args
try {
    Import-Module (Join-Path $PSScriptRoot 'preflight.psm1')
    if ($args.Count -eq 1 -and $args[0] -in @('--preflight', '--preflight-check')) {
        Invoke-RepoRadarPreflight -Check:($args[0] -eq '--preflight-check')
        exit 0
    }

    $repoInteractive = -not [Console]::IsInputRedirected -and -not [Console]::IsOutputRedirected
    $repoWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    $repoRequirements = @(Get-RepoRadarPrerequisites)
    $repoMissing = @($repoRequirements | Where-Object { -not $_.Path })
    if ($repoWindows -and $repoInteractive -and $args.Count -eq 0 -and $repoMissing.Count) {
        Write-Host ('Missing prerequisites: ' + ($repoMissing.Label -join ', '))
        Write-Host '1. Preflight'
        Write-Host '2. Exit'
        if ((Read-Host 'Select 1 to install the missing prerequisites') -ne '1') { exit 0 }
        Invoke-RepoRadarPreflight
        $repoRequirements = @(Get-RepoRadarPrerequisites)
    }

    $repoDotnet = ($repoRequirements | Where-Object Id -eq 'Microsoft.DotNet.Runtime.10').Path
    if (-not $repoDotnet) {
        throw 'Repo Radar requires the Microsoft .NET 10 Runtime for x64. On Windows run .\repo-radar.cmd --preflight. https://dotnet.microsoft.com/en-us/download/dotnet/10.0'
    }
    $repoHome = $PSScriptRoot
    $repoDll = Join-Path $repoHome 'repo-radar.dll'
    if (-not (Test-Path -LiteralPath $repoDll)) {
        $repoBuildPointer = Join-Path $repoHome 'artifacts\current-build.json'
        if (-not (Test-Path -LiteralPath $repoBuildPointer)) { throw 'No source build found. Run .\build.ps1 first (requires the .NET 10 SDK).' }
        $repoBuild = Get-Content -LiteralPath $repoBuildPointer -Raw | ConvertFrom-Json
        $repoDll = [System.IO.Path]::GetFullPath((Join-Path $repoHome $repoBuild.relativeDll))
        $repoExpectedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoHome 'artifacts')) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $repoDll.StartsWith($repoExpectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Build pointer is outside artifacts.' }
        if ([System.IO.Path]::GetFileName($repoDll) -ne 'repo-radar.dll') { throw 'Run .\build.ps1 to create a Repo Radar source build.' }
    }
    if (-not (Test-Path -LiteralPath $repoDll -PathType Leaf)) { throw 'Build output is missing. Run .\build.ps1.' }
    $repoPreviousHome = $env:REPO_RADAR_HOME
    $repoPreviousPath = $env:PATH
    try {
        $env:REPO_RADAR_HOME = $repoHome
        do {
            $repoGit = ($repoRequirements | Where-Object Id -eq 'Git.Git').Path
            if ($repoWindows -and $repoGit) {
                $gitDirectory = Split-Path -Parent $repoGit
                $paths = @($gitDirectory) + @($env:PATH -split ';')
                $env:PATH = ($paths | Select-Object -Unique) -join ';'
            }
            & $repoDotnet $repoDll @repoForwardedArguments
            $repoExitCode = $LASTEXITCODE
            # The application releases its settings lock before returning this menu selection.
            if ($repoExitCode -eq 78) {
                Invoke-RepoRadarPreflight
                $repoRequirements = @(Get-RepoRadarPrerequisites)
                $repoDotnet = ($repoRequirements | Where-Object Id -eq 'Microsoft.DotNet.Runtime.10').Path
                $null = Read-Host 'Press Enter to return to Repo Radar'
            }
        } while ($repoExitCode -eq 78)
    }
    finally {
        $env:REPO_RADAR_HOME = $repoPreviousHome
        $env:PATH = $repoPreviousPath
    }
    exit $repoExitCode
}
catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
