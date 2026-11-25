# VBWR B
# Project: MarkVibe | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; repeatable Win2D timing with isolated Markdown fixtures
# Human guidance: measure large folders, zoom, pan, memory and window presentation
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E
<#
.SYNOPSIS
Measure a source-built MarkVibe executable with synthetic folders in an interactive Windows test session.
.DESCRIPTION
WinApp drives the fixture window. The app's opt-in probes measure indexing and Win2D draw
callbacks. UI-observed times include automation latency. Process memory excludes WebView2
children. Use -IndexOnly to collect indexing and process memory without sending input.
Output goes to the pipeline. The optional screenshots are the only retained files.
.PARAMETER IncludeSamples
Include the original ordered Win2D probe batches on each result, preserving the stage
and the start-to-start intervals. Zero intervals mark the first draw of a gesture.
#>
[CmdletBinding()]
param(
    [string]$Executable = $env:MARKVIBE_WINDOWS_EXECUTABLE,
    [int[]]$DocumentCounts = @(128, 512, 1024),
    [switch]$IncludeBudgetCase,
    [switch]$IndexOnly,
    [switch]$Capture,
    [switch]$IncludeSamples
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Run the graph benchmark in an interactive Windows test session.' }
if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw 'Pass -Executable with the path to a source-built MarkVibe.Windows.exe.'
}
$exe = (Get-Item -LiteralPath $Executable).FullName
if ([IO.Path]::GetFileName($exe) -ne 'MarkVibe.Windows.exe') { throw 'Select MarkVibe.Windows.exe.' }
if (-not $DocumentCounts.Count -or @($DocumentCounts | Where-Object { $_ -lt 32 -or $_ -gt 1024 }).Count) {
    throw 'Choose document counts from 32 to 1,024.'
}
if ($IndexOnly -and $Capture) { throw 'Captures require the full interactive benchmark.' }
$winapp = if (-not $IndexOnly) { (Get-Command winapp -CommandType Application -ErrorAction Stop).Source }
if (-not $IndexOnly -and -not ('MarkVibeBenchWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class MarkVibeBenchWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, ref Input input, int size);

    private const uint Wheel = 0x0800;
    private const uint PreservePlacement = 0x13; // NOSIZE | NOMOVE | NOACTIVATE.

    /// <summary>Resolves WinUI child hit surfaces to their physical top-level window.</summary>
    private static IntPtr RootAt(int x, int y) =>
        GetAncestor(WindowFromPhysicalPoint(new Point { X = x, Y = y }), 2);

    /// <summary>Clicks only noninteractive caption chrome verified immediately before the input.</summary>
    private static bool TryCaption(IntPtr window, int x, int y)
    {
        if (RootAt(x, y) != window) return false;
        var packed = new IntPtr((y << 16) | (x & 65535));
        if (SendMessageTimeout(window, 0x84, IntPtr.Zero, packed, 2, 100, out var hit) == IntPtr.Zero ||
            hit.ToInt32() != 2) return false; // WM_NCHITTEST must return HTCAPTION.
        if (!SetPhysicalCursorPos(x, y) || RootAt(x, y) != window ||
            !GetPhysicalCursorPos(out Point actual) || actual.X != x || actual.Y != y) return false;
        Send(0x0002);
        Send(0x0004);
        Thread.Sleep(100);
        return GetForegroundWindow() == window;
    }

    /// <summary>Keeps the owned fixture exposed between gestures and activates it in physical screen coordinates.</summary>
    public static void Activate(IntPtr window, int expectedProcess)
    {
        GetWindowThreadProcessId(window, out int owner);
        if (owner != expectedProcess)
        {
            throw new InvalidOperationException("The window to activate must belong to the fixture.");
        }
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            if (IsIconic(window)) ShowWindow(window, 9);
            ShowWindow(window, 3);
            if (!SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, PreservePlacement))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SetForegroundWindow(window);
            Thread.Sleep(250);
            if (GetForegroundWindow() == window) return;
            if (!GetWindowRect(window, out Rect rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int center = (rect.Left + rect.Right) / 2;
            foreach (int dy in new[] { 16, 24, 32, 10 })
                foreach (int dx in new[] { 0, -90, 90 })
                    if (TryCaption(window, center + dx, rect.Top + dy)) return;
            throw new InvalidOperationException("The owned fixture could not obtain foreground.");
        }
        finally
        {
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    /// <summary>Restores ordinary z-order only while the recorded HWND belongs to this fixture.</summary>
    public static void Release(IntPtr window, int expectedProcess)
    {
        GetWindowThreadProcessId(window, out int owner);
        if (owner == expectedProcess) SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, PreservePlacement);
    }

    /// <summary>Distinguishes legacy iconic coordinates from loss of foreground without logging window titles.</summary>
    public static string Describe(IntPtr window)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int foreground);
        string name = "none";
        if (foreground != 0)
        {
            try
            {
                using (var process = Process.GetProcessById(foreground)) { name = process.ProcessName; }
            }
            catch (ArgumentException) { name = "closed"; }
        }
        return $"Fixture minimized={IsIconic(window)}; foreground process={name}.";
    }

    /// <summary>Measures sustained wheel handling without command startup time between detents.</summary>
    public static void ScrollBurst(int x, int y, int expectedProcess, int direction, int detents)
    {
        Position(x, y, expectedProcess);
        for (int index = 0; index < detents; index++)
        {
            RequireTarget(x, y, expectedProcess);
            Send(Wheel, direction * 120);
            Thread.Sleep(36);
        }
    }

    /// <summary>Checks physical hit testing without moving the pointer or changing foreground.</summary>
    public static bool ContainsPoint(int x, int y, int expectedProcess)
    {
        GetWindowThreadProcessId(RootAt(x, y), out int hit);
        return hit == expectedProcess;
    }

    /// <summary>Moves to a physical point only when its window and foreground belong to the fixture.</summary>
    private static void Position(int x, int y, int expectedProcess)
    {
        RequireTarget(x, y, expectedProcess, requireCursor: false);
        if (!SetPhysicalCursorPos(x, y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        RequireTarget(x, y, expectedProcess);
    }

    /// <summary>Rejects another foreground window, a foreign hit target or an unexpected cursor position.</summary>
    private static void RequireTarget(int x, int y, int expectedProcess, bool requireCursor = true)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int foreground);
        if (foreground != expectedProcess)
        {
            throw new InvalidOperationException("The fixture lost foreground before an input event.");
        }
        if (!ContainsPoint(x, y, expectedProcess))
        {
            throw new InvalidOperationException($"Physical point ({x}, {y}) is outside the fixture.");
        }
        if (requireCursor && (!GetPhysicalCursorPos(out Point current) || current.X != x || current.Y != y))
        {
            throw new InvalidOperationException("The cursor is not at the validated physical point.");
        }
    }

    /// <summary>Sends exactly one checked Windows mouse input event.</summary>
    private static void Send(uint flags, int data = 0)
    {
        var input = new Input { Mouse = new MouseInput { Flags = flags, Data = unchecked((uint)data) } };
        if (SendInput(1, ref input, Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
'@
}

<#
.SYNOPSIS
Create a root README and strongly linked Markdown pages in bounded directory groups.
#>
function New-GraphFixture([string]$Workspace, [int]$Count, [int]$PageBytes) {
    [IO.Directory]::CreateDirectory($Workspace) | Out-Null
    $paths = @('README.md') + @(1..($Count - 1) | ForEach-Object {
        'group-{0:D2}/page-{1:D4}.md' -f [int][math]::Floor($_ / 32), $_
    })
    for ($index = 0; $index -lt $Count; $index++) {
        $file = Join-Path $Workspace $paths[$index]
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        $prefix = if ($index -eq 0) { '' } else { '../' }
        $links = for ($offset = 1; $offset -le 12; $offset++) {
            $target = $paths[($index + $offset) % $Count]
            "[Related page]($prefix$target)"
        }
        $title = 'Synthetic graph page {0:D4}' -f $index
        $source = "# $title" + [char]10 + [char]10 + ($links -join [char]10) + [char]10 + [char]10
        [IO.File]::WriteAllText($file, $source.PadRight($PageBytes, [char]'x'))
    }
}

<#
.SYNOPSIS
Record the answered first-launch offer in this synthetic profile. Installed MSIX and
MSI targets identify their own registration; no shell associations are changed.
#>
function Initialize-GraphAssociationOffer([string]$Profile, [string]$ExecutablePath) {
    $install = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($ExecutablePath))
    $manifestPath = Join-Path $install.FullName 'AppxManifest.xml'
    $settingsUri = $null
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $parts = $install.Name.Split('_')
        $entry = @($manifest.Package.Applications.Application | Where-Object {
            $_.Executable -eq [IO.Path]::GetFileName($ExecutablePath)
        })
        # Windows installs to PackageFullName. Its first and last fields form PackageFamilyName.
        if ($parts.Count -eq 5 -and $parts[0] -eq $manifest.Package.Identity.Name -and $entry.Count -eq 1) {
            $applicationId = $parts[0] + '_' + $parts[4] + '!' + $entry[0].Id
            $settingsUri = 'ms-settings:defaultapps?registeredAUMID=' + [Uri]::EscapeDataString($applicationId)
        }
    }
    if (!$settingsUri) {
        $registrations = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\RegisteredApplications')
        try {
            if ($registrations) {
                foreach ($name in $registrations.GetValueNames() | Where-Object { $_ -like 'MarkVibe.*' }) {
                    $capabilities = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrations.GetValue($name))
                    try {
                        if ($capabilities -and $capabilities.GetValue('ApplicationIcon') -eq ('"' + $ExecutablePath + '",0')) {
                            $settingsUri = 'ms-settings:defaultapps?registeredAppUser=' + [Uri]::EscapeDataString($name)
                            break
                        }
                    } finally { if ($capabilities) { $capabilities.Dispose() } }
                }
            }
        } finally { if ($registrations) { $registrations.Dispose() } }
    }
    if ($settingsUri) {
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($settingsUri)))
        [IO.File]::WriteAllBytes((Join-Path $Profile "markdown-association-$hash"), [byte[]]@(1))
    }
}

