<#
.SYNOPSIS
Repeats the production queue stress tests for a bounded observation window.
.DESCRIPTION
Runs real HiFi request/reply, blocked-producer cancellation, and metadata
snapshot tests. This validates queue lifecycle and ordering with synthetic
payloads; it is not a decoder throughput or memory-leak benchmark.
#>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateRange(1, 86400)][int]$DurationSeconds = 60,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ArtifactRoot = (Join-Path $PSScriptRoot '../artifacts/queue-stress'),
    [string]$ArtifactsPath,
    [switch]$NoBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$session = Join-Path ([IO.Path]::GetFullPath($ArtifactRoot)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session -ErrorAction Stop | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$artifactArguments = @()
if ($ArtifactsPath) { $artifactArguments = @('--artifacts-path', [IO.Path]::GetFullPath($ArtifactsPath)) }
$iterations = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::new()
$failed = $false
$sdk = $null
Push-Location -LiteralPath $repository
try {
    $sdk = (& dotnet --version) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pinned SDK version.' }
    if (-not $NoBuild) {
        & dotnet build tests/VHSDecode.Tests/VHSDecode.Tests.csproj --configuration $Configuration @artifactArguments
        if ($LASTEXITCODE -ne 0) { throw 'Queue stress build failed.' }
    }
    $watch.Start()
    do {
        $index = $iterations.Count + 1
        $resultPath = Join-Path $session ('iteration-{0:D4}' -f $index)
        $log = $resultPath + '.log'
        & dotnet test tests/VHSDecode.Tests/VHSDecode.Tests.csproj --configuration $Configuration `
            --no-build --no-restore @artifactArguments --filter-class VHSDecode.Tests.DecodeQueueStressTests `
            --minimum-expected-tests 6 --timeout 2m --report-trx --results-directory $resultPath *> $log
        $exitCode = $LASTEXITCODE
        $text = [IO.File]::ReadAllText($log) -replace '\r?\n', "`r`n"
        [IO.File]::WriteAllText($log, $text, $utf8)
        $iterations.Add([ordered]@{iteration=$index; exitCode=$exitCode; elapsedSeconds=$watch.Elapsed.TotalSeconds; log=$log})
        Write-Host "Queue stress iteration $index : exit $exitCode"
        if ($exitCode -ne 0) { $failed = $true; break }
    } while ($watch.Elapsed.TotalSeconds -lt $DurationSeconds)
}
catch {
    $failed = $true
    throw
}
finally {
    $watch.Stop()
    $report = [ordered]@{
        sdk=$sdk; platform=[Runtime.InteropServices.RuntimeInformation]::OSDescription
        requestedSeconds=$DurationSeconds; elapsedSeconds=$watch.Elapsed.TotalSeconds
        passed=(-not $failed -and $iterations.Count -gt 0); iterations=$iterations
        scope='Synthetic payloads through production queues; no performance or full-tape claim.'
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $session 'report.json'), (($report -replace '\r?\n', "`r`n") + "`r`n"), $utf8)
    Pop-Location
    Write-Host "Queue stress evidence: $session"
}
if ($failed) { throw 'Queue stress failed; inspect the retained TRX and log.' }
