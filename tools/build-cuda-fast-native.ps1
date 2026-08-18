[CmdletBinding()]
param(
    [string]$CuVhsSourceDirectory,
    [string]$NvCodecHeadersSourceDirectory,
    [string]$CudaToolkitDirectory,
    [switch]$SkipRuntimeTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$sourceDirectory = Join-Path $repositoryRoot 'src\VHSDecode.CudaFast.Native'
$buildDirectory = Join-Path $repositoryRoot '.artifacts\cuda-fast-native'
$artifactDirectory = Join-Path $repositoryRoot 'artifacts\native\Release\win-x64'
$nativeName = 'vhsdecode_cuda_fast.dll'
$smokeName = 'vhsdecode_cuda_fast_smoke.exe'
$cancellationTestName = 'vhsdecode_cuda_fast_cancellation_test.exe'
$syncPulseTestName = 'vhsdecode_cuda_fast_sync_pulses_test.exe'
$dropoutTestName = 'vhsdecode_cuda_fast_dropout_test.exe'
$syntheticNtscTestName = 'vhsdecode_cuda_fast_synthetic_ntsc_test.exe'
$cuFftName = 'cufft64_12.dll'
$pinnedCuVhsCommit = 'c55e72073f44b27e8839efb842e4345af39887f7'
$pinnedNvCodecHeadersCommit = '1889e62e2d35ff7aa9baca2bceb14f053785e6f1'

if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'CMakeLists.txt') -PathType Leaf)) {
    throw "CUDA-fast native project was not found: $sourceDirectory"
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswherePath = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
    $vswhereCommand = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($null -eq $vswhereCommand) {
        throw 'vswhere.exe was not found. Install Visual Studio with Desktop development with C++.'
    }

    $vswherePath = $vswhereCommand.Source
}

