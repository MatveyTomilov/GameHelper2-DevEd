#Requires -Version 7.2
param(
    [Parameter(Mandatory)] [string] $Headless,
    [Parameter(Mandatory)] [string] $Projects,
    [Parameter(Mandatory)] [string] $ClientCopy,
    [Parameter(Mandatory)] [int] $TimeoutSeconds,
    [Parameter(Mandatory)] [string] $Scripts,
    [Parameter(Mandatory)] [string] $Script,
    [Parameter(Mandatory)] [string] $Log,
    [Parameter(Mandatory)] [string] $Console,
    [ValidateSet('Standard', 'Recipe')] [string] $AnalysisProfile = 'Standard',
    [string] $KnownSites
)

$arguments = @($Projects, 'poe2', '-import', $ClientCopy, '-max-cpu', '2',
    '-analysisTimeoutPerFile', "$TimeoutSeconds", '-scriptPath', $Scripts, '-log', $Log)
if ($AnalysisProfile -eq 'Recipe') { $arguments += @('-preScript', 'ConfigureFrameworkAnalysis.java') }
if ($KnownSites -and (Test-Path -LiteralPath $KnownSites -PathType Leaf)) {
    $arguments += @('-postScript', 'InspectFrameworkCoverage.java', $KnownSites)
}
$arguments += @('-postScript', $Script)
& $Headless @arguments *> $Console
exit $LASTEXITCODE
