[CmdletBinding()]
param(
    [switch]$SkipNativeBuild,
    [switch]$SkipTests,
    [switch]$AllowNewerGlibc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$workRoot = Join-Path $artifactsRoot 'linux-x64-release-build'
$downloadRoot = Join-Path $artifactsRoot 'downloads\linux-x64'
$nativeStage = Join-Path $artifactsRoot 'native\Release\linux-x64'
$publishRoot = Join-Path $artifactsRoot 'publish\linux-x64'
$releaseRoot = Join-Path $artifactsRoot 'release'
$packageName = 'vhs-decode-dotnet-linux-x64'
$archivePath = Join-Path $releaseRoot "$packageName.tar.gz"
$archiveHashPath = "$archivePath.sha256"
$solutionPath = Join-Path $repositoryRoot 'VHSDecodeDotNet.slnx'
$cliProjectPath = Join-Path $repositoryRoot 'src\VHSDecode.Cli\VHSDecode.Cli.csproj'
$linuxReadmePath = Join-Path $repositoryRoot 'docs\LINUX_X64.md'
$maximumReleaseGlibc = [Version]'2.35'
$jobs = [Math]::Max(1, [Environment]::ProcessorCount)
Set-Location -LiteralPath $repositoryRoot
$expectedSmokeHashes = [ordered]@{
    Input = 'b92d1a345f7807850b0a6a45478275f5d6cb2fd9fd1062f2e49cc7ac7e677c98'
    Tbc = 'e2a6d06c5814946f756e3edb25aca0c8d4ae5d08bf0f451aa644edad6f0005b1'
    Database = 'b41e0afa9324c6a0b9cd64ace03f1558e98514f4585befc013ab7ec78645e86b'
    PortableJson = '44fe0da6b5ea1a0254c9807517c95f35610ef894af84fac6db73b439c574a784'
}

# These pure oracle methods freeze Windows UCRT or Windows-generated
# transcendental input bit patterns and contain no independent portable
# assertions. Mixed methods keep running on Linux and gate only their frozen
# Windows assertions inside the test body. The final-tar gate supplies a
# fixed-input Linux Exact artifact oracle. Keep this list method-scoped so
# discovery drift fails the minimum-count gate instead of broadening silently.
$windowsFrozenBitOracleMethods = @(
    'ComplexFftMatchesScipyDuccPacketTransforms',
    'CvbsV04BlockFiltersMatchUpstreamFloat32Hashes',
    'FirFrequencyResponseMatchesScipyDuccPacketFft',
    'LaserDiscIirFiltersMatchScipy18Bits',
    'NtscBetamaxHifiRfFilterMatchesV040ScipyBits',
    'NtscVhsRfHighPassMatchesScipySosResponseBits',
    'PalLaserDiscV04BlockDemodulationMatchesUpstreamBits',
    'PalVhsRfResponseUsesScipyExtraLowPassPath',
    'PocketComplexFftOddPassOutputRemainsBitExact',
    'VhsChromaBurstMagnitudeMatchesReleaseHypot',
    'VhsRfExtraLowPassMatchesScipySosBits',
    'VhsVideoLowPassMatchesScipySosResponseBits',
    'RealFftRadix4AvxStagesPreserveScalarBitPatterns',
    'ThirtyTwoKilobyteComplexFftPreservesSignedZeroPacketizedHash',
    'ThirtyTwoKilobyteRealFftRadixStagesRemainBitExact',
    'PalLdPilotCircularMeanMatchesNumpyComplex128Bits',
    'MtfPowersMatchReleaseFour',
    'ComplexFftDirectOutputMatchesFrozenPowerOfTwoHashes')
$minimumLinuxTestCount = 1551

$sources = [ordered]@{
    libogg = [pscustomobject]@{
        FileName = 'libogg-1.3.5.tar.xz'
        SourceDirectory = 'libogg-1.3.5'
        Url = 'https://downloads.xiph.org/releases/ogg/libogg-1.3.5.tar.xz'
        Sha256 = 'c4d91be36fc8e54deae7575241e03f4211eb102afb3fc0775fbbc1b740016705'
    }
    libvorbis = [pscustomobject]@{
        FileName = 'libvorbis-1.3.7.tar.xz'
        SourceDirectory = 'libvorbis-1.3.7'
        Url = 'https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz'
        Sha256 = 'b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b'
    }
    opus = [pscustomobject]@{
        FileName = 'opus-1.4.tar.gz'
        SourceDirectory = 'opus-1.4'
        Url = 'https://downloads.xiph.org/releases/opus/opus-1.4.tar.gz'
        Sha256 = 'c9b32b4253be5ae63d1ff16eea06b94b5f0f2951b7a02aceef58e3a3ce49c51f'
    }
    flac = [pscustomobject]@{
        FileName = 'flac-1.4.2.tar.xz'
        SourceDirectory = 'flac-1.4.2'
        Url = 'https://downloads.xiph.org/releases/flac/flac-1.4.2.tar.xz'
        Sha256 = 'e322d58a1f48d23d9dd38f432672865f6f79e73a6f9cc5a5f57fcaa83eb5a8e4'
    }
    libsndfile = [pscustomobject]@{
        FileName = 'libsndfile-1.2.2.tar.xz'
        SourceDirectory = 'libsndfile-1.2.2'
        Url = 'https://github.com/libsndfile/libsndfile/releases/download/1.2.2/libsndfile-1.2.2.tar.xz'
        Sha256 = '3799ca9924d3125038880367bf1468e53a1b7e3686a934f098b7e1d286cdb80e'
    }
}

$managedLicenseSources = [ordered]@{
    NetMQ = [pscustomobject]@{
        FileName = 'NetMQ-4.0.4.3-COPYING.LESSER'
        Url = 'https://raw.githubusercontent.com/zeromq/netmq/ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010/COPYING.LESSER'
        Sha256 = '5c435f899811e8e93e055a4dfaa0782fdd1f6b1e67a1f695a23eb610b74e9e57'
    }
    NetMQSource = [pscustomobject]@{
        FileName = 'NetMQ-ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010-source.tar.gz'
        Url = 'https://codeload.github.com/zeromq/netmq/tar.gz/ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010'
        Sha256 = '066138b4e15ebf517a32431f3f1a61cdd95218e67f2953b9879d0309f7062764'
    }
    Mpl20 = [pscustomobject]@{
        FileName = 'MPL-2.0.txt'
        Url = 'https://raw.githubusercontent.com/somdoron/AsyncIO/0b0cf4c65b049b2e483b172e530f4db970db25e4/LICENSE.md'
        Sha256 = 'af175b9d96ee93c21a036152e1b905b0b95304d4ae8c2c921c7609100ba8df7e'
    }
    SQLitePclRawLicense = [pscustomobject]@{
        FileName = 'SQLitePCLRaw-3.0.5-LICENSE.txt'
        Url = 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/ed046114d5a30534e13294d94d78eb73de896ad4/LICENSE.TXT'
        Sha256 = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
    }
    SQLitePclRawNotice = [pscustomobject]@{
        FileName = 'SQLitePCLRaw-3.0.5-NOTICE.txt'
        Url = 'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/ed046114d5a30534e13294d94d78eb73de896ad4/NOTICE.TXT'
        Sha256 = '5a1ab2f670f86dd2f8d42e40a8459cc278ca6d5225a7e2a5fa3ea0184396a6d9'
    }
}

$soxrSourcePath = Join-Path $repositoryRoot 'third_party\libsoxr\soxr-a66f3ee-source.zip'
$soxrSourceSha256 = 'd43f523965810ab91337d86da2ecbffaaeda0001609e48a14bb1fcc8e864d4d2'

function Invoke-Native {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @(),

        [switch]$CaptureOutput
    )

    Write-Host ">> $FilePath $($Arguments -join ' ')"
    if ($CaptureOutput) {
        [string[]]$commandOutput = @(& $FilePath @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $commandOutput | ForEach-Object { Write-Host $_ }
            throw "$FilePath failed with exit code $exitCode."
        }

        return $commandOutput
    }

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Assert-Command {
    param([Parameter(Mandatory)][string]$Name)

    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Assert-PathUnderArtifacts {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
    $prefix = $fullArtifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $comparison = if ([OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }

    if ($fullPath -eq $fullArtifactsRoot -or -not $fullPath.StartsWith($prefix, $comparison)) {
        throw "Refusing to modify a path outside an artifacts subdirectory: $fullPath"
    }

    return $fullPath
}

function Reset-Directory {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = Assert-PathUnderArtifacts $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    return $fullPath
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Text)

    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )

    $actual = Get-Sha256 $Path
    if ($actual -cne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for ${Path}: expected $Expected, got $actual."
    }
}

function Get-VerifiedDownload {
    param([Parameter(Mandatory)]$Source)

    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
    $destination = Join-Path $downloadRoot $Source.FileName
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        try {
            Assert-Sha256 $destination $Source.Sha256
            Write-Host "Using verified cached source: $destination"
            return $destination
        }
        catch {
            Remove-Item -LiteralPath $destination -Force
        }
    }

    $temporaryPath = "$destination.download"
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }

    Write-Host "Downloading $($Source.Url)"
    Invoke-WebRequest `
        -Uri $Source.Url `
        -OutFile $temporaryPath `
        -ConnectionTimeoutSeconds 30 `
        -OperationTimeoutSeconds 300 `
        -MaximumRetryCount 2 `
        -RetryIntervalSec 5
    Assert-Sha256 $temporaryPath $Source.Sha256
    Move-Item -LiteralPath $temporaryPath -Destination $destination
    return $destination
}

