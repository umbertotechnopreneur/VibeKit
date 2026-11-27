Set-StrictMode -Version Latest

function Get-StudioVersion {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    [xml]$project = Get-Content -LiteralPath (Join-Path $ProjectRoot 'Windows/StudioControls.Windows.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'The Windows project must declare a package-safe semantic version.' }
    return $version
}

function Get-PublishFiles {
    param([Parameter(Mandatory)][string]$Directory)
    if ((Get-Item -LiteralPath $Directory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Publish directory must not be a symbolic link or reparse point.'
    }
    $items = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if ($items.Where({ $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Publish output must not contain symbolic links or reparse points.'
    }
    return @($items | Where-Object {
        -not $_.PSIsContainer -and $_.Extension -ne '.pdb' -and
        $_.Name -notin @('.publish-manifest.json', 'windows-verification.txt', 'ui-smoke.txt')
    } | Sort-Object FullName)
}

function Assert-PublishLayout {
    param([Parameter(Mandatory)][string]$Directory)
    foreach ($relative in @(
        'StudioControls.Windows.exe', 'StudioControls.Windows.dll',
        'StudioControls.Windows.deps.json', 'StudioControls.Windows.runtimeconfig.json',
        'StudioControls.Windows.pri', 'App.xbf',
        'Microsoft.WinUI.dll', 'Assets/CREDITS.txt', 'Assets/CRT-test.mp4',
        'Assets/OBS-logo.png', 'Assets/VibeWare-logo.png', 'Scenes/studio-windows.json',
        'README.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Licenses/dependencies.json'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $relative) -PathType Leaf)) {
            throw "Required package file is missing: $relative"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $Directory 'StudioControls.Windows.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($runtime.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or $runtime.runtimeOptions.framework.version -notlike '10.*') { throw 'Expected a framework-dependent .NET 10 publish.' }
    foreach ($file in @('coreclr.dll', 'hostfxr.dll')) { if (Test-Path -LiteralPath (Join-Path $Directory $file)) { throw "Bundled .NET runtime file: $file" } }
}

function Copy-DependencyNotices {
    param([Parameter(Mandatory)][string]$AssetsFile, [Parameter(Mandatory)][string]$Destination)
    $assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
    $packages = @{}
    foreach ($library in $assets.libraries.Values) {
        if ($library.type -eq 'package') { $packages[$library.path] = $true }
    }
    foreach ($framework in $assets.project.frameworks.Values) {
        if (-not $framework.ContainsKey('downloadDependencies')) { continue }
        foreach ($dependency in $framework.downloadDependencies) {
            $version = $dependency.version.Trim('[', ']', ' ').Split(',')[0].Trim()
            $packages[$dependency.name.ToLowerInvariant() + '/' + $version] = $true
        }
    }
    $notices = Join-Path $Destination 'Licenses'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    $inventory = @()
    foreach ($relative in ($packages.Keys | Sort-Object)) {
        if ($relative -notmatch '^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.+-]+$') { throw 'Invalid dependency package path.' }
        $packageDirectory = $null
        foreach ($folder in $assets.packageFolders.Keys) {
            $candidate = Join-Path $folder $relative
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        # SDK targeting packs may already be installed and need not be restored
        # into NuGet. They are build inputs; the published runtime pack is required below.
        if (-not $packageDirectory) { continue }
        $parts = $relative.Split('/')
        $packageNotices = Join-Path $notices $relative
        New-Item -ItemType Directory -Path $packageNotices -Force | Out-Null
        $copied = @()
        foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File) {
            if ($file.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_ ]PARTY[-_ ]NOTICES?)(\..*)?$') {
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageNotices $file.Name)
                $copied += $file.Name
            }
        }
        $inventory += [ordered]@{ id = $parts[0]; version = $parts[1]; notices = $copied; packageUrl = "https://www.nuget.org/packages/$($parts[0])/$($parts[1])" }
    }
    foreach ($required in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsappsdk.winui')) {
        if (-not @($inventory | Where-Object { $_.id -eq $required -and $_.notices.Count -gt 0 }).Count) {
            throw "Redistributed runtime license was not found in the restored packages: $required"
        }
    }
    ConvertTo-Json -InputObject $inventory -Depth 6 | Set-Content -LiteralPath (Join-Path $notices 'dependencies.json') -Encoding utf8NoBOM
}

function Write-PublishManifest {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version)
    Assert-PublishLayout -Directory $Directory
    $files = @(foreach ($file in Get-PublishFiles -Directory $Directory) {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    [ordered]@{ format = 1; application = 'StudioControls'; version = $Version; runtime = 'win-x64'; files = $files } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Directory '.publish-manifest.json') -Encoding utf8NoBOM
}

function Assert-StudioArtifactPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ArtifactRoot)
    $root = [IO.Path]::GetFullPath($ArtifactRoot)
    $relative = [IO.Path]::GetRelativePath($root, [IO.Path]::GetFullPath($Path))
    if ($relative -eq '.' -or [IO.Path]::IsPathRooted($relative) -or $relative -match '^\.\.(?:[\\/]|$)') {
        throw "Publish destination escapes the artifact root: $Path"
    }
    $current = Split-Path $root -Parent
    foreach ($part in @((Split-Path $root -Leaf)) + ($relative -split '[\\/]')) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Managed artifacts must not traverse a reparse point: $current"
        }
    }
}

function Move-StudioManagedPublish {
    param([Parameter(Mandatory)][string]$Staged, [Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$ArtifactRoot)
    $parent = Split-Path $Destination -Parent
    Assert-StudioArtifactPath -Path $Staged -ArtifactRoot $ArtifactRoot
    $lockName = ".{0}.publish.lock" -f (Split-Path $Destination -Leaf)
    $lockPath = Join-Path $parent $lockName
    Assert-StudioArtifactPath -Path $lockPath -ArtifactRoot $ArtifactRoot
    $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        $previous = Join-Path $parent ('.studio-previous-' + [Guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $Destination) {
            Assert-StudioArtifactPath -Path $Destination -ArtifactRoot $ArtifactRoot
            if (@(Get-ChildItem -LiteralPath $Destination -Force).Count) {
                [void](Get-PublishFiles -Directory $Destination)
                $manifest = Get-Content -LiteralPath (Join-Path $Destination '.publish-manifest.json') -Raw | ConvertFrom-Json -AsHashtable
                if ($manifest.application -ne 'StudioControls' -or $manifest.runtime -ne 'win-x64') {
                    throw "Refusing to replace an unrelated publish directory: $Destination"
                }
            }
            [IO.Directory]::Move($Destination, $previous)
        }
        try { [IO.Directory]::Move($Staged, $Destination) }
        catch {
            if (Test-Path -LiteralPath $previous) { [IO.Directory]::Move($previous, $Destination) }
            throw
        }
        if (Test-Path -LiteralPath $previous) {
            Assert-StudioArtifactPath -Path $previous -ArtifactRoot $ArtifactRoot
            Remove-Item -LiteralPath $previous -Recurse -Force
        }
    } finally { $lock.Dispose() }
}

Export-ModuleMember -Function Get-StudioVersion, Get-PublishFiles, Assert-PublishLayout, Copy-DependencyNotices, Write-PublishManifest, Assert-StudioArtifactPath, Move-StudioManagedPublish
