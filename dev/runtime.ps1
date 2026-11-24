$ErrorActionPreference = 'Stop'

$RadarProfileFiles = @(
    'Plugins/Radar/boss_arena_tgt_files.txt'
    'Plugins/Radar/important_tgt_files.txt'
    'Plugins/Radar/stairs_tgt_files.txt'
)

function Invoke-Checked {
    param([string] $Command, [string[]] $Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE) { throw "$Command exited with code $LASTEXITCODE." }
}

function Get-RuntimeFile {
    param([string] $Root, [string] $RelativePath)
    if ($RelativePath -notmatch '\A[^\\:/]+(?:/[^\\:/]+)*\z' -or
        ($RelativePath -split '/') -match '^\.\.?$') {
        throw "Invalid relative path in runtime manifest: $RelativePath"
    }

    $path = $Root
    foreach ($part in ($RelativePath -split '/')) {
        $path = Join-Path $path $part
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point in runtime: $path"
        }
    }
    $path
}

function Read-RuntimeManifest {
    param([string] $Root, [switch] $AllowProfileChanges)
    $manifestPath = Get-RuntimeFile $Root 'developer-build.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.schema -ne 1 -or $manifest.files -isnot [Collections.IDictionary] -or
        -not $manifest.files.Contains('GameHelper.exe') -or -not $manifest.files.Contains('GameHelper.dll')) {
        throw 'Invalid developer-build.json.'
    }
    foreach ($entry in $manifest.files.GetEnumerator()) {
        $path = Get-RuntimeFile $Root $entry.Key
        if ($AllowProfileChanges -and $entry.Key -cin $RadarProfileFiles) { continue }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value) {
            throw "Runtime hash mismatch: $($entry.Key)"
        }
    }
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
        if ($file.Extension -notin '.exe', '.dll') { continue }
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if (-not $manifest.files.Contains($relative)) {
            throw "Unexpected binary in runtime: $relative"
        }
    }
    $manifest
}

function Copy-RuntimeSettings {
    param([string] $Source, [string] $Destination)
    $choices = [Collections.Generic.List[string]]::new()
    foreach ($relative in 'configs', 'imgui.ini', 'Plugins/PreloadAlert/preloads.txt') { $choices.Add($relative) }
    $choices.AddRange([string[]]$RadarProfileFiles)
    $pluginRoot = Join-Path $Source 'Plugins'
    if (Test-Path -LiteralPath $pluginRoot -PathType Container) {
        foreach ($plugin in (Get-ChildItem -LiteralPath $pluginRoot -Directory)) {
            $choices.Add("Plugins/$($plugin.Name)/config")
        }
    }
    foreach ($choice in $choices) {
        if (-not (Test-Path -LiteralPath (Join-Path $Source $choice))) { continue }
        $path = Get-RuntimeFile $Source $choice
        $files = if (Test-Path -LiteralPath $path -PathType Container) {
            Get-ChildItem -LiteralPath $path -File -Recurse -Force
        } else { Get-Item -LiteralPath $path -Force }
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
            $from = Get-RuntimeFile $Source $relative
            $to = Join-Path $Destination $relative
            [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::Copy($from, $to, ($relative -cin $RadarProfileFiles))
            if ((Get-FileHash -LiteralPath $from).Hash -ne (Get-FileHash -LiteralPath $to).Hash) {
                throw "Settings changed during backup: $relative. Close GameHelper and retry."
            }
        }
    }
}