function Expand-TarSource {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Invoke-Native tar @('-xf', $Archive, '-C', $Destination)
}

function Expand-NormalizedZip {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $destinationFullPath = [System.IO.Path]::GetFullPath($Destination)
    $destinationPrefix = $destinationFullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $normalizedName = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($normalizedName)) {
                continue
            }
            if (
                $normalizedName.StartsWith('/', [StringComparison]::Ordinal) -or
                $normalizedName -match '(^|/)\.\.(/|$)'
            ) {
                throw "Unsafe ZIP entry: $($entry.FullName)"
            }

            $relativePath = $normalizedName.Replace(
                '/',
                [System.IO.Path]::DirectorySeparatorChar)
            $targetPath = [System.IO.Path]::GetFullPath(
                (Join-Path $destinationFullPath $relativePath))
            if (-not $targetPath.StartsWith($destinationPrefix, [StringComparison]::Ordinal)) {
                throw "ZIP entry escapes the extraction root: $($entry.FullName)"
            }

            if ($normalizedName.EndsWith('/', [StringComparison]::Ordinal)) {
                New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
                continue
            }

            $parent = Split-Path -Parent $targetPath
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            $inputStream = $entry.Open()
            try {
                $outputStream = [System.IO.File]::Open(
                    $targetPath,
                    [System.IO.FileMode]::Create,
                    [System.IO.FileAccess]::Write,
                    [System.IO.FileShare]::None)
                try {
                    $inputStream.CopyTo($outputStream)
                }
                finally {
                    $outputStream.Dispose()
                }
            }
            finally {
                $inputStream.Dispose()
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Invoke-CMakeConfigure {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Build,
        [string[]]$Options = @()
    )

    Invoke-Native cmake (@(
        '-S', $Source,
        '-B', $Build,
        '-G', 'Ninja',
        '-DCMAKE_POLICY_VERSION_MINIMUM=3.5',
        '-DCMAKE_BUILD_TYPE=Release'
    ) + $Options)
}

function Invoke-CMakeBuildAndInstall {
    param([Parameter(Mandatory)][string]$Build)

    Invoke-Native cmake @('--build', $Build, '--parallel', [string]$jobs)
    Invoke-Native cmake @('--install', $Build)
}

function Get-RealSharedObject {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Stem
    )

    [System.IO.FileInfo[]]$candidates = @(
        Get-ChildItem -LiteralPath $Root -File -Recurse |
            Where-Object {
                ($_.Name -eq "$Stem.so" -or $_.Name -like "$Stem.so.*") -and
                [string]::IsNullOrEmpty($_.LinkType)
            } |
            Sort-Object Length -Descending
    )
    if ($candidates.Count -eq 0) {
        throw "No regular $Stem shared object was found under $Root."
    }

    return $candidates[0].FullName
}

function Get-MaximumGlibcSymbolVersion {
    param([Parameter(Mandatory)][string]$Path)

    [string[]]$versionOutput = Invoke-Native readelf @('--version-info', $Path) -CaptureOutput
    [Version[]]$versions = @(
        foreach ($line in $versionOutput) {
            foreach ($match in [regex]::Matches($line, 'GLIBC_(\d+\.\d+)')) {
                [Version]$match.Groups[1].Value
            }
        }
    )
    if ($versions.Count -eq 0) {
        return [Version]'0.0'
    }

    return $versions | Sort-Object -Descending | Select-Object -First 1
}

function Assert-ElfX64 {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if (
        $bytes.Length -lt 20 -or
        $bytes[0] -ne 0x7f -or
        $bytes[1] -ne 0x45 -or
        $bytes[2] -ne 0x4c -or
        $bytes[3] -ne 0x46
    ) {
        throw "Expected an ELF file: $Path"
    }

    [string[]]$header = Invoke-Native readelf @('-h', $Path) -CaptureOutput
    $headerText = $header -join "`n"
    if (
        $headerText -notmatch 'Class:\s+ELF64' -or
        $headerText -notmatch 'Machine:\s+Advanced Micro Devices X86-64'
    ) {
        throw "Expected an ELF64 x86-64 file: $Path"
    }
}

function Assert-DynamicDependencies {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$AllowedNeeded = @()
    )

    [string[]]$dynamicSection = Invoke-Native readelf @('-d', $Path) -CaptureOutput
    [string[]]$needed = @(
        foreach ($line in $dynamicSection) {
            if ($line -match 'Shared library: \[([^\]]+)\]') {
                $Matches[1]
            }
        }
    ) | Sort-Object -Unique
    if ($AllowedNeeded.Count -gt 0) {
        [string[]]$unexpected = @($needed | Where-Object { $_ -notin $AllowedNeeded })
        if ($unexpected.Count -gt 0) {
            throw "Unexpected dynamic dependencies for ${Path}: $($unexpected -join ', ')"
        }
    }

    [string[]]$lddOutput = Invoke-Native ldd @($Path) -CaptureOutput
    if (($lddOutput -join "`n") -match 'not found') {
        $lddOutput | ForEach-Object { Write-Host $_ }
        throw "Unresolved dynamic dependency for $Path."
    }

    return $needed
}

