<#
.SYNOPSIS
Runs an interleaved Exact versus Intel IPP decode benchmark on Windows.

.DESCRIPTION
Creates a unique session below ArtifactRoot, warms both backends, then runs at
least five measured pairs in alternating exact/ipp-fast and ipp-fast/exact
order. Each run retains stdout, stderr, decoded artifacts, hashes, process
metrics, and exit status. The script never deletes or overwrites an existing
path. Pair reports compare the complete artifact manifest, summarize every
numeric fileLoc in *.tbc.json, and compare raw plus timestamp-normalized logs;
these digests do not replace formal field-aligned compatibility analysis.

Resource metrics cover the decode process itself. Child processes such as
FFmpeg are not aggregated and this limitation is written into the JSON report.
Managed/native allocation and GC values are null because this harness does not
collect an EventPipe or ETW trace.

.PARAMETER DecodeExe
Path to the decode.exe binary under test.

.PARAMETER Subcommand
Decode facade subcommand: vhs, cvbs, ld, or hifi. The current v1 ipp-fast
implementation supports only the VHS real-RF FFT path; unsupported commands are
expected to fail clearly rather than silently run Exact kernels.

.PARAMETER InputPath
Path to the capture used by every run.

.PARAMETER ArtifactRoot
Directory below which a new, uniquely named benchmark session is created.

.PARAMETER ForwardArguments
Option tokens forwarded before the harness-controlled length, thread, backend,
input, and output arguments. Pass tokens as an array, for example
@('--system', 'PAL', '--start_fileloc', '620000000').

.PARAMETER Frames
Value passed to --length for vhs, cvbs, and ld. Defaults to 160 for those
commands. HiFi has no frame-length option and rejects an explicitly supplied
Frames value.

.PARAMETER Runs
Number of measured A/B pairs. The minimum and default are 5.

.PARAMETER Warmup
Number of unmeasured warmup pairs. Defaults to 1.

.PARAMETER Threads
Value passed to --threads. Defaults to 20.

.PARAMETER GateMode
Gain requires the paired median wall-time gain to reach MinimumGainPercent.
Regression permits a slowdown no larger than MaximumRegressionPercent. Both
requires both checks.

.PARAMETER MinimumGainPercent
Required paired median wall-time gain. Use 5 for the RF pilot gate or 10 for
the release target.

.PARAMETER MaximumRegressionPercent
Maximum allowed paired median wall-time slowdown for the regression gate.
Defaults to 2.

.PARAMETER DryRun
Validates arguments and prints the complete run plan without creating,
deleting, or executing anything.

.EXAMPLE
.\tools\benchmark-ipp.ps1 -DecodeExe .\decode.exe -Subcommand vhs `
    -InputPath D:\captures\sample.lds -ArtifactRoot D:\benchmarks `
    -Frames 160 -Runs 5 -Warmup 1 -Threads 20 `
    -ForwardArguments @('--system', 'PAL') -MinimumGainPercent 5

.EXAMPLE
.\tools\benchmark-ipp.ps1 -DecodeExe .\decode.exe -Subcommand vhs `
    -InputPath D:\captures\sample.lds -ArtifactRoot D:\benchmarks -DryRun
#>

#Requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DecodeExe,

    [Parameter(Mandatory)]
    [ValidateSet('vhs', 'cvbs', 'ld', 'hifi')]
    [string]$Subcommand,

    [Parameter(Mandatory)]
    [Alias('Input')]
    [string]$InputPath,

    [Parameter(Mandatory)]
    [Alias('OutputArtifactRoot')]
    [string]$ArtifactRoot,

    [string[]]$ForwardArguments = @(),

    [ValidateRange(1, 99999999)]
    [int]$Frames,

    [ValidateRange(5, 1000)]
    [int]$Runs = 5,

    [ValidateRange(0, 100)]
    [int]$Warmup = 1,

    [ValidateRange(0, 1024)]
    [int]$Threads = 20,

    [ValidateSet('Gain', 'Regression', 'Both')]
    [string]$GateMode = 'Gain',

    [ValidateSet(5.0, 10.0)]
    [double]$MinimumGainPercent = 5.0,

    [ValidateRange(0.0, 100.0)]
    [double]$MaximumRegressionPercent = 2.0,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The IPP A/B benchmark harness supports Windows only.'
}

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-StrictChildPath {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Candidate
    )

    $parentFullPath = Get-FullPath $Parent
    $candidateFullPath = Get-FullPath $Candidate
    $parentPrefix = $parentFullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $candidateFullPath.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not a strict child of the benchmark artifact root: $candidateFullPath"
    }

    return $candidateFullPath
}

function New-UniqueDirectory {
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Path
    )

    $safePath = Assert-StrictChildPath -Parent $Parent -Candidate $Path
    if (Test-Path -LiteralPath $safePath) {
        throw "Refusing to delete or overwrite an existing benchmark path: $safePath"
    }

    New-Item -ItemType Directory -Path $safePath | Out-Null
    $directory = Get-Item -LiteralPath $safePath
    if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use a reparse-point benchmark directory: $safePath"
    }

    return $safePath
}

