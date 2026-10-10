[CmdletBinding()]
param([switch]$AllowNewerGlibc)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsLinux() -or
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne
        [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'This builder requires glibc Linux x64. Use build-ipp-native.ps1 on Windows.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$workRoot = Join-Path $artifactsRoot 'linux-ipp-build'
$downloadRoot = Join-Path $artifactsRoot 'downloads/linux-ipp'
$stageRoot = Join-Path $artifactsRoot 'native/Release/linux-x64'
$configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'linux-ipp-packages.json') -Raw |
    ConvertFrom-Json

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    Write-Host ">> $Command $($Arguments -join ' ')"
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

function Reset-IppDirectory {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd('/') + '/'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "Refusing to replace a directory outside artifacts: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    return $resolved
}

foreach ($name in @('cmake', 'ninja', 'g++', 'dpkg-deb', 'python3', 'readelf', 'nm', 'strip', 'getconf')) {
    if ($null -eq (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required Linux IPP build command '$name' is missing."
    }
}
$glibcText = (& getconf GNU_LIBC_VERSION).Trim()
if ($LASTEXITCODE -ne 0 -or $glibcText -notmatch '^glibc (\d+\.\d+)$') {
    throw 'Intel IPP requires glibc Linux; musl is not a supported target.'
}
$hostGlibc = [Version]$Matches[1]
if (-not $AllowNewerGlibc -and $hostGlibc -gt [Version]'2.35') {
    throw "Build host glibc $hostGlibc is newer than the Ubuntu 22.04 build baseline. Use -AllowNewerGlibc only for local validation."
}

New-Item -ItemType Directory -Path $downloadRoot, $stageRoot -Force | Out-Null
$workRoot = Reset-IppDirectory $workRoot
$extractedRoot = Join-Path $workRoot 'packages'
New-Item -ItemType Directory -Path $extractedRoot -Force | Out-Null
foreach ($package in $configuration.packages) {
    $archive = Join-Path $downloadRoot $package.fileName
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $temporary = "$archive.download"
        Invoke-WebRequest -Uri $package.url -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) {
            throw "Intel IPP package checksum mismatch: $($package.fileName)"
        }
        Move-Item -LiteralPath $temporary -Destination $archive -Force
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) {
        throw "Cached Intel IPP package checksum mismatch: $($package.fileName)"
    }
    Invoke-Checked dpkg-deb @('--extract', $archive, $extractedRoot)
}

$ippRoot = Join-Path $extractedRoot 'opt/intel/oneapi/ipp/2026.0'
$buildRoot = Join-Path $workRoot 'build'
Invoke-Checked cmake @(
    '-S', (Join-Path $repositoryRoot 'src/VHSDecode.Ipp.Native'),
    '-B', $buildRoot, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release',
    "-DIPP_ROOT=$ippRoot", '-DBUILD_TESTING=ON')
Invoke-Checked cmake @('--build', $buildRoot, '--parallel', [string][Math]::Max(1, [Environment]::ProcessorCount))
Invoke-Checked ctest @('--test-dir', $buildRoot, '--output-on-failure')

$fixtureRoot = Reset-IppDirectory (Join-Path $artifactsRoot 'native/Tests/linux-x64/ipp-probe-fixtures')
Get-ChildItem -LiteralPath (Join-Path $buildRoot 'probe-fixtures') -Filter '*.so' -File |
    Copy-Item -Destination $fixtureRoot

$bridgePath = Join-Path $stageRoot 'libvhsdecode_ipp.so'
Copy-Item -LiteralPath (Join-Path $buildRoot 'libvhsdecode_ipp.so') -Destination $bridgePath -Force
Invoke-Checked strip @('--strip-unneeded', $bridgePath)
$licenseRoot = Reset-IppDirectory (Join-Path $stageRoot 'intel-ipp')
$auditPath = Join-Path $licenseRoot 'ELF-ABI.json'
$maximumGlibc = if ($AllowNewerGlibc) { [string]$hostGlibc } else { '2.34' }
Invoke-Checked python3 @(
    (Join-Path $PSScriptRoot 'verify-linux-native.py'), 'ipp', $bridgePath,
    '--max-glibc', $maximumGlibc, '--output', $auditPath)

# Keep the actual license and notices from these exact Linux packages.
$vendorLicense = Join-Path $extractedRoot 'opt/intel/oneapi/licensing/2026.0/licensing/2026.0/license.htm'
$vendorNotices = Join-Path $ippRoot 'share/doc/ipp/licensing/third-party-programs.txt'
foreach ($source in @($vendorLicense, $vendorNotices)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "The pinned Intel IPP package lacks required licensing material: $source"
    }
}
Copy-Item -LiteralPath $vendorLicense -Destination (Join-Path $licenseRoot 'Intel-2026-license.htm')
Copy-Item -LiteralPath $vendorNotices -Destination (Join-Path $licenseRoot 'third-party-programs.txt')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'third_party/intel-ipp/LICENSE.txt') -Destination $licenseRoot

$manifest = [ordered]@{
    packageVersion = $configuration.ippVersion
    runtimeVersion = $configuration.runtimeVersion
    abi = '0x00010004'
    linkage = 'static-pic-sequential'
    cppRuntime = 'system-libstdc++'
    hostGlibc = [string]$hostGlibc
    releaseBuildHost = -not $AllowNewerGlibc.IsPresent
    bridgeSha256 = (Get-FileHash -LiteralPath $bridgePath -Algorithm SHA256).Hash.ToLowerInvariant()
    packages = $configuration.packages
}
$manifestText = ($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n").Replace("`n", "`r`n") + "`r`n"
[IO.File]::WriteAllText((Join-Path $licenseRoot 'BUILD-MANIFEST.json'), $manifestText, [Text.UTF8Encoding]::new($false))
Write-Host "Verified Linux IPP bridge: $bridgePath"
