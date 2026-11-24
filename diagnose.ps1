#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $RuntimeDirectory,
    [string] $ClientExe,
    [string] $GhidraHome,
    [string] $JdkHome,
    [string] $ReportDirectory,
    [switch] $Offline,
    [switch] $FullGhidraAnalysis,
    [ValidateRange(30, 1800)] [int] $AnalysisTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev/runtime.ps1')

if ($RuntimeDirectory) { $RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path }
if ($ClientExe) { $ClientExe = (Resolve-Path -LiteralPath $ClientExe).Path }
if ($GhidraHome) { $GhidraHome = (Resolve-Path -LiteralPath $GhidraHome).Path }
if ($JdkHome) { $JdkHome = (Resolve-Path -LiteralPath $JdkHome).Path }

if ($ReportDirectory) {
    $root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportDirectory)
    if (Test-Path -LiteralPath $root) { throw 'Choose a new report directory.' }
} else {
    $root = Join-Path $PSScriptRoot ("artifacts/diagnostics/{0}-{1}" -f
        (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
[void] [IO.Directory]::CreateDirectory($root)
$result = [ordered] @{
    timestamp = [DateTimeOffset]::Now.ToString('o')
    integrity = 'NOT_RUN'
    process = 'NOT_RUN'
    signatures = 'NOT_RUN'
    mcp = 'NOT_RUN'
    ghidra = 'NOT_RUN'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    if ($RuntimeDirectory) {
        try {
            $manifest = Read-RuntimeManifest $RuntimeDirectory -AllowProfileChanges
            $result.integrity = 'PASS'
            $result.buildId = $manifest.buildId
            $candidate = Join-Path $RuntimeDirectory 'GameHelper.exe'
            $result.process = if (Get-Process -Name GameHelper -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq $candidate }) { 'RUNNING' } else { 'STOPPED' }
        } catch {
            $result.integrity = 'FAIL'
            $result.integrityError = $_.Exception.Message
        }
    }
    if ($ClientExe) {
        try {
            $project = 'dev/SignatureCheck/SignatureCheck.csproj'
            Invoke-Checked dotnet @('restore', $project, '--locked-mode', '--configfile', 'NuGet.config') |
                Set-Content -LiteralPath (Join-Path $root 'scanner-build.log')
            Invoke-Checked dotnet @('build', $project, '--no-restore', '-c', 'Release') |
                Add-Content -LiteralPath (Join-Path $root 'scanner-build.log')
            & dotnet 'dev/SignatureCheck/bin/Release/net10.0-windows/win-x64/SignatureCheck.dll' $ClientExe |
                Set-Content -LiteralPath (Join-Path $root 'signatures.json')
            $result.signatures = switch ($LASTEXITCODE) {
                0 { 'PASS' }
                1 { 'FAIL' }
                default { 'BLOCKED' }
            }
        } catch {
            $result.signatures = 'BLOCKED'
            $result.signaturesError = $_.Exception.Message
        }
    }

    $tokenPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GameHelper2.Dev/mcp-token'
    if ($Offline) {
        $result.mcp = 'OFFLINE'
    } elseif (Test-Path -LiteralPath $tokenPath -PathType Leaf) {
        try {
            $body = '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}'
            $headers = @{
                Authorization = "Bearer $([IO.File]::ReadAllText($tokenPath).Trim())"
                Accept = 'application/json, text/event-stream'
            }
            $request = @{
                Method = 'Post'
                Uri = 'http://127.0.0.1:3002/'
                NoProxy = $true
                MaximumRedirection = 0
                ContentType = 'application/json'
                TimeoutSec = 7
                Body = $body
                Headers = $headers
            }
            $response = Invoke-RestMethod @request
            if ($response.error -or $response.result.isError) { throw 'Bridge returned a tool error.' }
            $state = $response.result.content[0].text | ConvertFrom-Json
            $result.mcp = 'PASS'
            $result.attached = $state.attached -eq $true
            $result.areaLoaded = $state.areaLoaded -eq $true
            $result.gameState = $state.gameState
        } catch {
            $result.mcp = 'BLOCKED'
            $result.mcpError = 'The authenticated local status call failed.'
        }
    } else { $result.mcp = 'BLOCKED' }

    if ($GhidraHome -or $JdkHome -or $FullGhidraAnalysis) {
        $previousJdk = $env:JAVA_HOME
        try {
            if (-not $ClientExe) { throw 'Pass -ClientExe to import an executable.' }
            & ./preflight.ps1 -GhidraHome $GhidraHome -JdkHome $JdkHome -RequireGhidra | Out-Null
            if (-not $GhidraHome) { $GhidraHome = $env:GHIDRA_HOME }
            if ($JdkHome) { $env:JAVA_HOME = $JdkHome }
            $headless = Join-Path $GhidraHome 'support/analyzeHeadless.bat'
            $projectDir = Join-Path $root 'ghidra'
            [void] [IO.Directory]::CreateDirectory($projectDir)
            $consoleLog = Join-Path $root 'ghidra-console.log'
            $headlessArgs = @($projectDir, 'poe2', '-import', $ClientExe,
                              '-max-cpu', '2', '-log', (Join-Path $root 'ghidra.log'))
            $ghidraReport = Join-Path $root 'ghidra-signatures.json'
            if ($FullGhidraAnalysis) {
                $headlessArgs += @('-analysisTimeoutPerFile', $AnalysisTimeoutSeconds)
            } else {
                $headlessArgs += @('-noanalysis', '-scriptPath', (Join-Path $PSScriptRoot 'dev/ghidra'),
                    '-postScript', 'VerifySignatureSites.java', (Join-Path $root 'signatures.json'), $ghidraReport)
            }
            & $headless @headlessArgs *> $consoleLog
            if ($LASTEXITCODE) { throw "Headless analysis exited with code $LASTEXITCODE." }
            if (-not (Select-String -LiteralPath $consoleLog -SimpleMatch 'Import succeeded' -Quiet)) {
                throw 'Ghidra did not confirm import. See ghidra-console.log.'
            }
            $result.ghidra = if (-not $FullGhidraAnalysis) {
                if (-not (Test-Path -LiteralPath $ghidraReport -PathType Leaf)) {
                    throw 'Ghidra did not produce the targeted signature report.'
                }
                $decoded = Get-Content -LiteralPath $ghidraReport -Raw | ConvertFrom-Json
                if ($decoded.allConfirmed) { 'PASS' } else { 'REVIEW' }
            } elseif (Select-String -LiteralPath $consoleLog -Pattern 'analysis timed out|analysis timeout occurred' -Quiet) {
                'IMPORTED_ANALYSIS_TIMEOUT'
            } elseif (Select-String -LiteralPath $consoleLog -SimpleMatch 'Analysis succeeded' -Quiet) {
                'PASS'
            } else { 'IMPORTED_ANALYSIS_UNCONFIRMED' }
        } catch {
            $result.ghidra = 'BLOCKED'
            $result.ghidraError = $_.Exception.Message
        } finally { $env:JAVA_HOME = $previousJdk }
    }
} finally {
    Pop-Location
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'report.json')
    Write-Output $root
}
if ($result.integrity -eq 'FAIL' -or $result.signatures -eq 'FAIL' -or
    ($ClientExe -and $result.signatures -ne 'PASS') -or
    (($GhidraHome -or $JdkHome -or $FullGhidraAnalysis) -and $result.ghidra -ne 'PASS')) {
    throw "An explicitly requested offline check did not pass. See $root/report.json."
}