[string[]]$installationPaths = @(
    & $vswherePath -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($LASTEXITCODE -ne 0 -or $installationPaths.Count -eq 0) {
    throw 'vswhere.exe could not locate a Visual Studio C++ installation.'
}

$visualStudioPath = $installationPaths[0]
$vsDevCmdPath = Join-Path $visualStudioPath 'Common7\Tools\VsDevCmd.bat'
[string[]]$cmakeCandidates = @(
    & $vswherePath -latest -products * -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
[string[]]$ninjaCandidates = @(
    & $vswherePath -latest -products * -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$bundledCmakePath = Join-Path $visualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$bundledNinjaPath = Join-Path $visualStudioPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
if ($cmakeCandidates.Count -eq 0 -and (Test-Path -LiteralPath $bundledCmakePath -PathType Leaf)) {
    $cmakeCandidates = @($bundledCmakePath)
}
if ($ninjaCandidates.Count -eq 0 -and (Test-Path -LiteralPath $bundledNinjaPath -PathType Leaf)) {
    $ninjaCandidates = @($bundledNinjaPath)
}
if (-not (Test-Path -LiteralPath $vsDevCmdPath -PathType Leaf) -or
    $cmakeCandidates.Count -eq 0 -or
    $ninjaCandidates.Count -eq 0) {
    throw 'Visual Studio CMake, Ninja, or VsDevCmd.bat was not found.'
}

$cmakePath = $cmakeCandidates[0]
$ninjaPath = $ninjaCandidates[0]
$toolsetRoot = Join-Path $visualStudioPath 'VC\Tools\MSVC'
[System.IO.DirectoryInfo[]]$compatibleToolsets = @(
    Get-ChildItem -LiteralPath $toolsetRoot -Directory |
        Where-Object {
            try {
                [version]$_.Name -le [version]'14.44.99999'
            }
            catch {
                $false
            }
        } |
        Sort-Object { [version]$_.Name } -Descending
)
if ($compatibleToolsets.Count -eq 0) {
    throw 'CUDA 13 requires an MSVC 14.44-or-earlier host toolset; install the VS 2022 v143 14.44 component.'
}

$hostToolsetVersion = $compatibleToolsets[0].Name
$hostToolsetSelector = ([version]$hostToolsetVersion).ToString(2)
$dumpbinPath = Join-Path $toolsetRoot "$hostToolsetVersion\bin\Hostx64\x64\dumpbin.exe"
if (-not (Test-Path -LiteralPath $dumpbinPath -PathType Leaf)) {
    throw "dumpbin.exe was not found for MSVC $hostToolsetVersion."
}

if ([string]::IsNullOrWhiteSpace($CudaToolkitDirectory)) {
    $CudaToolkitDirectory = [Environment]::GetEnvironmentVariable('CUDA_PATH')
}
if ([string]::IsNullOrWhiteSpace($CudaToolkitDirectory)) {
    $cudaRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'NVIDIA GPU Computing Toolkit\CUDA'
    [System.IO.DirectoryInfo[]]$cudaCandidates = @(
        Get-ChildItem -LiteralPath $cudaRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending
    )
    if ($cudaCandidates.Count -gt 0) {
        $CudaToolkitDirectory = $cudaCandidates[0].FullName
    }
}
if ([string]::IsNullOrWhiteSpace($CudaToolkitDirectory)) {
    throw 'CUDA Toolkit 13 was not found. Set CUDA_PATH or pass -CudaToolkitDirectory.'
}

$CudaToolkitDirectory = [System.IO.Path]::GetFullPath($CudaToolkitDirectory)
$nvccPath = Join-Path $CudaToolkitDirectory 'bin\nvcc.exe'
$cuFftPath = Join-Path $CudaToolkitDirectory "bin\x64\$cuFftName"
if (-not (Test-Path -LiteralPath $nvccPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $cuFftPath -PathType Leaf)) {
    throw "CUDA Toolkit compiler or cuFFT runtime was not found under $CudaToolkitDirectory."
}

$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$buildDirectoryFullPath = [System.IO.Path]::GetFullPath($buildDirectory)
if (-not $buildDirectoryFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a build directory outside the repository: $buildDirectoryFullPath"
}
if (Test-Path -LiteralPath $buildDirectoryFullPath) {
    Remove-Item -LiteralPath $buildDirectoryFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $buildDirectoryFullPath -Force | Out-Null

function Quote-CmdArgument([string]$value) {
    if ($value.Contains('"')) {
        throw "A build path contains an unsupported quote character: $value"
    }

    return '"' + $value + '"'
}

$configureArguments = @(
    '-S', (Quote-CmdArgument $sourceDirectory),
    '-B', (Quote-CmdArgument $buildDirectoryFullPath),
    '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release',
    '-DCMAKE_CUDA_FLAGS=-allow-unsupported-compiler',
    ('-DCMAKE_MAKE_PROGRAM=' + (Quote-CmdArgument $ninjaPath)),
    ('-DCMAKE_CUDA_COMPILER=' + (Quote-CmdArgument $nvccPath))
)
if (-not [string]::IsNullOrWhiteSpace($CuVhsSourceDirectory)) {
    $CuVhsSourceDirectory = [System.IO.Path]::GetFullPath($CuVhsSourceDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $CuVhsSourceDirectory 'src\pipeline\pipeline.cu') -PathType Leaf)) {
        throw "-CuVhsSourceDirectory is not a cuVHS source tree: $CuVhsSourceDirectory"
    }

    $gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        throw 'git.exe is required to validate a local cuVHS source checkout.'
    }
    $safeDirectory = 'safe.directory=' + $CuVhsSourceDirectory.Replace('\', '/')
    [string]$localCommit = (& $gitCommand.Source -c $safeDirectory -C $CuVhsSourceDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $localCommit -ne $pinnedCuVhsCommit) {
        throw "Local cuVHS checkout must be pinned to $pinnedCuVhsCommit; found '$localCommit'."
    }
    & $gitCommand.Source -c $safeDirectory -C $CuVhsSourceDirectory diff --quiet --
    if ($LASTEXITCODE -ne 0) {
        throw 'Local cuVHS checkout has tracked modifications; use the pristine pinned source.'
    }

    $configureArguments += '-DVHSDECODE_CUVHS_SOURCE_DIR=' + (Quote-CmdArgument $CuVhsSourceDirectory)
}

if (-not [string]::IsNullOrWhiteSpace($NvCodecHeadersSourceDirectory)) {
    $NvCodecHeadersSourceDirectory = [System.IO.Path]::GetFullPath($NvCodecHeadersSourceDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $NvCodecHeadersSourceDirectory 'include\ffnvcodec\nvEncodeAPI.h') -PathType Leaf)) {
        throw "-NvCodecHeadersSourceDirectory is not an nv-codec-headers source tree: $NvCodecHeadersSourceDirectory"
    }

    $gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        throw 'git.exe is required to validate a local nv-codec-headers source checkout.'
    }
    $safeDirectory = 'safe.directory=' + $NvCodecHeadersSourceDirectory.Replace('\', '/')
    [string]$localCommit = (& $gitCommand.Source -c $safeDirectory -C $NvCodecHeadersSourceDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $localCommit -ne $pinnedNvCodecHeadersCommit) {
        throw "Local nv-codec-headers checkout must be pinned to $pinnedNvCodecHeadersCommit; found '$localCommit'."
    }
    & $gitCommand.Source -c $safeDirectory -C $NvCodecHeadersSourceDirectory diff --quiet --
    if ($LASTEXITCODE -ne 0) {
        throw 'Local nv-codec-headers checkout has tracked modifications; use the pristine pinned source.'
    }

    $configureArguments += '-DVHSDECODE_NV_CODEC_HEADERS_SOURCE_DIR=' + (Quote-CmdArgument $NvCodecHeadersSourceDirectory)
}