function Assert-Exports {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Names
    )

    [string[]]$symbols = Invoke-Native nm @('-D', '--defined-only', $Path) -CaptureOutput
    $symbolText = $symbols -join "`n"
    foreach ($name in $Names) {
        if ($symbolText -notmatch "(?m)\s$([regex]::Escape($name))(@@[^\s]+)?$") {
            throw "Required export '$name' was not found in $Path."
        }
    }
}

function Assert-NativeSidecar {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Exports
    )

    Assert-ElfX64 $Path
    Assert-Exports $Path $Exports
    [string[]]$needed = Assert-DynamicDependencies $Path @('libm.so.6', 'libc.so.6')
    $maximumGlibc = Get-MaximumGlibcSymbolVersion $Path
    if (-not $AllowNewerGlibc -and $maximumGlibc -gt $maximumReleaseGlibc) {
        throw "$Path requires GLIBC_$maximumGlibc, newer than the release ceiling GLIBC_$maximumReleaseGlibc."
    }

    Write-Host "Verified $(Split-Path -Leaf $Path): GLIBC_$maximumGlibc; NEEDED=$($needed -join ',')"
}

function Assert-PublishLayout {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$RequireDistributionMetadata
    )

    [string[]]$required = @(
        'decode',
        'decode.dll',
        'vhs-decode',
        'cvbs-decode',
        'ld-decode',
        'hifi-decode',
        'libcoreclr.so',
        'libhostfxr.so',
        'libe_sqlite3.so',
        'libsndfile.so',
        'libsoxr.so'
    )
    if ($RequireDistributionMetadata) {
        $required += @(
            'LICENSE',
            'README-LINUX-X64.md',
            'THIRD-PARTY-NOTICES.md',
            'BUILD-PROVENANCE.txt',
            'third_party/licenses/numpy-LICENSE.txt',
            'third_party/licenses/scipy-LICENSE.txt',
            'third_party/licenses/x86-simd-sort-LICENSE.md',
            'third_party/licenses/msvc-stl-LICENSE.txt',
            'licenses/dotnet-runtime/LICENSE.txt',
            'licenses/dotnet-runtime/ThirdPartyNotices.txt',
            'licenses/aspnetcore-runtime/LICENSE.txt',
            'licenses/aspnetcore-runtime/ThirdPartyNotices.txt',
            'licenses/managed-nuget/MANIFEST.txt',
            'licenses/managed-nuget/SHA256SUMS.txt',
            'licenses/managed-nuget/NetMQ-4.0.4.3-COPYING.LESSER',
            'licenses/managed-nuget/NetMQ-ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010-source.tar.gz',
            'licenses/managed-nuget/MPL-2.0.txt',
            'licenses/managed-nuget/SQLitePCLRaw-3.0.5-LICENSE.txt',
            'licenses/managed-nuget/SQLitePCLRaw-3.0.5-NOTICE.txt',
            'licenses/managed-nuget/SQLite-3.53.4-LICENSE.txt',
            'licenses/managed-nuget/Microsoft-dotnet-MIT.txt',
            'licenses/managed-nuget/Microsoft.Extensions.ObjectPool-10.0.0-THIRD-PARTY-NOTICES.txt',
            'licenses/managed-nuget/System.Security.Cryptography.Pkcs-11.0.0-preview.7.26381.103-THIRD-PARTY-NOTICES.txt',
            'licenses/managed-nuget/System.Security.Cryptography.Xml-11.0.0-preview.7.26381.103-THIRD-PARTY-NOTICES.txt',
            'licenses/managed-nuget/System.ServiceModel.Primitives-10.0.652802-LICENSE.txt',
            'licenses/managed-nuget/System.ServiceModel.Primitives-10.0.652802-THIRD-PARTY-NOTICES.txt'
        )
    }
    foreach ($name in $required) {
        $candidate = Join-Path $Path $name
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Linux publish output is missing $name."
        }
    }

    [string[]]$forbiddenNames = @(
        'sndfile.dll',
        'soxr.dll',
        'vhsdecode_ipp.dll',
        'vhsdecode_cuda_fast.dll',
        'THIRD-PARTY-NOTICES-CUDA-FAST.md'
    )
    [System.IO.FileInfo[]]$forbidden = @(
        Get-ChildItem -LiteralPath $Path -File -Recurse |
            Where-Object {
                $_.Name -in $forbiddenNames -or
                $_.Name -like 'cufft*.dll' -or
                $_.Extension -ieq '.exe' -or
                $_.FullName -match '[/\\]win-x64[/\\]'
            }
    )
    if ($forbidden.Count -gt 0) {
        throw "Linux publish output contains forbidden Windows assets: $($forbidden.FullName -join ', ')"
    }

    if ((Get-ChildItem -LiteralPath $Path -File).Count -lt 25) {
        throw 'Linux publish unexpectedly resembles a single-file build.'
    }

    foreach ($name in @(
        'decode',
        'vhs-decode',
        'cvbs-decode',
        'ld-decode',
        'hifi-decode',
        'libe_sqlite3.so',
        'libsndfile.so',
        'libsoxr.so')) {
        $candidate = Join-Path $Path $name
        Assert-ElfX64 $candidate
        Assert-DynamicDependencies $candidate | Out-Null
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse
    }
}

function New-ReproducibleArchive {
    param(
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$Destination
    )

    $destinationFullPath = Assert-PathUnderArtifacts $Destination
    $tarPath = $destinationFullPath -replace '\.gz$', ''
    foreach ($candidate in @($tarPath, $destinationFullPath)) {
        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Force
        }
    }

    $packageParent = Split-Path -Parent $PackageDirectory
    $packageLeaf = Split-Path -Leaf $PackageDirectory
    Invoke-Native tar @(
        '--sort=name',
        '--mtime=@0',
        '--owner=0',
        '--group=0',
        '--numeric-owner',
        '--format=gnu',
        '--mode=u+rwX,go+rX,go-w',
        '-cf', $tarPath,
        '-C', $packageParent,
        $packageLeaf)
    Invoke-Native gzip @('-n', '-9', '-f', $tarPath)
    if (-not (Test-Path -LiteralPath $destinationFullPath -PathType Leaf)) {
        throw "Archive creation did not produce $destinationFullPath."
    }
}

