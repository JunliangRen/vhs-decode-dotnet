# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-18.01 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 11 で再実装するプロジェクトです。互換性の対象は
upstream release `v0.4.0`、commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4` です。

現在の .NET port release は `v0.4.0-2.3.1`（application version `2.3.1`）です。

> [!IMPORTANT]
> この互換移植は現在も開発中です。トップレベルのデコード経路は実装済みで
> 多数のテストがありますが、すべての実キャプチャとまれなオプションの組み合わせで
> バイト単位の一致を保証する段階にはまだ達していません。

完全な互換性マトリクス、実装の詳細、過去の性能測定、検証根拠、残っている差異は
**[日本語の詳細リファレンス](docs/README.detailed.ja.md)** を参照してください。

## 目次

- [概要](#概要)
- [クイックスタート](#クイックスタート)
- [Behavior profile と backend](#behavior-profile-と-backend)
- [最新の性能](#最新の性能)
- [互換性の状態](#互換性の状態)
- [Build と test](#build-と-test)

<!-- SECTION: overview -->

## 概要

- 対象はデコードのみで、VHS、CVBS、LaserDisc、HiFi を実装します。
- release 4.0 のコマンド、オプション、alias、default、diagnostic、output
  lifecycle を互換性の対象とします。
- VHS family には VHS/S-VHS、Betamax、Video8/Hi8、U-matic、Type C、EIAJ、
  upstream が対応する PAL/NTSC variant が含まれます。
- TBC utility、ダブルクリック GUI、開発者向け plot window は対象外です。
- Visual Studio 2026 の `.slnx` には **1,562** 件の標準 xUnit v3 test があり、
  Test Explorer と `dotnet test` の両方で実行できます。

<!-- SECTION: start -->

## クイックスタート

[GitHub Releases](https://github.com/JunliangRen/vhs-decode-dotnet/releases)
から binary-only の Windows x64 package を入手できます。release の中心は
single-file `decode.exe` です。

```powershell
decode.exe vhs [upstream options] input.lds output
decode.exe cvbs [upstream options] input.lds output
decode.exe ld [upstream options] input.lds output
decode.exe hifi [upstream options] input.lds output.wav
```

`vhs-decode.exe`、`ld-decode.exe` などの standalone alias も使用できます。
完全な互換 option は `decode.exe <command> --help` で確認してください。

### Seek 可能な RF preview server

VHS は `decode.exe vhs --preview-server --pal input.lds`、LaserDisc は
`decode.exe ld --preview-server --pal input.ldf` で起動できます。command は
loopback の web player URL と標準 HLS/fMP4 playlist URL を表示します。output
base は不要で、TBC、JSON、SQLite、EFM、audio、decode log を生成しません。

これは位置確認用の低精度 mode です。軽量な 4fSC 1D demodulator で colour を
維持し、隣接する burst line から PAL V-switch を検出して 4-field hue flicker を
防ぎ、dropout concealment を常時適用します。audio と正式 export 用の重い
comb/repair stage は省略し、2 秒 window ごとに timeline 上の連続した全 frame を
decode します。muted web player は自動再生し、2 window 分を先読みします。
top-field-first の入力 field は field rate で deinterlace され、NTSC は progressive
640x480、60000/1001 fps、PAL は progressive 768x576、50 fps で配信されます。
起動時に完全な fMP4 pipeline を CUDA YADIF + NVENC、QSV advanced VPP + QSV、
CPU YADIF + AMF、CPU YADIF + libx264 の順に実際に検証します。
`--preview-crf` は 0 から 51 を受け付け、default は 31 です。hardware encoder では
最も近い quality/QP control に対応付けるため、backend 間で bitrate は一致しません。
IPP が利用可能なら `ipp-fast` を自動選択し、それ以外では portable な managed
backend に戻ります。標準 40 MSPS VHS preview は固定 anti-alias filter を通した後、
内部 RF を 20 MSPS で decode します。native 20 MSPS VHS input は 20 MSPS のままで、
S-VHS、その他の tape format、LaserDisc、通常の decode/export path は従来の
sample-rate behavior を維持します。この最適化は自動で、user-facing option は追加しません。
起動時には選択した video pipeline、IPP-FAST の初期化成否、
実際の decode thread 数、別々の行で更新される window ID と realtime FPS を表示します。少なくとも
1 つの pipeline が利用できる FFmpeg が必要で、`VHSDECODE_FFMPEG` と
`VHSDECODE_FFPROBE` で path を明示できます。

<!-- SECTION: profiles -->

## Behavior profile と backend

`--compat-version` は upstream behavior を選択します。

| 値 | 意味 |
| --- | --- |
| `v0.4.0` | default。固定した Python release behavior を対象にします。 |
| `current` | upstream PR 341 の段階的 behavior を opt-in し、新しい VHS sync と color-under processing を含みます。 |

厳密な互換性 oracle は Python v0.4.0 commit `g4315520` の
`--threads 0` 出力です。Python upstream は worker 数によって output hash が
安定しないため、Python の multithread run は速度測定にだけ使用します。

`--dsp-backend` は DSP implementation を選択します。

| 値 | 意味 |
| --- | --- |
| `exact` | default の managed path。互換性を重視する decode に使用します。 |
| `ipp-fast` | Intel IPP を使う experimental Windows x64 VHS / LaserDisc real-RF path。浮動小数点 bit が変化する可能性があり、`exact` へ silent fallback しません。 |
| `cuda-fast` | NVIDIA CUDA 13 を使う experimental Windows x64 full-signal VHS path。独立した numerical contract を持ち、現時点では native-rate 40 MSPS PAL/NTSC VHS のみを support し、CPU backend へ silent fallback しません。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
decode.exe vhs --dsp-backend cuda-fast --pal `
  --start 100 --length 20 input.ldf output
