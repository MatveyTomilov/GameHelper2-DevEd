$ErrorActionPreference = 'Stop'

function Write-RecoveryJson {
    param([string] $Path, $Value)
    ConvertTo-Json -InputObject $Value -Depth 64 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-RecoveryTools {
    param([string] $Repository, [switch] $Install)

    $pins = Get-Content -LiteralPath (Join-Path $Repository 'dev/tools.json') -Raw | ConvertFrom-Json
    $cache = Join-Path $Repository '.upstream/tools'
    $paths = [ordered] @{}
    foreach ($name in 'ghidra', 'jdk') {
        $pin = $pins.$name
        $target = Join-Path $cache $pin.directory
        if ($Install -and -not (Test-Path -LiteralPath $target -PathType Container)) {
            [void] [IO.Directory]::CreateDirectory($cache)
            $file = Join-Path $cache ([uri]$pin.url).Segments[-1]
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                $download = "$file.$([Guid]::NewGuid().ToString('N')).partial"
                try {
                    Invoke-WebRequest -Uri $pin.url -OutFile $download
                    if ((Get-FileHash -LiteralPath $download).Hash -ne $pin.sha256) { throw "SHA-256 mismatch for $name." }
                    [IO.File]::Move($download, $file)
                } finally { [IO.File]::Delete($download) }
            }

            if ((Get-FileHash -LiteralPath $file).Hash -ne $pin.sha256) { throw "SHA-256 mismatch for $name." }
            Invoke-Checked tar.exe @('-xf', $file, '-C', $cache) | Out-Null
        }

        $paths[$name] = if (Test-Path -LiteralPath $target -PathType Container) { $target }
    }

    $paths
}

function Get-RecoveryClient {
    param([string] $ClientExe, $Baseline)

    if (-not $ClientExe) {
        $running = @(Get-Process -Name 'PathOfExileSteam', 'PathOfExile', 'PathOfExile_x64', 'PathOfExileEGS', 'PathOfExile_KG' -ErrorAction SilentlyContinue |
            ForEach-Object Path | Where-Object { $_ } | Sort-Object -Unique)
        if ($running.Count -gt 1) { throw 'Multiple clients are running. Pass -ClientExe for the client to assess.' }
        $ClientExe = if ($running.Count -eq 1) { $running[0] } elseif ($Baseline) { $Baseline.client.path }
    }

    if (-not $ClientExe) { throw 'Start the client once or pass -ClientExe to diagnose a stopped installation.' }
    $file = Get-Item -LiteralPath $ClientExe
    if ($file.PSIsContainer -or $file.Extension -ne '.exe') { throw 'Pass the x64 client executable.' }

    $buildId = $null
    if ($file.Name -eq 'PathOfExileSteam.exe' -and $file.Directory.Parent -and $file.Directory.Parent.Parent) {
        $manifest = Join-Path $file.Directory.Parent.Parent.FullName 'appmanifest_2694490.acf'
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            $install = Get-Content -LiteralPath $manifest -Raw
            if ($install -match '(?m)^\s*"installdir"\s+"([^"]+)"' -and $Matches[1] -eq $file.Directory.Name -and
                $install -match '(?m)^\s*"buildid"\s+"([0-9]+)"') {
                $buildId = $Matches[1]
            }
        }
    }

    $version = $null
    $log = Join-Path $file.DirectoryName 'logs/LatestClient.txt'
    if ((Test-Path -LiteralPath $log -PathType Leaf) -and (Get-Item -LiteralPath $log).LastWriteTimeUtc -ge $file.LastWriteTimeUtc) {
        $startup = Select-String -LiteralPath $log -Pattern '(?<date>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}).*User agent: PoE \S*/tags/(?<version>\d+\.\d+\.\d+[a-z]?)\b' |
            Select-Object -Last 1
        if ($startup) {
            $hit = $startup.Matches[0]
            $started = [datetime]::ParseExact($hit.Groups['date'].Value, 'yyyy/MM/dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
            if ($started -ge $file.LastWriteTime) { $version = $hit.Groups['version'].Value }
        }
    }

    [ordered] @{
        path = $file.FullName
        version = $version
        steamBuild = $buildId
        sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash
        modifiedUtc = $file.LastWriteTimeUtc.ToString('o')
    }
}

