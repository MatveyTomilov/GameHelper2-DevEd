#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $ClientExe,
    [string] $RuntimeDirectory,
    [string] $BaselineReport,
    [string] $OffsetDump,
    [string] $CandidatePatterns,
    [string] $GhidraHome,
    [string] $JdkHome,
    [switch] $InstallTools,
    [switch] $Offline,
    [switch] $Framework,
    [switch] $NewWorktree,
    [string] $CandidateSource,
    [switch] $TrialCandidate,
    [switch] $SaveBaseline,
    [ValidateRange(30, 1800)] [int] $AnalysisTimeoutSeconds = 300,
    [ValidateSet('Standard', 'Recipe')] [string] $FrameworkAnalysisProfile = 'Standard',
    [ValidateRange(60, 1200)] [int] $FrameworkPostTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev/runtime.ps1')
. (Join-Path $PSScriptRoot 'dev/recovery.ps1')

$store = [IO.Directory]::CreateDirectory((Join-Path $PSScriptRoot 'artifacts/recovery')).FullName
$stamp = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$folder = [IO.Directory]::CreateDirectory((Join-Path $store $stamp)).FullName
$baselinePointer = Join-Path $store 'baseline.txt'
$reportFile = Join-Path $folder 'recovery.json'
$report = [ordered] @{
    schema = 1
    timestamp = [DateTimeOffset]::Now.ToString('o')
    sourceCommit = $null
    client = $null
    baselineReport = $null
    backup = $null
    phases = [ordered] @{ signatures = 'NOT TESTED'; structures = 'NOT TESTED'; liveCore = 'NOT TESTED'; plugins = 'NOT TESTED' }
    findings = [Collections.Generic.List[string]]::new()
}

