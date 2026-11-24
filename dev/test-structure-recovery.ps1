#Requires -Version 7.2
[CmdletBinding()]
param([string] $ClientExe, [string] $Revision = 'HEAD', [ValidateRange(1, 100000000)] [int] $ExpectedMaxLife)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'runtime.ps1')
. (Join-Path $PSScriptRoot 'recovery.ps1')
$id = [guid]::NewGuid().ToString('N').Substring(0, 10)
$output = [IO.Directory]::CreateDirectory((Join-Path $repo "artifacts/structure-audit/$id")).FullName
$tree = Join-Path $output 'source'
$report = [ordered]@{ revision = $Revision; worktree = $tree; fields = [ordered]@{} }
$fixtures = [ordered]@{
    'EntityOffsets.Id' = @('GameOffsets/Objects/States/InGameState/EntityOffsets.cs', 0x7C)
    'ActorOffset.ActiveSkillsPtr' = @('GameOffsets/Objects/Components/Actor.cs', 0xB60)
    'LifeOffset.Health' = @('GameOffsets/Objects/Components/Life.cs', 0x158)
    'RenderOffsets.CurrentWorldPosition' = @('GameOffsets/Objects/Components/Render.cs', 0x15C)
    'UiElementBaseOffset.Self' = @('GameOffsets/Objects/UiElement/UiElementBaseOffset.cs', 0x030)
    'TerrainStruct.TileDetailsPtr' = @('GameOffsets/Objects/States/InGameState/AreaInstanceOffsets.cs', 0x70)
}

function Set-TestField($path, $name, $offset) {
    $source = [IO.File]::ReadAllText($path)
    $pattern = '(?m)^(\s*\[FieldOffset\(0x)([0-9A-Fa-f]+)(\)\] public \w+(?:<\w+>)? ' + [regex]::Escape($name) + ';)'
    $matches = [regex]::Matches($source, $pattern)
    if ($matches.Count -ne 1) { throw "Could not isolate $name in $path" }
    $previous = [Convert]::ToInt32($matches[0].Groups[2].Value, 16)
    $updated = [regex]::Replace($source, $pattern, ('${1}' + ('{0:X}' -f $offset) + '${3}'))
    [IO.File]::WriteAllText($path, $updated)
    return $previous
}

function Build-TestRuntime($name) {
    $build = & "$tree/build.ps1" 6> (Join-Path $output "$name-build.log") | Select-Object -Last 1
    if (-not $build -or -not (Test-Path -LiteralPath $build -PathType Container)) { throw "No runtime for $name" }
    return $build
}

try {
    $commit = Invoke-Checked git @('-C', $repo, 'rev-parse', "$Revision^{commit}")
    $report.revision = $commit
    $client = Get-RecoveryClient -ClientExe $ClientExe
    $report.clientSha256 = $client.sha256
    $report.expectedMaxLife = if ($ExpectedMaxLife) { $ExpectedMaxLife }
    Invoke-Checked git @('-C', $repo, 'worktree', 'add', '--detach', $tree, $commit) | Out-Null
    $original = @{}
    foreach ($field in $fixtures.Keys) {
        $file, $offset = $fixtures[$field]
        $original[$field] = Set-TestField (Join-Path $tree $file) ($field.Split('.')[-1]) $offset
        if ($original[$field] -eq $offset) { throw "The test offset for $field equals the field's configured offset." }
    }

    $faultBuild = Build-TestRuntime 'faults'
    $trial = Invoke-RecoveryTrial $repo $output $faultBuild 'faults' $client.sha256 -StructureAudit -ExpectedMaxLife $ExpectedMaxLife
    $report.faultReport = $trial.file
    if (-not $trial.data.structureAudit.results) { throw 'No structure evidence in the fault runtime dump.' }

    foreach ($field in $fixtures.Keys) {
        $finding = $trial.data.structureAudit.results | Where-Object Field -CEQ $field
        $candidates = @($finding.Candidates)
        $leaders = @($candidates | Where-Object { $_.SupportingRoots -eq $candidates[0].SupportingRoots })
        $unique = $leaders.Count -eq 1 -and $leaders[0].Offset -eq $original[$field] -and $leaders[0].Offset -ne $fixtures[$field][1]
        $report.fields[$field] = [ordered]@{ injected = $fixtures[$field][1]; reference = $original[$field]
            sampledRoots = $finding.SampledRoots; candidates = $candidates; uniqueCorrect = $unique }
        # Restore the recorded reference for ambiguous fields so they cannot block checking
        # the fields the search actually recovered. Those cases remain unconfirmed.
        $repaired = if ($unique) { $leaders[0].Offset } else { $original[$field] }
        [void](Set-TestField (Join-Path $tree $fixtures[$field][0]) ($field.Split('.')[-1]) $repaired)
    }

    $fixedBuild = Build-TestRuntime 'repaired'
    $trial = Invoke-RecoveryTrial $repo $output $fixedBuild 'repaired' $client.sha256 -StructureAudit -ExpectedMaxLife $ExpectedMaxLife
    $report.repairedReport = $trial.file
    $before = Get-Content -LiteralPath $report.faultReport -Raw | ConvertFrom-Json -AsHashtable
    $report.sameAreaInstance = $before.clientPid -eq $trial.data.clientPid -and
        $before.structureAudit.areaId -ceq $trial.data.structureAudit.areaId -and
        $before.structureAudit.areaHash -ceq $trial.data.structureAudit.areaHash
    foreach ($field in $fixtures.Keys) {
        $after = $trial.data.structureAudit.results | Where-Object Field -CEQ $field
        $candidates = @($after.Candidates)
        $leaders = @($candidates | Where-Object { $_.SupportingRoots -eq $candidates[0].SupportingRoots })
        $report.fields[$field].confirmed = $report.fields[$field].uniqueCorrect -and $leaders.Count -eq 1 -and
            $leaders[0].Offset -eq $original[$field] -and $report.sameAreaInstance
        $report.fields[$field].repairedEvidence = $after
    }
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    $path = Join-Path $output 'structure-audit.json'
    Write-RecoveryJson $path $report
    Write-Output $path
}