```

default Windows release は小型 CUDA-fast bridge を保持しますが、271 MiB の cuFFT DLL
は埋め込みません。`--dsp-backend cuda-fast` を明示したときだけ compatible CUDA 13/
cuFFT 12 を検索し、見つからない場合は NVIDIA driver を先に確認してから NVIDIA の
pinned 202.2 MiB redistributable を download します。archive と DLL を個別に
SHA-256 検証し、`%LOCALAPPDATA%\vhs-decode-dotnet\cuda\cufft` へ一度だけ install
します。Exact、IPP、preview path は network access しません。offline/system runtime
は `VHSDECODE_CUDA_RUNTIME_PATH`、cache root は `VHSDECODE_CUDA_CACHE_PATH`、automatic
download の無効化は `VHSDECODE_CUDA_AUTO_DOWNLOAD=0` で指定できます。

LaserDisc の video、EFM、analog-audio full-complex FFT stage は IPP に接続済みです。
CVBS と HiFi は引き続き `ipp-fast` を拒否します。release-compatible な動作が
必要な場合は `exact` を使用してください。互換性を重視する用途では
[backend の詳細](docs/README.detailed.ja.md#パフォーマンス)を先に確認してください。
tested RTX 4070 と 1 本の real PAL capture では、画質修正後の FP32 CUDA-full path は
Exact の見え方に大幅に近づきましたが、measured CPU throughput は上回りません。同じ
`--start_fileloc 320000000 --length 500` request の current same-session interleaved
comparison では、CUDA は 15.605/15.748 秒、`ipp-fast --threads 20` は
14.108/14.064 秒でした。median は 15.676/14.086 秒（31.895/35.495 fps）で、CUDA は
wall time が 11.29% 長く、IPP throughput の 0.8986x です。別の隣接 A-B-B-A comparison
では CUDA の median が直前 build の 21.918 秒から 16.057 秒へ短縮されました
（wall time 26.74% 減、throughput 36.50% 増）。final CUDA run 2 回の
luma/chroma/JSON は byte-identical でした。default の export-side dropout correction
を使い Exact と alignment した
79-frame lossless comparison の SSIM Y/U/V/All は
0.954905/0.988109/0.991285/0.972301、PSNR Y/U/V/average は
33.196867/41.243137/43.586266/35.699053 dB でした。manual inspection でも scene
content、colour、motion は非常に近く見えましたが、numerical equality は主張しません。
この結果は当該 hardware/capture に限定され、`cuda-fast` は experimental のままで CPU
numerical contract とも異なります。

<!-- SECTION: performance -->

## 最新の性能

これは同じ private local 40 MHz PAL VHS `.ldf` fixture を使う、startup cost を含む
`--start 100 --length 160` snapshot です。source filename は公開しません。
2026-08-12 の固定 Python reference 30 run を保持します。全 60 回の .NET 測定は
commit `21b8b01` から build した self-contained .NET 11 Preview 7 candidate で
2026-08-15 にまとめて更新しました。各 cell は 3 complete run を持ち、この documentation/toolchain
refresh では新しい tag や Release を公開しません。互換性と速度は別々に評価します。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 52.811 s | 54.243 s | 13.977 s / 3.778x | 12.556 s / 4.320x | 12.214 s / 4.324x | 9.419 s / 5.759x |
| `--threads 1` | 57.067 s | 56.762 s | 36.434 s / 1.566x | 40.155 s / 1.414x | 25.468 s / 2.241x | 27.561 s / 2.060x |
| `--threads 5` | 52.920 s | 55.722 s | 14.055 s / 3.765x | 12.655 s / 4.403x | 12.244 s / 4.322x | 9.104 s / 6.121x |
| `--threads 10` | 52.965 s | 54.949 s | 11.795 s / 4.491x | 10.198 s / 5.388x | 10.535 s / 5.027x | 7.467 s / 7.359x |
| `--threads 20` | 53.555 s | 54.842 s | 10.667 s / 5.021x | 9.533 s / 5.753x | 9.216 s / 5.811x | 6.991 s / 7.845x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 dotnet-current-runs=30 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-15 dotnet-current-date=2026-08-15 phase22-200-ab-pairs=20 phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 phase22-gc-traces=2 phase22-tests=1438 phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 phase24-thread-gate-runs=12 phase24-tests=1442 phase25-public-cell-runs=15 phase25-public-ab-pairs=15 phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 phase25-tests=1446 phase26-kernel-ab-pairs=8 phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 phase26-public-cell-runs=30 phase26-tests=1447 phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 phase27-thread-backend-runs=24 phase27-public-cell-runs=60 phase27-tests=1448 phase28-kernel-ab-pairs=8 phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 phase28-intrinsic-runs=3 phase28-public-cell-runs=60 phase28-tests=1448 phase30-burst-kernel-runs=14 phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 phase30-memory-runs=2 phase30-public-cell-runs=60 phase30-tests=1448 phase31-interleaved-ab-pairs=9 phase31-long-gate-runs=8 phase31-thread-backend-runs=24 phase31-memory-runs=4 phase31-public-cell-runs=60 phase31-tests=1459 phase32-vblank-short-ab-pairs=6 phase32-vblank-long-ab-pairs=2 phase32-thread-backend-runs=24 phase32-gc-traces=2 phase32-counter-runs=2 phase32-tests=1460 phase33-sync-list-short-ab-pairs=6 phase33-sync-list-long-ab-pairs=2 phase33-thread-backend-runs=24 phase33-gc-traces=1 phase33-memory-runs=4 phase33-public-cell-runs=60 phase33-tests=1463 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する speedup の順で、
default は **5 workers** です。3-run range は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。ratio は
分子の Python time と分母の .NET time の両方で動き、別 fixture/window の過去表とも
直接比較できません。causal regression は、過去の ratio cell ではなく同時刻の .NET
revision A/B で判断します。

2.1.0 release は bounded な 2-field VHS wavefront を追加します。cross-field state、output、
metadata、diagnostic は ordered serial のままで、input-independent field tail だけを次の RF
read と重ねます。interleaved A/B で Exact `current` に gain がなかったため、この profile は
従来 path のままです。render/dropout 完了後は lookahead 前に大きな RF span を返却します。

最終 1,000-frame `--threads 20` gate は全 compatibility surface で一致しました。Exact
v0.4.0 は 46.047 から 42.575 秒、IPP-fast v0.4.0 は 44.980 から 42.084 秒、IPP-fast
current は 31.009 から 25.260 秒へ短縮しました。memory sample を 500 から 1,000 frame に
倍増しても candidate peak working set は 473/469 MiB、main は 354/354 MiB で、追加 window
は固定かつ bounded です。

その後の Exact-current audit では、full cross-field wavefront は 1,000-frame A/B で
wall time が 6.05% 増え、effective core use も改善しなかったため却下しました。採用した
sync-analysis 変更は throughput-neutral ですが、同じ 500-frame counter pair で managed
allocation を 46.0% 削減し、Gen0 collection を 60 から 30、GC pause を 44.4 から
24.2 ms へ減らしました。そのため public speed table は変更していません。

次の Exact-current pass は field-local な classified/refined pulse list を再利用し、public
API の ownership semantics は維持します。順序を逆にした 1,000-frame 2 pair で wall-time
median は 32.95 から 32.11 秒、CPU time は 298.40 から 288.02 秒へ短縮し、sampled
allocation は 8.65% 減りました。保守的な reverse-order memory pair では peak working
set が 390.8 から 360.5 MiB、private bytes が 409.9 から 374.4 MiB へ減少しました。

`--threads 0`、default-five、20-worker の 24-run gate と、更新した 60-run Exact/IPP-fast
matrix は、luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` の
各 surface で 1 hash を維持しました。標準 xUnit v3 suite の **1,500** tests も成功しました。