function Invoke-RecoveryFramework {
    param([string] $Repository, [string] $Output, [string] $ClientCopy, [string] $Ghidra, [string] $Jdk,
        [int] $TimeoutSeconds, [int] $PostTimeoutSeconds = 180, [string] $AnalysisProfile = 'Standard', [string] $KnownSites)

    $pin = (Get-Content -LiteralPath (Join-Path $Repository 'dev/tools.json') -Raw | ConvertFrom-Json).offsetRecovery
    $cache = Join-Path $Repository '.upstream/GameHelper2-Offset-Recovery-Framework'
    if (-not (Test-Path -LiteralPath (Join-Path $cache '.git') -PathType Container)) {
        Invoke-Checked git @('clone', '--no-checkout', $pin.url, $cache) | Out-Null
    }
    Invoke-Checked git @('-C', $cache, 'cat-file', '-e', "$($pin.commit)^{commit}") | Out-Null

    $directory = Join-Path $Output 'framework'
    $scripts = Join-Path $directory 'scripts'
    $projects = Join-Path $directory 'projects'
    [void] [IO.Directory]::CreateDirectory($projects)
    $zip = Join-Path $directory 'source.zip'
    Invoke-Checked git @('-C', $cache, 'archive', '--format=zip', "--output=$zip", $pin.commit, $pin.script, 'LICENSE', 'README.md') | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $scripts)
    foreach ($localScript in 'ConfigureFrameworkAnalysis.java', 'InspectFrameworkCoverage.java') {
        [IO.File]::Copy((Join-Path $Repository "dev/ghidra/$localScript"), (Join-Path $scripts $localScript), $false)
    }

    $console = Join-Path $directory 'console.log'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Environment['JAVA_HOME'] = $Jdk
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $Repository 'dev/run-recovery-framework.ps1'),
        '-Headless', (Join-Path $Ghidra 'support/analyzeHeadless.bat'), '-Projects', $projects,
        '-ClientCopy', $ClientCopy, '-TimeoutSeconds', $TimeoutSeconds.ToString(), '-Scripts', $scripts,
        '-Script', $pin.script, '-Log', (Join-Path $directory 'ghidra.log'), '-Console', $console,
        '-AnalysisProfile', $AnalysisProfile, '-KnownSites', $KnownSites)) {
        $start.ArgumentList.Add($argument)
    }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $deadlineExceeded = $false
    try {
        if (-not $process.WaitForExit(($TimeoutSeconds + $PostTimeoutSeconds) * 1000)) {
            $deadlineExceeded = $true
            if (-not $process.HasExited) {
                $process.Kill($true)
                [void] $process.WaitForExit(5000)
            }
        }
        $exitCode = if ($process.HasExited) { $process.ExitCode }
    } finally { $process.Dispose() }

    $lines = if (Test-Path -LiteralPath $console -PathType Leaf) { Get-Content -LiteralPath $console } else { @() }
    $summary = $lines | Select-String -Pattern 'Recovered:\s+(\d+)\s*/\s*(\d+)' | Select-Object -Last 1
    $completed = -not $deadlineExceeded -and $exitCode -eq 0 -and $null -ne $summary
    [ordered] @{
        status = if ($completed) { 'REVIEW' } else { 'BLOCKED' }
        commit = $pin.commit
        exitCode = $exitCode
        deadlineExceeded = $deadlineExceeded
        analysisTimedOut = [bool]($lines | Select-String -Pattern 'analysis timed out|analysis timeout occurred' | Select-Object -First 1)
        analysisProfile = $AnalysisProfile
        analysisBudgetSeconds = $TimeoutSeconds
        remainingProcessBudgetSeconds = $PostTimeoutSeconds
        processSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1)
        statistics = Get-RecoveryFrameworkStatistics $lines
        recovered = if ($summary) { [int]$summary.Matches[0].Groups[1].Value }
        total = if ($summary) { [int]$summary.Matches[0].Groups[2].Value }
        candidates = @(Read-RecoveryFrameworkCandidates $lines)
        console = $console
    }
}

