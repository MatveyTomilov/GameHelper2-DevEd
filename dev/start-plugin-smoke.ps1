#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BuildDirectory,
    [string] $ProfileDirectory,
    [string] $RemoveRadarRulesFor
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'runtime.ps1')
. (Join-Path $PSScriptRoot 'recovery.ps1')
if (-not $ProfileDirectory) { $ProfileDirectory = (Get-Content -LiteralPath (Join-Path $repo 'artifacts/active-runtime.txt') -Raw).Trim() }
$source = (Resolve-Path -LiteralPath $BuildDirectory).Path
$profile = (Resolve-Path -LiteralPath $ProfileDirectory).Path
$manifest = Read-RuntimeManifest $source -AllowProfileChanges
$output = [IO.Directory]::CreateDirectory((Join-Path $repo ('artifacts/plugin-smoke/' + [guid]::NewGuid().ToString('N')))).FullName
$runtime = Join-Path $output 'runtime'
foreach ($file in @($manifest.files.Keys) + 'developer-build.json') {
    $original = Get-RuntimeFile $source $file
    $destination = Join-Path $runtime $file
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($original, $destination, $false)
}
[void](Read-RuntimeManifest $runtime -AllowProfileChanges)
Copy-RuntimeSettings $profile $runtime
$config = [IO.Directory]::CreateDirectory((Join-Path $runtime 'configs')).FullName
$corePath = Join-Path $config 'core_settings.json'
$core = if (Test-Path -LiteralPath $corePath) { Get-Content -LiteralPath $corePath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$core.AutomateNpcQuests = $false
$core.HideSettingWindowOnStart = $true
Write-RecoveryJson $corePath $core
$plugins = @{}
foreach ($plugin in $manifest.plugins) { $plugins[$plugin] = @{ Enable = $plugin -in 'Radar','Atlas2','LootValue','McpBridge' } }
Write-RecoveryJson (Join-Path $config 'plugins.json') $plugins

$radarSettings = Join-Path $runtime 'Plugins/Radar/config/settings.txt'
$lootSettings = Join-Path $runtime 'Plugins/LootValue/config/settings.txt'
function Read-Settings($path) {
    if (Test-Path -LiteralPath $path) { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable) }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    @{}
}
$radar = Read-Settings $radarSettings
$radar.DrawWalkableMap = $true
$radar.DrawWhenNotInHideoutOrTown = $false
Write-RecoveryJson $radarSettings $radar
$loot = Read-Settings $lootSettings
$loot.ShowInventoryOverlay = $true
$loot.ShowStashOverlay = $true
Write-RecoveryJson $lootSettings $loot

if ($RemoveRadarRulesFor) {
    $poi = Join-Path $runtime 'Plugins/Radar/important_tgt_files.txt'
    $rules = Get-Content -LiteralPath $poi -Raw | ConvertFrom-Json -AsHashtable
    [void]$rules.Remove($RemoveRadarRulesFor)
    Write-RecoveryJson $poi $rules
}

if (Get-NetTCPConnection -State Listen -LocalAddress 127.0.0.1 -LocalPort 3002 -ErrorAction SilentlyContinue) {
    throw 'Close the overlay currently listening at 127.0.0.1:3002 before starting the smoke copy.'
}
$snapshot = Join-Path $output 'plugin-data.json'
$p = & "$repo/start.ps1" -RuntimeDirectory $runtime -SmokeReport $snapshot -LogDirectory (Join-Path $output 'logs')
$launch = @{ runtime = $runtime; snapshot = $snapshot; overlayPid = $p.Id; output = $output
    removedRadarRulesFor = $RemoveRadarRulesFor }
Write-RecoveryJson (Join-Path $output 'launch.json') $launch
Write-Output (Join-Path $output 'launch.json')
