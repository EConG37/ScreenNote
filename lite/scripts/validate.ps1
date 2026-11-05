[CmdletBinding()]
param([switch]$SkipUi)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tests = @('gif-recording-validation', 'video-recording-validation', 'media-roundtrip')
if (-not $SkipUi) { $tests += 'record-flow-ui' }
foreach ($name in $tests) {
    $project = Join-Path $root ('tests/' + $name + '.csproj')
    $output = Join-Path $root ('.build/validation/' + $name)
    & dotnet run --project $project -c Release -- $output
    if ($LASTEXITCODE -ne 0) { throw ('Validation failed: ' + $name) }
}
Write-Output 'All requested lite validation suites passed.'