function Get-RecoveryFrameworkStatistics {
    param([string[]] $Lines)
    $analyzers = [ordered]@{}
    $stages = [ordered]@{}
    $active = $null
    $strings = $null
    $coverage = @()
    $anchors = @()
    foreach ($line in $Lines) {
        if ($line -match '^    (\S.*?)[ ]{2,}(\d+(?:\.\d+)?) secs\s*$') {
            $analyzers[$Matches[1]] = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture)
        }
        if ($line -match 'Cached defined strings:\s+(\d+)') { $strings = [int]$Matches[1] }
        if ($line -match 'InspectFrameworkCoverage\.java> COVERAGE (.*) \(GhidraScript\)') {
            $coverage += ConvertFrom-Json -InputObject $Matches[1] -AsHashtable
        }
        if ($line -match 'InspectFrameworkCoverage\.java> ANCHOR (.*) \(GhidraScript\)') {
            $anchors += ConvertFrom-Json -InputObject $Matches[1] -AsHashtable
        }
        if ($line -match 'OffsetRecoveryFramework\.java> == (.+?) ==') {
            $active = if ($Matches[1] -ne 'Summary') { $Matches[1] }
            if ($active) { $stages[$active] = @{ status = 'STARTED' } }
        } elseif ($active -and $line -match 'FAILED: (.*?) \(GhidraScript\)') {
            $stages[$active] = @{ status = 'FAILED'; reason = $Matches[1] }
        } elseif ($active -and $line -match 'Resolved address\s*:\s*([0-9a-fA-F]+)') {
            $stages[$active] = @{ status = 'CANDIDATE'; address = $Matches[1] }
        }
    }
    @{ definedStrings = $strings; analyzerSeconds = $analyzers; recipes = $stages; knownSiteCoverage = $coverage; anchors = $anchors }
}

function Read-RecoveryFrameworkCandidates {
    param([string[]] $Lines)

    $candidate = $null
    foreach ($line in $Lines) {
        if ($line -match 'OffsetRecoveryFramework\.java> == (?<name>.+?) ==') {
            $candidate = [ordered] @{ name = $Matches.name; pattern = $null; resolvedAddress = $null; confidence = $null }
        } elseif ($candidate) {
            if ($line -match 'Resolved address\s*:\s*([0-9a-fA-F]+)') { $candidate.resolvedAddress = $Matches[1] }
            if ($line -match 'Output pattern\s*:\s*([0-9A-Fa-f?^ ]+?)\s+\(GhidraScript\)') { $candidate.pattern = $Matches[1] }
            if ($line -match 'Confidence\s*:\s*(\w+)\s+\((\d+)/100\)') {
                $candidate.confidence = $Matches[1]
                $candidate.score = [int]$Matches[2]
                if ($candidate.pattern -and $candidate.resolvedAddress) { $candidate }
                $candidate = $null
            }
        }
    }
}

function Test-RecoveryPatternFile {
    param([string] $Repository, [string] $Client, [string] $Patterns, [string] $Output)

    $scanner = Join-Path $Repository 'dev/SignatureCheck/bin/Release/net10.0-windows/win-x64/SignatureCheck.dll'
    $previous = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        & dotnet $scanner $Client $Patterns 2> "$Output.stderr.log" | Set-Content -LiteralPath $Output
        [ordered] @{ report = $Output; staticExitCode = $LASTEXITCODE; status = 'REVIEW' }
    } finally { $PSNativeCommandUseErrorActionPreference = $previous }
}

