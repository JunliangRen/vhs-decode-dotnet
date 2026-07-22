[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),
    [string] $BuildDirectory = 'artifacts\cuda-native',
    [string] $OutputDirectory = 'artifacts\cuda-package',
    [string] $CudaToolkitRoot,
    [string] $Generator = 'Ninja',
    [string] $MsvcToolsetVersion = '14.44',
    [ValidateSet('Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipConfigure,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $RequireGpuTest,
    [switch] $SkipArchive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $BasePath
    )

    if ([IO.Path]::IsPathFullyQualified($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory)]
        [string] $Source,
        [Parameter(Mandatory)]
        [string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required package input was not found: $Source"
    }

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-PackageFileEntry {
    param(
        [Parameter(Mandatory)]
        [string] $PackageRoot,
        [Parameter(Mandatory)]
        [string] $RelativePath,
        [Parameter(Mandatory)]
        [string] $Kind,
        [string] $Version
    )

    $platformRelativePath = $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $file = Get-Item -LiteralPath (Join-Path $PackageRoot $platformRelativePath)
    [ordered]@{
        path = $RelativePath
        kind = $Kind
        version = $Version
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Import-MsvcDeveloperEnvironment {
    param([string] $ToolsetVersion)

    if ($env:OS -ne 'Windows_NT') {
        return
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Ninja CUDA builds require the MSVC x64 developer environment, but vswhere.exe was not found.'
    }
    $installationValue = & $vswhere `
        -latest `
        -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1
    $installationPath = ([string] $installationValue).Trim()
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw 'Ninja CUDA builds require a Visual Studio installation with the MSVC x64 tools.'
    }
    $vsDevCmd = Join-Path $installationPath 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $vsDevCmd -PathType Leaf)) {
        throw "Visual Studio developer environment script was not found: $vsDevCmd"
    }

    $toolsetArgument = if ([string]::IsNullOrWhiteSpace($ToolsetVersion)) {
        ''
    } else {
        " -vcvars_ver=$ToolsetVersion"
    }
    $environmentCommand = '"{0}" -no_logo -arch=x64 -host_arch=x64{1} >nul && set' -f @(
        $vsDevCmd,
        $toolsetArgument)
    $environmentLines = @(& $env:ComSpec /d /s /c $environmentCommand)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not initialize the MSVC x64 developer environment.'
    }
    foreach ($line in $environmentLines) {
        if ($line -notmatch '^([^=]+)=(.*)$') {
            continue
        }
        [Environment]::SetEnvironmentVariable(
            $Matches[1],
            $Matches[2],
            [EnvironmentVariableTarget]::Process)
    }
}

$repositoryFull = Get-AbsolutePath -Path $RepositoryRoot -BasePath $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $repositoryFull 'VHSDecodeDotNet.slnx') -PathType Leaf)) {
    throw "Repository root is invalid: $repositoryFull"
}

$buildFull = Get-AbsolutePath -Path $BuildDirectory -BasePath $repositoryFull
$outputFull = Get-AbsolutePath -Path $OutputDirectory -BasePath $repositoryFull
$nativeSource = Join-Path $repositoryFull 'native\cuda'

if ([string]::IsNullOrWhiteSpace($CudaToolkitRoot)) {
    $CudaToolkitRoot = $env:CUDAToolkit_ROOT
}
if ([string]::IsNullOrWhiteSpace($CudaToolkitRoot)) {
    $CudaToolkitRoot = $env:CUDA_PATH
}
if ([string]::IsNullOrWhiteSpace($CudaToolkitRoot)) {
    $CudaToolkitRoot = 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0'
}

$cudaRoot = [IO.Path]::GetFullPath($CudaToolkitRoot)
$cudaVersionPath = Join-Path $cudaRoot 'version.json'
$cudaLicensePath = Join-Path $cudaRoot 'LICENSE'
$nvccPath = Join-Path $cudaRoot 'bin\nvcc.exe'
foreach ($requiredToolkitFile in @($cudaVersionPath, $cudaLicensePath, $nvccPath)) {
    if (-not (Test-Path -LiteralPath $requiredToolkitFile -PathType Leaf)) {
        throw "CUDA Toolkit 13.0 Update 2 is incomplete; missing '$requiredToolkitFile'."
    }
}

$toolkitVersion = Get-Content -LiteralPath $cudaVersionPath -Raw | ConvertFrom-Json
if ($toolkitVersion.cuda.version -ne '13.0.2' -or $toolkitVersion.cuda_nvcc.version -ne '13.0.88') {
    throw "CUDA Toolkit 13.0 Update 2 is required (SDK 13.0.2, nvcc 13.0.88); found SDK '$($toolkitVersion.cuda.version)', nvcc '$($toolkitVersion.cuda_nvcc.version)'."
}