<#
.SYNOPSIS
Run one bounded WinApp command against the benchmark process and return its JSON output.
#>
function Invoke-GraphUi([int]$TargetPid, [string[]]$UiArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($winapp)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $target = if ($script:graphWindow) { @('-w', $script:graphWindow) } else { @('-a', "$TargetPid") }
    foreach ($argument in (@('ui') + $UiArguments + $target + @('--json'))) {
        $start.ArgumentList.Add($argument)
    }
    $command = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $command.StandardOutput.ReadToEndAsync()
        $stderr = $command.StandardError.ReadToEndAsync()
        if (-not $command.WaitForExit(45000)) {
            $command.Kill($true)
            throw "WinApp timed out during $($UiArguments[0])."
        }
        if ($command.ExitCode -ne 0) {
            throw "WinApp $($UiArguments[0]) failed. $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())"
        }
        $output = $stdout.GetAwaiter().GetResult()
        if ($output.Trim()) { ConvertFrom-Json -InputObject $output }
    } finally {
        $command.Dispose()
    }
}

<# .SYNOPSIS
Find the canvas after activation and reject stale minimized-window coordinates.
#>
function Get-GraphCanvas([int]$TargetPid) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            $result = Invoke-GraphUi $TargetPid @('search', 'Document map', '--class-name', 'WindowsGraphCanvas')
        } catch {
            if ($attempt -eq 2 -or $_.Exception.Message -notmatch 'stale|ElementNotAvailable|UIA_E_ELEMENTNOTAVAILABLE|0x80040201') { throw }
            Start-Sleep -Milliseconds 150
            continue
        }
        $matches = @($result.matches | Where-Object { -not $_.isOffscreen -and $_.width -ge 480 -and $_.height -ge 400 })
        if ($matches.Count -eq 1) {
            $surface = $matches[0]
            $x = [int]($surface.x + $surface.width / 2)
            $y = [int]($surface.y + $surface.height / 2)
            if ([MarkVibeBenchWindow]::ContainsPoint($x, $y, $TargetPid)) { return $surface }
        }
        Start-Sleep -Milliseconds 150
    }
    throw 'The map canvas did not provide a live physical rectangle in the fixture.'
}