function Compare-RecoveryFrameworkTargets {
    param([object[]] $Suggestions, $Original, $Rescan)

    foreach ($suggestion in $Suggestions) {
        $candidate = $Rescan.patterns | Where-Object Name -CEQ $suggestion.name | Select-Object -First 1
        $configured = $Original.patterns | Where-Object Name -CEQ $suggestion.name | Select-Object -First 1
        $rva = [Convert]::ToInt64($suggestion.resolvedAddress, 16) - [long]$Rescan.imageBase
        $verdict = 'REVIEW_NO_UNIQUE_MATCH'
        if ($candidate.Count -eq 1 -and -not $candidate.Candidates[0].TargetValid) {
            $verdict = 'REVIEW_INVALID_TARGET_SECTION'
        } elseif ($candidate.Count -eq 1) {
            $verdict = if ($candidate.Candidates[0].TargetRva -ne $rva) { 'REVIEW_DISASSEMBLY_MISMATCH' }
                elseif ($configured.Count -ne 1 -or -not $configured.Candidates[0].TargetValid) { 'REVIEW_NEW_TARGET' }
                elseif ($configured.Candidates[0].TargetRva -ne $rva) { 'REVIEW_CONFLICTING_TARGETS' }
                else { 'AGREES_WITH_CONFIGURED_TARGET' }
        }
        @{ name = $suggestion.name; frameworkTargetRva = $rva; verdict = $verdict }
    }
}

function Invoke-RecoveryMcp {
    param([string] $Repository, [string] $Output, [string] $Tool, [hashtable] $Arguments = @{}, [string] $Label = $Tool)

    $relay = Join-Path $Repository 'dev/McpRelay/bin/Release/net10.0-windows/win-x64/McpRelay.dll'
    $rpc = @{ jsonrpc = '2.0'; id = 1; method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments } }
    $request = $rpc | ConvertTo-Json -Compress -Depth 8
    $reply = $request | & dotnet $relay | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -or -not $reply) { throw "MCP transport failed for $Tool." }
    if ($reply.error) { throw "MCP $($reply.error.message)" }
    $message = $reply.result.content[0].text
    if ($reply.result.isError) { throw "${Tool}: $message" }
    $data = $message | ConvertFrom-Json -AsHashtable
    Write-RecoveryJson (Join-Path $Output "$Label.json") $data
    $data
}

function Compare-RecoverySignatures {
    param($Current, $Baseline)

    $previous = @{}
    if ($Baseline -and $Baseline.signatureReport -and (Test-Path -LiteralPath $Baseline.signatureReport -PathType Leaf)) {
        $before = Get-Content -LiteralPath $Baseline.signatureReport -Raw | ConvertFrom-Json -AsHashtable
        if ($before.sha256 -ne $Baseline.client.sha256) { throw 'The baseline signature report belongs to a different executable.' }
        foreach ($pattern in $before.patterns) { $previous[$pattern.Name] = $pattern }
    }

    foreach ($pattern in $Current.patterns) {
        $old = $previous[$pattern.Name]
        $target = if ($pattern.Count -eq 1) { $pattern.Candidates[0] }
        $one = $target.TargetValid -eq $true
        $oldRva = if ($old -and $old.Count -eq 1) { $old.Candidates[0].TargetRva }
        [ordered] @{
            name = $pattern.Name
            count = $pattern.Count
            verdict = if ($one) { 'UNIQUE' } elseif ($pattern.Count -eq 0) { 'MISSING' }
                elseif ($pattern.Count -gt 1) { 'AMBIGUOUS' }
                elseif ($target.TargetInImage) { 'INVALID_TARGET_SECTION' } else { 'OUTSIDE_IMAGE' }
            matchRva = $target.MatchRva
            targetRva = $target.TargetRva
            targetSection = $target.TargetSection
            previousCount = if ($old) { $old.Count }
            previousTargetRva = $oldRva
            targetRvaDelta = if ($one -and $null -ne $oldRva) { $pattern.Candidates[0].TargetRva - $oldRva }
        }
    }
}

function Import-RecoveryOffsetDump {
    param([string] $Path, [string] $Output, [string] $ExpectedHash)

    $dump = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($dump.InGame -ne $true -or -not $dump.probes) { throw 'Provide a completed GH_OFFSET_DUMP from a loaded area.' }
    if ($dump.clientSha256 -and $dump.clientSha256 -ne $ExpectedHash) { throw 'The offset dump belongs to a different executable.' }
    $destination = Join-Path $Output 'offset-dump.json'
    [IO.File]::Copy($Path, $destination, $false)
    [ordered] @{
        report = $destination
        hashMatched = $dump.clientSha256 -eq $ExpectedHash
        signatures = @($dump.signatures).Count
        intact = $dump.Intact
        degraded = $dump.Degraded
        unverifiable = $dump.Unverifiable
        noRoot = $dump.NoRoot
        candidates = @($dump.probes | Where-Object { $_.Recoveries } | ForEach-Object {
            @{ structure = $_.Name; suggestions = $_.Recoveries }
        })
    }
}

