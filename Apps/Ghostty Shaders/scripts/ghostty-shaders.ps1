[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Apply', 'Disable', 'Status')][string]$Action = 'Menu',
    [string]$Preset,
    [string]$Editor,
    [string[]]$EditorArguments = @(),
    [string]$ConfigRoot = $(if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $HOME '.config' })
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$ghosttyDir = Join-Path $ConfigRoot 'ghostty'
$config = if (Test-Path -LiteralPath (Join-Path $ghosttyDir 'config')) { Join-Path $ghosttyDir 'config' } else { Join-Path $ghosttyDir 'config.ghostty' }
$generated = Join-Path $ghosttyDir 'vibekit-crt.glsl'
$begin = '# BEGIN VIBEKIT GHOSTTY SHADERS'
$end = '# END VIBEKIT GHOSTTY SHADERS'
$libraryDir = Join-Path $ghosttyDir 'vibekit-shaders'
$sourcePath = Join-Path $project 'shaders/crt.glsl'
if ($Preset) {
    if ($Preset -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid preset name.' }
    $sourcePath = Join-Path $project "shaders/$Preset.glsl"
} elseif (Test-Path -LiteralPath $generated) { $sourcePath = $generated }
$template = Get-Content -LiteralPath $sourcePath -Raw
$selectedName = [IO.Path]::GetFileNameWithoutExtension($sourcePath)
$dirty = $false
$schema = [ordered]@{
    ENABLE_CURVE = @('Curve',0,1); ENABLE_OVERSCAN = @('Overscan',0,1)
    ENABLE_BLOOM = @('Bloom',0,1); ENABLE_BLUR = @('Blur',0,1)
    ENABLE_GRAYSCALE = @('Grayscale',0,1); ENABLE_BLACKLEVEL = @('Black level',0,1)
    ENABLE_REFRESHLINE = @('Animated refresh line',0,1); ENABLE_SCANLINES = @('Scanlines',0,1)
    ENABLE_TINT = @('Phosphor tint',0,1); ENABLE_GRAIN = @('Animated grain',0,1)
    CURVE_INTENSITY = @('Curve intensity',0,3); OVERSCAN_PERCENTAGE = @('Overscan fraction',0,0.25)
    BLOOM_OFFSET = @('Bloom offset',0,0.05); BLOOM_STRENGTH = @('Bloom strength',0,3)
    BLUR_MULTIPLIER = @('Blur multiplier',0,3); BLUR_STRENGTH = @('Blur strength',0,1)
    BLUR_OFFSET = @('Blur offset',0,0.05); GRAYSCALE_MODE = @('Grayscale: 0 intensity, 1 gleam, 2 luminance, 3 luma',0,3)
    BLACKLEVEL = @('Black floor',0,1); TINT_R = @('Tint red',0,1); TINT_G = @('Tint green',0,1)
    TINT_B = @('Tint blue',0,1); GRAIN_INTENSITY = @('Grain strength',0,0.2)
    PALETTE_MIX = @('Retro palette mix',0,1); PAPER_R = @('Background red',0,1)
    PAPER_G = @('Background green',0,1); PAPER_B = @('Background blue',0,1)
    GLOW_STRENGTH = @('Phosphor glow',0,1)
    SCANLINE_STRENGTH = @('Scanline strength',0,1); SCANLINE_PIXEL_SIZE = @('Scanline width in pixels',1,8)
}
function Load-Options {
    $script:values = [ordered]@{}
    foreach ($key in $schema.Keys) {
        $match = [regex]::Match($template, '(?m)^#define ' + [regex]::Escape($key) + ' ([0-9.]+)[ \t]*$')
        if (-not $match.Success) { continue }
        $n = 0.0
        if (-not [double]::TryParse($match.Groups[1].Value, [Globalization.NumberStyles]::Float, [cultureinfo]::InvariantCulture, [ref]$n) -or $n -lt $schema[$key][1] -or $n -gt $schema[$key][2] -or (($key -match '^ENABLE_' -or $key -eq 'GRAYSCALE_MODE') -and $n -ne [math]::Floor($n))) { throw "Invalid shader option: $key" }
        $script:values[$key] = $match.Groups[1].Value
    }
}
Load-Options
function Read-Config {
    if (Test-Path -LiteralPath $config) { return [IO.File]::ReadAllText($config) }
    return ''
}
function Remove-ManagedBlock([string]$text) {
    $starts = [regex]::Matches($text, '(?m)^' + [regex]::Escape($begin) + '\r?$').Count
    $ends = [regex]::Matches($text, '(?m)^' + [regex]::Escape($end) + '\r?$').Count
    if ($starts -ne $ends -or $starts -gt 1) { throw 'Managed markers are incomplete or duplicated; configuration was not changed.' }
    if ($starts -eq 0) { return $text }
    $pattern = '(?ms)^' + [regex]::Escape($begin) + '\r?\n.*?^' + [regex]::Escape($end) + '(?:\r?\n|$)'
    $result = [regex]::Replace($text, $pattern, '')
    if ($result -eq $text) { throw 'Managed markers are out of order; configuration was not changed.' }
    return $result
}
function Backup-Config {
    if (Test-Path -LiteralPath $config) {
        $backup = "$config.vibekit-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([guid]::NewGuid().ToString('N').Substring(0,8)).bak"
        Copy-Item -LiteralPath $config -Destination $backup
        Write-Host "Backup: $backup"
    }
}
function Render-Shader {
    $shader = $template
    foreach ($key in $values.Keys) {
        $pattern = '(?m)^(#define ' + [regex]::Escape($key) + ' )[0-9.]+'
        $value = $values[$key]
        # Floating-point constants require a decimal point in GLSL.
        if ($key -notmatch '^ENABLE_' -and $key -ne 'GRAYSCALE_MODE' -and $value -notmatch '\.') { $value += '.0' }
        $shader = [regex]::Replace($shader, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $m.Groups[1].Value + $value })
    }
    return $shader
}
function Set-Effect {
    $original = Read-Config
    $clean = Remove-ManagedBlock $original
    $shader = Render-Shader
    New-Item -ItemType Directory -Path $ghosttyDir -Force | Out-Null
    [IO.File]::WriteAllText($generated, $shader, [Text.UTF8Encoding]::new($false))
    $animation = if ($values.ENABLE_REFRESHLINE -eq '1' -or $values.ENABLE_GRAIN -eq '1') { 'true' } else { 'false' }
    if ($clean.Length -gt 0 -and -not $clean.EndsWith("`n")) { $clean += "`n" }
    $block = "$begin`ncustom-shader = $generated`ncustom-shader-animation = $animation`n$end`n"
    Backup-Config
    [IO.File]::WriteAllText($config, ($clean + $block), [Text.UTF8Encoding]::new($false))
    $script:dirty = $false
    Write-Host "CRT applied to $config. Reload Ghostty with Ctrl+Shift+, or open a new window."
    Write-Host 'Existing custom shaders remain in the chain; later config files may override animation.'
}
function Disable-Effect {
    $original = Read-Config
    $clean = Remove-ManagedBlock $original
    if ($clean -ceq $original) { Write-Host 'No managed shader is active.'; return }
    Backup-Config
    [IO.File]::WriteAllText($config, $clean, [Text.UTF8Encoding]::new($false))
    Write-Host 'CRT disabled. Reload Ghostty with Ctrl+Shift+, or open a new window.'
}
function Show-Status {
    $raw = Read-Config
    $null = Remove-ManagedBlock $raw
    $status = if ($raw.Contains($begin)) { 'enabled' } else { 'disabled' }
    Write-Host "VibeKit CRT: $status; configuration: $config"
}
function Select-Shader {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $project 'shaders') -Filter '*.glsl' | Sort-Object Name)
    if (Test-Path -LiteralPath $libraryDir) { $files += @(Get-ChildItem -LiteralPath $libraryDir -Filter '*.glsl' | Sort-Object Name) }
    Write-Host "`n🎨 SHADER LIBRARY" -ForegroundColor Magenta
    for ($i=0; $i -lt $files.Count; $i++) {
        $tag = if ($files[$i].DirectoryName -eq $libraryDir) { 'personal' } else { 'bundled' }
        Write-Host ('  {0,2}  {1}  [{2}]' -f ($i+1), $files[$i].BaseName, $tag) -ForegroundColor Cyan
    }
    $choice = Read-Host 'Select a shader (Enter cancels; selection discards unapplied changes)'
    $index = 0
    if (-not [int]::TryParse($choice,[ref]$index) -or $index -lt 1 -or $index -gt $files.Count) { return }
    $candidate = Get-Content -LiteralPath $files[$index-1].FullName -Raw
    $script:template = $candidate
    Load-Options
    $script:sourcePath = $files[$index-1].FullName
    $script:selectedName = $files[$index-1].BaseName
    $script:dirty = $true
}
function Copy-Shader {
    $name = (Read-Host 'Name for personal copy (letters, numbers, hyphens; Enter cancels)').Trim()
    if (-not $name) { return $false }
    if ($name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$') { throw 'Use letters, numbers and hyphens, up to 64 characters.' }
    New-Item -ItemType Directory -Path $libraryDir -Force | Out-Null
    $target = Join-Path $libraryDir "$name.glsl"
    # CreateNew refuses to overwrite an existing personal shader.
    $stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((Render-Shader))
        $stream.Write($bytes,0,$bytes.Length)
    } finally { $stream.Dispose() }
    $script:sourcePath = $target
    $script:selectedName = $name
    $script:template = Get-Content -LiteralPath $target -Raw
    $script:dirty = $true
    Write-Host "✅ Personal copy saved: $target" -ForegroundColor Green
    return $true
}
function Open-ShaderEditor {
    # Keep shipped presets and the active generated shader intact while editing.
    if ([IO.Path]::GetDirectoryName($sourcePath) -ne $libraryDir) {
        Write-Host 'Create a personal copy before editing.' -ForegroundColor Yellow
        if (-not (Copy-Shader)) { return }
    }
    [IO.File]::WriteAllText($sourcePath, (Render-Shader), [Text.UTF8Encoding]::new($false))
    $command = $Editor
    $arguments = @($EditorArguments)
    if (-not $command) {
        $preference = if ($env:VISUAL) { $env:VISUAL } elseif ($env:EDITOR) { $env:EDITOR } else { '' }
        if ($preference) {
            # Parse a command and literal arguments only; never evaluate shell code.
            $parseErrors = $null
            $tokens = @([Management.Automation.PSParser]::Tokenize($preference,[ref]$parseErrors))
            if ($parseErrors.Count -gt 0 -or $tokens.Count -eq 0 -or @($tokens | Where-Object { $_.Type -notin @('Command','CommandArgument','CommandParameter','String','Number') }).Count -gt 0) { throw 'Editor must be an executable with literal arguments. Use -Editor and -EditorArguments for complex paths.' }
            $command = $tokens[0].Content
            $arguments = @($tokens | Select-Object -Skip 1 | ForEach-Object { $_.Content })
        } else { $command = 'xdg-open' }
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $command
    $start.UseShellExecute = $false
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.ArgumentList.Add($sourcePath)
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Editor exited with code $($process.ExitCode)." }
    $null = Read-Host 'Save in the editor, then press Enter here to reload the shader'
    $script:template = Get-Content -LiteralPath $sourcePath -Raw
    Load-Options
    $script:dirty = $true
    Write-Host '✅ Reloaded. Choose Apply when ready.' -ForegroundColor Green
}
function Edit-Parameters {
    $keys = @($values.Keys)
    if ($keys.Count -eq 0) { Write-Host 'This shader has no menu parameters; use the editor.'; return }
    for ($i=0; $i -lt $keys.Count; $i++) {
        $key = $keys[$i]
        Write-Host ('  {0,2}. {1,-38} {2}' -f ($i+1),$schema[$key][0],$values[$key]) -ForegroundColor Cyan
    }
    $index = 0
    $choice = Read-Host 'Parameter number (Enter cancels)'
    if (-not [int]::TryParse($choice,[ref]$index) -or $index -lt 1 -or $index -gt $keys.Count) { return }
    $key = $keys[$index-1]; $spec = $schema[$key]
    $inputValue = (Read-Host "Value ($($spec[1])..$($spec[2]); decimal point)").Trim()
    $n = 0.0
    if (-not [double]::TryParse($inputValue,[Globalization.NumberStyles]::AllowDecimalPoint,[cultureinfo]::InvariantCulture,[ref]$n) -or $n -lt $spec[1] -or $n -gt $spec[2] -or (($key -match '^ENABLE_' -or $key -eq 'GRAYSCALE_MODE') -and $n -ne [math]::Floor($n))) { throw 'Invalid value.' }
    $values[$key] = $n.ToString('0.################',[cultureinfo]::InvariantCulture)
    $script:dirty = $true
}
if ($Action -eq 'Apply') { Set-Effect; return }
if ($Action -eq 'Disable') { Disable-Effect; return }
if ($Action -eq 'Status') { Show-Status; return }
while ($true) {
    Write-Host "`n╭──────────────────────────────────────────────────╮" -ForegroundColor DarkCyan
    Write-Host '  📺  GHOSTTY SHADERS  ·  RETRO LAB' -ForegroundColor Cyan
    Write-Host '  ✦   UmbertoCiccobi.biz' -ForegroundColor Magenta
    Write-Host '╰──────────────────────────────────────────────────╯' -ForegroundColor DarkCyan
    Write-Host "  🎨 Selected: $selectedName" -ForegroundColor Yellow
    if ($dirty) { Write-Host '  ● Pending selection / edits — Apply to activate' -ForegroundColor Yellow }
    Show-Status
    Write-Host "`n  S  🎨 Select shader       P  🎛️  Parameters" -ForegroundColor Cyan
    Write-Host '  A  ✨ Apply shader        C  📋 Duplicate' -ForegroundColor Green
    Write-Host '  E  📝 Open in editor      R  ↩️  Reload source' -ForegroundColor Cyan
    Write-Host '  D  ⏻  Disable effect      Q  🚪 Quit' -ForegroundColor DarkYellow
    $choice = Read-Host "`n  Your choice"
    if ($null -eq $choice -or $choice -eq 'q') { break }
    try {
        switch ($choice.Trim().ToLowerInvariant()) {
            's' { Select-Shader }
            'p' { Edit-Parameters }
            'a' { Set-Effect }
            'c' { $null = Copy-Shader }
            'e' { Open-ShaderEditor }
            'r' { $script:template = Get-Content -LiteralPath $sourcePath -Raw; Load-Options; $script:dirty = $true }
            'd' { Disable-Effect }
            default { Write-Host 'Choose one of the letters above.' -ForegroundColor Yellow }
        }
    } catch { Write-Host "❌ $($_.Exception.Message)" -ForegroundColor Red }
}