$configureCommand = (Quote-CmdArgument $vsDevCmdPath) + " -arch=x64 -vcvars_ver=$hostToolsetSelector && " +
    (Quote-CmdArgument $cmakePath) + ' ' + ($configureArguments -join ' ')
Write-Host "Configuring CUDA-fast with CUDA $CudaToolkitDirectory and MSVC $hostToolsetVersion"
& $env:ComSpec /d /s /c $configureCommand
if ($LASTEXITCODE -ne 0) {
    throw "CUDA-fast CMake configure failed with exit code $LASTEXITCODE."
}

$buildCommand = (Quote-CmdArgument $vsDevCmdPath) + " -arch=x64 -vcvars_ver=$hostToolsetSelector && " +
    (Quote-CmdArgument $cmakePath) + ' --build ' + (Quote-CmdArgument $buildDirectoryFullPath) + ' --config Release'
Write-Host 'Building CUDA-fast native bridge'
& $env:ComSpec /d /s /c $buildCommand
if ($LASTEXITCODE -ne 0) {
    throw "CUDA-fast native build failed with exit code $LASTEXITCODE."
}

$nativeOutputPath = Join-Path $buildDirectoryFullPath $nativeName
$smokeOutputPath = Join-Path $buildDirectoryFullPath $smokeName
$cancellationTestOutputPath = Join-Path $buildDirectoryFullPath $cancellationTestName
$syncPulseTestOutputPath = Join-Path $buildDirectoryFullPath $syncPulseTestName
$dropoutTestOutputPath = Join-Path $buildDirectoryFullPath $dropoutTestName
$syntheticNtscTestOutputPath = Join-Path $buildDirectoryFullPath $syntheticNtscTestName
if (-not (Test-Path -LiteralPath $nativeOutputPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $smokeOutputPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $cancellationTestOutputPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $syncPulseTestOutputPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $dropoutTestOutputPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $syntheticNtscTestOutputPath -PathType Leaf)) {
    throw 'CUDA-fast native build did not produce the bridge and native test executables.'
}

Write-Host "Checking native dependencies in $nativeOutputPath"
$dumpbinOutput = @(& $dumpbinPath /nologo /dependents $nativeOutputPath 2>&1)
if ($LASTEXITCODE -ne 0) {
    $dumpbinOutput | ForEach-Object { Write-Host $_ }
    throw "dumpbin.exe failed with exit code $LASTEXITCODE."
}

[string[]]$dependencyNames = @(
    foreach ($line in $dumpbinOutput) {
        $text = [string]$line
        if ($text -match '^\s+([A-Za-z0-9][A-Za-z0-9_.-]*\.dll)\s*$') {
            $Matches[1].ToUpperInvariant()
        }
    }
) | Sort-Object -Unique
if ($dependencyNames -notcontains $cuFftName.ToUpperInvariant()) {
    $dumpbinOutput | ForEach-Object { Write-Host $_ }
    throw "The CUDA-fast bridge did not report its required $cuFftName dependency."
}

[string[]]$forbiddenDependencies = @(
    $dependencyNames | Where-Object {
        $_ -match '^(CUDART|VCRUNTIME|MSVCP|MSVCRT|CONCRT|UCRTBASE|API-MS-WIN-CRT-)'
    }
)
if ($forbiddenDependencies.Count -gt 0) {
    throw "The CUDA-fast bridge has unstaged CUDA Runtime or Visual C++ runtime DLL dependencies: $($forbiddenDependencies -join ', ')"
}