更新した各 .NET profile/thread cell は 3 run 内で deterministic でした。固定 reference の
merged Python PR341 も deterministic でした。Python v0.4.0 は 15 run で 15 種類の luma、
chroma、JSON、normalized-log hash を生成したため、strict oracle は引き続き Python
v0.4.0 `g4315520 --threads 0` です。command、range、binary hash、memory bound、過去の測定は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。

<!-- SECTION: compatibility -->

## 互換性の状態

主要な decode pipeline、streaming output、recovery behavior、CLI surface は
実装済みです。focused test と real-RF gate は luma、chroma、JSON、ordered
`fileLoc`、stdout、normalized stderr/log、determinism、bounded memory を
検証します。まれな capture と option interaction は引き続き検証中であり、
build success や同じ file size だけを互換性の証明とはしません。

TBC、chroma、JSON、log は decode 中も concurrent read できるため、対応する
preview tool は writer を止めずに partial output を確認できます。

native-input route では、40 kHz mono PCM16 かつ total sample count が
`Int32.MaxValue` 以下の direct raw `fLaC` `.ldf`/`.flac` input に bundled
libsndfile を使います。通常の parallel VHS decode は、seek table のない厳密に
gate した oversized fixed-block raw FLAC にも libsndfile を使えます。integer mapping
は固定した FFmpeg/PyAV の frame start と rewind/restart boundary を再現し、失敗時は
同じ logical sample から一方向に FFmpeg へ fallback します。`--threads 0/1`、
debug-plot/GNU Radio AFE mode、nonzero `--sharpness`、他 command family、default VHS
`.flac`、全 CVBS、Ogg/FLAC、stereo、PCM24、他 sample rate、未完了または非 eligible
header は FFmpeg を維持します。

<!-- SECTION: build -->

## Build と test

固定 SDK は .NET `11.0.100-preview.7.26381.103` です。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1562
```

Visual Studio 2026 で `VHSDecodeDotNet.slnx` を開くと、build、debug、
Test Explorer からの xUnit v3 実行ができます。

<!-- SECTION: detail -->

## 詳細情報

- [日本語の詳細リファレンス](docs/README.detailed.ja.md)
- [互換性の根拠](docs/COMPATIBILITY_EVIDENCE.md)
- [English overview](README.md)
- [简体中文概览](README.zh-CN.md)

<!-- SECTION: license -->

## ライセンス

GPL-3.0。[`LICENSE`](LICENSE) を参照してください。
