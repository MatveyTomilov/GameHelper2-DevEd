#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $RuntimeDirectory = $PSScriptRoot,
    [string] $OffsetDump,
    [string] $SmokeReport,
    [switch] $StructureAudit,
    [ValidateRange(1, 100000000)] [int] $ExpectedMaxLife,
    [string] $LogDirectory,
    [switch] $AllowAutomation,
    [switch] $StartExchangeTrader
)

$ErrorActionPreference = 'Stop'
$runtime = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$overlay = Join-Path $runtime 'GameHelper.exe'
if (-not (Test-Path -LiteralPath $overlay -PathType Leaf)) { throw "GameHelper.exe is missing in $runtime" }
$metadata = Join-Path $runtime 'configs/plugins.json'
$coreSettings = Join-Path $runtime 'configs/core_settings.json'
if ($StartExchangeTrader) {
    if (-not $AllowAutomation) { throw '-StartExchangeTrader requires -AllowAutomation.' }
    $saved = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json -AsHashtable
    if ($saved.ExchangeTrader.Enable -ne $true) { throw 'Enable ExchangeTrader in this profile before requesting a sale.' }
}
if (-not $AllowAutomation -and (Test-Path -LiteralPath $coreSettings -PathType Leaf)) {
    $savedCore = Get-Content -LiteralPath $coreSettings -Raw | ConvertFrom-Json -AsHashtable
    if ($savedCore.AutomateNpcQuests -eq $true) {
        throw 'The saved profile enables NPC dialogue automation. Disable it in the profile or explicitly pass -AllowAutomation.'
    }
}
if (-not $AllowAutomation -and (Test-Path -LiteralPath $metadata -PathType Leaf)) {
    $saved = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in 'AutoHotKeyTrigger', 'PickupHelper', 'ExchangeTrader') {
        if ($saved[$name].Enable -eq $true) {
            throw "The saved profile enables $name. Disable it in the profile or explicitly pass -AllowAutomation."
        }
    }
}

$tokenDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GameHelper2.Dev'
[IO.Directory]::CreateDirectory($tokenDirectory) | Out-Null
$tokenFile = Join-Path $tokenDirectory 'mcp-token'
if (-not (Test-Path -LiteralPath $tokenFile -PathType Leaf)) {
    $temporaryToken = "$tokenFile.$([Guid]::NewGuid().ToString('N'))"
    try {
        $newToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        [IO.File]::WriteAllText($temporaryToken, $newToken)
        try { [IO.File]::Move($temporaryToken, $tokenFile) }
        catch [IO.IOException] {
            if (-not (Test-Path -LiteralPath $tokenFile -PathType Leaf)) { throw }
        }
    } finally { [IO.File]::Delete($temporaryToken) }
}
if ([IO.File]::ReadAllText($tokenFile).Trim() -cnotmatch '\A[0-9A-F]{64}\z') {
    throw "Invalid MCP token file. Rename it and retry: $tokenFile"
}

$processOptions = @{
    FilePath = $overlay
    WorkingDirectory = $runtime
    WindowStyle = 'Hidden'
    PassThru = $true
}
if ($LogDirectory) {
    $log = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogDirectory)).FullName
    $logName = "GameHelper-$([Guid]::NewGuid().ToString('N'))"
    $processOptions.RedirectStandardOutput = Join-Path $log "$logName.stdout.log"
    $processOptions.RedirectStandardError = Join-Path $log "$logName.stderr.log"
}
if ($OffsetDump) {
    $dumpPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OffsetDump)
    if (Test-Path -LiteralPath $dumpPath) { throw "Choose a new offset dump path: $dumpPath" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dumpPath)) | Out-Null
}
$previousDump = $env:GH_OFFSET_DUMP
$previousSmoke = $env:GH_PLUGIN_SMOKE_REPORT
$previousStructureAudit = $env:GH_STRUCTURE_AUDIT
$previousMaxLife = $env:GH_STRUCTURE_MAX_LIFE
$previousTradeStart = $env:GH_EXCHANGE_TRADER_START
try {
    $env:GH_EXCHANGE_TRADER_START = if ($StartExchangeTrader) { '1' } else { $null }
    if ($StructureAudit -and -not $OffsetDump) { throw '-StructureAudit requires -OffsetDump.' }
    if ($ExpectedMaxLife -and -not $StructureAudit) { throw '-ExpectedMaxLife requires -StructureAudit.' }
    if ($StructureAudit) {
        $env:GH_STRUCTURE_AUDIT = '1'
        if ($ExpectedMaxLife) { $env:GH_STRUCTURE_MAX_LIFE = [string]$ExpectedMaxLife }
    }
    if ($OffsetDump) {
        $env:GH_OFFSET_DUMP = $dumpPath
    }
    if ($SmokeReport) {
        $smokePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SmokeReport)
        if (Test-Path -LiteralPath $smokePath) { throw "Choose a new smoke report path: $smokePath" }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($smokePath)) | Out-Null
        $env:GH_PLUGIN_SMOKE_REPORT = $smokePath
    }
    Start-Process @processOptions
} finally {
    $env:GH_OFFSET_DUMP = $previousDump
    $env:GH_PLUGIN_SMOKE_REPORT = $previousSmoke
    $env:GH_STRUCTURE_AUDIT = $previousStructureAudit
    $env:GH_STRUCTURE_MAX_LIFE = $previousMaxLife
    $env:GH_EXCHANGE_TRADER_START = $previousTradeStart
}
