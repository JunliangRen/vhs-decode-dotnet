[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "CUDA package directory does not exist: $packageRoot"
}

$manifestPath = Join-Path $packageRoot 'cuda-component.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "CUDA package manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.component -ne 'vhsdecode-cuda' -or $manifest.platform -ne 'win-x64') {
    throw 'CUDA package manifest identity is invalid.'
}
if ($manifest.abiVersion -ne 1) {
    throw "CUDA package ABI is invalid: $($manifest.abiVersion)"
}
if ($manifest.toolchain.cudaSdk -ne '13.0.2' -or $manifest.toolchain.nvcc -ne '13.0.88') {
    throw 'CUDA package was not built with CUDA Toolkit 13.0 Update 2.'
}
if ($manifest.autoEligible -ne $false) {
    throw 'CUDA package must not advertise auto eligibility before the performance release gate passes.'
}
if ($manifest.repositoryCommit -notmatch '^[0-9a-f]{40}$' -or
    $manifest.repositorySourceTreeSha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]::IsNullOrWhiteSpace($manifest.repositorySourceIdentity) -or
    $null -eq $manifest.repositoryDirty -or
    $manifest.repositoryDirtyPathCount -lt 0) {
    throw 'CUDA package source provenance is incomplete.'
}
$expectedDirtyState = $manifest.repositoryDirtyPathCount -gt 0
if ($manifest.repositoryDirty -ne $expectedDirtyState) {
    throw 'CUDA package dirty state and dirty-path count are inconsistent.'
}
$expectedSourceIdentity = if ($manifest.repositoryDirty -eq $true) {
    "$($manifest.repositoryCommit)+dirty.$($manifest.repositorySourceTreeSha256)"
} else {
    [string] $manifest.repositoryCommit
}
if ([string] $manifest.repositorySourceIdentity -cne $expectedSourceIdentity) {
    throw 'CUDA package source identity does not match its commit, tree hash, and dirty state.'
}

$requiredRootFiles = @(
    'vhsdecode_cuda.dll',
    'cudart64_13.dll',
    'cufft64_12.dll',
    'README-CUDA.md',
    'THIRD-PARTY-NOTICES-CUDA.md',
    'cuda-component.json'
)
$requiredLicenseFiles = @(
    'licenses\vhsdecode-cuda-GPL-3.0.txt',
    'licenses\NVIDIA-CUDA-LICENSE.txt',
    'licenses\NVIDIA-CUDA-version.json'
)
foreach ($relativePath in $requiredRootFiles + $requiredLicenseFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relativePath) -PathType Leaf)) {
        throw "CUDA package is missing required file '$relativePath'."
    }
}

$expectedDlls = @('cudart64_13.dll', 'cufft64_12.dll', 'vhsdecode_cuda.dll')
$actualDlls = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.dll' -File | ForEach-Object Name | Sort-Object)
$unexpectedDlls = @($actualDlls | Where-Object { $_ -notin $expectedDlls })
if ($unexpectedDlls.Count -ne 0) {
    throw "CUDA package contains unexpected DLLs: $($unexpectedDlls -join ', ')"
}
foreach ($expectedDll in $expectedDlls) {
    if ($expectedDll -notin $actualDlls) {
        throw "CUDA package is missing expected DLL '$expectedDll'."
    }
}

foreach ($entry in $manifest.files) {
    $relativePath = $entry.path.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $filePath = Join-Path $packageRoot $relativePath
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "Manifest references missing file '$($entry.path)'."
    }

    $file = Get-Item -LiteralPath $filePath
    if ($file.Length -ne $entry.size) {
        throw "Manifest size mismatch for '$($entry.path)'."
    }

    $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.sha256) {
        throw "Manifest SHA-256 mismatch for '$($entry.path)'."
    }
}

$manifestPaths = @($manifest.files.path | ForEach-Object {
        $_.Replace('/', [IO.Path]::DirectorySeparatorChar)
    } | Sort-Object -Unique)
$requiredManifestPaths = @($requiredRootFiles + $requiredLicenseFiles |
    Where-Object { $_ -ne 'cuda-component.json' } |
    Sort-Object -Unique)
$missingManifestEntries = @($requiredManifestPaths | Where-Object {
        $_ -notin $manifestPaths
    })
if ($missingManifestEntries.Count -ne 0) {
    throw "CUDA manifest omits required files: $($missingManifestEntries -join ', ')"
}
$actualPackagedFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
    ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot, $_.FullName)
    } |
    Where-Object { $_ -ne 'cuda-component.json' } |
    Sort-Object -Unique)
$unmanifestedFiles = @($actualPackagedFiles | Where-Object {
        $_ -notin $manifestPaths
    })
if ($unmanifestedFiles.Count -ne 0) {
    throw "CUDA package contains unmanifested files: $($unmanifestedFiles -join ', ')"
}

Write-Host "CUDA package verified: $packageRoot"