try {
    if ($TrialCandidate -and (-not $CandidateSource -or $Offline)) {
        throw 'Use -TrialCandidate with -CandidateSource and a running game.'
    }
    $report.sourceCommit = Invoke-Checked git @('-C', $PSScriptRoot, 'rev-parse', 'HEAD')
    if (-not $BaselineReport -and (Test-Path -LiteralPath $baselinePointer -PathType Leaf)) {
        $BaselineReport = (Get-Content -LiteralPath $baselinePointer -Raw).Trim()
    }
    $baseline = if ($BaselineReport) {
        $report.baselineReport = (Resolve-Path -LiteralPath $BaselineReport).Path
        Get-Content -LiteralPath $report.baselineReport -Raw | ConvertFrom-Json -AsHashtable
    }

    $report.client = Get-RecoveryClient -ClientExe $ClientExe -Baseline $baseline
    $clientCopy = Join-Path $folder (Split-Path -Leaf $report.client.path)
    [IO.File]::Copy($report.client.path, $clientCopy, $false)
    if ((Get-FileHash -LiteralPath $clientCopy).Hash -ne $report.client.sha256 -or
        (Get-FileHash -LiteralPath $report.client.path).Hash -ne $report.client.sha256) {
        throw 'The client changed while it was being copied. Wait for Steam and retry.'
    }
    $report.client.copy = $clientCopy
    if (-not $report.client.version) { $report.findings.Add('A current startup log with the client version is unavailable. Use the build ID and SHA-256.') }

    if (-not $RuntimeDirectory) {
        $active = Join-Path $PSScriptRoot 'artifacts/active-runtime.txt'
        if (Test-Path -LiteralPath $active -PathType Leaf) { $RuntimeDirectory = (Get-Content -LiteralPath $active -Raw).Trim() }
    }
    if ($RuntimeDirectory) {
        $RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
        $report.runtime = $RuntimeDirectory
        try {
            $saved = @(& "$PSScriptRoot/patch-day.ps1" -InstalledDirectory $RuntimeDirectory -ClientExe $clientCopy)
            $report.backup = ($saved | Where-Object { $_ -like 'Backup: *' }).Substring(8)
        } catch {
            $report.findings.Add("Backup: $($_.Exception.Message)")
        }
    } else {
        $report.findings.Add('No active runtime was found. Pass -RuntimeDirectory to preserve or assess an installation.')
    }

    $tools = Get-RecoveryTools -Repository $PSScriptRoot -Install:$InstallTools
    if (-not $GhidraHome) { $GhidraHome = if ($tools.ghidra) { $tools.ghidra } else { $env:GHIDRA_HOME } }
    if (-not $JdkHome) { $JdkHome = if ($tools.jdk) { $tools.jdk } else { $env:JAVA_HOME } }
    $report.environment = & "$PSScriptRoot/preflight.ps1" -GhidraHome $GhidraHome -JdkHome $JdkHome

    $diagnostics = Join-Path $folder 'diagnostics'
    $doctor = @{ ClientExe = $clientCopy; ReportDirectory = $diagnostics; Offline = $true }
    if ($RuntimeDirectory) { $doctor.RuntimeDirectory = $RuntimeDirectory }
    if ($report.environment.ghidraConfigured) {
        $doctor.GhidraHome = $GhidraHome
        $doctor.JdkHome = $JdkHome
    }
    try { & "$PSScriptRoot/diagnose.ps1" @doctor | Out-Null } catch { $report.findings.Add($_.Exception.Message) }
    $report.diagnostics = Get-Content -LiteralPath (Join-Path $diagnostics 'report.json') -Raw | ConvertFrom-Json -AsHashtable
    $report.signatureReport = Join-Path $diagnostics 'signatures.json'
    if (Test-Path -LiteralPath $report.signatureReport -PathType Leaf) {
        $static = Get-Content -LiteralPath $report.signatureReport -Raw | ConvertFrom-Json -AsHashtable
        $report.phases.signatures = if ($static.unique -and $report.diagnostics.ghidra -eq 'PASS') { 'READY' }
            elseif ($static.unique -or $static.patterns.Count -gt 0) { 'PARTIAL' } else { 'BLOCKED' }
        $report.signatures = Compare-RecoverySignatures -Current $static -Baseline $baseline
        if (-not $static.unique) { $report.findings.Add('One or more static signatures need review before candidate launch.') }
    } else { $report.phases.signatures = 'BLOCKED' }

    if ($Framework) {
        if ($report.environment.ghidraConfigured) {
            try {
                $report.framework = Invoke-RecoveryFramework -Repository $PSScriptRoot -Output $folder -ClientCopy $clientCopy `
                    -Ghidra $GhidraHome -Jdk $JdkHome -TimeoutSeconds $AnalysisTimeoutSeconds `
                    -PostTimeoutSeconds $FrameworkPostTimeoutSeconds -AnalysisProfile $FrameworkAnalysisProfile `
                    -KnownSites $report.signatureReport
                $knownNames = @($static.patterns.Name)
                $suggestions = @($report.framework.candidates | Where-Object { $_.name -cin $knownNames })
                if ($suggestions.Count) {
                    $path = Join-Path $folder 'framework-patterns.json'
                    Write-RecoveryJson $path $suggestions
                    $report.framework.validation = Test-RecoveryPatternFile $PSScriptRoot $clientCopy $path `
                        (Join-Path $folder 'framework-signatures.json')
                    if ($report.framework.validation.staticExitCode -in 0, 1) {
                        $rescanned = Get-Content -LiteralPath $report.framework.validation.report -Raw | ConvertFrom-Json -AsHashtable
                        $report.framework.validation.targets = @(Compare-RecoveryFrameworkTargets $suggestions $static $rescanned)
                    }
                }
            } catch { $report.framework = @{ status = 'BLOCKED'; reason = $_.Exception.Message } }
        } else { $report.framework = @{ status = 'BLOCKED'; reason = 'Ghidra and JDK are unavailable.' } }
    }

    if ($CandidatePatterns) {
        $candidateInput = (Resolve-Path -LiteralPath $CandidatePatterns).Path
        $candidateOutput = Join-Path $folder 'candidate-signatures.json'
        $report.candidatePatterns = Test-RecoveryPatternFile $PSScriptRoot $clientCopy $candidateInput $candidateOutput
    }

    if ($OffsetDump) {
        $dump = (Resolve-Path -LiteralPath $OffsetDump).Path
        $report.offsetDump = Import-RecoveryOffsetDump -Path $dump -Output $folder -ExpectedHash $report.client.sha256
        $report.phases.structures = 'PARTIAL'
    }

    if (-not $Offline) {
        Read-RecoveryLive -Repository $PSScriptRoot -Output $folder -Report $report
    }

    if ($NewWorktree) {
        $worktree = Join-Path $store "worktrees/$stamp"
        [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $worktree))
        $branch = "recovery/$stamp"
        Invoke-Checked git @('-C', $PSScriptRoot, 'worktree', 'add', '-b', $branch, $worktree, $report.sourceCommit) | Out-Null
        $report.repair = @{ branch = $branch; worktree = $worktree }
    }
    if ($CandidateSource) {
        if (-not $RuntimeDirectory -or -not $report.backup) { throw 'A verified runtime backup is required before staging a candidate.' }
        $report.candidate = Build-RecoveryCandidate -Repository $PSScriptRoot -Source $CandidateSource -Output $folder `
            -ClientCopy $clientCopy -InstalledDirectory $RuntimeDirectory -Ghidra $doctor.GhidraHome -Jdk $doctor.JdkHome
        if ($TrialCandidate) {
            try {
                $checked = Get-Content -LiteralPath (Join-Path $report.candidate.diagnostics 'signatures.json') -Raw | ConvertFrom-Json
                $trial = Invoke-RecoveryTrial $PSScriptRoot $folder $report.candidate.runtime 'candidate-trial' $report.client.sha256 $checked.patterns.Count
                $report.candidate.trial = Import-RecoveryOffsetDump $trial.file $folder $report.client.sha256
            } catch { $report.candidate.trial = @{ status = 'BLOCKED'; reason = $_.Exception.Message } }
        }
    }
    if ($SaveBaseline) {
        if ($report.phases.signatures -ne 'READY' -or $report.phases.liveCore -ne 'READY' -or -not $report.backup) {
            throw 'Recording a baseline requires a verified backup, Ghidra signatures and live core checks.'
        }
        $report.baselineSaved = $true
    }
} catch {
    $report.failure = $_.Exception.Message
    throw
} finally {
    Write-RecoveryJson $reportFile $report
    if ($report.baselineSaved) {
        $pending = Join-Path $store ".$stamp.baseline"
        [IO.File]::WriteAllText($pending, $reportFile)
        [IO.File]::Move($pending, $baselinePointer, $true)
    }
    Write-Output $reportFile
}
