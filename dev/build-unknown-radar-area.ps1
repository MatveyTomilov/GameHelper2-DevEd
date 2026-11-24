#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9_]+$')] [string] $AreaId,
    [string] $Revision = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'runtime.ps1')
. (Join-Path $PSScriptRoot 'recovery.ps1')
$output = [IO.Directory]::CreateDirectory((Join-Path $repo ('artifacts/unknown-radar/' + [guid]::NewGuid().ToString('N')))).FullName
$tree = Join-Path $output 'source'
$commit = Invoke-Checked git @('-C', $repo, 'rev-parse', "$Revision^{commit}")
Invoke-Checked git @('-C', $repo, 'worktree', 'add', '--detach', $tree, $commit) | Out-Null

$namesFile = Join-Path $tree 'GameHelper/Data/WorldAreaNames.tsv'
$tagsFile = Join-Path $tree 'GameHelper/Data/WorldAreaTags.json'
$prefix = $AreaId + [char]9
$entries = [IO.File]::ReadAllLines($namesFile)
$retained = @($entries | Where-Object { -not $_.StartsWith($prefix, [StringComparison]::Ordinal) })
if ($entries.Length -eq $retained.Length) { throw "No exact named catalog entry for $AreaId in $commit." }
[IO.File]::WriteAllLines($namesFile, $retained)
$tags = Get-Content -LiteralPath $tagsFile -Raw | ConvertFrom-Json -AsHashtable
$hadTags = $tags.Contains($AreaId)
if ($hadTags) {
    [void]$tags.Remove($AreaId)
    Write-RecoveryJson $tagsFile $tags
}

$build = & "$tree/build.ps1" 6> (Join-Path $output 'build.log') | Select-Object -Last 1
if (-not $build -or -not (Test-Path -LiteralPath $build -PathType Container)) { throw 'Unknown area worktree did not produce a runtime.' }
$path = Join-Path $output 'fixture.json'
Write-RecoveryJson $path @{ areaId = $AreaId; baseCommit = $commit; namesRemoved = $entries.Length - $retained.Length
    tagsRemoved = $hadTags; worktree = $tree; build = $build }
Write-Output $path
