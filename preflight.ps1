#Requires -Version 7.2
[CmdletBinding()]
param([string] $GhidraHome, [string] $JdkHome, [switch] $RequireGhidra)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Use x64 PowerShell 7 on Windows.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    $expectedSdk = (Get-Content -Raw -LiteralPath 'global.json' | ConvertFrom-Json).sdk.version
    $actualSdk = & dotnet --version
    if ($LASTEXITCODE -or $actualSdk -ne $expectedSdk) {
        throw "Install Microsoft .NET SDK $expectedSdk for Windows x64."
    }
    $git = & git --version
    if ($LASTEXITCODE) { throw 'Git for Windows is required.' }

    if (-not $GhidraHome) { $GhidraHome = $env:GHIDRA_HOME }
    if (-not $JdkHome) { $JdkHome = $env:JAVA_HOME }
    $headless = if ($GhidraHome) { Join-Path $GhidraHome 'support/analyzeHeadless.bat' }
    $compiler = if ($JdkHome) { Join-Path $JdkHome 'bin/javac.exe' }
    $ghidraReady = $headless -and $compiler -and
        (Test-Path -LiteralPath $headless -PathType Leaf) -and
        (Test-Path -LiteralPath $compiler -PathType Leaf)
    if ($ghidraReady) {
        $javacVersion = & $compiler -version 2>&1 | Out-String
        $ghidraReady = ($LASTEXITCODE -eq 0) -and
            ($javacVersion -match 'javac (\d+)') -and ([int]$Matches[1] -ge 25)
    }
    if ($RequireGhidra -and -not $ghidraReady) {
        throw 'Provide a Ghidra installation and a JDK via -GhidraHome and -JdkHome. See PATCH_DAY.md.'
    }

    [pscustomobject] @{
        windowsX64 = $true
        dotnetSdk = $actualSdk
        powershell = $PSVersionTable.PSVersion.ToString()
        git = $git
        codexAvailable = [bool](Get-Command codex -ErrorAction SilentlyContinue)
        mcpPort = 3002
        ghidraConfigured = [bool]$ghidraReady
    }
} finally { Pop-Location }