<#
.SYNOPSIS
Extract a named JSON probe event from the fixture's existing local logs.
#>
function Get-GraphProbe([string]$Profile, [string]$EventName) {
    $logPath = Join-Path $Profile 'logs'
    if (-not (Test-Path -LiteralPath $logPath -PathType Container)) { return }
    $pattern = ' \[INF\] ' + [regex]::Escape($EventName) + ' (?<data>\{.*\})$'
    foreach ($log in Get-ChildItem -LiteralPath $logPath -Filter 'VibeWare-*.log' -File) {
        foreach ($line in Get-Content -LiteralPath $log.FullName) {
            if ($line -match $pattern) { ConvertFrom-Json -InputObject $Matches.data }
        }
    }
}

<#
.SYNOPSIS
Wait for indexing to finish or stop within the byte budget, then return its measured status.
#>
function Wait-GraphIndex([string]$Profile) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 45) {
        $event = Get-GraphProbe $Profile 'workspace.performance' | Select-Object -Last 1
        if ($null -ne $event) { return $event }
        Start-Sleep -Milliseconds 200
    }
    throw 'The indexing probe did not finish within 45 seconds. Verify the benchmark executable and fixture.'
}

<#
.SYNOPSIS
Return the nearest-rank 95th percentile, leaving undersampled metrics null.
#>
function Get-GraphP95([double[]]$Values) {
    if ($Values.Count -lt 2) { return $null }
    $ordered = @($Values | Sort-Object)
    [math]::Round($ordered[[int][math]::Ceiling($ordered.Count * 0.95) - 1], 2)
}