function Write-NewTextFile {
    param(
        [Parameter(Mandatory)][string]$SessionRoot,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    $safePath = Assert-StrictChildPath -Parent $SessionRoot -Candidate $Path
    if (Test-Path -LiteralPath $safePath) {
        throw "Refusing to overwrite an existing benchmark file: $safePath"
    }

    [System.IO.File]::WriteAllText(
        $safePath,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Get-BackendOrder {
    param([Parameter(Mandatory)][int]$PairNumber)

    if (($PairNumber % 2) -eq 1) {
        return @('exact', 'ipp-fast')
    }

    return @('ipp-fast', 'exact')
}

function Get-DecodeArguments {
    param(
        [Parameter(Mandatory)][string]$Backend,
        [Parameter(Mandatory)][string]$OutputBase
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add($Subcommand)
    foreach ($argument in $ForwardArguments) {
        $arguments.Add($argument)
    }

    if ($null -ne $script:EffectiveFrames) {
        $arguments.Add('--length')
        $arguments.Add([string]$script:EffectiveFrames)
    }

    $arguments.Add('--threads')
    $arguments.Add([string]$Threads)
    $arguments.Add('--dsp-backend')
    $arguments.Add($Backend)
    $arguments.Add($script:InputFullPath)
    $arguments.Add($OutputBase)
    return $arguments.ToArray()
}

function Format-CommandToken {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Token)

    if ($Token.Length -gt 0 -and $Token -notmatch '[\s"]') {
        return $Token
    }

    return '"' + $Token.Replace('"', '\"') + '"'
}

function New-PrivateWorkingSetCounter {
    param([Parameter(Mandatory)][int]$ProcessId)

    try {
        $processName = [System.Diagnostics.Process]::GetProcessById($ProcessId).ProcessName
        $category = [System.Diagnostics.PerformanceCounterCategory]::new('Process')
        $instanceNames = @(
            $category.GetInstanceNames() |
                Where-Object {
                    $_.Equals($processName, [StringComparison]::OrdinalIgnoreCase) -or
                    $_.StartsWith($processName + '#', [StringComparison]::OrdinalIgnoreCase)
                }
        )
        foreach ($instanceName in $instanceNames) {
            $idCounter = $null
            try {
                $idCounter = [System.Diagnostics.PerformanceCounter]::new(
                    'Process',
                    'ID Process',
                    $instanceName,
                    $true)
                if ([int]$idCounter.NextValue() -ne $ProcessId) {
                    continue
                }

                $counter = [System.Diagnostics.PerformanceCounter]::new(
                    'Process',
                    'Working Set - Private',
                    $instanceName,
                    $true)
                [void]$counter.NextValue()
                return [pscustomobject]@{
                    Counter = $counter
                    Reason = $null
                }
            }
            catch {
                # Process performance-counter instances can disappear between
                # enumeration and sampling. Continue through only this process
                # name's candidates rather than aborting the timed run.
                continue
            }
            finally {
                if ($null -ne $idCounter) {
                    $idCounter.Dispose()
                }
            }
        }

        return [pscustomobject]@{
            Counter = $null
            Reason = "No '$processName' Performance Counter instance matched process id $ProcessId."
        }
    }
    catch {
        return [pscustomobject]@{
            Counter = $null
            Reason = "Windows private-working-set counter unavailable: $($_.Exception.Message)"
        }
    }
}

function Get-FileRecord {
    param(
        [Parameter(Mandatory)][string]$SessionRoot,
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][System.IO.FileInfo]$File
    )

    return [pscustomobject][ordered]@{
        path = [System.IO.Path]::GetRelativePath($SessionRoot, $File.FullName)
        runRelativePath = [System.IO.Path]::GetRelativePath($RunRoot, $File.FullName)
        length = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
    }
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Add-FileLocValues {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$Values,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[string]]$InvalidValues
    )

    switch ($Element.ValueKind) {
        ([System.Text.Json.JsonValueKind]::Object) {
            foreach ($property in $Element.EnumerateObject()) {
                if ($property.Name -ceq 'fileLoc') {
                    if ($property.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Number) {
                        [void]$Values.Add($property.Value.GetRawText())
                    }
                    else {
                        [void]$InvalidValues.Add(
                            "fileLoc has non-numeric JSON kind $($property.Value.ValueKind)")
                    }
                }
                else {
                    Add-FileLocValues -Element $property.Value -Values $Values -InvalidValues $InvalidValues
                }
            }
            break
        }
        ([System.Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Element.EnumerateArray()) {
                Add-FileLocValues -Element $item -Values $Values -InvalidValues $InvalidValues
            }
            break
        }
    }
}

function Get-FileLocEvidence {
    param(
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][System.IO.FileInfo[]]$OutputFiles
    )

    $jsonFiles = @(
        $OutputFiles |
            Where-Object { $_.Name.EndsWith('.tbc.json', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object FullName
    )
    if ($jsonFiles.Count -eq 0) {
        return [pscustomobject][ordered]@{
            count = $null
            sha256 = $null
            files = @()
            unavailableReason = 'No *.tbc.json output was produced.'
            encoding = 'Run-relative path, tab, raw JSON numeric token, LF; document traversal order.'
        }
    }

    $combinedValues = [System.Collections.Generic.List[string]]::new()
    $fileRecords = [System.Collections.Generic.List[object]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($jsonFile in $jsonFiles) {
        $relativePath = [System.IO.Path]::GetRelativePath($RunRoot, $jsonFile.FullName)
        $values = [System.Collections.Generic.List[string]]::new()
        $invalidValues = [System.Collections.Generic.List[string]]::new()
        $document = $null
        try {
            $jsonText = [System.IO.File]::ReadAllText(
                $jsonFile.FullName,
                [System.Text.UTF8Encoding]::new($false, $true))
            $options = [System.Text.Json.JsonDocumentOptions]::new()
            $options.AllowTrailingCommas = $true
            $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
            $options.MaxDepth = 1024
            $document = [System.Text.Json.JsonDocument]::Parse($jsonText, $options)
            Add-FileLocValues `
                -Element $document.RootElement `
                -Values $values `
                -InvalidValues $invalidValues
            if ($invalidValues.Count -gt 0) {
                throw "Non-numeric fileLoc values: $($invalidValues -join '; ')"
            }

            foreach ($value in $values) {
                [void]$combinedValues.Add("$relativePath`t$value")
            }
            $fileRecords.Add([pscustomobject][ordered]@{
                path = $relativePath
                count = $values.Count
                sha256 = Get-TextSha256 -Text ($values -join "`n")
                unavailableReason = $null
            })
        }
        catch {
            $reason = "$relativePath could not be parsed reliably: $($_.Exception.Message)"
            [void]$errors.Add($reason)
            $fileRecords.Add([pscustomobject][ordered]@{
                path = $relativePath
                count = $null
                sha256 = $null
                unavailableReason = $reason
            })
        }
        finally {
            if ($null -ne $document) {
                $document.Dispose()
            }
        }
    }

    if ($errors.Count -gt 0) {
        return [pscustomobject][ordered]@{
            count = $null
            sha256 = $null
            files = $fileRecords.ToArray()
            unavailableReason = $errors -join ' | '
            encoding = 'Run-relative path, tab, raw JSON numeric token, LF; document traversal order.'
        }
    }

    return [pscustomobject][ordered]@{
        count = $combinedValues.Count
        sha256 = Get-TextSha256 -Text ($combinedValues -join "`n")
        files = $fileRecords.ToArray()
        unavailableReason = $null
        encoding = 'Run-relative path, tab, raw JSON numeric token, LF; document traversal order.'
    }
}

function Get-NormalizedLogEvidence {
    param(
        [Parameter(Mandatory)][string]$RunRoot,
        [Parameter(Mandatory)][System.IO.FileInfo[]]$OutputFiles,
        [Parameter(Mandatory)][object[]]$ArtifactRecords
    )

    $artifactsByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($artifact in $ArtifactRecords) {
        $artifactsByPath[[string]$artifact.runRelativePath] = $artifact
    }

    $timestampPattern = '(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3}(?= - lddecode - )'
    $ippDiagnosticPattern = '(?m)^<timestamp> - lddecode - INFO - Intel IPP DSP backend:.*(?:\r?\n|$)'
    return @(
        foreach ($logFile in $OutputFiles |
            Where-Object { $_.Extension.Equals('.log', [StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object FullName) {
            $relativePath = [System.IO.Path]::GetRelativePath($RunRoot, $logFile.FullName)
            $rawArtifact = $artifactsByPath[$relativePath]
            try {
                $rawBytes = [System.IO.File]::ReadAllBytes($logFile.FullName)
                $rawText = [System.Text.UTF8Encoding]::new($false, $true).GetString($rawBytes)
                $timestampMatches = [regex]::Matches($rawText, $timestampPattern).Count
                $normalizedText = [regex]::Replace($rawText, $timestampPattern, '<timestamp>')
                $ippDiagnosticMatches = [regex]::Matches(
                    $normalizedText,
                    $ippDiagnosticPattern).Count
                $normalizedText = [regex]::Replace(
                    $normalizedText,
                    $ippDiagnosticPattern,
                    '')
                [pscustomobject][ordered]@{
                    path = $relativePath
                    rawLength = $rawArtifact.length
                    rawSha256 = $rawArtifact.sha256
                    normalizedLength = [System.Text.UTF8Encoding]::new($false).GetByteCount($normalizedText)
                    normalizedSha256 = Get-TextSha256 -Text $normalizedText
                    normalizedTimestampCount = $timestampMatches
                    normalizedIppDiagnosticCount = $ippDiagnosticMatches
                    normalization = 'Replace lddecode record timestamps and remove the expected explicit Intel IPP backend info record.'
                    unavailableReason = $null
                }
            }
            catch {
                [pscustomobject][ordered]@{
                    path = $relativePath
                    rawLength = $rawArtifact.length
                    rawSha256 = $rawArtifact.sha256
                    normalizedLength = $null
                    normalizedSha256 = $null
                    normalizedTimestampCount = $null
                    normalizedIppDiagnosticCount = $null
                    normalization = 'Replace lddecode record timestamps and remove the expected explicit Intel IPP backend info record.'
                    unavailableReason = "Log could not be decoded and normalized reliably: $($_.Exception.Message)"
                }
            }
        }
    )
}

function Compare-ArtifactManifests {
    param(
        [Parameter(Mandatory)][object[]]$ExactArtifacts,
        [Parameter(Mandatory)][object[]]$IppArtifacts
    )

    $exactByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $ippByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($artifact in $ExactArtifacts) {
        $exactByPath[[string]$artifact.runRelativePath] = $artifact
    }
    foreach ($artifact in $IppArtifacts) {
        $ippByPath[[string]$artifact.runRelativePath] = $artifact
    }

    $paths = @(@($exactByPath.Keys) + @($ippByPath.Keys) | Sort-Object -Unique)
    $differences = @(
        foreach ($path in $paths) {
            $exactArtifact = if ($exactByPath.ContainsKey($path)) { $exactByPath[$path] } else { $null }
            $ippArtifact = if ($ippByPath.ContainsKey($path)) { $ippByPath[$path] } else { $null }
            if ($null -eq $exactArtifact -or
                $null -eq $ippArtifact -or
                $exactArtifact.length -ne $ippArtifact.length -or
                $exactArtifact.sha256 -ne $ippArtifact.sha256) {
                [pscustomobject][ordered]@{
                    path = $path
                    exact = $exactArtifact
                    ippFast = $ippArtifact
                }
            }
        }
    )
    return [pscustomobject][ordered]@{
        equal = $differences.Count -eq 0
        exactCount = $ExactArtifacts.Count
        ippFastCount = $IppArtifacts.Count
        differences = $differences
    }
}

function Compare-NormalizedLogManifests {
    param(
        [Parameter(Mandatory)][object[]]$ExactLogs,
        [Parameter(Mandatory)][object[]]$IppLogs
    )

    $exactByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $ippByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($log in $ExactLogs) {
        $exactByPath[[string]$log.path] = $log
    }
    foreach ($log in $IppLogs) {
        $ippByPath[[string]$log.path] = $log
    }

    if ($ExactLogs.Count -eq 0 -and $IppLogs.Count -eq 0) {
        return [pscustomobject][ordered]@{
            equal = $null
            exactCount = 0
            ippFastCount = 0
            differences = @()
            unavailableReason = 'Neither run produced a .log artifact.'
        }
    }

    $paths = @(@($exactByPath.Keys) + @($ippByPath.Keys) | Sort-Object -Unique)
    $unavailableReasons = [System.Collections.Generic.List[string]]::new()
    $differences = @(
        foreach ($path in $paths) {
            $exactLog = if ($exactByPath.ContainsKey($path)) { $exactByPath[$path] } else { $null }
            $ippLog = if ($ippByPath.ContainsKey($path)) { $ippByPath[$path] } else { $null }
            if ($null -eq $exactLog -or $null -eq $ippLog) {
                [pscustomobject][ordered]@{ path = $path; exact = $exactLog; ippFast = $ippLog }
                continue
            }
            if ($null -ne $exactLog.unavailableReason -or $null -ne $ippLog.unavailableReason) {
                [void]$unavailableReasons.Add(
                    "${path}: exact=$($exactLog.unavailableReason); ipp-fast=$($ippLog.unavailableReason)")
                continue
            }
            if ($exactLog.normalizedSha256 -ne $ippLog.normalizedSha256) {
                [pscustomobject][ordered]@{ path = $path; exact = $exactLog; ippFast = $ippLog }
            }
        }
    )

    return [pscustomobject][ordered]@{
        equal = if ($unavailableReasons.Count -gt 0) { $null } else { $differences.Count -eq 0 }
        exactCount = $ExactLogs.Count
        ippFastCount = $IppLogs.Count
        differences = $differences
        unavailableReason = if ($unavailableReasons.Count -gt 0) {
            $unavailableReasons -join ' | '
        }
        else {
            $null
        }
    }
}

function Complete-RunArtifacts {
    param(
        [Parameter(Mandatory)][pscustomobject]$Result,
        [Parameter(Mandatory)][string]$SessionRoot
    )

    $runRoot = [string]$Result.runDirectory
    $stdoutFullPath = Join-Path $SessionRoot ([string]$Result.stdout.path)
    $stderrFullPath = Join-Path $SessionRoot ([string]$Result.stderr.path)
    $outputFiles = @(
        Get-ChildItem -LiteralPath $runRoot -File -Recurse |
            Where-Object {
                $_.FullName -ne $stdoutFullPath -and
                $_.FullName -ne $stderrFullPath
            } |
            Sort-Object FullName
    )

    $artifactRecords = @(
        foreach ($file in $outputFiles) {
            Get-FileRecord -SessionRoot $SessionRoot -RunRoot $runRoot -File $file
        }
    )
    $Result.artifacts = $artifactRecords
    $Result.fileLocEvidence = Get-FileLocEvidence -RunRoot $runRoot -OutputFiles $outputFiles
    $Result.normalizedLogs = Get-NormalizedLogEvidence `
        -RunRoot $runRoot `
        -OutputFiles $outputFiles `
        -ArtifactRecords $artifactRecords

    $expectedPrimaryPath = if ($Subcommand -eq 'hifi') {
        [string]$Result.outputBase
    }
    else {
        [string]$Result.outputBase + '.tbc'
    }
    $expectedPrimaryFullPath = Get-FullPath $expectedPrimaryPath
    $primaryFile = $outputFiles |
        Where-Object { $_.FullName -eq $expectedPrimaryFullPath } |
        Select-Object -First 1
    if ($null -eq $primaryFile) {
        $primaryFile = $outputFiles |
            Sort-Object Length -Descending |
            Select-Object -First 1
    }

    $Result.primaryOutput = if ($null -eq $primaryFile) {
        $null
    }
    else {
        Get-FileRecord -SessionRoot $SessionRoot -RunRoot $runRoot -File $primaryFile
    }

    foreach ($streamName in @('stdout', 'stderr')) {
        $stream = $Result.$streamName
        $streamFullPath = Join-Path $SessionRoot ([string]$stream.path)
        $streamFile = Get-Item -LiteralPath $streamFullPath
        $stream.length = $streamFile.Length
        $stream.sha256 = (Get-FileHash -LiteralPath $streamFullPath -Algorithm SHA256).Hash
    }

    $Result.PSObject.Properties.Remove('runDirectory')
}

function Invoke-DecodeRun {
    param(
        [Parameter(Mandatory)][ValidateSet('warmup', 'measured')][string]$Phase,
        [Parameter(Mandatory)][int]$PairNumber,
        [Parameter(Mandatory)][int]$PositionInPair,
        [Parameter(Mandatory)][int]$Sequence,
        [Parameter(Mandatory)][string]$Backend,
        [Parameter(Mandatory)][string]$SessionRoot
    )

    $directoryName = '{0}-{1:D3}-{2:D2}-{3}' -f $Phase, $PairNumber, $PositionInPair, $Backend
    $runDirectory = New-UniqueDirectory -Parent $SessionRoot -Path (Join-Path $SessionRoot $directoryName)
    $outputBase = if ($Subcommand -eq 'hifi') {
        Join-Path $runDirectory 'decode-output.wav'
    }
    else {
        Join-Path $runDirectory 'decode-output'
    }
    [void](Assert-StrictChildPath -Parent $SessionRoot -Candidate $outputBase)

    $arguments = Get-DecodeArguments -Backend $Backend -OutputBase $outputBase
    $processStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $script:DecodeExeFullPath
    $processStartInfo.WorkingDirectory = $runDirectory
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.CreateNoWindow = $true
    $processStartInfo.RedirectStandardOutput = $true
    $processStartInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) {
        $processStartInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processStartInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::new()
    $stdout = ''
    $stderr = ''
    $exitCode = $null
    $startError = $null
    $cpuSeconds = $null
    $peakWorkingSetBytes = $null
    $peakPrivateBytes = $null
    $peakPrivateWorkingSetBytes = $null
    $privateWorkingSetReason = $null
    $privateWorkingSetCounter = $null

    try {
        $stopwatch.Start()
        if (-not $process.Start()) {
            throw 'Process.Start returned false.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $privateCounterResult = New-PrivateWorkingSetCounter -ProcessId $process.Id
        $privateWorkingSetCounter = $privateCounterResult.Counter
        $privateWorkingSetReason = $privateCounterResult.Reason

        [long]$peakWorkingSet = 0
        [long]$peakPrivate = 0
        [long]$peakPrivateWorkingSet = 0
        while (-not $process.WaitForExit(100)) {
            try {
                $process.Refresh()
                $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
                $peakPrivate = [Math]::Max($peakPrivate, $process.PrivateMemorySize64)
                if ($null -ne $privateWorkingSetCounter) {
                    $peakPrivateWorkingSet = [Math]::Max(
                        $peakPrivateWorkingSet,
                        [long]$privateWorkingSetCounter.NextValue())
                }
            }
            catch {
                if ($null -eq $privateWorkingSetReason) {
                    $privateWorkingSetReason = "Private working-set sampling stopped: $($_.Exception.Message)"
                }
                if ($null -ne $privateWorkingSetCounter) {
                    $privateWorkingSetCounter.Dispose()
                    $privateWorkingSetCounter = $null
                }
            }
        }

        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $stopwatch.Stop()
        $process.Refresh()
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
        $peakPrivate = [Math]::Max($peakPrivate, $process.PrivateMemorySize64)
        $exitCode = $process.ExitCode
        $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        $peakWorkingSetBytes = $peakWorkingSet
        $peakPrivateBytes = $peakPrivate
        if ($null -ne $privateWorkingSetCounter) {
            try {
                $peakPrivateWorkingSet = [Math]::Max(
                    $peakPrivateWorkingSet,
                    [long]$privateWorkingSetCounter.NextValue())
            }
            catch {
                if ($null -eq $privateWorkingSetReason) {
                    $privateWorkingSetReason = "Final private working-set sample unavailable: $($_.Exception.Message)"
                }
            }
        }
        if ($peakPrivateWorkingSet -gt 0) {
            $peakPrivateWorkingSetBytes = $peakPrivateWorkingSet
        }
        elseif ($null -eq $privateWorkingSetReason) {
            $privateWorkingSetReason = 'Windows private-working-set counter returned no samples.'
        }
    }
    catch {
        if ($stopwatch.IsRunning) {
            $stopwatch.Stop()
        }
        $startError = $_.Exception.ToString()
        $stderr = $startError
    }
    finally {
        if ($null -ne $privateWorkingSetCounter) {
            $privateWorkingSetCounter.Dispose()
        }
        $process.Dispose()
    }

    $stdoutPath = Join-Path $runDirectory 'stdout.txt'
    $stderrPath = Join-Path $runDirectory 'stderr.txt'
    Write-NewTextFile -SessionRoot $SessionRoot -Path $stdoutPath -Content $stdout
    Write-NewTextFile -SessionRoot $SessionRoot -Path $stderrPath -Content $stderr

    return [pscustomobject][ordered]@{
        phase = $Phase
        pair = $PairNumber
        positionInPair = $PositionInPair
        sequence = $Sequence
        backend = $Backend
        command = @($script:DecodeExeFullPath) + $arguments
        commandDisplay = (@($script:DecodeExeFullPath) + $arguments |
            ForEach-Object { Format-CommandToken $_ }) -join ' '
        outputBase = $outputBase
        exitCode = $exitCode
        startError = $startError
        wallSeconds = $stopwatch.Elapsed.TotalSeconds
        cpuSeconds = $cpuSeconds
        peakWorkingSetBytes = $peakWorkingSetBytes
        peakPrivateBytes = $peakPrivateBytes
        peakPrivateWorkingSetBytes = $peakPrivateWorkingSetBytes
        privateWorkingSetUnavailableReason = $privateWorkingSetReason
        resourceMetricScope = 'decode process only; descendant processes are not aggregated'
        allocationMetrics = [pscustomobject][ordered]@{
            managedAllocatedBytes = $null
            nativeAllocatedBytes = $null
            gcCollections = $null
            unavailableReason = 'This harness does not collect EventPipe or ETW allocation/GC traces; process counters cannot provide these values reliably.'
        }
        stdout = [pscustomobject][ordered]@{
            path = [System.IO.Path]::GetRelativePath($SessionRoot, $stdoutPath)
            length = $null
            sha256 = $null
        }
        stderr = [pscustomobject][ordered]@{
            path = [System.IO.Path]::GetRelativePath($SessionRoot, $stderrPath)
            length = $null
            sha256 = $null
        }
        artifacts = @()
        primaryOutput = $null
        fileLocEvidence = $null
        normalizedLogs = @()
        runDirectory = $runDirectory
    }
}

function Get-Median {
    param([double[]]$Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return $null
    }

    [double[]]$sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2.0)
    if (($sorted.Count % 2) -eq 1) {
        return $sorted[$middle]
    }

    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Format-OptionalNumber {
    param(
        [AllowNull()][object]$Value,
        [Parameter(Mandatory)][string]$Format
    )

    if ($null -eq $Value) {
        return 'n/a'
    }

    return ([double]$Value).ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}

function New-BackendStatistics {
    param(
        [Parameter(Mandatory)][string]$Backend,
        [Parameter(Mandatory)][object[]]$Results
    )

    $backendResults = @(
        $Results | Where-Object { $_.backend -eq $Backend -and $_.exitCode -eq 0 }
    )
    return [pscustomobject][ordered]@{
        backend = $Backend
        successfulRuns = $backendResults.Count
        medianWallSeconds = Get-Median @($backendResults | ForEach-Object { [double]$_.wallSeconds })
        medianCpuSeconds = Get-Median @($backendResults | ForEach-Object { [double]$_.cpuSeconds })
        medianPeakWorkingSetBytes = Get-Median @(
            $backendResults |
                Where-Object { $null -ne $_.peakWorkingSetBytes } |
                ForEach-Object { [double]$_.peakWorkingSetBytes }
        )
        medianPeakPrivateBytes = Get-Median @(
            $backendResults |
                Where-Object { $null -ne $_.peakPrivateBytes } |
                ForEach-Object { [double]$_.peakPrivateBytes }
        )
        medianPeakPrivateWorkingSetBytes = Get-Median @(
            $backendResults |
                Where-Object { $null -ne $_.peakPrivateWorkingSetBytes } |
                ForEach-Object { [double]$_.peakPrivateWorkingSetBytes }
        )
    }
}

$script:DecodeExeFullPath = Get-FullPath $DecodeExe
if (-not (Test-Path -LiteralPath $script:DecodeExeFullPath -PathType Leaf)) {
    throw "Decode executable was not found: $($script:DecodeExeFullPath)"
}

$script:InputFullPath = Get-FullPath $InputPath
if (-not (Test-Path -LiteralPath $script:InputFullPath -PathType Leaf)) {
    throw "Benchmark input was not found: $($script:InputFullPath)"
}

$artifactRootFullPath = Get-FullPath $ArtifactRoot
if ($artifactRootFullPath -eq [System.IO.Path]::GetPathRoot($artifactRootFullPath)) {
    throw 'ArtifactRoot must not be a filesystem root.'
}
if (Test-Path -LiteralPath $artifactRootFullPath -PathType Leaf) {
    throw "ArtifactRoot points to a file: $artifactRootFullPath"
}
if (Test-Path -LiteralPath $artifactRootFullPath -PathType Container) {
    $existingArtifactRoot = Get-Item -LiteralPath $artifactRootFullPath -Force
    if (($existingArtifactRoot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ArtifactRoot must not be a reparse point: $artifactRootFullPath"
    }
}

$reservedArguments = @(
    '--dsp-backend', '--threads', '-t', '--length', '-l', '--overwrite',
    '--write-test-ldf'
)
foreach ($argument in $ForwardArguments) {
    if ([string]::IsNullOrWhiteSpace($argument)) {
        throw 'ForwardArguments must not contain empty tokens.'
    }

    $isReserved = $reservedArguments -contains $argument
    $usesReservedAssignment = $argument -match '^(--dsp-backend|--threads|--length|--write-test-ldf)='
    if ($isReserved -or $usesReservedAssignment) {
        throw "ForwardArguments cannot override harness-controlled or externally writing option '$argument'."
    }
}

if ($Subcommand -eq 'hifi') {
    if ($PSBoundParameters.ContainsKey('Frames')) {
        throw 'HiFi has no --length frame option; omit Frames for a HiFi benchmark.'
    }
    $script:EffectiveFrames = $null
}
else {
    $script:EffectiveFrames = if ($PSBoundParameters.ContainsKey('Frames')) { $Frames } else { 160 }
}

$sessionName = 'ipp-ab-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$sessionRoot = Assert-StrictChildPath -Parent $artifactRootFullPath -Candidate (Join-Path $artifactRootFullPath $sessionName)

$planEntries = [System.Collections.Generic.List[object]]::new()
$sequence = 0
foreach ($phaseDefinition in @(
    [pscustomobject]@{ Phase = 'warmup'; PairCount = $Warmup },
    [pscustomobject]@{ Phase = 'measured'; PairCount = $Runs }
)) {
    for ($pair = 1; $pair -le $phaseDefinition.PairCount; $pair++) {
        $position = 0
        foreach ($backend in Get-BackendOrder -PairNumber $pair) {
            $sequence++
            $position++
            $directoryName = '{0}-{1:D3}-{2:D2}-{3}' -f $phaseDefinition.Phase, $pair, $position, $backend
            $plannedRunDirectory = Join-Path $sessionRoot $directoryName
            $plannedOutputBase = if ($Subcommand -eq 'hifi') {
                Join-Path $plannedRunDirectory 'decode-output.wav'
            }
            else {
                Join-Path $plannedRunDirectory 'decode-output'
            }
            $plannedArguments = Get-DecodeArguments -Backend $backend -OutputBase $plannedOutputBase
            $planEntries.Add([pscustomobject][ordered]@{
                phase = $phaseDefinition.Phase
                pair = $pair
                positionInPair = $position
                sequence = $sequence
                backend = $backend
                runDirectory = $plannedRunDirectory
                command = @($script:DecodeExeFullPath) + $plannedArguments
            })
        }
    }
}

$plan = [pscustomobject][ordered]@{
    schemaVersion = 1
    dryRun = [bool]$DryRun
    decodeExe = $script:DecodeExeFullPath
    subcommand = $Subcommand
    inputPath = $script:InputFullPath
    artifactRoot = $artifactRootFullPath
    sessionRoot = $sessionRoot
    frames = $script:EffectiveFrames
    runs = $Runs
    warmup = $Warmup
    threads = $Threads
    gateMode = $GateMode
    minimumGainPercent = $MinimumGainPercent
    maximumRegressionPercent = $MaximumRegressionPercent
    orderPolicy = 'Odd pairs exact then ipp-fast; even pairs ipp-fast then exact.'
    entries = $planEntries.ToArray()
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 8
    return
}

if (-not (Test-Path -LiteralPath $artifactRootFullPath)) {
    New-Item -ItemType Directory -Path $artifactRootFullPath | Out-Null
}
$artifactRootItem = Get-Item -LiteralPath $artifactRootFullPath
if (-not $artifactRootItem.PSIsContainer) {
    throw "ArtifactRoot is not a directory: $artifactRootFullPath"
}
$sessionRoot = New-UniqueDirectory -Parent $artifactRootFullPath -Path $sessionRoot

$runResults = [System.Collections.Generic.List[object]]::new()
$executionFailure = $null
foreach ($entry in $planEntries) {
    Write-Host ('[{0}/{1}] {2} pair {3}, position {4}: {5}' -f
        $entry.sequence,
        $planEntries.Count,
        $entry.phase,
        $entry.pair,
        $entry.positionInPair,
        $entry.backend)

    $result = Invoke-DecodeRun `
        -Phase $entry.phase `
        -PairNumber $entry.pair `
        -PositionInPair $entry.positionInPair `
        -Sequence $entry.sequence `
        -Backend $entry.backend `
        -SessionRoot $sessionRoot
    $runResults.Add($result)
    if ($null -eq $result.exitCode -or $result.exitCode -ne 0) {
        $executionFailure = "Run $($entry.sequence) ($($entry.backend)) failed; see $($result.stderr.path)."
        break
    }
}

Write-Host 'Hashing retained benchmark artifacts after all decode timings are complete.'
foreach ($result in $runResults) {
    Complete-RunArtifacts -Result $result -SessionRoot $sessionRoot
}

$measuredResults = @($runResults | Where-Object { $_.phase -eq 'measured' })
$pairComparisons = [System.Collections.Generic.List[object]]::new()
for ($pair = 1; $pair -le $Runs; $pair++) {
    $exactResult = $measuredResults |
        Where-Object { $_.pair -eq $pair -and $_.backend -eq 'exact' -and $_.exitCode -eq 0 } |
        Select-Object -First 1
    $ippResult = $measuredResults |
        Where-Object { $_.pair -eq $pair -and $_.backend -eq 'ipp-fast' -and $_.exitCode -eq 0 } |
        Select-Object -First 1
    if ($null -eq $exactResult -or $null -eq $ippResult) {
        continue
    }

    $wallGainPercent = if ($exactResult.wallSeconds -gt 0) {
        (($exactResult.wallSeconds - $ippResult.wallSeconds) / $exactResult.wallSeconds) * 100.0
    }
    else {
        $null
    }
    $primaryEqual = $null
    if ($null -ne $exactResult.primaryOutput -and $null -ne $ippResult.primaryOutput) {
        $primaryEqual =
            $exactResult.primaryOutput.length -eq $ippResult.primaryOutput.length -and
            $exactResult.primaryOutput.sha256 -eq $ippResult.primaryOutput.sha256
    }
    $artifactManifestComparison = Compare-ArtifactManifests `
        -ExactArtifacts $exactResult.artifacts `
        -IppArtifacts $ippResult.artifacts
    $fileLocEqual = if ($null -eq $exactResult.fileLocEvidence.sha256 -or
        $null -eq $ippResult.fileLocEvidence.sha256) {
        $null
    }
    else {
        $exactResult.fileLocEvidence.count -eq $ippResult.fileLocEvidence.count -and
        $exactResult.fileLocEvidence.sha256 -eq $ippResult.fileLocEvidence.sha256
    }
    $normalizedLogComparison = Compare-NormalizedLogManifests `
        -ExactLogs $exactResult.normalizedLogs `
        -IppLogs $ippResult.normalizedLogs

    $pairComparisons.Add([pscustomobject][ordered]@{
        pair = $pair
        order = @(
            $measuredResults |
                Where-Object { $_.pair -eq $pair } |
                Sort-Object positionInPair |
                ForEach-Object { $_.backend }
        )
        exactWallSeconds = $exactResult.wallSeconds
        ippFastWallSeconds = $ippResult.wallSeconds
        wallGainPercent = $wallGainPercent
        primaryOutputByteIdentical = $primaryEqual
        artifactManifest = $artifactManifestComparison
        fileLoc = [pscustomobject][ordered]@{
            equal = $fileLocEqual
            exact = $exactResult.fileLocEvidence
            ippFast = $ippResult.fileLocEvidence
        }
        normalizedLogs = $normalizedLogComparison
    })
}

$pairedMedianGainPercent = Get-Median @(
    $pairComparisons |
        Where-Object { $null -ne $_.wallGainPercent } |
        ForEach-Object { [double]$_.wallGainPercent }
)
$exactStatistics = New-BackendStatistics -Backend 'exact' -Results $measuredResults
$ippStatistics = New-BackendStatistics -Backend 'ipp-fast' -Results $measuredResults
$allMeasuredRunsSuccessful =
    $null -eq $executionFailure -and
    $measuredResults.Count -eq ($Runs * 2) -and
    @($measuredResults | Where-Object { $_.exitCode -ne 0 }).Count -eq 0
$gainPass = $null -ne $pairedMedianGainPercent -and $pairedMedianGainPercent -ge $MinimumGainPercent
$regressionPass = $null -ne $pairedMedianGainPercent -and $pairedMedianGainPercent -ge (-$MaximumRegressionPercent)
$thresholdPass = switch ($GateMode) {
    'Gain' { $gainPass }
    'Regression' { $regressionPass }
    'Both' { $gainPass -and $regressionPass }
}
$overallPass = $allMeasuredRunsSuccessful -and $thresholdPass

$inputFile = Get-Item -LiteralPath $script:InputFullPath
$decodeFile = Get-Item -LiteralPath $script:DecodeExeFullPath
$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('O')
    passed = $overallPass
    executionFailure = $executionFailure
    sessionRoot = $sessionRoot
    decodeExecutable = [pscustomobject][ordered]@{
        path = $script:DecodeExeFullPath
        length = $decodeFile.Length
        sha256 = (Get-FileHash -LiteralPath $script:DecodeExeFullPath -Algorithm SHA256).Hash
        fileVersion = $decodeFile.VersionInfo.FileVersion
    }
    input = [pscustomobject][ordered]@{
        path = $script:InputFullPath
        length = $inputFile.Length
        lastWriteTimeUtc = $inputFile.LastWriteTimeUtc.ToString('O')
        sha256 = $null
        hashUnavailableReason = 'The input is not hashed to avoid an additional full-capture read that could perturb cache state.'
    }
    configuration = [pscustomobject][ordered]@{
        subcommand = $Subcommand
        frames = $script:EffectiveFrames
        runs = $Runs
        warmup = $Warmup
        threads = $Threads
        forwardArguments = $ForwardArguments
        orderPolicy = $plan.orderPolicy
    }
    resourceMetrics = [pscustomobject][ordered]@{
        scope = 'decode process only'
        descendantProcessesAggregated = $false
        limitationReason = 'The harness does not assign the process tree to a Windows Job Object; FFmpeg or other descendants are excluded from CPU and memory metrics.'
        managedAllocatedBytes = $null
        nativeAllocatedBytes = $null
        gcCollections = $null
        allocationAndGcUnavailableReason = 'No EventPipe or ETW trace is collected, so managed/native allocation and GC values cannot be reported reliably.'
    }
    statistics = [pscustomobject][ordered]@{
        exact = $exactStatistics
        ippFast = $ippStatistics
        pairedMedianWallGainPercent = $pairedMedianGainPercent
    }
    gate = [pscustomobject][ordered]@{
        mode = $GateMode
        metric = 'median of per-pair wall-time gain percentages'
        minimumGainPercent = $MinimumGainPercent
        maximumRegressionPercent = $MaximumRegressionPercent
        gainPass = $gainPass
        regressionPass = $regressionPass
        allMeasuredRunsSuccessful = $allMeasuredRunsSuccessful
        passed = $overallPass
    }
    compatibility = [pscustomobject][ordered]@{
        note = 'Manifests, fileLoc sequence digests, and normalized-log hashes are screening evidence only. They do not replace formal field alignment by source fileLoc, semantic metadata comparison, sidecar lifecycle checks, or diagnostic-sequence review.'
        allComparedPrimaryOutputsByteIdentical =
            $pairComparisons.Count -eq $Runs -and
            @($pairComparisons | Where-Object { $_.primaryOutputByteIdentical -ne $true }).Count -eq 0
        allComparedArtifactManifestsByteIdentical =
            $pairComparisons.Count -eq $Runs -and
            @($pairComparisons | Where-Object { $_.artifactManifest.equal -ne $true }).Count -eq 0
        allFileLocDigestsEqual = if (
            $pairComparisons.Count -ne $Runs -or
            @($pairComparisons | Where-Object { $null -eq $_.fileLoc.equal }).Count -gt 0) {
            $null
        }
        else {
            @($pairComparisons | Where-Object { $_.fileLoc.equal -ne $true }).Count -eq 0
        }
        allNormalizedLogsEqual = if (
            $pairComparisons.Count -ne $Runs -or
            @($pairComparisons | Where-Object { $null -eq $_.normalizedLogs.equal }).Count -gt 0) {
            $null
        }
        else {
            @($pairComparisons | Where-Object { $_.normalizedLogs.equal -ne $true }).Count -eq 0
        }
        pairs = $pairComparisons.ToArray()
    }
    runs = $runResults.ToArray()
}

$reportPath = Join-Path $sessionRoot 'benchmark-results.json'
$summaryPath = Join-Path $sessionRoot 'summary.txt'
$reportJson = $report | ConvertTo-Json -Depth 12
Write-NewTextFile -SessionRoot $sessionRoot -Path $reportPath -Content ($reportJson + [Environment]::NewLine)

$statusText = if ($overallPass) { 'PASS' } else { 'FAIL' }
$exactMedianWallText = Format-OptionalNumber -Value $exactStatistics.medianWallSeconds -Format 'F3'
$ippMedianWallText = Format-OptionalNumber -Value $ippStatistics.medianWallSeconds -Format 'F3'
$pairedMedianGainText = Format-OptionalNumber -Value $pairedMedianGainPercent -Format 'F2'
$summaryLines = @(
    "Intel IPP A/B benchmark: $statusText",
    "Measured pairs: $($pairComparisons.Count)/$Runs (alternating A/B and B/A)",
    "Exact median wall: $exactMedianWallText s",
    "IPP Fast median wall: $ippMedianWallText s",
    "Paired median wall gain: $pairedMedianGainText%",
    "Gate: $GateMode; gain >= $MinimumGainPercent%; regression <= $MaximumRegressionPercent%",
    "Resource scope: decode process only; child processes are not aggregated",
    "Managed/native allocations and GC: unavailable (no EventPipe/ETW trace)",
    "JSON report: $reportPath"
)
if ($null -ne $executionFailure) {
    $summaryLines += "Execution failure: $executionFailure"
}
$summaryText = $summaryLines -join [Environment]::NewLine
Write-NewTextFile -SessionRoot $sessionRoot -Path $summaryPath -Content ($summaryText + [Environment]::NewLine)
Write-Host $summaryText

if (-not $overallPass) {
    throw "Intel IPP A/B benchmark gate failed. See $reportPath"
}