function Write-PalCvbsSmokeSignal {
    param([Parameter(Mandatory)][string]$Path)

    if ($null -eq ('VhsDecodeLinuxRelease.PalCvbsSmokeSignal' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;

namespace VhsDecodeLinuxRelease
{
    public static class PalCvbsSmokeSignal
    {
        private const int LineLength = 2560;
        private const int TotalLines = 2003;
        private const short Sync = 2000;
        private const short Blank = 10000;
        private static readonly short[] BurstOffsets =
        {
            0, 547, 837, 735, 286, -297, -741, -835, -538
        };

        public static void Write(string path)
        {
            var samples = new short[LineLength];
            var bytes = new byte[LineLength * sizeof(short)];
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            for (int lineNumber = 0; lineNumber < TotalLines; lineNumber++)
            {
                int relative = lineNumber - 10;
                int fieldLine = relative >= 0 ? relative % 313 : -1;
                if (fieldLine >= 0 && fieldLine <= 7)
                {
                    FillVerticalLine(samples, fieldLine);
                }
                else
                {
                    FillRegularLine(samples, lineNumber);
                }

                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static void FillRegularLine(short[] line, int lineNumber)
        {
            Array.Fill(line, Blank);
            Fill(line, 0, 188, Sync);

            for (int i = 224; i < 324; i++)
            {
                line[i] = (short)(Blank + BurstOffsets[(i - 224) % BurstOffsets.Length]);
            }

            for (int i = 400; i < LineLength; i++)
            {
                int horizontalPhase = (i - 400) % 320;
                int horizontal = horizontalPhase < 160
                    ? -900 + ((horizontalPhase * 1800) / 160)
                    : 900 - (((horizontalPhase - 160) * 1800) / 160);
                int vertical = -300 + (((lineNumber % 47) * 600) / 46);
                line[i] = (short)(13500 + horizontal + vertical);
            }
        }

        private static void FillVerticalLine(short[] line, int fieldLine)
        {
            Array.Fill(line, Blank);
            switch (fieldLine)
            {
                case 0:
                case 1:
                case 2:
                case 6:
                case 7:
                    Fill(line, 0, 94, Sync);
                    Fill(line, 1280, 94, Sync);
                    break;
                case 3:
                case 4:
                    Fill(line, 0, 1092, Sync);
                    Fill(line, 1280, 1092, Sync);
                    break;
                case 5:
                    Fill(line, 0, 1092, Sync);
                    Fill(line, 1280, 94, Sync);
                    break;
            }
        }

        private static void Fill(short[] line, int start, int length, short value)
        {
            Array.Fill(line, value, start, length);
        }
    }
}
'@
    }

    [VhsDecodeLinuxRelease.PalCvbsSmokeSignal]::Write($Path)
}

function Get-NormalizedRuntimeText {
    param([Parameter(Mandatory)][string[]]$Lines)

    return (($Lines | ForEach-Object {
        $_ `
            -replace '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3} - ', '' `
            -replace 'Took \d+(?:\.\d+)? seconds', 'Took <elapsed> seconds' `
            -replace '\(\d+(?:\.\d+)? FPS post-setup\)', '(<fps> FPS post-setup)'
    }) -join "`n").Trim()
}

function Invoke-FinalTarSmoke {
    param([Parameter(Mandatory)][string]$Archive)

    $smokeRoot = Reset-Directory (Join-Path $workRoot 'final-tar-smoke')
    Invoke-Native tar @('-xzf', $Archive, '-C', $smokeRoot)
    $packageRoot = Join-Path $smokeRoot $packageName
    Assert-PublishLayout $packageRoot -RequireDistributionMetadata

    foreach ($alias in @('vhs-decode', 'cvbs-decode', 'ld-decode', 'hifi-decode')) {
        Invoke-Native (Join-Path $packageRoot $alias) @('--help')
    }
    Invoke-Native (Join-Path $packageRoot 'decode') @('--help')

    $inputPath = Join-Path $smokeRoot 'synthetic-pal-cvbs.s16'
    Write-PalCvbsSmokeSignal $inputPath
    Assert-Sha256 $inputPath $expectedSmokeHashes.Input
    [hashtable]$hashesByRun = @{}
    [hashtable]$normalizedTextByRun = @{}
    foreach ($run in 1, 2) {
        $outputBase = Join-Path $smokeRoot "exact-sqlite-$run"
        [string[]]$console = @(
            & (Join-Path $packageRoot 'cvbs-decode') `
                '--pal' `
                '--length' '1' `
                '--overwrite' `
                '--write_db' `
                '--dsp-backend' 'exact' `
                $inputPath `
                $outputBase 2>&1 |
                ForEach-Object { [string]$_ }
        )
        $exitCode = $LASTEXITCODE
        $console | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) {
            throw "Final tar Exact+SQLite smoke run $run failed with exit code $exitCode."
        }

        [string[]]$requiredOutputs = @('.tbc', '.tbc.db', '.tbc.json', '.log')
        foreach ($suffix in $requiredOutputs) {
            $outputPath = "$outputBase$suffix"
            if (
                -not (Test-Path -LiteralPath $outputPath -PathType Leaf) -or
                (Get-Item -LiteralPath $outputPath).Length -eq 0
            ) {
                throw "Final tar smoke did not create a non-empty $outputPath."
            }
        }

        $databaseBytes = [System.IO.File]::ReadAllBytes("$outputBase.tbc.db")
        $databaseMagic = [System.Text.Encoding]::ASCII.GetString($databaseBytes, 0, 16)
        if ($databaseMagic -cne "SQLite format 3`0") {
            throw 'Final tar smoke database does not have a SQLite 3 header.'
        }

        $jsonText = [System.IO.File]::ReadAllText("$outputBase.tbc.json")
        $portableJson = [System.Text.RegularExpressions.Regex]::Replace(
            $jsonText,
            '"osInfo":"[^"]*"',
            '"osInfo":"<os>"').TrimEnd([char[]]"`r`n")
        $hashesByRun[$run] = [ordered]@{
            Tbc = Get-Sha256 "$outputBase.tbc"
            Database = Get-Sha256 "$outputBase.tbc.db"
            Json = Get-Sha256 "$outputBase.tbc.json"
            PortableJson = Get-TextSha256 $portableJson
        }
        [string[]]$runtimeText = @($console) + @(Get-Content -LiteralPath "$outputBase.log")
        $normalizedTextByRun[$run] = Get-NormalizedRuntimeText $runtimeText
    }

    foreach ($key in @('Tbc', 'Database', 'Json', 'PortableJson')) {
        if ($hashesByRun[1][$key] -cne $hashesByRun[2][$key]) {
            throw "Final tar smoke is non-deterministic for $key."
        }
    }
    if ($normalizedTextByRun[1] -cne $normalizedTextByRun[2]) {
        throw 'Final tar smoke console/log output differs after normalization.'
    }

    foreach ($key in @('Tbc', 'Database', 'PortableJson')) {
        if ($hashesByRun[1][$key] -cne $expectedSmokeHashes[$key]) {
            throw (
                "Final tar smoke $key oracle mismatch: expected " +
                "$($expectedSmokeHashes[$key]), got $($hashesByRun[1][$key]).")
        }
    }

    Write-Host (
        "Final tar smoke hashes: TBC=$($hashesByRun[1].Tbc); " +
        "SQLite=$($hashesByRun[1].Database); " +
        "portable JSON=$($hashesByRun[1].PortableJson); " +
        "raw JSON=$($hashesByRun[1].Json)")
}

if (-not [OperatingSystem]::IsLinux()) {
    throw 'This release builder must run on glibc Linux.'
}
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne
    [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'This release builder supports only 64-bit x86 Linux (linux-x64).'
}

foreach ($command in @(
    'cmake',
    'ninja',
    'gcc',
    'g++',
    'pkg-config',
    'python3',
    'tar',
    'gzip',
    'getconf',
    'readelf',
    'nm',
    'strip',
    'ldd',
    'chmod',
    'git',
    'ffmpeg',
    'ffprobe',
    'dotnet')) {
    Assert-Command $command
}

[string[]]$glibcOutput = Invoke-Native getconf @('GNU_LIBC_VERSION') -CaptureOutput
if ($glibcOutput.Count -ne 1) {
    throw "This release builder requires glibc; getconf returned '$($glibcOutput -join ' ')'."
}
$glibcMatch = [System.Text.RegularExpressions.Regex]::Match(
    $glibcOutput[0],
    '^glibc\s+(\d+\.\d+)$',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $glibcMatch.Success) {
    throw "This release builder requires glibc; getconf returned '$($glibcOutput -join ' ')'."
}
$hostGlibc = [Version]$glibcMatch.Groups[1].Value
if (-not $AllowNewerGlibc -and $hostGlibc -gt $maximumReleaseGlibc) {
    throw "Host glibc $hostGlibc is newer than the release ceiling $maximumReleaseGlibc. Use Ubuntu 22.04, or -AllowNewerGlibc only for non-release validation."
}
if ($AllowNewerGlibc -and $hostGlibc -gt $maximumReleaseGlibc) {
    Write-Warning "Building on glibc $hostGlibc. This output is for validation and is not certified for the Ubuntu 22.04 / glibc 2.35 release baseline."
}

if (-not (Test-Path -LiteralPath $linuxReadmePath -PathType Leaf)) {
    throw "Linux release documentation is missing: $linuxReadmePath"
}
if (-not (Test-Path -LiteralPath $soxrSourcePath -PathType Leaf)) {
    throw "Bundled libsoxr source archive is missing: $soxrSourcePath"
}
Assert-Sha256 $soxrSourcePath $soxrSourceSha256

Invoke-Native ffmpeg @('-version')
Invoke-Native ffprobe @('-version')

[string[]]$sourceCommitOutput = Invoke-Native git @(
    '-c', "safe.directory=$repositoryRoot",
    '-C', $repositoryRoot, 'rev-parse', 'HEAD') -CaptureOutput
if ($sourceCommitOutput.Count -ne 1 -or
    $sourceCommitOutput[0] -notmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve one full source commit from $repositoryRoot."
}
$sourceCommit = $sourceCommitOutput[0]
[string[]]$workingTreeStatus = Invoke-Native git @(
    '-c', "safe.directory=$repositoryRoot",
    '-C', $repositoryRoot, 'status', '--porcelain', '--untracked-files=normal') -CaptureOutput
$workingTreeDirty = $workingTreeStatus.Count -ne 0

$workRoot = Reset-Directory $workRoot
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$sourceRoot = Join-Path $workRoot 'sources'
$buildRoot = Join-Path $workRoot 'build'
$prefixRoot = Join-Path $workRoot 'prefix'
$dotnetArtifactsRoot = Join-Path $workRoot 'dotnet-artifacts'
New-Item -ItemType Directory -Path $sourceRoot, $buildRoot, $prefixRoot -Force | Out-Null

[hashtable]$sourceArchives = @{}
foreach ($entry in $sources.GetEnumerator()) {
    $sourceArchives[$entry.Key] = Get-VerifiedDownload $entry.Value
    Expand-TarSource $sourceArchives[$entry.Key] $sourceRoot
}
Expand-NormalizedZip $soxrSourcePath $sourceRoot

if (-not $SkipNativeBuild) {
    $commonStaticOptions = @(
        "-DCMAKE_INSTALL_PREFIX=$prefixRoot",
        '-DCMAKE_INSTALL_LIBDIR=lib',
        '-DCMAKE_POSITION_INDEPENDENT_CODE=ON',
        '-DBUILD_SHARED_LIBS=OFF'
    )

    $oggBuild = Join-Path $buildRoot 'libogg'
    Invoke-CMakeConfigure `
        (Join-Path $sourceRoot $sources.libogg.SourceDirectory) `
        $oggBuild `
        ($commonStaticOptions + @('-DBUILD_TESTING=OFF', '-DINSTALL_DOCS=OFF'))
    Invoke-CMakeBuildAndInstall $oggBuild

    $savedPkgConfigPath = $env:PKG_CONFIG_PATH
    $savedPkgConfigLibdir = $env:PKG_CONFIG_LIBDIR
    $savedCmakePrefixPath = $env:CMAKE_PREFIX_PATH
    $savedCmakeLibraryPath = $env:CMAKE_LIBRARY_PATH
    $savedCmakeIncludePath = $env:CMAKE_INCLUDE_PATH
    try {
        $env:PKG_CONFIG_PATH = Join-Path $prefixRoot 'lib\pkgconfig'
        $env:PKG_CONFIG_LIBDIR = $env:PKG_CONFIG_PATH
        $env:CMAKE_PREFIX_PATH = $prefixRoot
        $env:CMAKE_LIBRARY_PATH = Join-Path $prefixRoot 'lib'
        $env:CMAKE_INCLUDE_PATH = Join-Path $prefixRoot 'include'

        $vorbisBuild = Join-Path $buildRoot 'libvorbis'
        Invoke-CMakeConfigure `
            (Join-Path $sourceRoot $sources.libvorbis.SourceDirectory) `
            $vorbisBuild `
            ($commonStaticOptions + @('-DBUILD_TESTING=OFF', '-DBUILD_EXAMPLES=OFF'))
        Invoke-CMakeBuildAndInstall $vorbisBuild

        $opusBuild = Join-Path $buildRoot 'opus'
        Invoke-CMakeConfigure `
            (Join-Path $sourceRoot $sources.opus.SourceDirectory) `
            $opusBuild `
            ($commonStaticOptions + @(
                '-DOPUS_BUILD_TESTING=OFF',
                '-DOPUS_BUILD_PROGRAMS=OFF',
                '-DOPUS_BUILD_EXAMPLES=OFF'))
        Invoke-CMakeBuildAndInstall $opusBuild

        $flacBuild = Join-Path $buildRoot 'flac'
        Invoke-CMakeConfigure `
            (Join-Path $sourceRoot $sources.flac.SourceDirectory) `
            $flacBuild `
            ($commonStaticOptions + @(
                '-DBUILD_CXXLIBS=OFF',
                '-DBUILD_PROGRAMS=OFF',
                '-DBUILD_EXAMPLES=OFF',
                '-DBUILD_TESTING=OFF',
                '-DBUILD_DOCS=OFF',
                '-DINSTALL_MANPAGES=OFF',
                '-DWITH_OGG=ON'))
        Invoke-CMakeBuildAndInstall $flacBuild

        $sndfileBuild = Join-Path $buildRoot 'libsndfile'
        Invoke-CMakeConfigure `
            (Join-Path $sourceRoot $sources.libsndfile.SourceDirectory) `
            $sndfileBuild `
            @(
                "-DCMAKE_INSTALL_PREFIX=$prefixRoot",
                '-DCMAKE_INSTALL_LIBDIR=lib',
                '-DBUILD_SHARED_LIBS=ON',
                '-DBUILD_PROGRAMS=OFF',
                '-DBUILD_EXAMPLES=OFF',
                '-DBUILD_TESTING=OFF',
                '-DBUILD_REGTEST=OFF',
                '-DENABLE_EXTERNAL_LIBS=ON',
                '-DENABLE_MPEG=OFF',
                '-DENABLE_CPACK=OFF',
                '-DINSTALL_PKGCONFIG_MODULE=OFF',
                '-DINSTALL_CMAKE_CONFIG_MODULE=OFF')
        Invoke-CMakeBuildAndInstall $sndfileBuild
    }
    finally {
        $env:PKG_CONFIG_PATH = $savedPkgConfigPath
        $env:PKG_CONFIG_LIBDIR = $savedPkgConfigLibdir
        $env:CMAKE_PREFIX_PATH = $savedCmakePrefixPath
        $env:CMAKE_LIBRARY_PATH = $savedCmakeLibraryPath
        $env:CMAKE_INCLUDE_PATH = $savedCmakeIncludePath
    }

    $soxrBuild = Join-Path $buildRoot 'libsoxr'
    Invoke-CMakeConfigure `
        (Join-Path $sourceRoot 'soxr-a66f3ee') `
        $soxrBuild `
        @(
            '-DBUILD_SHARED_LIBS=ON',
            '-DBUILD_TESTS=OFF',
            '-DBUILD_LSR_TESTS=OFF',
            '-DWITH_OPENMP=OFF')
    Invoke-Native cmake @('--build', $soxrBuild, '--parallel', [string]$jobs)

    $nativeStage = Reset-Directory $nativeStage
    $sndfileRealPath = Get-RealSharedObject $sndfileBuild 'libsndfile'
    $soxrRealPath = Get-RealSharedObject $soxrBuild 'libsoxr'
    [System.IO.File]::Copy($sndfileRealPath, (Join-Path $nativeStage 'libsndfile.so'), $true)
    [System.IO.File]::Copy($soxrRealPath, (Join-Path $nativeStage 'libsoxr.so'), $true)
    Invoke-Native strip @('--strip-unneeded', (Join-Path $nativeStage 'libsndfile.so'))
    Invoke-Native strip @('--strip-unneeded', (Join-Path $nativeStage 'libsoxr.so'))
}

$sndfileStagePath = Join-Path $nativeStage 'libsndfile.so'
$soxrStagePath = Join-Path $nativeStage 'libsoxr.so'
foreach ($candidate in @($sndfileStagePath, $soxrStagePath)) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Missing staged Linux native sidecar: $candidate"
    }
}
Assert-NativeSidecar $sndfileStagePath @(
    'sf_open',
    'sf_command',
    'sf_writef_float',
    'sf_write_short',
    'sf_write_sync',
    'sf_seek',
    'sf_readf_short',
    'sf_readf_int',
    'sf_error',
    'sf_close',
    'sf_strerror')
Assert-NativeSidecar $soxrStagePath @(
    'soxr_version',
    'soxr_io_spec',
    'soxr_quality_spec',
    'soxr_create',
    'soxr_process',
    'soxr_delay',
    'soxr_clear',
    'soxr_delete')

$expectedSdk = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw |
    ConvertFrom-Json).sdk.version
$actualSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -cne $expectedSdk) {
    throw "Expected .NET SDK $expectedSdk, but dotnet resolved $actualSdk."
}
Invoke-Native dotnet @('--info')

Invoke-Native dotnet @('restore', $solutionPath, '--artifacts-path', $dotnetArtifactsRoot)
Invoke-Native dotnet @(
    'build', $solutionPath,
    '--configuration', 'Release',
    '--no-restore',
    '--artifacts-path', $dotnetArtifactsRoot)
if (-not $SkipTests) {
    [string[]]$testArguments = @(
        'test',
        '--solution', $solutionPath,
        '--configuration', 'Release',
        '--no-build',
        '--no-restore',
        '--artifacts-path', $dotnetArtifactsRoot)
    foreach ($method in $windowsFrozenBitOracleMethods) {
        $testArguments += @('--filter-not-method', "*$method*")
    }
    $testArguments += @(
        '--minimum-expected-tests', [string]$minimumLinuxTestCount)
    Write-Host (
        "Linux test gate: at least $minimumLinuxTestCount tests; excluding " +
        "$($windowsFrozenBitOracleMethods.Count) method-scoped Windows frozen-bit oracles.")
    Invoke-Native dotnet $testArguments
}
else {
    Write-Warning 'Skipping the full test suite. This output is not release-certified.'
}

Invoke-Native dotnet @(
    'restore', $cliProjectPath,
    '--runtime', 'linux-x64',
    '--artifacts-path', $dotnetArtifactsRoot)
$publishRoot = Reset-Directory $publishRoot
Invoke-Native dotnet @(
    'publish', $cliProjectPath,
    '--configuration', 'Release',
    '--runtime', 'linux-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--artifacts-path', $dotnetArtifactsRoot,
    '--property:PublishProfile=LinuxX64Folder',
    '--output', $publishRoot)

foreach ($name in @('decode', 'vhs-decode', 'cvbs-decode', 'ld-decode', 'hifi-decode')) {
    Invoke-Native chmod @('0755', (Join-Path $publishRoot $name))
}
Assert-PublishLayout $publishRoot

$packageStagingParent = Join-Path $workRoot 'package'
$packageDirectory = Join-Path $packageStagingParent $packageName
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-DirectoryContents $publishRoot $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageDirectory
Copy-Item -LiteralPath $linuxReadmePath -Destination (Join-Path $packageDirectory 'README-LINUX-X64.md')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $packageDirectory

$referencedLicenseRoot = Join-Path $packageDirectory 'third_party\licenses'
New-Item -ItemType Directory -Path $referencedLicenseRoot -Force | Out-Null
Copy-DirectoryContents (Join-Path $repositoryRoot 'third_party\licenses') $referencedLicenseRoot

$licenseRoot = Join-Path $packageDirectory 'licenses'
$sndfileLicenseRoot = Join-Path $licenseRoot 'libsndfile'
$soxrLicenseRoot = Join-Path $licenseRoot 'libsoxr'
$codecSourceRoot = Join-Path $licenseRoot 'codec-sources'
$dotnetRuntimeLicenseRoot = Join-Path $licenseRoot 'dotnet-runtime'
$aspnetCoreRuntimeLicenseRoot = Join-Path $licenseRoot 'aspnetcore-runtime'
$managedNugetLicenseRoot = Join-Path $licenseRoot 'managed-nuget'
New-Item -ItemType Directory -Path $sndfileLicenseRoot, $soxrLicenseRoot, $codecSourceRoot, $dotnetRuntimeLicenseRoot, $aspnetCoreRuntimeLicenseRoot, $managedNugetLicenseRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party\libsndfile\COPYING.LGPL') -Destination $sndfileLicenseRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party\libsndfile\README.md') -Destination $sndfileLicenseRoot
Copy-Item -LiteralPath $sourceArchives.libsndfile -Destination $sndfileLicenseRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party\libsoxr\COPYING.LGPL') -Destination $soxrLicenseRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party\libsoxr\LICENCE') -Destination $soxrLicenseRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party\libsoxr\README.md') -Destination $soxrLicenseRoot
Copy-Item -LiteralPath $soxrSourcePath -Destination $soxrLicenseRoot
foreach ($key in @('libogg', 'libvorbis', 'opus', 'flac')) {
    Copy-Item -LiteralPath $sourceArchives[$key] -Destination $codecSourceRoot
}

