#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $RuntimeDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev/runtime.ps1')
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$manifest = Read-RuntimeManifest $runtime
if ($manifest.buildId -notmatch '\A[0-9]{8}-[0-9]{6}-(debug|release)-[a-f0-9]{8}\z') {
    throw 'Invalid build ID.'
}
$parent = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'artifacts/packages')).FullName
$zipPath = Join-Path $parent "GameHelper2-DevEd-$($manifest.releaseVersion)-$($manifest.buildId)-win-x64.zip"
$temp = "$zipPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $zip = [IO.Compression.ZipFile]::Open($temp, 'Create')
    try {
        foreach ($relative in @($manifest.files.Keys) + @('developer-build.json')) {
            $path = Get-RuntimeFile $runtime $relative
            [void] [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, $relative)
        }
    } finally { $zip.Dispose() }
    [IO.File]::Move($temp, $zipPath)
} finally { [IO.File]::Delete($temp) }

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath "$zipPath.sha256"
Get-Item -LiteralPath $zipPath
