#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $RefreshLocks
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev/runtime.ps1')

function Initialize-PluginSource {
    param($Plugin)
    $projectDirectory = "Plugins/$($Plugin.name)"
    $marker = Join-Path $projectDirectory '.source-commit'
    if (Test-Path -LiteralPath $projectDirectory) {
        if (-not (Test-Path -LiteralPath $marker) -or
            (Get-Content -Raw -LiteralPath $marker).Trim() -ne $Plugin.commit) {
            throw "Existing $projectDirectory does not match the pinned source. Move it aside before restoring."
        }
        return
    }

    $cache = [IO.Directory]::CreateDirectory((Join-Path $PWD.Path '.upstream')).FullName
    $source = Join-Path $cache $Plugin.name
    if (-not (Test-Path -LiteralPath (Join-Path $source '.git'))) {
        Invoke-Checked git @('init', '--quiet', $source)
    }
    & git -C $source cat-file -e "$($Plugin.commit)^{commit}" 2>$null
    if ($LASTEXITCODE) {
        Invoke-Checked git @('-C', $source, 'fetch', '--quiet', '--depth=1', $Plugin.url, $Plugin.commit)
    }
    $temporary = Join-Path $cache ([Guid]::NewGuid().ToString('N'))
    [void] [IO.Directory]::CreateDirectory($temporary)
    $archive = "$temporary.zip"
    try {
        Invoke-Checked git @('-C', $source, 'archive', '--format=zip', "--output=$archive", $Plugin.commit)
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $temporary)
        if ($Plugin.patch) {
            $relativeSource = [IO.Path]::GetRelativePath($PWD.Path, $temporary).Replace('\', '/')
            Invoke-Checked git @('apply', "--directory=$relativeSource", $Plugin.patch)
        }
        Set-Content -LiteralPath (Join-Path $temporary '.source-commit') -Value $Plugin.commit
        [IO.Directory]::Move($temporary, (Join-Path $PWD.Path $projectDirectory))
    } finally { [IO.File]::Delete($archive) }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & ./preflight.ps1 | Out-Host
    $pins = Get-Content -LiteralPath 'dev/sources.json' -Raw | ConvertFrom-Json
    $targets = [Collections.Generic.List[string]]::new()
    $targets.Add('GameOverlay.sln')
    $targets.Add('dev/SignatureCheck/SignatureCheck.csproj')
    $targets.Add('dev/McpRelay/McpRelay.csproj')
    foreach ($plugin in $pins.plugins) {
        Initialize-PluginSource $plugin
        $project = "Plugins/$($plugin.name)/$($plugin.name).csproj"
        $targets.Add($project)
        if (-not $RefreshLocks) {
            Copy-Item -LiteralPath "dev/locks/$($plugin.name).json" -Destination "Plugins/$($plugin.name)/packages.lock.json"
        }
    }

    $restoreMode = if ($RefreshLocks) { @('--use-lock-file', '--force-evaluate') } else { @('--locked-mode') }
    foreach ($target in $targets) {
        Invoke-Checked dotnet (@('restore', $target, '--configfile', 'NuGet.config', '--nologo') + $restoreMode)
    }
    if ($RefreshLocks) {
        [void] [IO.Directory]::CreateDirectory((Join-Path $PWD.Path 'dev/locks'))
        foreach ($plugin in $pins.plugins) {
            Copy-Item -LiteralPath "Plugins/$($plugin.name)/packages.lock.json" -Destination "dev/locks/$($plugin.name).json"
        }
    }
    $id = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Configuration.ToLowerInvariant(), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    $outputBase = "bin/developer-edition/$id/"
    foreach ($target in $targets) {
        $arguments = @('build', $target, '--no-restore', '--nologo', '-c', $Configuration)
        if ($target -notlike 'dev/*') { $arguments += "-p:BaseOutputPath=$outputBase" }
        Invoke-Checked dotnet $arguments
    }

    $runtime = (Resolve-Path -LiteralPath "GameHelper/$outputBase$Configuration/net10.0-windows/win-x64").Path
    $parent = [IO.Directory]::CreateDirectory((Join-Path $PWD.Path 'artifacts/build')).FullName
    $pending = Join-Path $parent ".$id"
    [void] [IO.Directory]::CreateDirectory($pending)
    $plugins = @('Atlas2', 'AutoHotKeyTrigger', 'ExchangeTrader', 'HealthBars', 'LootValue',
        'PreloadAlert', 'Radar', 'RitualWispAlert') + @($pins.plugins.name)
    foreach ($name in $plugins) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtime "Plugins/$name/$name.dll") -PathType Leaf)) {
            throw "Missing staged plugin: $name"
        }
    }

    $hashes = [ordered] @{}
    foreach ($file in (Get-ChildItem -LiteralPath $runtime -File -Recurse)) {
        $relative = [IO.Path]::GetRelativePath($runtime, $file.FullName).Replace('\', '/')
        if ($Configuration -eq 'Release' -and $file.Extension -in '.pdb', '.dbg', '.ilk', '.ipdb', '.iobj') { continue }
        if ($relative -match '^(configs/|Launcher\.|imgui\.ini$|Error\.log$|tempFileLocations\.dat$)' -or
            $relative -match '^Plugins/[^/]+/(config/|price_cache\.json$)' -or
            ($relative -match '^Plugins/([^/]+)/' -and $Matches[1] -notin $plugins)) { continue }
        $destination = Join-Path $pending $relative
        [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy($file.FullName, $destination, $false)
        $hashes[$relative] = (Get-FileHash -LiteralPath $destination).Hash
    }
    foreach ($relative in 'start.ps1', 'PATCH_DAY.md', 'PATCH_RECOVERY.md', 'RECOVERY_REHEARSAL.md', 'SECOND_READINESS_AUDIT.md', 'CURRENT_BASELINE.md') {
        $destination = Join-Path $pending $relative
        [IO.File]::Copy((Join-Path $PWD.Path $relative), $destination, $true)
        $hashes[$relative] = (Get-FileHash -LiteralPath $destination).Hash
    }
    $manifest = [ordered] @{
        schema = 1
        product = 'GameHelper2 DevEd'
        releaseVersion = '0.1.0'
        buildId = $id
        configuration = $Configuration
        created = [DateTimeOffset]::Now.ToString('o')
        commit = (Invoke-Checked git @('rev-parse', 'HEAD'))
        trackedChanges = [bool](Invoke-Checked git @('status', '--porcelain', '--untracked-files=normal'))
        sdk = (Invoke-Checked dotnet @('--version'))
        upstreamCore = $pins.core.commit
        upstreamPlugins = $pins.plugins
        plugins = $plugins
        files = $hashes
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $pending 'developer-build.json')
    [void] (Read-RuntimeManifest $pending)
    $result = Join-Path $parent $id
    [IO.Directory]::Move($pending, $result)
    Write-Output $result
} finally { Pop-Location }
