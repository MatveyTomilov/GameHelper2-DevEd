#Requires -Version 7.2
[CmdletBinding()]
param([string] $ClientExe, [string] $RuntimeDirectory, [switch] $Offline)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'runtime.ps1')
. (Join-Path $PSScriptRoot 'recovery.ps1')

$stamp = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$output = [IO.Directory]::CreateDirectory((Join-Path $repo "artifacts/rehearsals/$stamp")).FullName
$tree = Join-Path $output 'worktree'
$branch = "recovery/rehearsal-$stamp"
$reportPath = Join-Path $output 'rehearsal.json'
$summary = [ordered] @{ branch = $branch; worktree = $tree; static = 'NOT TESTED'; structures = 'NOT TESTED'; rollback = 'NOT TESTED' }
$signatureFile = 'GameOffsets/StaticOffsetsPatterns.cs'
$areaFile = 'GameOffsets/Objects/States/InGameState/AreaInstanceOffsets.cs'

function Get-HistoricalFile {
    param([string] $Revision, [string] $File)
    (Invoke-Checked git @('-C', $repo, 'show', "${Revision}:$File")) -join "`n"
}

function Get-RecordedMask {
    param([string] $Source, [string] $Name)
    $pattern = '"' + [regex]::Escape($Name) + '"\s*,\s*(?://[^\r\n]*\r?\n\s*)*"([A-Fa-f0-9?^ ]+)"'
    $found = [regex]::Matches($Source, $pattern)
    if ($found.Count -ne 1) { throw "Could not isolate historical mask for $Name." }
    $found[0].Groups[1].Value
}

function Get-RecordedField {
    param([string] $Source, [string] $Name)
    $pattern = '\[FieldOffset\(0x[0-9A-Fa-f]+\)\] public \w+ ' + [regex]::Escape($Name) + ';'
    $found = [regex]::Matches($Source, $pattern)
    if ($found.Count -ne 1) { throw "Could not isolate historical field $Name." }
    $found[0].Value
}

function Save-RehearsalCommit {
    param([string] $Message, [string[]] $Files)
    Invoke-Checked git (@('-C', $tree, 'add', '--') + $Files) | Out-Null
    Invoke-Checked git @('-C', $tree, '-c', 'user.name=GameHelper2 Developer Edition', '-c', 'user.email=gamehelper2-dev@localhost', 'commit', '-m', $Message) | Out-Null
    Invoke-Checked git @('-C', $tree, 'rev-parse', 'HEAD')
}

