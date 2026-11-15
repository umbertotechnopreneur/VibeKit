# Shared console helpers. Dot-source this file to use its functions.

function Test-VibeKitCommands {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]] $Name)

    $missing = @($Name | Where-Object { -not (Get-Command -Name $_ -ErrorAction SilentlyContinue) })
    foreach ($command in $missing) { Write-Warning "Missing command: $command" }
    return ($missing.Count -eq 0)
}

function Read-VibeKitChoice {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]] $Options)

    for ($index = 0; $index -lt $Options.Count; $index++) {
        Write-Host ("{0}) {1}" -f ($index + 1), $Options[$index])
    }
    $answer = Read-Host 'Select an option'
    $selection = 0
    if (-not [int]::TryParse($answer, [ref] $selection) -or $selection -lt 1 -or $selection -gt $Options.Count) {
        throw 'Invalid selection.'
    }
    return $selection
}
