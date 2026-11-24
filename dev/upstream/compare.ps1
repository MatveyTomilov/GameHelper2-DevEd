#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [ValidatePattern('\A[a-fA-F0-9]{40}\z')] [string] $Before,
    [Parameter(Mandatory)] [ValidatePattern('\A[a-fA-F0-9]{40}\z')] [string] $After
)

$ErrorActionPreference = 'Stop'
$sources = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json).sources
if ($Repository -cnotin $sources.repo) { throw 'Repository is not on the configured upstream list.' }
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cache = Join-Path $root ('artifacts/compare/' + $Repository.Replace('/', '-'))
if (-not (Test-Path -LiteralPath $cache)) { & git init --quiet $cache; if ($LASTEXITCODE) { throw 'git init failed.' } }
function Invoke-CompareGit([string[]] $arguments) {
    & git -C $cache @arguments
    if ($LASTEXITCODE) { throw "Git comparison failed: $($arguments[0])." }
}

$url = "https://github.com/$Repository.git"
Invoke-CompareGit @('fetch', '--quiet', '--no-tags', $url, $Before, $After)
Invoke-CompareGit @('log', '--oneline', "$Before..$After")
Invoke-CompareGit @('diff', '--no-ext-diff', '--name-status', $Before, $After)
Invoke-CompareGit @('diff', '--no-ext-diff', '--stat', $Before, $After)