try {
    if (Invoke-Checked git @('-C', $repo, 'status', '--porcelain', '--untracked-files=normal')) {
        throw 'Commit the recovery tooling before creating the rehearsal worktree.'
    }
    $clean = Invoke-Checked git @('-C', $repo, 'rev-parse', 'HEAD')
    $client = Get-RecoveryClient -ClientExe $ClientExe
    if ($client.sha256 -ne 'DFC10EF7E4E4411DF7418779C5467D804DEF225E80814BE6EE88F96CBD10DCCA') {
        throw 'This historical fault fixture requires the verified Steam 0.5.5h executable. Pass its saved copy and -Offline for static replay.'
    }
    $copy = Join-Path $output (Split-Path -Leaf $client.path)
    [IO.File]::Copy($client.path, $copy, $false)
    if ((Get-FileHash -LiteralPath $copy).Hash -ne $client.sha256) { throw 'The client changed while copying.' }
    $summary.client = $client

    if (-not $RuntimeDirectory) { $RuntimeDirectory = (Get-Content -LiteralPath (Join-Path $repo 'artifacts/active-runtime.txt') -Raw).Trim() }
    $RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
    $tools = Get-RecoveryTools $repo
    & "$repo/preflight.ps1" -GhidraHome $tools.ghidra -JdkHome $tools.jdk -RequireGhidra | Out-Null
    Invoke-Checked git @('-C', $repo, 'worktree', 'add', '-b', $branch, $tree, $clean) | Out-Null

    $staleSignatures = Get-Content -LiteralPath (Join-Path $tree $signatureFile) -Raw
    foreach ($item in @(@{ name = 'Game States'; revision = 'a99b8ba^' }, @{ name = 'AreaChangeCounter'; revision = '3f1b541^' })) {
        $mask = Get-RecordedMask $staleSignatures $item.name
        $old = Get-RecordedMask (Get-HistoricalFile $item.revision $signatureFile) $item.name
        if ($mask -eq $old) { throw "The baseline already contains the obsolete $($item.name) mask." }
        $staleSignatures = $staleSignatures.Replace($mask, $old)
    }
    [IO.File]::WriteAllText((Join-Path $tree $signatureFile), $staleSignatures)

    $staleArea = Get-Content -LiteralPath (Join-Path $tree $areaFile) -Raw
    foreach ($item in @(@{ name = 'Environments'; revision = '3293e56^' }, @{ name = 'PlayerInfo'; revision = '3f1b541^' })) {
        $field = Get-RecordedField $staleArea $item.name
        $old = Get-RecordedField (Get-HistoricalFile $item.revision $areaFile) $item.name
        if ($field -eq $old) { throw "The baseline already contains the obsolete $($item.name) field." }
        $staleArea = $staleArea.Replace($field, $old)
    }
    [IO.File]::WriteAllText((Join-Path $tree $areaFile), $staleArea)
    $summary.faultCommit = Save-RehearsalCommit 'Rehearse two historical signatures and an older area layout' @($signatureFile, $areaFile)

    $before = Join-Path $output 'stale-signatures'
    try { & "$tree/diagnose.ps1" -ClientExe $copy -ReportDirectory $before -Offline | Out-Null } catch { $summary.staticDetection = $_.Exception.Message }
    $broken = Get-Content -LiteralPath (Join-Path $before 'signatures.json') -Raw | ConvertFrom-Json -AsHashtable
    $badNames = @($broken.patterns | Where-Object { $_.Count -ne 1 -or -not $_.Candidates[0].TargetValid } | ForEach-Object Name)
    if ($badNames.Count -ne 2 -or 'Game States' -notin $badNames -or 'AreaChangeCounter' -notin $badNames) {
        throw 'The scanner did not isolate the two historical signature faults.'
    }
    $summary.failedSignatures = $badNames

    Invoke-Checked git @('-C', $tree, 'restore', '--source', $clean, '--', $signatureFile) | Out-Null
    $summary.signatureRepairCommit = Save-RehearsalCommit 'Restore independently checked static signatures' @($signatureFile)
    $after = Join-Path $output 'repaired-signatures'
    & "$tree/diagnose.ps1" -ClientExe $copy -ReportDirectory $after -Offline -GhidraHome $tools.ghidra -JdkHome $tools.jdk | Out-Null

    # Keep the unique byte match but point the displacement marker at the call, which targets code.
    $stateMask = Get-RecordedMask (Get-HistoricalFile $clean $signatureFile) 'Game States'
    $callMask = $stateMask.Replace('^ ', '').Replace('E8 ?? ?? ?? ??', 'E8 ^ ?? ?? ?? ??')
    $wrongTargetPatterns = Join-Path $output 'wrong-target-patterns.json'
    $wrongTargetReport = Join-Path $output 'wrong-target-signatures.json'
    Write-RecoveryJson $wrongTargetPatterns @(@{ name = 'Game States'; pattern = $callMask })
    $wrongTarget = Test-RecoveryPatternFile $tree $copy $wrongTargetPatterns $wrongTargetReport
    $wrongResults = Get-Content -LiteralPath $wrongTargetReport -Raw | ConvertFrom-Json -AsHashtable
    $stateCall = $wrongResults.patterns | Where-Object Name -CEQ 'Game States'
    if ($wrongTarget.staticExitCode -ne 1 -or $stateCall.Count -ne 1 -or
        $stateCall.Candidates[0].TargetSection -cne '.text' -or $stateCall.Candidates[0].TargetValid) {
        throw 'The scanner did not reject the unique signature that points to a code section.'
    }
    $summary.wrongTarget = $wrongTargetReport
    $summary.static = 'READY'

    if (-not $Offline) {
        $build = & "$tree/build.ps1" 6> (Join-Path $output 'stale-structure-build.log') | Select-Object -Last 1
        try {
            $trial = Invoke-RecoveryTrial $repo $output $build 'stale-structure' $client.sha256
            $area = $trial.data.probes | Where-Object Name -eq 'AreaInstanceOffsets'
            $summary.staleArea = @{ report = $trial.file; verdict = $area.Verdict; candidates = $area.Recoveries; fields = $area.fields }
            if ($area.Verdict -ne 'Degraded') { throw 'The old structure did not manifest a semantic failure in this area.' }
            $summary.structures = 'PARTIAL'
        } catch { $summary.structures = 'BLOCKED'; $summary.structureError = $_.Exception.Message }
    }

    Invoke-Checked git @('-C', $tree, 'restore', '--source', $clean, '--', $areaFile) | Out-Null
    $summary.structureRepairCommit = Save-RehearsalCommit 'Restore the live area structure layout' @($areaFile)
    $stagedReport = & "$repo/recover.ps1" -ClientExe $copy -RuntimeDirectory $RuntimeDirectory -CandidateSource $tree -Offline
    $candidate = (Get-Content -LiteralPath ($stagedReport | Select-Object -Last 1) -Raw | ConvertFrom-Json -AsHashtable).candidate
    if (-not $candidate.runtime) { throw 'The repaired candidate was not staged.' }
    $summary.candidate = $candidate

    if (-not $Offline) {
        try {
            $trial = Invoke-RecoveryTrial $repo $output $candidate.build 'repaired-structure' $client.sha256
            $area = $trial.data.probes | Where-Object Name -eq 'AreaInstanceOffsets'
            $summary.repairedArea = @{ report = $trial.file; verdict = $area.Verdict; fields = $area.fields }
            if ($area.Verdict -ne 'Intact') { throw 'The repaired live area layout did not pass the sweep.' }
            if ($summary.staleArea.verdict -eq 'Degraded') { $summary.structures = 'READY' }
        } catch { $summary.structureError = $_.Exception.Message }

        try {
            $trial = Invoke-RecoveryTrial $repo $output $RuntimeDirectory 'rollback-launch' $client.sha256
            $summary.rollback = 'READY'
            $summary.rollbackDump = $trial.file
        } catch { $summary.rollback = 'BLOCKED'; $summary.rollbackError = $_.Exception.Message }
    }
} catch {
    $summary.failure = $_.Exception.Message
    throw
} finally {
    Write-RecoveryJson $reportPath $summary
    Write-Output $reportPath
}