function Read-RecoveryLive {
    param([string] $Repository, [string] $Output, $Report)

    $directory = [IO.Directory]::CreateDirectory((Join-Path $Output 'live')).FullName
    $Report.live = [ordered] @{}
    $live = $Report.live
    try {
        $status = Invoke-RecoveryMcp $Repository $directory status
        $live.transport = 'READY'
        if ($Report.runtime) {
            if ($status.overlayPid -le 0 -or
                (Get-Process -Id $status.overlayPid -ErrorAction Stop).Path -ne (Join-Path $Report.runtime 'GameHelper.exe')) {
                throw 'The local Bridge listener does not belong to the selected runtime.'
            }
        }
        if ($status.attached -ne $true -or $status.pid -le 0 -or $status.moduleBase -notmatch '^0x[0-9a-fA-F]+$' -or
            $status.moduleBase -eq '0x0' -or $status.areaLoaded -ne $true -or
            (Get-Process -Id $status.pid -ErrorAction Stop).Path -ne $Report.client.path) {
            throw 'The selected client has no attached and loaded game area.'
        }

        $state = Invoke-RecoveryMcp $Repository $directory state
        if ($state.gameState -ne 'InGameState' -or $state.area.id -notmatch '^[A-Za-z0-9_]+$' -or
            $state.area.level -notin (1..100) -or $state.player.id -le 0 -or $state.player.path -notlike 'Metadata/Characters/*') {
            throw 'State is missing a plausible area, level or local player.'
        }
        $entities = Invoke-RecoveryMcp $Repository $directory entities @{ maxResults = 20 }
        $live.entities = if ($entities.counts.returned -gt 0 -and @($entities.entities | Where-Object { $_.path -like 'Metadata/*' }).Count -gt 0) { 'READY' }
            else { 'PARTIAL' }
        $Report.phases.liveCore = if ($live.entities -eq 'READY') { 'READY' } else { 'PARTIAL' }
    } catch {
        $live.coreError = $_.Exception.Message
        $Report.phases.liveCore = 'BLOCKED'
        if ($Report.phases.structures -eq 'NOT TESTED') { $Report.phases.structures = 'BLOCKED' }
        $Report.phases.plugins = 'BLOCKED'
        return
    }

    try {
        $sweep = Invoke-RecoveryMcp $Repository $directory offsets_verify
        if ($sweep.inGame -ne $true -or $sweep.summary.intact -le 0) { throw 'OffsetHelper found no verified roots.' }
        $live.offsetSummary = $sweep.summary
        $Report.phases.structures = if ($sweep.summary.degraded -or $sweep.summary.unverifiable -or $sweep.summary.noRoot) { 'PARTIAL' } else { 'READY' }
        $queued = @($sweep.structs | Where-Object { $_.recovery -or $_.verdict -eq 'Degraded' })
        $live.detailProbes = @($queued | Select-Object -First 8 | ForEach-Object -Begin { $sequence = 0 } -Process {
            $sequence++
            $label = 'offset-detail-{0:D2}' -f $sequence
            try {
                $detail = Invoke-RecoveryMcp $Repository $directory offsets_verify @{ name = $_.name } $label
                @{ name = $_.name; report = "$label.json"; candidates = $detail.detail.Recoveries.Count }
            } catch { @{ name = $_.name; reason = $_.Exception.Message } }
        })
        $live.detailProbesRemaining = [Math]::Max(0, $queued.Count - 8)
    } catch {
        $live.offsetError = $_.Exception.Message
        if ($Report.phases.structures -ne 'PARTIAL') { $Report.phases.structures = 'BLOCKED' }
    }

    try {
        $map = Invoke-RecoveryMcp $Repository $directory ui_element @{ root = 'largeMap'; withChildren = $false } 'ui-large-map'
        $live.ui = if ($map.address -ne '0x0' -and $map.size.x -gt 0 -and $map.size.y -gt 0) { 'READY' } else { 'PARTIAL' }
    } catch { $live.ui = 'BLOCKED'; $live.uiError = $_.Exception.Message }

    try {
        $atlasRoot = Invoke-RecoveryMcp $Repository $directory ui_element @{ root = 'atlas'; withChildren = $false } 'ui-atlas'
        if ($atlasRoot.isVisible -ne $true) { throw 'Open the World Atlas to verify Atlas data.' }
        $atlas = Invoke-RecoveryMcp $Repository $directory atlas_nodes @{ maxResults = 5 }
        $live.atlas = if ($atlas.total -gt 0 -and @($atlas.nodes).Count -gt 0) { 'READY' } else { 'PARTIAL' }
    } catch { $live.atlas = 'BLOCKED'; $live.atlasError = $_.Exception.Message }

    try {
        $loaded = Invoke-RecoveryMcp $Repository $directory plugins
        $manifest = if ($Report.runtime -and $Report.diagnostics.integrity -eq 'PASS') { Read-RuntimeManifest $Report.runtime -AllowProfileChanges }
        $expected = if ($manifest) { $manifest.plugins } else { @($loaded.plugins.name) }
        $live.pluginLoad = @(foreach ($name in $expected) {
            $item = @($loaded.plugins | Where-Object name -eq $name) | Select-Object -First 1
            if (-not $item) { @{ name = $name; status = 'BLOCKED'; reason = 'Plugin missing from live list.' }; continue }
            try {
                $plugin = Invoke-RecoveryMcp $Repository $directory plugin_state @{ name = $name } "plugin-$name"
                @{ name = $name; status = 'PARTIAL'; enabled = $plugin.enabled; type = $plugin.type }
            } catch { @{ name = $name; status = 'BLOCKED'; reason = $_.Exception.Message } }
        })
        $Report.phases.plugins = if ($live.pluginLoad.Count -gt 0) { 'PARTIAL' } else { 'BLOCKED' }
    } catch { $live.pluginError = $_.Exception.Message; $Report.phases.plugins = 'BLOCKED' }

    try {
        $end = Invoke-RecoveryMcp $Repository $directory state @{} 'state-end'
        if ($end.area.hash -ne $state.area.hash -or $end.player.id -ne $state.player.id) {
            $Report.phases.liveCore = 'PARTIAL'
            $Report.phases.structures = 'PARTIAL'
            $Report.findings.Add('The game area or character changed during the sweep. Repeat the live checks in one area.')
        }
    } catch { $Report.phases.liveCore = 'PARTIAL'; $live.endError = $_.Exception.Message }
}