<#
.SYNOPSIS
Summarize actual draws recorded during a gesture, including when the stage has no samples.
#>
function Measure-GraphGesture($Batches, [string]$Stage) {
    $records = @($Batches | Where-Object stage -eq $Stage)
    $draws = @($records | ForEach-Object { $_.drawMs })

    $intervals = @($records | ForEach-Object { $_.intervalMs } | Where-Object { $_ -gt 0 })
    $allocated = ($records | Measure-Object -Property allocatedBytes -Sum).Sum
    [pscustomobject]@{
        DrawCount = $draws.Count
        DrawP95Ms = Get-GraphP95 $draws
        DrawMaxMs = if ($draws.Count) { [math]::Round(($draws | Measure-Object -Maximum).Maximum, 2) }
        CallbackStartIntervalCount = $intervals.Count
        CallbackStartIntervalP95Ms = Get-GraphP95 $intervals
        DrawAllocationBytes = [long]$allocated
    }
}

<#
.SYNOPSIS
Drive the map of one isolated folder, summarize its probes and close only the fixture process.
#>
function Invoke-GraphCase([int]$Count, [int]$PageBytes) {
    $case = Join-Path $fixtureRoot "$Count-$PageBytes"
    $workspace = Join-Path $case 'workspace'
    $profile = Join-Path $case 'profile'
    New-GraphFixture $workspace $Count $PageBytes
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    Initialize-GraphAssociationOffer $profile $exe
    [IO.File]::WriteAllText((Join-Path $profile 'desktop-settings.json'),
        '{"preferences":{"language":"en","rememberLastDocument":false}}')

    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.Environment['MARKVIBE_MAP_PERFORMANCE'] = '1'
    foreach ($argument in @('--data-dir', $profile, '--open', (Join-Path $workspace 'README.md'))) {
        $start.ArgumentList.Add($argument)
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $app = [Diagnostics.Process]::Start($start)
    $script:graphWindow = $null
    $graphHandle = [IntPtr]::Zero
    try {
        $pageReady = $mapCommandMs = $null
        if (-not $IndexOnly) {
            Invoke-GraphUi $app.Id @('wait-for', 'Synthetic graph page 0000', '--type', 'Text', '--timeout', '30000') | Out-Null
            $pageReady = $clock.Elapsed.TotalMilliseconds
            $windows = Invoke-GraphUi $app.Id @('list-windows')
            $main = $windows | Where-Object title -eq 'Synthetic graph page 0000 — MarkVibe' | Select-Object -First 1
            if (-not $main.hwnd) { throw 'The synthetic MarkVibe main window was not found.' }
            $script:graphWindow = [string]$main.hwnd
            $graphHandle = if ($script:graphWindow.StartsWith('0x')) {
                [IntPtr][Convert]::ToInt64($script:graphWindow.Substring(2), 16)
            } else {
                [IntPtr][long]$script:graphWindow
            }
            Invoke-GraphUi $app.Id @('wait-for', 'MapButton', '--property', 'IsEnabled', '--value', 'true', '--timeout', '20000') | Out-Null
            $mapStarted = $clock.Elapsed.TotalMilliseconds
            Invoke-GraphUi $app.Id @('invoke', 'MapButton', '--action', 'select') | Out-Null
            Invoke-GraphUi $app.Id @('wait-for', 'MapButton', '--property', 'IsSelected', '--value', 'true', '--timeout', '10000') | Out-Null
            Invoke-GraphUi $app.Id @('wait-for', 'Document map', '--class-name', 'WindowsGraphCanvas', '--timeout', '20000') | Out-Null
            $mapReady = $clock.Elapsed.TotalMilliseconds
            $mapCommandMs = $mapReady - $mapStarted
        }
        $index = Wait-GraphIndex $profile
        $expectedIndexed = [math]::Min($Count, [math]::Floor(8MB / $PageBytes))
        if ($index.documents -ne $Count -or $index.indexed -ne $expectedIndexed -or
            $index.complete -ne ($expectedIndexed -eq $Count)) {
            throw 'The indexed workspace does not match the expected fixture.'
        }
        $app.Refresh()
        $readyPrivate = $app.PrivateMemorySize64
        $gestureError = $null
        if (-not $IndexOnly) {
            try {
                [MarkVibeBenchWindow]::Activate($graphHandle, $app.Id)
                Start-Sleep -Milliseconds 350
                $fitMatches = Invoke-GraphUi $app.Id @('search', 'Fit', '--type', 'Button')
                $fitSelector = if ($fitMatches.matches.automationId -contains 'Fit') { 'Fit' } else { 'Fit document map' }
                Invoke-GraphUi $app.Id @('invoke', $fitSelector, '--action', 'invoke') | Out-Null
                $canvas = Get-GraphCanvas $app.Id
                Start-Sleep -Milliseconds 500
                if ($Capture) {
                    Invoke-GraphUi $app.Id @('screenshot', '-o', (Join-Path $screenshotRoot "map-$Count-$PageBytes-overview.png")) | Out-Null
                    [MarkVibeBenchWindow]::Activate($graphHandle, $app.Id)
                    $canvas = Get-GraphCanvas $app.Id
                }

                # Use real wheel input over the center of the live canvas at a steady cadence.
                $centerX = [int]($canvas.x + $canvas.width * 0.50)
                $centerY = [int]($canvas.y + $canvas.height * 0.50)
                [MarkVibeBenchWindow]::ScrollBurst($centerX, $centerY, $app.Id, 1, 12)
                Start-Sleep -Milliseconds 300
                if ($Capture) {
                    Invoke-GraphUi $app.Id @('screenshot', '-o', (Join-Path $screenshotRoot "map-$Count-$PageBytes-detail.png")) | Out-Null
                    [MarkVibeBenchWindow]::Activate($graphHandle, $app.Id)
                    $canvas = Get-GraphCanvas $app.Id
                    $centerX = [int]($canvas.x + $canvas.width * 0.50)
                    $centerY = [int]($canvas.y + $canvas.height * 0.50)
                }
                # Zoom out around the center before dragging in the lower left, away from cards and minimap.
                [MarkVibeBenchWindow]::ScrollBurst($centerX, $centerY, $app.Id, -1, 18)
                Start-Sleep -Milliseconds 300
                [MarkVibeBenchWindow]::Activate($graphHandle, $app.Id)
                $canvas = Get-GraphCanvas $app.Id
                $left = [int]($canvas.x + $canvas.width * 0.15)
                $right = [int]($canvas.x + $canvas.width * 0.34)
                $middle = [int](($left + $right) / 2)
                $bottom = $null
                foreach ($fraction in @(0.74, 0.83, 0.65)) {
                    $y = [int]($canvas.y + $canvas.height * $fraction)
                    if ([MarkVibeBenchWindow]::ContainsPoint($left, $y, $app.Id) -and
                        [MarkVibeBenchWindow]::ContainsPoint($middle, $y, $app.Id) -and
                        [MarkVibeBenchWindow]::ContainsPoint($right, $y, $app.Id)) {
                        $bottom = $y
                        break
                    }
                }
                if ($null -eq $bottom) { throw 'No unobstructed drag lane was found on the fixture.' }
                for ($pass = 0; $pass -lt 2; $pass++) {
                    Invoke-GraphUi $app.Id @('drag', "$left,$bottom", "$right,$bottom", '--hold-ms', '120') | Out-Null
                    Invoke-GraphUi $app.Id @('drag', "$right,$bottom", "$left,$bottom", '--hold-ms', '120') | Out-Null
                }
            } catch {
                $gestureError = $_.Exception.GetBaseException().Message + ' ' + [MarkVibeBenchWindow]::Describe($graphHandle)
            }
        }
        $app.Refresh()
        $endPrivate = $app.PrivateMemorySize64
        $peakResident = $app.PeakWorkingSet64
        # Closing normally drains the shared log. The last gesture must have finished before exit.
        if ($graphHandle -ne [IntPtr]::Zero) {
            [MarkVibeBenchWindow]::Release($graphHandle, $app.Id)
            $graphHandle = [IntPtr]::Zero
        }
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(6000)) { throw 'The synthetic MarkVibe window did not close cleanly.' }
        $batches = @(Get-GraphProbe $profile 'map.performance')
        $zoom = if (-not $IndexOnly) { Measure-GraphGesture $batches 'zoom' }
        $pan = if (-not $IndexOnly) { Measure-GraphGesture $batches 'pan' }
        $drawStages = @($batches | Group-Object -Property stage | ForEach-Object {
            $drawCount = @($_.Group | ForEach-Object { $_.drawMs }).Count
            "$($_.Name)=$drawCount"
        })
        $result = [ordered]@{
            Mode = if ($IndexOnly) { 'index' } else { 'interactive' }
            Documents = $Count
            BytesPerDocument = $PageBytes
            IndexedDocuments = $index.indexed
            IndexComplete = $index.complete
            IndexMs = [math]::Round($index.elapsedMs, 2)
            StartupToTitleObservedMs = if ($null -ne $pageReady) { [math]::Round($pageReady, 2) }
            MapCommandToCanvasObservedMs = if ($null -ne $mapCommandMs) { [math]::Round($mapCommandMs, 2) }
            ProjectedNodes = ($batches | Measure-Object -Property nodes -Maximum).Maximum
            ProjectedEdges = ($batches | Measure-Object -Property edges -Maximum).Maximum
            ReadyPrivateMiB = [math]::Round($readyPrivate / 1MB, 1)
            EndPrivateMiB = [math]::Round($endPrivate / 1MB, 1)
            PeakWorkingSetMiB = [math]::Round($peakResident / 1MB, 1)
            Zoom = $zoom
            Pan = $pan
            GestureError = $gestureError
            RecordedDrawStages = $drawStages
            ScreenshotDirectory = if ($Capture) { $screenshotRoot }
        }
        if ($IncludeSamples) { $result['DrawBatches'] = $batches }
        [pscustomobject]$result
        if ($gestureError) {
            throw "The interactive benchmark stopped safely: $gestureError"
        }
        if (-not $IndexOnly -and ($zoom.DrawCount -eq 0 -or $pan.DrawCount -eq 0)) {
            $observed = if ($drawStages.Count) { $drawStages -join ', ' } else { 'none' }
            throw "Required zoom and pan callbacks were missing. Recorded draws by stage: $observed. Verify that the camera moved and the benchmark executable contains the current probes."
        }
    } finally {
        $script:graphWindow = $null
        if (-not $app.HasExited) {
            if ($graphHandle -ne [IntPtr]::Zero) { [MarkVibeBenchWindow]::Release($graphHandle, $app.Id) }
            $app.Kill($true)
            [void]$app.WaitForExit(5000)
        }
        $app.Dispose()
    }
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
if ((Get-Item -LiteralPath $artifactRoot).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'The project artifacts folder must be a local directory.'
}
$runId = [guid]::NewGuid().ToString('N')
$fixtureRoot = Join-Path $artifactRoot "map-performance-fixture-$runId"
$screenshotRoot = Join-Path $artifactRoot "map-performance-captures-$runId"
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
try {
    $env:WINAPP_UI_WORKFLOW_ID = $runId
    if ($Capture) { [IO.Directory]::CreateDirectory($screenshotRoot) | Out-Null }
    foreach ($count in $DocumentCounts | Sort-Object -Unique) { Invoke-GraphCase $count 4096 }
    if ($IncludeBudgetCase) { Invoke-GraphCase 1024 32768 }
} finally {
    try {
        if ($winapp) { & $winapp ui yield 2>$null | Out-Null }
    } finally {
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
        if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
            $resolved = (Get-Item -LiteralPath $fixtureRoot).FullName
            $prefix = $artifactRoot + [IO.Path]::DirectorySeparatorChar
            if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($resolved) -ne "map-performance-fixture-$runId") {
                throw 'Performance fixture cleanup escaped the project artifacts folder.'
            }
            for ($attempt = 1; $attempt -le 8; $attempt++) {
                try {
                    Remove-Item -LiteralPath $resolved -Recurse -Force
                    break
                } catch {
                    # WebView2 may still be releasing its isolated profile after the app exits.
                    if ($attempt -eq 8) { throw }
                    Start-Sleep -Milliseconds 350
                }
            }
        }
    }
}