$cudaBinaryDirectory = Join-Path $cudaRoot 'bin\x64'
if (-not (Test-Path -LiteralPath $cudaBinaryDirectory -PathType Container)) {
    $cudaBinaryDirectory = Join-Path $cudaRoot 'bin'
}
$env:PATH = "$cudaBinaryDirectory;$env:PATH"
$isMultiConfigGenerator = $Generator -match '^(?i:Visual Studio|Xcode|Ninja Multi-Config)'
if (-not $isMultiConfigGenerator) {
    Import-MsvcDeveloperEnvironment -ToolsetVersion $MsvcToolsetVersion
    # VsDevCmd prepends its own paths, so keep the pinned CUDA runtime first.
    $env:PATH = "$cudaBinaryDirectory;$env:PATH"
}

if (-not $SkipConfigure) {
    New-Item -ItemType Directory -Path $buildFull -Force | Out-Null
    $configureArguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        '-S', $nativeSource,
        '-B', $buildFull,
        '-G', $Generator,
        "-DCMAKE_CUDA_COMPILER=$nvccPath",
        "-DCUDAToolkit_ROOT=$cudaRoot",
        '-DVHSDECODE_CUDA_ENABLE=ON',
        '-DVHSDECODE_CUDA_BUILD_TESTS=ON'
    )) {
        $configureArguments.Add($argument)
    }
    if ($isMultiConfigGenerator) {
        $configureArguments.Insert(6, 'x64')
        $configureArguments.Insert(6, '-A')
    } else {
        $configureArguments.Add("-DCMAKE_BUILD_TYPE=$Configuration")
    }
    Invoke-Checked -FilePath 'cmake' -Arguments @($configureArguments)
}

if (-not $SkipBuild) {
    Invoke-Checked -FilePath 'cmake' -Arguments @('--build', $buildFull, '--config', $Configuration, '--parallel')
}

if (-not $SkipTests) {
    Invoke-Checked -FilePath 'ctest' -Arguments @('--test-dir', $buildFull, '-C', $Configuration, '--output-on-failure')
    if ($RequireGpuTest) {
        $smokeTest = Join-Path $buildFull $(if ($isMultiConfigGenerator) {
            "$Configuration\vhsdecode_cuda_smoke.exe"
        } else {
            'vhsdecode_cuda_smoke.exe'
        })
        if (-not (Test-Path -LiteralPath $smokeTest -PathType Leaf)) {
            throw "The native CUDA smoke test was not found at '$smokeTest'."
        }
        # The smoke executable returns 77 when no device is available. Running
        # it directly makes that a hard failure on the dedicated GPU runner.
        Invoke-Checked -FilePath $smokeTest -Arguments @()
    }
}

$nativeBinary = Join-Path $buildFull $(if ($isMultiConfigGenerator) {
    "$Configuration\vhsdecode_cuda.dll"
} else {
    'vhsdecode_cuda.dll'
})
if (-not (Test-Path -LiteralPath $nativeBinary -PathType Leaf)) {
    throw "The built CUDA sidecar was not found at '$nativeBinary'."
}

$cudartBinary = Join-Path $cudaBinaryDirectory 'cudart64_13.dll'
$cufftBinary = Join-Path $cudaBinaryDirectory 'cufft64_12.dll'

$packageName = 'vhs-decode-dotnet_cuda-win-x64_cuda-13.0.2'
$packageRoot = Join-Path $outputFull $packageName
if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

Copy-RequiredFile -Source $nativeBinary -Destination (Join-Path $packageRoot 'vhsdecode_cuda.dll')
Copy-RequiredFile -Source $cudartBinary -Destination (Join-Path $packageRoot 'cudart64_13.dll')
Copy-RequiredFile -Source $cufftBinary -Destination (Join-Path $packageRoot 'cufft64_12.dll')
Copy-RequiredFile -Source (Join-Path $repositoryFull 'docs\CUDA.md') -Destination (Join-Path $packageRoot 'README-CUDA.md')
Copy-RequiredFile -Source (Join-Path $nativeSource 'THIRD-PARTY-NOTICES-CUDA.md') -Destination (Join-Path $packageRoot 'THIRD-PARTY-NOTICES-CUDA.md')
Copy-RequiredFile -Source (Join-Path $repositoryFull 'LICENSE') -Destination (Join-Path $packageRoot 'licenses\vhsdecode-cuda-GPL-3.0.txt')
Copy-RequiredFile -Source $cudaLicensePath -Destination (Join-Path $packageRoot 'licenses\NVIDIA-CUDA-LICENSE.txt')
Copy-RequiredFile -Source $cudaVersionPath -Destination (Join-Path $packageRoot 'licenses\NVIDIA-CUDA-version.json')