$cliAssetsPath = Join-Path $dotnetArtifactsRoot 'obj\VHSDecode.Cli\project.assets.json'
if (-not (Test-Path -LiteralPath $cliAssetsPath -PathType Leaf)) {
    throw "The CLI restore assets file is missing at $cliAssetsPath."
}

$cliAssets = Get-Content -LiteralPath $cliAssetsPath -Raw | ConvertFrom-Json -AsHashtable
$nugetPackageRoot = [string]$cliAssets['project']['restore']['packagesPath']
[string[]]$targetFrameworks = @($cliAssets['project']['frameworks'].Keys)
if ($targetFrameworks.Count -ne 1) {
    throw "Expected one CLI target framework, found $($targetFrameworks.Count)."
}

[string[]]$expectedManagedPackages = @(
    'AsyncIO/0.1.69',
    'Microsoft.Data.Sqlite.Core/11.0.0-preview.7.26381.103',
    'Microsoft.Extensions.ObjectPool/10.0.0',
    'NaCl.Net/0.1.13',
    'NetMQ/4.0.4.3',
    'SQLite/3.53.4',
    'SQLitePCLRaw.bundle_e_sqlite3/3.0.5',
    'SQLitePCLRaw.config.e_sqlite3/3.0.5',
    'SQLitePCLRaw.core/3.0.5',
    'SQLitePCLRaw.provider.e_sqlite3/3.0.5',
    'System.Security.Cryptography.Pkcs/11.0.0-preview.7.26381.103',
    'System.Security.Cryptography.Xml/11.0.0-preview.7.26381.103',
    'System.ServiceModel.Primitives/10.0.652802')
