[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$harness = Join-Path $repositoryRoot 'eng\cuda\Compare-CudaDecode.ps1'
$fakeDecoder = Join-Path $PSScriptRoot 'Fake-CudaQualificationDecoder.ps1'
$pwsh = (Get-Process -Id $PID).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'vhsdecode-cuda-harness-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$previousHarnessSelfTest = [Environment]::GetEnvironmentVariable(
    'VHSDECODE_CUDA_HARNESS_SELF_TEST',
    [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable(
    'VHSDECODE_CUDA_HARNESS_SELF_TEST',
    '1',
    [EnvironmentVariableTarget]::Process)

try {
    $inputPath = Join-Path $testRoot 'input.bin'
    [IO.File]::WriteAllBytes($inputPath, [byte[]] @(9, 8, 7, 6))

    $matchingReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'matching') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'same') `
        -GpuSampleIntervalMs 100
    $matchingReport = Get-Content -LiteralPath $matchingReportPath -Raw | ConvertFrom-Json
    Assert-True $matchingReport.qualificationPassed `
        'Allowed timestamp, backend, elapsed, and rate diagnostics should qualify.'
    Assert-True (-not $matchingReport.outputComparison.allFilesRawMatch) `
        'The fake backend log should retain a raw mismatch.'
    Assert-True $matchingReport.outputComparison.allFilesQualificationMatch `
        'The normalized fake backend log should qualify.'
    Assert-True (-not $matchingReport.consoleComparison.stderr.rawEqual) `
        'Fake stderr should retain its raw backend mismatch.'
    Assert-True $matchingReport.consoleComparison.stderr.normalizedEqual `
        'Fake stderr should match after allowed normalization.'
    Assert-True $matchingReport.binaryArtifactEvidence.harnessSelfTestMode `
        'The repository fake decoder must be the only enabled sidecar-evidence bypass.'
    Assert-True $matchingReport.inputFile.unchangedDuringQualification `
        'The matching fixture input must remain unchanged across both runs.'

    [Environment]::SetEnvironmentVariable(
        'VHSDECODE_CUDA_HARNESS_SELF_TEST',
        $null,
        [EnvironmentVariableTarget]::Process)
    try {
        $missingSidecarReportPath = & $harness `
            -DecodeExecutable $pwsh `
            -DecodeCommand '-File' `
            -InputFile $inputPath `
            -OutputDirectory (Join-Path $testRoot 'missing-sidecars') `
            -AdditionalArguments @($fakeDecoder, '--payload-mode', 'same') `
            -GpuSampleIntervalMs 100
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'VHSDECODE_CUDA_HARNESS_SELF_TEST',
            '1',
            [EnvironmentVariableTarget]::Process)
    }
    $missingSidecarReport = Get-Content -LiteralPath $missingSidecarReportPath -Raw |
        ConvertFrom-Json
    Assert-True (-not $missingSidecarReport.qualificationPassed) `
        'Qualification without the required sidecars and manifest must fail outside self-test mode.'
    Assert-True (-not $missingSidecarReport.binaryArtifactEvidence.qualificationMatch) `
        'The report must expose missing binary artifact evidence.'

    $emptyStderrReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'empty-stderr') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'empty-stderr') `
        -GpuSampleIntervalMs 100
    $emptyStderrReport = Get-Content -LiteralPath $emptyStderrReportPath -Raw |
        ConvertFrom-Json
    Assert-True $emptyStderrReport.qualificationPassed `
        'An empty stderr stream must not prevent backend evidence from the decoder log.'
    Assert-True ($emptyStderrReport.consoleComparison.stderr.rawEqual) `
        'Both empty stderr streams should compare exactly.'

    $sparseReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'integer-sparse-lsb') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'integer-sparse-lsb') `
        -GpuSampleIntervalMs 100
    $sparseReport = Get-Content -LiteralPath $sparseReportPath -Raw | ConvertFrom-Json
    $sparseFile = @($sparseReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc')[0]
    Assert-True ($sparseReport.schemaVersion -eq 4) `
        'The structured tolerance and artifact-evidence report must use schema version 4.'
    Assert-True $sparseReport.qualificationPassed `
        'One 1-LSB difference in 10,000 samples should qualify at the 0.01% boundary.'
    Assert-True (-not $sparseReport.outputComparison.allFilesRawMatch) `
        'The sparse integer difference must retain its raw hash mismatch.'
    Assert-True ($sparseFile.qualificationBasis -eq 'integer16-engineering-tolerance') `
        'The sparse integer output must identify the tolerance as its qualification basis.'
    Assert-True ($sparseFile.integerEngineeringTolerance.sampleFormat -eq 'uint16-little-endian') `
        'TBC output must be compared using its UInt16 sample format.'
    Assert-True $sparseFile.integerEngineeringTolerance.equalLength `
        'Integer tolerance comparison requires equal byte length.'
    Assert-True $sparseFile.integerEngineeringTolerance.validSampleEncoding `
        'The fake integer output must be valid signed 16-bit data.'
    Assert-True ($sparseFile.integerEngineeringTolerance.sampleCount -eq 10000) `
        'The fake integer output should contain 10,000 samples.'
    Assert-True ($sparseFile.integerEngineeringTolerance.differingSampleCount -eq 1) `
        'Exactly one sparse sample should differ.'
    Assert-True ($sparseFile.integerEngineeringTolerance.maximumAbsoluteDifferenceLsb -eq 1) `
        'The sparse difference should be exactly 1 LSB.'
    Assert-True ($sparseFile.integerEngineeringTolerance.differingSampleRate -le 0.0001) `
        'The sparse difference rate should be within 0.01%.'
    Assert-True (-not $sparseReport.releaseGateCoverage.complete) `
        'The report must not claim complete release-gate coverage.'
    Assert-True (-not $sparseReport.fullReleaseGatePassed) `
        'Guard-band and representative-suite evidence must block the full release gate.'
    Assert-True ($sparseReport.releaseGateCoverage.blockers.Count -eq 2) `
        'The report should name the guard-band and representative-suite blockers.'
    Assert-True $sparseReport.releaseGateCoverage.implementedComparisonCoverageComplete `
        'The report should mark JSON/SQLite comparison implementation coverage complete.'

    $unsignedBoundaryReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'integer-unsigned-boundary') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'integer-unsigned-boundary') `
        -GpuSampleIntervalMs 100
    $unsignedBoundaryReport = Get-Content -LiteralPath $unsignedBoundaryReportPath -Raw |
        ConvertFrom-Json
    $unsignedBoundaryFile = @($unsignedBoundaryReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc')[0]
    Assert-True $unsignedBoundaryReport.qualificationPassed `
        'A 0x7fff to 0x8000 TBC change must qualify as one UInt16 LSB.'
    Assert-True ($unsignedBoundaryFile.integerEngineeringTolerance.sampleFormat -eq 'uint16-little-endian') `
        'The unsigned boundary fixture must report the UInt16 sample format.'
    Assert-True ($unsignedBoundaryFile.integerEngineeringTolerance.maximumAbsoluteDifferenceLsb -eq 1) `
        'The UInt16 boundary change must be reported as 1 LSB, not 65535.'

    $ldRfTbcReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'integer-signed-ld-rf-tbc') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'integer-signed-ld-rf-tbc') `
        -GpuSampleIntervalMs 100
    $ldRfTbcReport = Get-Content -LiteralPath $ldRfTbcReportPath -Raw |
        ConvertFrom-Json
    $ldRfTbcFile = @($ldRfTbcReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc.ldf')[0]
    Assert-True $ldRfTbcReport.qualificationPassed `
        'A -1 to 0 LD RF-TBC change must qualify as one Int16 LSB.'
    Assert-True ($ldRfTbcFile.integerEngineeringTolerance.outputKind -eq 'ld-rf-tbc') `
        'The .tbc.ldf suffix must be recognized as LD RF-TBC data.'
    Assert-True ($ldRfTbcFile.integerEngineeringTolerance.sampleFormat -eq 'int16-little-endian') `
        'LD RF-TBC output must report the signed Int16 sample format.'
    Assert-True ($ldRfTbcFile.integerEngineeringTolerance.maximumAbsoluteDifferenceLsb -eq 1) `
        'The signed LD RF-TBC boundary change must be reported as 1 LSB.'

    $overRateReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'integer-over-rate') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'integer-over-rate') `
        -GpuSampleIntervalMs 100
    $overRateReport = Get-Content -LiteralPath $overRateReportPath -Raw | ConvertFrom-Json
    $overRateFile = @($overRateReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc')[0]
    Assert-True (-not $overRateReport.qualificationPassed) `
        'Two 1-LSB differences in 10,000 samples must fail the 0.01% rate limit.'
    Assert-True ($overRateFile.integerEngineeringTolerance.maximumAbsoluteDifferenceLsb -eq 1) `
        'The over-rate fixture should fail only the differing-sample-rate limit.'
    Assert-True ($overRateFile.integerEngineeringTolerance.differingSampleRate -gt 0.0001) `
        'The over-rate fixture must exceed the reported rate limit.'

    $twoLsbReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'integer-two-lsb') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'integer-two-lsb') `
        -GpuSampleIntervalMs 100
    $twoLsbReport = Get-Content -LiteralPath $twoLsbReportPath -Raw | ConvertFrom-Json
    $twoLsbFile = @($twoLsbReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc')[0]
    Assert-True (-not $twoLsbReport.qualificationPassed) `
        'A sparse 2-LSB difference must fail the maximum-error limit.'
    Assert-True ($twoLsbFile.integerEngineeringTolerance.maximumAbsoluteDifferenceLsb -eq 2) `
        'The 2-LSB fixture must expose its maximum error in the report.'
    Assert-True ($twoLsbFile.integerEngineeringTolerance.differingSampleRate -le 0.0001) `
        'The 2-LSB fixture should fail only the maximum-error limit.'

    $floatWithinReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'float32-within') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'float32-within', '--export_raw_tbc') `
        -GpuSampleIntervalMs 100
    $floatWithinReport = Get-Content -LiteralPath $floatWithinReportPath -Raw | ConvertFrom-Json
    $floatWithinFile = @($floatWithinReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc')[0]
    Assert-True $floatWithinReport.qualificationPassed `
        'Raw TBC Float32 output inside the normalized error limits should qualify.'
    Assert-True ($floatWithinFile.qualificationBasis -eq 'float32-engineering-tolerance') `
        'Raw TBC must use the Float32 engineering tolerance, not UInt16 LSB comparison.'
    Assert-True $floatWithinFile.floatEngineeringTolerance.nonFiniteLayoutEqual `
        'Float32 comparison must report matching NaN/Inf positions.'
    Assert-True ($floatWithinFile.floatEngineeringTolerance.normalizedMaximumAbsoluteError -le 2e-6) `
        'The within-tolerance Float32 fixture must meet normalized maximum error.'

    $floatOverReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'float32-over') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'float32-over', '--export_raw_tbc') `
        -GpuSampleIntervalMs 100
    $floatOverReport = Get-Content -LiteralPath $floatOverReportPath -Raw | ConvertFrom-Json
    $floatOverFile = @($floatOverReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc')[0]
    Assert-True (-not $floatOverReport.qualificationPassed) `
        'Raw TBC Float32 output above the normalized maximum error must fail.'
    Assert-True ($floatOverFile.floatEngineeringTolerance.normalizedMaximumAbsoluteError -gt 2e-6) `
        'The failing Float32 fixture must expose its normalized maximum error.'

    $jsonSameReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'logical-json-same') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'logical-json-same') `
        -GpuSampleIntervalMs 100
    $jsonSameReport = Get-Content -LiteralPath $jsonSameReportPath -Raw | ConvertFrom-Json
    $jsonSameFile = @($jsonSameReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc.json')[0]
    Assert-True $jsonSameReport.qualificationPassed `
        'Formatting and property-order differences with identical fileLoc metadata should qualify.'
    Assert-True (-not $jsonSameReport.outputComparison.allFilesRawMatch) `
        'The logical JSON fixture must retain its raw mismatch.'
    Assert-True $jsonSameFile.logicalComparison.fileLocSequenceEqual `
        'Logical JSON comparison must verify ordered fileLoc values.'
    Assert-True ($jsonSameFile.qualificationBasis -eq 'fileloc-aligned-json-metadata') `
        'Logical JSON equality must be recorded as the qualification basis.'

    foreach ($jsonFailureMode in @('logical-json-field-difference', 'logical-json-fileloc-order')) {
        $jsonFailureReportPath = & $harness `
            -DecodeExecutable $pwsh `
            -DecodeCommand '-File' `
            -InputFile $inputPath `
            -OutputDirectory (Join-Path $testRoot $jsonFailureMode) `
            -AdditionalArguments @($fakeDecoder, '--payload-mode', $jsonFailureMode) `
            -GpuSampleIntervalMs 100
        $jsonFailureReport = Get-Content -LiteralPath $jsonFailureReportPath -Raw | ConvertFrom-Json
        Assert-True (-not $jsonFailureReport.qualificationPassed) `
            "JSON mismatch mode '$jsonFailureMode' must fail qualification."
    }

    $sqliteSameReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'logical-sqlite-same') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'logical-sqlite-same') `
        -GpuSampleIntervalMs 100
    $sqliteSameReport = Get-Content -LiteralPath $sqliteSameReportPath -Raw | ConvertFrom-Json
    $sqliteSameFile = @($sqliteSameReport.outputComparison.files |
        Where-Object relativePath -eq 'decode.tbc.db')[0]
    Assert-True $sqliteSameReport.qualificationPassed `
        'SQLite files with identical logical schema/rows but different physical layout should qualify.'
    Assert-True $sqliteSameFile.logicalComparison.rowsEqual `
        'SQLite logical comparison must report equal row multisets.'
    Assert-True ($sqliteSameFile.qualificationBasis -eq 'sqlite-logical-rows') `
        'SQLite logical equality must be recorded as the qualification basis.'

    $sqliteDifferentReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'logical-sqlite-row-difference') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'logical-sqlite-row-difference') `
        -GpuSampleIntervalMs 100
    $sqliteDifferentReport = Get-Content -LiteralPath $sqliteDifferentReportPath -Raw | ConvertFrom-Json
    Assert-True (-not $sqliteDifferentReport.qualificationPassed) `
        'A changed SQLite logical row must fail qualification.'

    $backendFallbackReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'backend-fallback') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'backend-fallback') `
        -GpuSampleIntervalMs 100
    $backendFallbackReport = Get-Content -LiteralPath $backendFallbackReportPath -Raw | ConvertFrom-Json
    Assert-True (-not $backendFallbackReport.qualificationPassed) `
        'A requested CUDA run that actually reports CPU must never qualify.'
    Assert-True (-not $backendFallbackReport.backendSelectionComparison.cuda.matchesExpectedBackend) `
        'The report must expose the unexpected CUDA-to-CPU fallback.'

    $semanticDurationReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'semantic-duration') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'semantic-duration-difference') `
        -GpuSampleIntervalMs 100
    $semanticDurationReport = Get-Content -LiteralPath $semanticDurationReportPath -Raw | ConvertFrom-Json
    Assert-True (-not $semanticDurationReport.qualificationPassed) `
        'A semantic dropout-duration difference must not be normalized as timing.'

    $emptyReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'no-output') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'no-output') `
        -GpuSampleIntervalMs 100
    $emptyReport = Get-Content -LiteralPath $emptyReportPath -Raw | ConvertFrom-Json
    Assert-True (-not $emptyReport.qualificationPassed) `
        'Exit-zero runs without a common non-log decoder output must not qualify.'
    Assert-True (-not $emptyReport.requiredOutputEvidence.qualificationMatch) `
        'The report must expose the missing required output evidence.'

    $noFilesReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'no-files') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'no-files') `
        -GpuSampleIntervalMs 100
    $noFilesReport = Get-Content -LiteralPath $noFilesReportPath -Raw | ConvertFrom-Json
    Assert-True (-not $noFilesReport.qualificationPassed) `
        'Exit-zero runs with completely empty output inventories must produce a failing report.'
    Assert-True ($noFilesReport.outputComparison.files.Count -eq 0) `
        'The empty-inventory fixture must retain an empty file comparison set.'

    $mutableInputPath = Join-Path $testRoot 'mutable-input.bin'
    [IO.File]::WriteAllBytes($mutableInputPath, [byte[]] @(1, 2, 3, 4))
    $mutableInputReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $mutableInputPath `
        -OutputDirectory (Join-Path $testRoot 'mutated-input') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'mutate-input') `
        -GpuSampleIntervalMs 100
    $mutableInputReport = Get-Content -LiteralPath $mutableInputReportPath -Raw |
        ConvertFrom-Json
    Assert-True (-not $mutableInputReport.qualificationPassed) `
        'An input changed between CPU and CUDA runs must fail qualification.'
    Assert-True (-not $mutableInputReport.inputFile.qualificationMatch) `
        'The report must expose the changed input fingerprint.'

    $differentReportPath = & $harness `
        -DecodeExecutable $pwsh `
        -DecodeCommand '-File' `
        -InputFile $inputPath `
        -OutputDirectory (Join-Path $testRoot 'different') `
        -AdditionalArguments @($fakeDecoder, '--payload-mode', 'different') `
        -GpuSampleIntervalMs 100
    $differentReport = Get-Content -LiteralPath $differentReportPath -Raw | ConvertFrom-Json
    Assert-True (-not $differentReport.qualificationPassed) `
        'A raw mismatch in a non-integer output must fail qualification.'
    Assert-True ($differentReport.outputComparison.disallowedMismatchCount -eq 1) `
        'Exactly one fake binary payload should be a disallowed mismatch.'
    Assert-True ($null -ne $differentReport.performance.cudaSpeedup) `
        'The report must include a CPU/CUDA wall-time speedup.'

    Write-Host 'Compare-CudaDecode fake decoder tests passed.'
}
finally {
    [Environment]::SetEnvironmentVariable(
        'VHSDECODE_CUDA_HARNESS_SELF_TEST',
        $previousHarnessSelfTest,
        [EnvironmentVariableTarget]::Process)
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolvedTestRoot.StartsWith(
            $temporaryRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedTestRoot)).StartsWith(
            'vhsdecode-cuda-harness-test-',
            [StringComparison]::Ordinal)) {
        throw "Refusing to clean unexpected test directory '$resolvedTestRoot'."
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
