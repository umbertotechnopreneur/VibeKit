# Builds one effect, or all five effects and the suite, from the current sources.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Widget,
    [Parameter(Mandatory)][string]$WixPath,
    [ValidateSet('local','public')][string]$Channel = $(if ($env:NOSTALGIA_CHANNEL) { $env:NOSTALGIA_CHANNEL } else { 'local' }),
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$WixPath = (Get-Item -LiteralPath $WixPath -ErrorAction Stop).FullName
if ($Channel -notin 'local','public') { throw "Unknown Nostalgia build channel '$Channel'." }
$products = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'windows-products.json') | ConvertFrom-Json
$entry = $products.PSObject.Properties[$Widget]
if (!$entry -and $Widget -ne 'All') { throw "Unknown Nostalgia effect '$Widget'." }
if ($Widget -eq 'All' -and $OutputDirectory) { throw 'Use NOSTALGIA_ARTIFACTS to change the output root for all packages.' }
$family = Split-Path $PSScriptRoot -Parent
$shared = (Resolve-Path -LiteralPath (Join-Path $family '../Shared')).Path
$signer = Join-Path $shared '../../scripts/packaging/Sign-VibeWareDevelopmentFile.ps1'
$artifacts = Join-Path $family 'artifacts'
if ($env:NOSTALGIA_ARTIFACTS) {
    $artifacts = [IO.Path]::GetFullPath([IO.Path]::Combine($family, $env:NOSTALGIA_ARTIFACTS))
}
$run = [guid]::NewGuid().ToString('N').Substring(0,12)
$stage = Join-Path $artifacts "stage/$run/windows/x64"
$payloads = Join-Path $stage 'payloads'
$workRoot = Join-Path $artifacts "work/windows/x64/$run"
New-Item -ItemType Directory -Path $payloads,$workRoot -Force | Out-Null

# Produce a private application payload once for both installer formats.
function Publish-Effect($product, $project, $work, $payload) {
    New-Item -ItemType Directory -Path $payload,$work -Force | Out-Null
    if ($product -eq 'RetroTerminal') {
        $nativeBuild = Join-Path $work 'native'
        & (Join-Path $family 'Shared/Native/Build-Engine.ps1') -Source (Join-Path $family "$product/Windows") -Build $nativeBuild -Configuration Release -Target RetroEngine | Out-Host
    }
    $properties = @('-p:Configuration=Release', '-p:Platform=x64', '-p:RuntimeIdentifier=win-x64',
        '-p:SelfContained=false', '-p:NuGetAudit=false', "-p:NostalgiaBuildDirectory=$work")
    & dotnet publish $project @properties -o $payload | Out-Host
    if ($LASTEXITCODE) { throw "$product source publish failed." }
    $metadata = & dotnet msbuild $project @properties '-getProperty:TargetDir,AssemblyName'
    if ($LASTEXITCODE) { throw "Cannot locate the $product build outputs." }
    $built = (($metadata -join "`n") | ConvertFrom-Json).Properties
    if ($product -eq 'WaterRipple') {
        Copy-Item -LiteralPath (Join-Path $built.TargetDir 'RippleRenderer.dll') -Destination $payload
        $shaders = New-Item -ItemType Directory -Path (Join-Path $payload 'Shaders') -Force
        foreach ($shader in 'RippleVS.cso','RipplePS.cso') {
            Copy-Item -LiteralPath (Join-Path $built.TargetDir "Shaders/$shader") -Destination $shaders.FullName
        }
    } elseif ($product -eq 'RetroTerminal') {
        Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release/RetroEngine.dll'),(Join-Path $family "$product/LICENSE") -Destination $payload
        $shaders = New-Item -ItemType Directory -Path (Join-Path $payload 'Shaders') -Force
        Copy-Item -LiteralPath (Join-Path $family "$product/Windows/Shaders/Retro.hlsl") -Destination $shaders.FullName
    }
    $name = "$($built.AssemblyName).exe"
    if ($Channel -eq 'local') { & $signer -Path (Join-Path $payload $name) | Out-Host }
    return $name
}

# Build and sign the MSI in staging, then atomically promote it to the package directory.
function New-EffectInstaller($product, $version, $executableArgument, $payload, $output) {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $installer = "$product-$version-windows-x64.msi"
    $wxs = Join-Path $stage "$product.wxs"
    & python (Join-Path $shared 'create-widget-wix.py') $product $version $executableArgument $payload $wxs
    if ($LASTEXITCODE) { throw "$product WiX source generation failed." }
    $stagedMsi = Join-Path $stage $installer
    & (Join-Path $shared 'Build-WindowsMsi.ps1') -WixPath $WixPath -Source $wxs -Output $stagedMsi -IntermediateDirectory (Join-Path $stage "wix/$product") -SkipSigning
    if ($Channel -eq 'local') { & $signer -Path $stagedMsi }

    # Copy next to the destination first so promotion also works across volumes.
    $candidate = Join-Path $output ".$run-$installer"
    $destination = Join-Path $output $installer
    [IO.File]::Copy($stagedMsi, $candidate)
    try {
        try { [IO.File]::Move($candidate, $destination) }
        catch [IO.IOException] {
            if (![IO.File]::Exists($destination)) { throw }
            [IO.File]::Replace($candidate, $destination, [System.Management.Automation.Language.NullString]::Value)
        }
    } finally {
        if ([IO.File]::Exists($candidate)) { [IO.File]::Delete($candidate) }
    }
    Write-Output "Package: $destination"

}

$selected = if ($Widget -eq 'All') { $products.PSObject.Properties } else { @($entry) }
$executables = [ordered]@{}
foreach ($item in $selected) {
    $product = $item.Name
    $project = Join-Path $family "$product/$($item.Value.project)"
    $version = ([xml](Get-Content -LiteralPath $project -Raw)).SelectSingleNode('//Version').InnerText
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Missing or invalid MSI version in $project." }
    $payload = Join-Path $payloads $product
    $executables[$product] = Publish-Effect $product $project (Join-Path $workRoot $product) $payload
    $output = if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) }
        else { Join-Path $artifacts "packages/$Channel/windows/x64/$product" }
    New-EffectInstaller $product $version $executables[$product] $payload $output
}
if ($Widget -eq 'All') {
    $suite = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'suite.json') | ConvertFrom-Json
    $manifest = Join-Path $stage 'executables.json'
    $executables | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
    New-EffectInstaller 'Nostalgia' $suite.windows_version $manifest $payloads (Join-Path $artifacts "packages/$Channel/windows/x64/Nostalgia")
}

# Only remove this invocation's private scratch directories after promotion.
foreach ($scratch in $workRoot,(Join-Path $artifacts "stage/$run")) {
    $resolved = (Resolve-Path -LiteralPath $scratch).Path
    if ((Split-Path $resolved -Leaf) -ne $run -or
        !$resolved.StartsWith(([IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected build scratch directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
}