[string[]]$actualManagedPackages = @(
    $cliAssets['libraries'].GetEnumerator() |
        Where-Object { $_.Value['type'] -ceq 'package' } |
        ForEach-Object { $_.Key } |
        Sort-Object)
[object[]]$managedPackageDrift = @(
    Compare-Object `
        -ReferenceObject ($expectedManagedPackages | Sort-Object) `
        -DifferenceObject $actualManagedPackages `
        -CaseSensitive)
if ($managedPackageDrift.Count -ne 0) {
    throw "The restored CLI NuGet graph drifted from the licensed package manifest: $($managedPackageDrift | Out-String)"
}

foreach ($entry in $managedLicenseSources.GetEnumerator()) {
    $downloadedLicense = Get-VerifiedDownload $entry.Value
    Copy-Item -LiteralPath $downloadedLicense -Destination $managedNugetLicenseRoot
}

[object[]]$restoredManagedNoticeFiles = @(
    [pscustomobject]@{
        Identity = 'SQLite/3.53.4'
        Source = 'LICENSE.txt'
        Destination = 'SQLite-3.53.4-LICENSE.txt'
    },
    [pscustomobject]@{
        Identity = 'Microsoft.Extensions.ObjectPool/10.0.0'
        Source = 'THIRD-PARTY-NOTICES.TXT'
        Destination = 'Microsoft.Extensions.ObjectPool-10.0.0-THIRD-PARTY-NOTICES.txt'
    },
    [pscustomobject]@{
        Identity = 'System.Security.Cryptography.Pkcs/11.0.0-preview.7.26381.103'
        Source = 'THIRD-PARTY-NOTICES.TXT'
        Destination = 'System.Security.Cryptography.Pkcs-11.0.0-preview.7.26381.103-THIRD-PARTY-NOTICES.txt'
    },
    [pscustomobject]@{
        Identity = 'System.Security.Cryptography.Xml/11.0.0-preview.7.26381.103'
        Source = 'THIRD-PARTY-NOTICES.TXT'
        Destination = 'System.Security.Cryptography.Xml-11.0.0-preview.7.26381.103-THIRD-PARTY-NOTICES.txt'
    },
    [pscustomobject]@{
        Identity = 'System.ServiceModel.Primitives/10.0.652802'
        Source = 'LICENSE.TXT'
        Destination = 'Microsoft-dotnet-MIT.txt'
    },
    [pscustomobject]@{
        Identity = 'System.ServiceModel.Primitives/10.0.652802'
        Source = 'LICENSE.TXT'
        Destination = 'System.ServiceModel.Primitives-10.0.652802-LICENSE.txt'
    },
    [pscustomobject]@{
        Identity = 'System.ServiceModel.Primitives/10.0.652802'
        Source = 'THIRD-PARTY-NOTICES.TXT'
        Destination = 'System.ServiceModel.Primitives-10.0.652802-THIRD-PARTY-NOTICES.txt'
    })
foreach ($file in $restoredManagedNoticeFiles) {
    $separator = $file.Identity.LastIndexOf('/')
    $packageId = $file.Identity.Substring(0, $separator).ToLowerInvariant()
    $packageVersion = $file.Identity.Substring($separator + 1)
    $packageRoot = Join-Path $nugetPackageRoot (Join-Path $packageId $packageVersion)
    $sourcePath = Join-Path $packageRoot $file.Source
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Restored package $($file.Identity) is missing $($file.Source)."
    }

    Copy-Item `
        -LiteralPath $sourcePath `
        -Destination (Join-Path $managedNugetLicenseRoot $file.Destination)
}

[string[]]$managedLicenseManifest = @(
    'Managed NuGet dependency license manifest',
    'This is the complete package graph restored for the Linux x64 CLI publish.',
    '',
    'NetMQ 4.0.4.3',
    'License: LGPL-3.0 with the NetMQ static-linking exception',
    'Source commit: ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010',
    'Source: https://github.com/zeromq/netmq/tree/ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010',
    'License file: NetMQ-4.0.4.3-COPYING.LESSER',
    'Corresponding source: NetMQ-ca87d32d5ca5d8a2675fb7a9925e4b3dc8c35010-source.tar.gz',
    'The archive keeps NetMQ.dll as a separate managed assembly.',
    '',
    'AsyncIO 0.1.69',
    'License: MPL-2.0',
    'Source commit from the package portable PDB SourceLink: 0b0cf4c65b049b2e483b172e530f4db970db25e4',
    'Source: https://github.com/somdoron/AsyncIO/tree/0b0cf4c65b049b2e483b172e530f4db970db25e4',
    'License file: MPL-2.0.txt',
    '',
    'NaCl.Net 0.1.13',
    'License: MPL-2.0',
    'Source tag commit: aee0c42faf09e7c4f4eea66a395aa77b2c30ce6d',
    'Source: https://github.com/somdoron/NaCl.net/tree/aee0c42faf09e7c4f4eea66a395aa77b2c30ce6d',
    'License file: MPL-2.0.txt',
    '',
    'Microsoft.Data.Sqlite.Core 11.0.0-preview.7.26381.103',
    'System.Security.Cryptography.Pkcs 11.0.0-preview.7.26381.103',
    'System.Security.Cryptography.Xml 11.0.0-preview.7.26381.103',
    'License: MIT',
    'Source commit: e2c1e00b3d0f96afb892fb261d5921565b400246',
    'Source: https://github.com/dotnet/dotnet/tree/e2c1e00b3d0f96afb892fb261d5921565b400246',
    'License file: Microsoft-dotnet-MIT.txt',
    'Package notices: System.Security.Cryptography.Pkcs-11.0.0-preview.7.26381.103-THIRD-PARTY-NOTICES.txt',
    '                 System.Security.Cryptography.Xml-11.0.0-preview.7.26381.103-THIRD-PARTY-NOTICES.txt',
    '',
    'Microsoft.Extensions.ObjectPool 10.0.0',
    'License: MIT',
    'Source commit: b0f34d51fccc69fd334253924abd8d6853fad7aa',
    'Source: https://github.com/dotnet/dotnet/tree/b0f34d51fccc69fd334253924abd8d6853fad7aa',
    'License file: Microsoft-dotnet-MIT.txt',
    'Package notice: Microsoft.Extensions.ObjectPool-10.0.0-THIRD-PARTY-NOTICES.txt',
    '',
    'System.ServiceModel.Primitives 10.0.652802',
    'License: MIT',
    'Source commit: e9d8c1c2a051618689bc22ab263f6ff0f2493d64',
    'Source: https://github.com/dotnet/wcf/tree/e9d8c1c2a051618689bc22ab263f6ff0f2493d64',
    'License file: System.ServiceModel.Primitives-10.0.652802-LICENSE.txt',
    'Package notice: System.ServiceModel.Primitives-10.0.652802-THIRD-PARTY-NOTICES.txt',
    '',
    'SQLitePCLRaw.bundle_e_sqlite3 3.0.5',
    'SQLitePCLRaw.config.e_sqlite3 3.0.5',
    'SQLitePCLRaw.core 3.0.5',
    'SQLitePCLRaw.provider.e_sqlite3 3.0.5',
    'License: Apache-2.0',
    'Config source commit: 96043b8cff323f21919df86a431c136655d81b4a',
    'Core/provider source commit: ed046114d5a30534e13294d94d78eb73de896ad4',
    'Source: https://github.com/ericsink/SQLitePCL.raw',
    'License file: SQLitePCLRaw-3.0.5-LICENSE.txt',
    'Notice file: SQLitePCLRaw-3.0.5-NOTICE.txt',
    '',
    'SQLite 3.53.4',
    'License: Public Domain',
    'Source: https://sqlite.org/',
    'License file from the restored package: SQLite-3.53.4-LICENSE.txt')
[System.IO.File]::WriteAllLines(
    (Join-Path $managedNugetLicenseRoot 'MANIFEST.txt'),
    $managedLicenseManifest,
    [System.Text.UTF8Encoding]::new($false))
[string[]]$managedLicenseHashLines = @(
    Get-ChildItem -LiteralPath $managedNugetLicenseRoot -File |
        Sort-Object Name |
        ForEach-Object { "$(Get-Sha256 $_.FullName)  $($_.Name)" })
[System.IO.File]::WriteAllLines(
    (Join-Path $managedNugetLicenseRoot 'SHA256SUMS.txt'),
    $managedLicenseHashLines,
    [System.Text.UTF8Encoding]::new($false))

$runtimePackDestinations = [ordered]@{
    'Microsoft.NETCore.App.Runtime.linux-x64' = $dotnetRuntimeLicenseRoot
    'Microsoft.AspNetCore.App.Runtime.linux-x64' = $aspnetCoreRuntimeLicenseRoot
}
foreach ($entry in $runtimePackDestinations.GetEnumerator()) {
    [object[]]$matches = @(
        $cliAssets['project']['frameworks'][$targetFrameworks[0]]['downloadDependencies'] |
            Where-Object { $_['name'] -ceq $entry.Key }
    )
    if ($matches.Count -ne 1) {
        throw "Expected one restored runtime pack named $($entry.Key), found $($matches.Count)."
    }

    [string[]]$versionBounds = ([string]$matches[0]['version']).Trim('[', ']').Split(',')
    if ($versionBounds.Count -ne 2 -or
        $versionBounds[0].Trim() -cne $versionBounds[1].Trim()) {
        throw "Runtime pack $($entry.Key) is not pinned to one exact version."
    }

    $runtimePackRoot = Join-Path $nugetPackageRoot (
        Join-Path $entry.Key.ToLowerInvariant() $versionBounds[0].Trim())
    [System.IO.FileInfo[]]$runtimePackFiles = @(Get-ChildItem -LiteralPath $runtimePackRoot -File)
    $runtimePackNoticeNames = [ordered]@{
        'LICENSE.TXT' = 'LICENSE.txt'
        'THIRD-PARTY-NOTICES.TXT' = 'ThirdPartyNotices.txt'
    }
    foreach ($name in $runtimePackNoticeNames.GetEnumerator()) {
        [System.IO.FileInfo[]]$source = @(
            $runtimePackFiles | Where-Object { $_.Name -ieq $name.Key }
        )
        if ($source.Count -ne 1) {
            throw "Runtime pack $($entry.Key) is missing $($name.Key)."
        }

        Copy-Item -LiteralPath $source[0].FullName -Destination (Join-Path $entry.Value $name.Value)
    }
}

[string[]]$sourceHashLines = @(
    foreach ($entry in $sources.GetEnumerator()) {
        "$($entry.Value.Sha256)  $($entry.Value.FileName)"
    }
    foreach ($entry in $managedLicenseSources.GetEnumerator()) {
        "$($entry.Value.Sha256)  $($entry.Value.FileName)"
    }
    "$soxrSourceSha256  $(Split-Path -Leaf $soxrSourcePath)"
)
[System.IO.File]::WriteAllLines(
    (Join-Path $licenseRoot 'SOURCE-SHA256SUMS.txt'),
    $sourceHashLines,
    [System.Text.UTF8Encoding]::new($false))

$maximumSndfileGlibc = Get-MaximumGlibcSymbolVersion $sndfileStagePath
$maximumSoxrGlibc = Get-MaximumGlibcSymbolVersion $soxrStagePath
$fullTestSuiteRun = -not $SkipTests.IsPresent
$nativeSidecarsRebuilt = -not $SkipNativeBuild.IsPresent
$glibcCeilingEnforced = -not $AllowNewerGlibc.IsPresent
[string[]]$provenance = @(
    'vhs-decode-dotnet linux-x64 build provenance',
    'Repository: https://github.com/JunliangRen/vhs-decode-dotnet',
    "Source commit: $sourceCommit",
    "Working tree dirty: $($workingTreeDirty.ToString().ToLowerInvariant())",
    "Full test suite run: $($fullTestSuiteRun.ToString().ToLowerInvariant())",
    "Native sidecars rebuilt: $($nativeSidecarsRebuilt.ToString().ToLowerInvariant())",
    "Release glibc ceiling enforced: $($glibcCeilingEnforced.ToString().ToLowerInvariant())",
    "Host glibc: $hostGlibc",
    "Release glibc ceiling: $maximumReleaseGlibc",
    "libsndfile maximum GLIBC symbol version: $maximumSndfileGlibc",
    "libsoxr maximum GLIBC symbol version: $maximumSoxrGlibc",
    "Pinned .NET SDK: $expectedSdk",
    'Runtime identifier: linux-x64',
    'Self-contained: true',
    'Single-file: false',
    'ReadyToRun: false',
    'Trimmed: false',
    'DSP release contract: exact',
    'Native codecs linked into libsndfile: libogg 1.3.5, libvorbis 1.3.7, opus 1.4, FLAC 1.4.2',
    'libsoxr source commit: a66f3eeeeb62a32403ff143b756eed92b1ec6b62')
[System.IO.File]::WriteAllLines(
    (Join-Path $packageDirectory 'BUILD-PROVENANCE.txt'),
    $provenance,
    [System.Text.UTF8Encoding]::new($false))

foreach ($name in @('decode', 'vhs-decode', 'cvbs-decode', 'ld-decode', 'hifi-decode')) {
    Invoke-Native chmod @('0755', (Join-Path $packageDirectory $name))
}
Assert-PublishLayout $packageDirectory -RequireDistributionMetadata

New-ReproducibleArchive $packageDirectory $archivePath
$firstArchiveHash = Get-Sha256 $archivePath
New-ReproducibleArchive $packageDirectory $archivePath
$secondArchiveHash = Get-Sha256 $archivePath
if ($firstArchiveHash -cne $secondArchiveHash) {
    throw "Repeated tar creation was not deterministic: $firstArchiveHash versus $secondArchiveHash."
}
[System.IO.File]::WriteAllText(
    $archiveHashPath,
    "$secondArchiveHash  $(Split-Path -Leaf $archivePath)`n",
    [System.Text.UTF8Encoding]::new($false))

Invoke-FinalTarSmoke $archivePath

$archive = Get-Item -LiteralPath $archivePath
Write-Host "Linux x64 release archive verified: $($archive.FullName) ($($archive.Length) bytes)"
Write-Host "SHA-256: $secondArchiveHash"