Write-Host "Running $cancellationTestOutputPath"
& $cancellationTestOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "CUDA-fast parallel cancellation test failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $cuFftPath -Destination (Join-Path $buildDirectoryFullPath $cuFftName) -Force
if (-not $SkipRuntimeTests) {
    Write-Host "Running $smokeOutputPath"
    & $smokeOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "CUDA-fast native smoke test failed with exit code $LASTEXITCODE."
    }

    Write-Host "Running $syncPulseTestOutputPath"
    & $syncPulseTestOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "CUDA-fast sync-pulse tests failed with exit code $LASTEXITCODE."
    }

    Write-Host "Running $dropoutTestOutputPath"
    & $dropoutTestOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "CUDA-fast Exact-style dropout tests failed with exit code $LASTEXITCODE."
    }

    Write-Host "Running $syntheticNtscTestOutputPath"
    & $syntheticNtscTestOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "CUDA-fast synthetic NTSC test failed with exit code $LASTEXITCODE."
    }

    # Force the rare one-field chroma-workspace allocation failure and require
    # the native ABI to fail closed instead of reporting unwritten chroma as a
    # successful decode.
    $previousForcedChromaWorkspaceFailure = [Environment]::GetEnvironmentVariable(
        'CUVHS_FORCE_CHROMA_WORKSPACE_FAILURE',
        [EnvironmentVariableTarget]::Process)
    try {
        [Environment]::SetEnvironmentVariable(
            'CUVHS_FORCE_CHROMA_WORKSPACE_FAILURE',
            '1',
            [EnvironmentVariableTarget]::Process)
        Write-Host "Running $syntheticNtscTestOutputPath with forced chroma-workspace failure"
        & $syntheticNtscTestOutputPath
        if ($LASTEXITCODE -eq 0) {
            throw 'CUDA-fast accepted a forced chroma-workspace failure.'
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'CUVHS_FORCE_CHROMA_WORKSPACE_FAILURE',
            $previousForcedChromaWorkspaceFailure,
            [EnvironmentVariableTarget]::Process)
    }

    # A five-field batch crosses both odd head-track parity and the NTSC
    # four/eight-field colour sequence at non-aligned boundaries. Run the same
    # end-to-end cadence/phase assertions under that diagnostic size as well as
    # the normal performance batch.
    $previousBatchOverride = [Environment]::GetEnvironmentVariable(
        'CUVHS_BATCH_SIZE',
        [EnvironmentVariableTarget]::Process)
    try {
        [Environment]::SetEnvironmentVariable(
            'CUVHS_BATCH_SIZE',
            '5',
            [EnvironmentVariableTarget]::Process)
        Write-Host "Running $syntheticNtscTestOutputPath with CUVHS_BATCH_SIZE=5"
        & $syntheticNtscTestOutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "CUDA-fast five-field-batch NTSC test failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'CUVHS_BATCH_SIZE',
            $previousBatchOverride,
            [EnvironmentVariableTarget]::Process)
    }
}
else {
    Write-Host 'Skipping CUDA runtime tests because this build host has no NVIDIA GPU.'
}

$artifactDirectoryFullPath = [System.IO.Path]::GetFullPath($artifactDirectory)
if (-not $artifactDirectoryFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write an artifact directory outside the repository: $artifactDirectoryFullPath"
}
New-Item -ItemType Directory -Path $artifactDirectoryFullPath -Force | Out-Null
$staleCuFftArtifactPath = Join-Path $artifactDirectoryFullPath $cuFftName
if (Test-Path -LiteralPath $staleCuFftArtifactPath -PathType Leaf) {
    Remove-Item -LiteralPath $staleCuFftArtifactPath -Force
}
Copy-Item -LiteralPath $nativeOutputPath -Destination (Join-Path $artifactDirectoryFullPath $nativeName) -Force
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'THIRD-PARTY-NOTICES-CUDA-FAST.md') -Destination $artifactDirectoryFullPath -Force

$nativeArtifact = Get-Item -LiteralPath (Join-Path $artifactDirectoryFullPath $nativeName)
Write-Host "Verified $($nativeArtifact.FullName) ($($nativeArtifact.Length) bytes)."
Write-Host 'cuFFT is intentionally not staged for publishing; cuda-fast resolves a compatible system runtime or downloads the pinned NVIDIA package on first use.'
