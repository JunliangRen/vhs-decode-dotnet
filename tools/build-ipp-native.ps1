[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$projectPath = Join-Path $repositoryRoot 'src\VHSDecode.Ipp.Native\VHSDecode.Ipp.Native.vcxproj'
$smokeProjectPath = Join-Path $repositoryRoot 'src\VHSDecode.Ipp.Native\tests\VHSDecode.Ipp.Native.Smoke.vcxproj'
$nativeOutputPath = Join-Path $repositoryRoot 'src\VHSDecode.Ipp.Native\bin\x64\Release\vhsdecode_ipp.dll'
$smokeOutputPath = Join-Path $repositoryRoot 'src\VHSDecode.Ipp.Native\bin\x64\Release\vhsdecode_ipp_smoke.exe'
$artifactDirectory = Join-Path $repositoryRoot 'artifacts\native\Release\win-x64'
$artifactPath = Join-Path $artifactDirectory 'vhsdecode_ipp.dll'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Intel IPP native project was not found: $projectPath"
}
if (-not (Test-Path -LiteralPath $smokeProjectPath -PathType Leaf)) {
    throw "Intel IPP native smoke project was not found: $smokeProjectPath"
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswherePath = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath -PathType Leaf)) {
    $vswhereCommand = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($null -eq $vswhereCommand) {
        throw 'vswhere.exe was not found. Install Visual Studio Build Tools with the MSBuild and Desktop development with C++ workloads.'
    }

    $vswherePath = $vswhereCommand.Source
}

[string[]]$msbuildCandidates = @(
    & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($LASTEXITCODE -ne 0 -or $msbuildCandidates.Count -eq 0) {
    throw 'vswhere.exe could not locate MSBuild.'
}

$msbuildPath = $msbuildCandidates[0]
[string[]]$dumpbinCandidates = @(
    & $vswherePath -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Descending
)
if ($LASTEXITCODE -ne 0 -or $dumpbinCandidates.Count -eq 0) {
    throw 'vswhere.exe could not locate the x64 C++ dumpbin.exe tool.'
}

$dumpbinPath = $dumpbinCandidates[0]
Write-Host "Restoring $projectPath"
& $msbuildPath $projectPath '/t:Restore' '/p:Configuration=Release' '/p:Platform=x64' '/m'
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild restore failed with exit code $LASTEXITCODE."
}

Write-Host "Building $projectPath"
& $msbuildPath $projectPath '/t:Build' '/p:Configuration=Release' '/p:Platform=x64' '/p:RestoreIgnoreFailedSources=false' '/m'
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $nativeOutputPath -PathType Leaf)) {
    throw "The native build completed without producing $nativeOutputPath."
}

Write-Host "Building $smokeProjectPath"
& $msbuildPath $smokeProjectPath '/t:Build' '/p:Configuration=Release' '/p:Platform=x64' '/p:RestoreIgnoreFailedSources=false' '/m'
if ($LASTEXITCODE -ne 0) {
    throw "Native smoke build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $smokeOutputPath -PathType Leaf)) {
    throw "The native smoke build completed without producing $smokeOutputPath."
}

Write-Host "Running $smokeOutputPath"
& $smokeOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Native smoke tests failed with exit code $LASTEXITCODE."
}

$artifactDirectoryFullPath = [System.IO.Path]::GetFullPath($artifactDirectory)
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $artifactDirectoryFullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace an artifact directory outside the repository: $artifactDirectoryFullPath"
}

if (Test-Path -LiteralPath $artifactDirectoryFullPath) {
    Remove-Item -LiteralPath $artifactDirectoryFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactDirectoryFullPath -Force | Out-Null
Copy-Item -LiteralPath $nativeOutputPath -Destination $artifactPath

Write-Host "Checking native dependencies in $artifactPath"
$dumpbinOutput = @(& $dumpbinPath /nologo /dependents $artifactPath 2>&1)
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

if ($dependencyNames.Count -eq 0) {
    $dumpbinOutput | ForEach-Object { Write-Host $_ }
    throw 'dumpbin.exe did not report any dependencies; refusing to accept an unverified artifact.'
}

[string[]]$forbiddenDependencies = @(
    $dependencyNames | Where-Object {
        $_ -match '^(IPP|LIBIOMP|IOMP|TBB|VCRUNTIME|MSVCP|MSVCRT|CONCRT|UCRTBASE|API-MS-WIN-CRT-)'
    }
)
if ($forbiddenDependencies.Count -gt 0) {
    throw "The IPP bridge has forbidden IPP, OpenMP, TBB, or Visual C++ runtime DLL dependencies: $($forbiddenDependencies -join ', ')"
}

$systemDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
[string[]]$nonSystemDependencies = @(
    $dependencyNames | Where-Object {
        $_ -notmatch '^(API|EXT)-MS-WIN-' -and
        -not (Test-Path -LiteralPath (Join-Path $systemDirectory $_) -PathType Leaf)
    }
)
if ($nonSystemDependencies.Count -gt 0) {
    throw "The IPP bridge has non-system DLL dependencies: $($nonSystemDependencies -join ', ')"
}

$unexpectedArtifacts = @(Get-ChildItem -LiteralPath $artifactDirectoryFullPath -Force | Where-Object { $_.Name -ne 'vhsdecode_ipp.dll' })
if ($unexpectedArtifacts.Count -gt 0) {
    throw "The native artifact directory contains unexpected files: $($unexpectedArtifacts.Name -join ', ')"
}

$artifact = Get-Item -LiteralPath $artifactPath
Write-Host "Verified $($artifact.FullName) ($($artifact.Length) bytes)."
Write-Host "Windows system dependencies: $($dependencyNames -join ', ')"