function Build-RecoveryCandidate {
    param([string] $Repository, [string] $Source, [string] $Output, [string] $ClientCopy, [string] $InstalledDirectory,
        [string] $Ghidra, [string] $Jdk)

    $Source = (Resolve-Path -LiteralPath $Source).Path
    $common = Invoke-Checked git @('-C', $Repository, 'rev-parse', '--path-format=absolute', '--git-common-dir')
    $candidateCommon = Invoke-Checked git @('-C', $Source, 'rev-parse', '--path-format=absolute', '--git-common-dir')
    $branch = Invoke-Checked git @('-C', $Source, 'branch', '--show-current')
    if ($Source -eq $Repository -or $common -ne $candidateCommon -or $branch -notlike 'recovery/*') {
        throw 'Build candidates from a separate recovery/* worktree in this repository.'
    }
    if (Invoke-Checked git @('-C', $Source, 'status', '--porcelain', '--untracked-files=normal')) {
        throw 'Commit the reviewed candidate changes in its worktree before packaging.'
    }
    if (-not $Ghidra -or -not $Jdk) { throw 'Candidate staging requires independent signature checks in Ghidra.' }

    $buildOutput = Join-Path $Output 'candidate-build.log'
    $product = & "$Source/build.ps1" 6> $buildOutput | Select-Object -Last 1
    if (-not $product -or -not (Test-Path -LiteralPath $product -PathType Container)) { throw "Candidate build failed. See $buildOutput." }
    $diagnostics = Join-Path $Output 'candidate-diagnostics'
    & "$Source/diagnose.ps1" -RuntimeDirectory $product -ClientExe $ClientCopy -GhidraHome $Ghidra -JdkHome $Jdk `
        -ReportDirectory $diagnostics -Offline | Out-Null
    $archive = & "$Source/package.ps1" -RuntimeDirectory $product
    $hash = (Get-FileHash -LiteralPath $archive.FullName).Hash
    $staged = @(& "$Repository/patch-day.ps1" -InstalledDirectory $InstalledDirectory -Archive $archive.FullName -ExpectedSha256 $hash -ClientExe $ClientCopy)
    [ordered] @{
        status = 'PARTIAL'
        branch = $branch
        commit = Invoke-Checked git @('-C', $Source, 'rev-parse', 'HEAD')
        build = $product
        diagnostics = $diagnostics
        archive = $archive.FullName
        archiveSha256 = $hash
        runtime = ($staged | Where-Object { $_ -like 'Staged update: *' }).Substring(15)
        rollbackRuntime = $InstalledDirectory
    }
}

function Invoke-RecoveryTrial {
    param([string] $Repository, [string] $Output, [string] $Runtime, [string] $Name, [string] $ExpectedHash,
        [int] $ExpectedSignatures = 6, [switch] $StructureAudit, [int] $ExpectedMaxLife)

    $trialDir = [IO.Directory]::CreateDirectory((Join-Path $output $Name)).FullName
    $staged = Join-Path $trialDir 'runtime'
    [void] [IO.Directory]::CreateDirectory($staged)
    $manifest = Read-RuntimeManifest $Runtime -AllowProfileChanges
    foreach ($file in @($manifest.files.Keys) + 'developer-build.json') {
        $from = Get-RuntimeFile $Runtime $file
        $to = Join-Path $staged $file
        [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
        [IO.File]::Copy($from, $to, $false)
    }
    [void] (Read-RuntimeManifest $staged -AllowProfileChanges)

    $config = [IO.Directory]::CreateDirectory((Join-Path $staged 'configs')).FullName
    $pluginMetadata = @{}
    foreach ($plugin in $manifest.plugins) { $pluginMetadata[$plugin] = @{ Enable = $false } }
    Write-RecoveryJson (Join-Path $config 'plugins.json') $pluginMetadata
    Write-RecoveryJson (Join-Path $config 'core_settings.json') @{ HideSettingWindowOnStart = $true; ShowPerfStats = $false; AutomateNpcQuests = $false }

    $dump = Join-Path $trialDir 'offsets.json'
    $launchArgs = @{ RuntimeDirectory = $staged; OffsetDump = $dump; StructureAudit = $StructureAudit
        LogDirectory = Join-Path $trialDir 'logs' }
    if ($ExpectedMaxLife) { $launchArgs.ExpectedMaxLife = $ExpectedMaxLife }
    $process = & "$Repository/start.ps1" @launchArgs
    try {
        $until = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $dump -PathType Leaf) -and -not $process.HasExited -and [DateTimeOffset]::UtcNow -lt $until) {
            Start-Sleep -Milliseconds 400
        }
        if (-not (Test-Path -LiteralPath $dump -PathType Leaf)) { throw "No dump in $Name. Enter a loaded area and inspect the trial logs." }
        $data = Get-Content -LiteralPath $dump -Raw | ConvertFrom-Json -AsHashtable
        if ($data.InGame -ne $true -or $data.clientSha256 -ne $ExpectedHash -or $data.signatures.Count -ne $ExpectedSignatures) {
            throw "Invalid or mismatched dump in $Name."
        }
        @{ file = $dump; data = $data }
    } finally {
        if (-not $process.HasExited) {
            if ($process.Path -ne (Join-Path $staged 'GameHelper.exe')) { throw 'The trial process identity changed.' }
            if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(2000)) {
                $process.Kill()
                [void] $process.WaitForExit(2000)
            }
        }
    }
}
