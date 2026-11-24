#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SmokeReport,
    [ValidateSet('McpBridge', 'Radar', 'RadarRoutes', 'Atlas2', 'LootValueInventory', 'LootValueStash')]
    [string[]] $Scenario = @('McpBridge', 'Radar', 'Atlas2', 'LootValueInventory'),
    [string] $ExpectedItemPath,
    [switch] $ExpectUnknownArea,
    [string] $RadarReferenceReport,
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'recovery.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/plugin-smoke/' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)).FullName
$results = [ordered]@{}

function Result($status, $reason, $evidence) { @{ status = $status; reason = $reason; evidence = $evidence } }
function Assert-Data($passed, $reason, $evidence) { Result $(if ($passed) { 'PASS' } else { 'FAIL' }) $reason $evidence }
function Read-Snapshot {
    $data = Get-Content -LiteralPath $SmokeReport -Raw | ConvertFrom-Json -AsHashtable
    $age = [DateTimeOffset]::UtcNow - [DateTimeOffset]$data.capturedAt
    if ($age.TotalSeconds -gt 8 -or $age.TotalSeconds -lt -2) { throw 'Local plugin report is stale.' }
    $data
}

try {
    $local = Read-Snapshot
    $status = Invoke-RecoveryMcp $repo $output status
    if ($status.overlayPid -ne $local.overlayPid -or $status.pid -ne $local.clientPid) {
        throw 'The local smoke report and MCP belong to different processes.'
    }
    if (-not $status.attached -or -not $status.areaLoaded -or $status.gameState -ne 'InGameState') {
        throw 'Enter a loaded area with a playable character.'
    }
    $state = Invoke-RecoveryMcp $repo $output state
    if ($state.area.id -cne $local.areaId -or $state.area.hash -cne $local.areaHash) {
        throw 'Area changed between the local snapshot and MCP. Repeat in a stable area.'
    }
    $roots = (Invoke-RecoveryMcp $repo $output ui_roots).roots
    foreach ($test in $Scenario) {
        try {
            $results[$test] = switch ($test) {
                'McpBridge' {
                    $entities = Invoke-RecoveryMcp $repo $output entities @{ pathFilter = [string]$state.player.path; maxResults = 32 }
                    $player = $entities.entities | Where-Object {
                        $_.id -eq $state.player.id -and $_.path -ceq $state.player.path -and $_.address -eq $state.player.address
                    } | Select-Object -First 1
                    $rpc = @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' } | ConvertTo-Json -Compress
                    $relay = Join-Path $repo 'dev/McpRelay/bin/Release/net10.0-windows/win-x64/McpRelay.dll'
                    $tools = ($rpc | & dotnet $relay | ConvertFrom-Json -AsHashtable).result.tools.name
                    $unauth = Invoke-WebRequest -Uri 'http://127.0.0.1:3002/' -Method Post -ContentType 'application/json' -Body $rpc -SkipHttpErrorCheck -NoProxy -TimeoutSec 5
                    $ok = $player -and $state.player.id -gt 0 -and $state.player.path -cmatch '^Metadata/Characters/' -and
                        $entities.area.hash -ceq $state.area.hash -and $unauth.StatusCode -eq 401 -and
                        'status' -in $tools -and 'atlas_nodes' -in $tools -and 'read_memory' -notin $tools
                    Assert-Data $ok 'Player identity, area, authenticated catalog and anonymous HTTP result' @{
                        playerMatched = [bool]$player; unauthorizedStatus = [int]$unauth.StatusCode; tools = $tools; awake = $entities.counts.awake
                    }
                }
                { $_ -in 'Radar', 'RadarRoutes' } {
                    $radar = $local.plugins.Radar
                    $map = $roots | Where-Object name -CEQ 'largeMap'
                    $world = $roots | Where-Object name -CEQ 'worldMapPanel'
                    $atlas = $roots | Where-Object name -CEQ 'atlas'
                    if (-not $radar -or $radar.error) { Result 'FAIL' 'Radar diagnostics unavailable' $radar; break }
                    if (-not $local.foreground -or -not $map.isVisible -or $world.isVisible -or $atlas.isVisible) {
                        Result 'BLOCKED' 'Close other panels and open the area map with Tab in focused PoE 2' $radar
                        break
                    }
                    $terrainOk = $radar.bytesPerRow -gt 0 -and $radar.heightRows -gt 0 -and $radar.tileKeys -gt 0 -and
                        $radar.textureWidth -eq 2 * $radar.bytesPerRow -and $radar.textureHeight -ge $radar.heightRows -and
                        $radar.walkableBytes -eq $radar.bytesPerRow * $radar.textureHeight
                    $unknownOk = -not $ExpectUnknownArea -or (-not $radar.areaInNameCatalog -and $radar.areaPoiRules -eq 0)
                    $ok = $radar.mapEnabled -and $radar.textureReady -and $radar.mapDrawnThisFrame -and $terrainOk -and $unknownOk
                    if ($RadarReferenceReport) {
                        $reference = Get-Content -LiteralPath $RadarReferenceReport -Raw | ConvertFrom-Json -AsHashtable
                        if ($reference.checks.Radar.status -ne 'PASS' -or $reference.area -cne $local.areaId -or
                            $reference.areaHash -cne $local.areaHash) {
                            Result 'BLOCKED' 'Radar reference must pass on the same area instance' $radar
                            break
                        }
                        foreach ($metric in 'bytesPerRow', 'heightRows', 'textureWidth', 'textureHeight', 'walkableBytes', 'tileKeys') {
                            $ok = $ok -and $reference.checks.Radar.evidence[$metric] -eq $radar[$metric]
                        }
                    }
                    if ($test -eq 'RadarRoutes') {
                        $ok = $ok -and $radar.routes.Count -gt 0 -and -not ($radar.routes | Where-Object {
                            [string]::IsNullOrWhiteSpace($_.name) -or ([long]$_.color -shr 24) -eq 0
                        })
                    }
                    Assert-Data $ok 'Current terrain geometry and draw submission, including a route when requested' $radar
                }
                'Atlas2' {
                    $atlas = $roots | Where-Object name -CEQ 'atlas'
                    if (-not $atlas.isVisible -or -not $local.foreground) {
                        Result 'BLOCKED' 'Open the endgame WORLD Atlas and focus the game' $null
                        break
                    }
                    $plugin = $local.plugins.Atlas2
                    $nodes = Invoke-RecoveryMcp $repo $output atlas_nodes @{ maxResults = 128 }
                    $byIndex = @{}
                    foreach ($node in $nodes.nodes) { $byIndex[[int]$node.index] = $node }
                    $comparable = @($plugin.nodes | Where-Object { $byIndex.ContainsKey([int]$_.index) })
                    $matching = @($comparable | Where-Object {
                        $other = $byIndex[[int]$_.index]
                        $links = @($other.connections | Select-Object -First 12 | ForEach-Object { "$($_.x),$($_.y)" } | Sort-Object)
                        $localLinks = @($_.links | ForEach-Object { "$($_.x),$($_.y)" } | Sort-Object)
                        $_.id -ceq $other.mapId -and $_.x -eq $other.grid.x -and $_.y -eq $other.grid.y -and
                        $_.state -ceq $other.state -and ($localLinks -join ';') -ceq ($links -join ';')
                    })
                    $ok = $plugin.current -and $nodes.total -gt 0 -and $plugin.total -ge $plugin.drawableCount -and
                        $comparable.Count -ge 3 -and $matching.Count -eq $comparable.Count -and
                        ($matching | Where-Object { $_.links.Count -gt 0 })
                    Assert-Data $ok 'Atlas2 cache matches MCP node IDs, grids, states and connections' @{
                        apiTotal = $nodes.total; cacheTotal = $plugin.total; comparable = $comparable.Count; matches = $matching.Count
                        samples = @($matching | Select-Object -First 4)
                    }
                }
                { $_ -in 'LootValueInventory', 'LootValueStash' } {
                    $inventory = $test -eq 'LootValueInventory'
                    $root = $roots | Where-Object name -CEQ $(if ($inventory) { 'rightPanel' } else { 'leftPanel' })
                    if (-not $root.isVisible -or -not $local.foreground) {
                        Result 'BLOCKED' 'Open the indicated panel with a known priced item and focus the game' $null
                        break
                    }
                    $plugin = $local.plugins.LootValue
                    if (-not $plugin -or $plugin.error) { Result 'FAIL' 'LootValue diagnostics unavailable' $plugin; break }
                    if ($plugin.priceEntries -eq 0) { Result 'BLOCKED' 'Price data is unavailable for this league/source' $plugin; break }
                    $panel = if ($inventory) { $plugin.inventory } else { $plugin.stash }
                    $prices = @($panel.items | Where-Object { $_.path -cmatch '^Metadata/Items/' -and
                        $_.exalted -gt 0 -and [double]::IsFinite($_.exalted) -and $_.label -and
                        (-not $ExpectedItemPath -or $_.path -ieq $ExpectedItemPath) })
                    $ok = $panel.enabled -and $panel.scanned -and $panel.valid -ge $panel.priced -and
                        $panel.priced -gt 0 -and $prices.Count -gt 0
                    Assert-Data $ok 'Price applied to a validated item in the current panel' $plugin
                }
            }
        } catch { $results[$test] = Result 'FAIL' $_.Exception.Message $null }
    }
    $end = Invoke-RecoveryMcp $repo $output state @{} 'state-after'
    if ($end.area.hash -cne $local.areaHash -or $end.area.id -cne $local.areaId -or
        (Read-Snapshot).overlayPid -ne $local.overlayPid) {
        throw 'Area or overlay changed during the smoke run.'
    }
} catch {
    foreach ($test in $Scenario) { $results[$test] = Result 'BLOCKED' $_.Exception.Message $null }
}

$report = Join-Path $output 'smoke.json'
Write-RecoveryJson $report @{ overlayPid = $local.overlayPid; clientPid = $local.clientPid; area = $local.areaId
    areaHash = $local.areaHash; capturedAt = $local.capturedAt; checks = $results }
foreach ($test in $Scenario) { Write-Host "$test $($results[$test].status)" }
Write-Output $report
if ($results.Values.status -contains 'FAIL') { exit 1 }
if ($results.Values.status -contains 'BLOCKED') { exit 2 }