$commit = $null
$repositoryDirty = $null
$repositoryDirtyPathCount = $null
$repositorySourceTreeSha256 = $null
if (Get-Command 'git' -ErrorAction SilentlyContinue) {
    $commitOutput = & git -C $repositoryFull rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        $commit = $commitOutput.Trim()

        $statusOutput = @(& git -C $repositoryFull status --porcelain=v1 --untracked-files=all)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not determine CUDA package source-tree status.'
        }
        $repositoryDirtyPathCount = $statusOutput.Count
        $repositoryDirty = $repositoryDirtyPathCount -gt 0

        $sourcePaths = @(& git -C $repositoryFull ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not enumerate CUDA package source-tree files.'
        }
        $sourceHash = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $utf8 = [Text.UTF8Encoding]::new($false)
            foreach ($relativeSourcePath in $sourcePaths | Sort-Object -Unique) {
                $sourceFile = Join-Path $repositoryFull $relativeSourcePath
                if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
                    continue
                }
                $normalizedSourcePath = $relativeSourcePath.Replace('\', '/')
                $sourceHash.AppendData($utf8.GetBytes($normalizedSourcePath))
                $sourceHash.AppendData([byte[]] @(0))
                $fileHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
                $sourceHash.AppendData($utf8.GetBytes($fileHash))
                $sourceHash.AppendData([byte[]] @(10))
            }
            $repositorySourceTreeSha256 = [Convert]::ToHexString(
                $sourceHash.GetHashAndReset()).ToLowerInvariant()
        }
        finally {
            $sourceHash.Dispose()
        }
    }
}

$files = @(
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'vhsdecode_cuda.dll' -Kind 'sidecar' -Version 'ABI-1'
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'cudart64_13.dll' -Kind 'runtime' -Version $toolkitVersion.cuda_cudart.version
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'cufft64_12.dll' -Kind 'runtime' -Version $toolkitVersion.libcufft.version
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'README-CUDA.md' -Kind 'documentation'
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'THIRD-PARTY-NOTICES-CUDA.md' -Kind 'notice'
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'licenses/vhsdecode-cuda-GPL-3.0.txt' -Kind 'license'
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'licenses/NVIDIA-CUDA-LICENSE.txt' -Kind 'license'
    Get-PackageFileEntry -PackageRoot $packageRoot -RelativePath 'licenses/NVIDIA-CUDA-version.json' -Kind 'toolchain-manifest' -Version $toolkitVersion.cuda.version
)

$manifest = [ordered]@{
    schemaVersion = 2
    component = 'vhsdecode-cuda'
    platform = 'win-x64'
    abiVersion = 1
    repositoryCommit = $commit
    repositoryDirty = $repositoryDirty
    repositoryDirtyPathCount = $repositoryDirtyPathCount
    repositorySourceTreeSha256 = $repositorySourceTreeSha256
    repositorySourceIdentity = if ($null -eq $commit) {
        $null
    } elseif ($repositoryDirty) {
        "$commit+dirty.$repositorySourceTreeSha256"
    } else {
        $commit
    }
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    toolchain = [ordered]@{
        cudaSdk = $toolkitVersion.cuda.version
        nvcc = $toolkitVersion.cuda_nvcc.version
        cudart = $toolkitVersion.cuda_cudart.version
        cufft = $toolkitVersion.libcufft.version
    }
    minimumDriver = '580.95'
    minimumComputeCapability = '7.5'
    architectures = @('sm_75', 'sm_86', 'sm_89', 'compute_89')
    autoEligible = $false
    autoEligibilityReason = 'The CPU guard-band/full differential gate and required >=1.25x representative VHS and LD end-to-end performance gate have not been validated.'
    files = $files
}

$manifestPath = Join-Path $packageRoot 'cuda-component.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

& (Join-Path $PSScriptRoot 'Test-CudaPackage.ps1') -PackagePath $packageRoot

$archivePath = Join-Path $outputFull "$packageName.zip"
if (-not $SkipArchive) {
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    Write-Host "CUDA package archive: $archivePath"
}

Write-Host "CUDA package directory: $packageRoot"
