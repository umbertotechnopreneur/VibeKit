#requires -Version 5.1

$script:repoOnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Find-RepoRadarPrerequisite {
    param([string]$Name, [string[]]$Arguments, [string]$OutputPattern, [string[]]$KnownPaths = @())

    $commands = Get-Command -Name $Name -CommandType Application -All -ErrorAction SilentlyContinue
    foreach ($path in (@($commands | Select-Object -ExpandProperty Source) + $KnownPaths | Select-Object -Unique)) {
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $output = @(& $path @Arguments 2>$null)
        if ($LASTEXITCODE -eq 0 -and ($output -match $OutputPattern)) { return $path }
    }
}

function Get-RepoRadarPrerequisites {
    $dotnetPaths = @()
    $gitPaths = @()
    if ($script:repoOnWindows) {
        $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
        if ($programFiles) {
            $dotnetPaths += Join-Path $programFiles 'dotnet\dotnet.exe'
            $gitPaths += Join-Path $programFiles 'Git\cmd\git.exe'
        }
        if ($env:LOCALAPPDATA) { $gitPaths += Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe' }
    }

    @(
        [pscustomobject]@{
            Label = 'Microsoft .NET 10 Runtime'
            Id = 'Microsoft.DotNet.Runtime.10'
            Path = Find-RepoRadarPrerequisite dotnet @('--list-runtimes') '^Microsoft\.NETCore\.App 10\.0\.\d+ \[' $dotnetPaths
        }
        [pscustomobject]@{
            Label = 'Git'
            Id = 'Git.Git'
            Path = Find-RepoRadarPrerequisite git @('--version') '^git version ' $gitPaths
        }
    )
}

function Update-RepoRadarSessionPath {
    $paths = @($env:PATH -split ';')
    foreach ($scope in @('Machine', 'User')) {
        $registeredPath = [Environment]::GetEnvironmentVariable('Path', $scope)
        if ($registeredPath) { $paths += [Environment]::ExpandEnvironmentVariables($registeredPath) -split ';' }
    }
    $env:PATH = ($paths | Where-Object { $_ } | Select-Object -Unique) -join ';'
}

function Invoke-RepoRadarPreflight {
    param([switch]$Check)

    # As in Armata, use an initial inventory and probe each prerequisite again after setup.
    $requirements = @(Get-RepoRadarPrerequisites)
    Write-Host "Repo Radar Preflight on $([Environment]::MachineName)"
    foreach ($requirement in $requirements) {
        $state = if ($requirement.Path) { 'Ready' } else { 'Missing' }
        Write-Host "  [${state}] $($requirement.Label)"
    }

    $missing = @($requirements | Where-Object { -not $_.Path })
    if (-not $missing.Count) { return }
    if ($Check) { throw 'Preflight found missing prerequisites.' }
    $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    if (-not $script:repoOnWindows -or $architecture -ne 'AMD64') {
        throw 'Automatic installation is available on Windows x64. Install the missing prerequisites for your platform.'
    }

    $winget = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'WinGet is unavailable. Install or update Microsoft App Installer, then run Preflight again. https://aka.ms/getwinget'
    }

    foreach ($requirement in $missing) {
        Write-Host "Installing $($requirement.Label) with WinGet..."
        & $winget.Source install --id $requirement.Id --exact --source winget --architecture x64 --no-upgrade --silent
        $installerExit = $LASTEXITCODE
        # Armata stops on a restart-required outcome. WinGet can wrap the native installer code.
        if ($installerExit -in @(1641, 3010, -1978334967, -1978334966, -1978334965)) {
            throw "$($requirement.Label) requires a Windows restart. Restart manually, then rerun Preflight."
        }
        if ($installerExit -ne 0) {
            throw "WinGet returned $installerExit for $($requirement.Label). Review the installer output above, then rerun Preflight."
        }
        Update-RepoRadarSessionPath
        $verified = Get-RepoRadarPrerequisites | Where-Object Id -eq $requirement.Id
        if (-not $verified.Path) { throw "$($requirement.Label) is still unavailable after installation. Open a new terminal and rerun Preflight." }
        Write-Host "  [Ready] $($verified.Label)" -ForegroundColor Green
    }
}

Export-ModuleMember -Function Get-RepoRadarPrerequisites, Invoke-RepoRadarPreflight
