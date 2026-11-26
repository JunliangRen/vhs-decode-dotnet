param([string]$Compiler = 'gcc', [string]$FfmpegPrefix = $env:VHSDECODE_PREVIEW_FFMPEG_PREFIX)
$ErrorActionPreference = 'Stop'
$compilerPath = (Get-Command $Compiler -CommandType Application -ErrorAction Stop).Source
$env:Path = (Split-Path -Parent $compilerPath) + [IO.Path]::PathSeparator + $env:Path
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src/VHSDecode.Preview.Native'
$output = Join-Path $root 'artifacts/native/Release/win-x64'
if ((& $compilerPath '-dumpmachine') -notmatch '^x86_64-.*mingw') {
    throw 'Use an x64 MinGW-w64 GCC compiler.'
}
$null = New-Item -ItemType Directory -Path $output -Force
$arguments = @(
    '-shared', '-std=c11', '-O3', '-ffast-math', '-funroll-loops', '-flto=auto', '-static-libgcc',
    '-o', (Join-Path $output 'vhsdecode_realtime_preview.dll'),
    (Join-Path $source 'gui_rf_preview.c'), (Join-Path $source 'gui_ld_rf_demod.c'),
    (Join-Path $source 'cvbs_frontend.c'),
    (Join-Path $source 'gui_rf_resampler.c'), (Join-Path $source 'gui_vhs_hifi.c'),
    (Join-Path $source 'gui_beta_hifi.c'), (Join-Path $source 'gui_ld_rf_analog.c'),
    (Join-Path $source 'gui_ld_efm_frontend.c'), (Join-Path $source 'gui_ld_rf_digital.c'),
    (Join-Path $source 'gui_ld_dts.c'), '-lm'
)
$a52 = Join-Path $source 'liba52'
$arguments += "-I$a52"
foreach ($name in 'gui_ld_ac3_rf.c', 'gui_ld_ac3_frames.c', 'gui_ld_ac3_codec.c', 'gui_ld_rf_ac3.c') {
    $arguments += Join-Path $source $name
}
foreach ($name in 'bit_allocate.c', 'bitstream.c', 'imdct.c', 'parse.c', 'downmix.c') {
    $arguments += Join-Path $a52 $name
}
if ($FfmpegPrefix) {
    $prefix = (Resolve-Path -LiteralPath $FfmpegPrefix).Path
    $arguments += @('-DMISRC_HAVE_LD_DTS=1', "-I$prefix/include",
        "$prefix/lib/libavcodec.a", "$prefix/lib/libavutil.a",
        '-Wl,--exclude-libs,ALL', '-lm', '-Wl,-Bstatic', '-latomic', '-lwinpthread', '-Wl,-Bdynamic', '-lole32', '-luser32', '-lbcrypt')
}
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native Realtime preview build failed.' }
