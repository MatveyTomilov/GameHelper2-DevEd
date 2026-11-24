#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $LocalSource)

$ErrorActionPreference = 'Stop'
$deved = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $LocalSource).Path
function Git-Checked($workspace, $arguments) {
    & git -C $workspace @arguments
    if ($LASTEXITCODE) { throw 'Local Git operation failed.' }
}

$sourceHead = Git-Checked $source @('rev-parse', 'HEAD')
Git-Checked $source @('merge-base', '--is-ancestor', '402f9788097e3a7488808c1b3171993ceafaaca8', $sourceHead)
$output = Join-Path $deved ('artifacts/local-build/' + [guid]::NewGuid().ToString('N'))
$workspace = Join-Path $output 'source'
[void][IO.Directory]::CreateDirectory($output)
Git-Checked $source @('worktree', 'add', '--detach', $workspace, $sourceHead) | Out-Host

foreach ($relative in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local-import.paths')) {
    if ($relative -cnotmatch '\A[\w./-]+\z' -or $relative.StartsWith('/') -or ($relative.Split('/') -contains '..')) {
        throw 'Invalid local overlay path.'
    }
    $from = Join-Path $deved $relative
    $into = Join-Path $workspace $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($into))
    [IO.File]::Copy($from, $into, $true)
}

$snapshot = Git-Checked $deved @('rev-parse', 'HEAD')
$release = & (Join-Path $workspace 'build.ps1') -Configuration Release
$runtime = $release | Select-Object -Last 1
$release | Select-Object -Last 8 | Out-Host
$zip = & (Join-Path $workspace 'package.ps1') -RuntimeDirectory $runtime
[pscustomobject]@{ localSourceCommit = $sourceHead; devedCommit = $snapshot; source = $workspace;
    runtime = $runtime; package = $zip.FullName; sha256 = (Get-FileHash -LiteralPath $zip.FullName).Hash }
