#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InstalledDirectory,
    [string] $Archive,
    [ValidatePattern('\A[A-Fa-f0-9]{64}\z')] [string] $ExpectedSha256,
    [string] $ClientExe
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev/runtime.ps1')
$source = (Resolve-Path -LiteralPath $InstalledDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $source 'GameHelper.exe') -PathType Leaf)) {
    throw "Expected a GameHelper installation: $source"
}
if ([bool] $Archive -ne [bool] $ExpectedSha256) { throw 'Provide both -Archive and -ExpectedSha256.' }
if ($Archive) {
    $Archive = (Resolve-Path -LiteralPath $Archive).Path
    if ((Get-FileHash -LiteralPath $Archive).Hash -ne $ExpectedSha256) { throw 'Archive SHA-256 mismatch.' }
}
if ($ClientExe) { $ClientExe = (Resolve-Path -LiteralPath $ClientExe).Path }

$stamp = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$backups = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'artifacts/backups')).FullName
$pending = Join-Path $backups ".$stamp"
$settings = Join-Path $pending 'settings'
[void] [IO.Directory]::CreateDirectory($settings)
Copy-RuntimeSettings $source $settings
$managed = Test-Path -LiteralPath (Join-Path $source 'developer-build.json') -PathType Leaf
if ($managed) {
    $previous = Read-RuntimeManifest $source -AllowProfileChanges
    foreach ($relative in @($previous.files.Keys) + @('developer-build.json')) {
        $from = Get-RuntimeFile $source $relative
        $to = Join-Path $pending "runtime/$relative"
        [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
        [IO.File]::Copy($from, $to, $false)
    }
    [void] (Read-RuntimeManifest (Join-Path $pending 'runtime') -AllowProfileChanges)
}
$record = [ordered] @{
    captured = [DateTimeOffset]::Now.ToString('o')
    git = (Invoke-Checked git @('-C', $PSScriptRoot, 'rev-parse', 'HEAD'))
    managedRuntimeCaptured = $managed
    client = if ($ClientExe) { [IO.Path]::GetFileName($ClientExe) }
    clientSha256 = if ($ClientExe) { (Get-FileHash -LiteralPath $ClientExe).Hash }
}
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $pending 'baseline.json')
$backup = Join-Path $backups $stamp
[IO.Directory]::Move($pending, $backup)
Write-Output "Backup: $backup"

if ($Archive) {
    $deployments = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'artifacts/deployments')).FullName
    $incoming = Join-Path $deployments ".$stamp"
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $incoming)
    $manifest = Read-RuntimeManifest $incoming
    foreach ($file in (Get-ChildItem -LiteralPath $incoming -File -Recurse -Force)) {
        $relative = [IO.Path]::GetRelativePath($incoming, $file.FullName).Replace('\', '/')
        if ($relative -ne 'developer-build.json' -and -not $manifest.files.Contains($relative)) {
            throw "Unexpected file in update: $relative"
        }
    }
    Copy-RuntimeSettings (Join-Path $backup 'settings') $incoming
    $newRuntime = Join-Path $deployments $stamp
    [IO.Directory]::Move($incoming, $newRuntime)
    Write-Output "Staged update: $newRuntime"
}
