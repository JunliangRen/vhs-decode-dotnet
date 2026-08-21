# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-21.01 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 11 で再実装するプロジェクトです。互換性の対象は
upstream release `v0.4.0`、commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4` です。

現在の .NET port release は `v0.4.0-2.5.0`（application version `2.5.0`）です。

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
- Visual Studio 2026 の `.slnx` には **1,592** 件の標準 xUnit v3 test があり、
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

release workflow は self-contained、multi-file の glibc `linux-x64` tar も
生成します。Linux package は portable `exact` backend のみを対象とし、Linux 用
SQLite、libsndfile、libsoxr native asset を同梱して Windows IPP/CUDA DLL を除外します。
FFmpeg と文書化された OS library をインストールした後に実行できます。

```bash
tar -xzf vhs-decode-dotnet-linux-x64.tar.gz
cd vhs-decode-dotnet-linux-x64
./vhs-decode --pal --dsp-backend exact input.lds output
```

Ubuntu 22.04/glibc 2.35 baseline、runtime package、checksum、build provenance、
release gate は [Linux x64 release](docs/LINUX_X64.md) を参照してください。
32-bit x86、ARM/ARM64、musl は対象外です。

### Seek 可能な RF preview server

VHS は `decode.exe vhs --preview-server --pal input.lds`、LaserDisc は
`decode.exe ld --preview-server --pal input.ldf` で起動できます。command は
loopback の web player URL と標準 HLS/fMP4 playlist URL を表示します。default は
`127.0.0.1:8080` から開始し、使用中なら `8180` まで 1 ずつ増やします。明示的な
`--preview-port` は指定値を厳守し、`--preview-port 0` は OS の dynamic port を使います。output
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
対象となる native-rate 40 MSPS PAL/NTSC VHS preview は、まず軽量な CUDA driver/device
preflight を行います。この段階では cuFFT の load、CUDA context の作成、NVENC の初期化を
行いません。device が通過した場合だけ CUDA/cuFFT/NVENC の完全初期化を一度試し、
preflight または完全起動が利用できなければ `ipp-fast`、portable managed backend の順に
fallback します。その他の preview input は同じ IPP→managed CPU 順序から始めます。
標準 40 MSPS VHS preview は固定 anti-alias filter を通した後、
内部 RF を 20 MSPS で decode します。native 20 MSPS VHS input は 20 MSPS のままで、
supported VHS preview route は full decode の `--decode-at-20msps` を強制した場合と
同じ behavior です。full VHS decode では `ipp-fast` または `cuda-fast` でこの option を
明示的に選べます。Exact、S-VHS、その他の tape format、LaserDisc は従来の
sample-rate behavior を維持します。
起動時には選択した video pipeline、IPP-FAST の初期化成否、
実際の decode thread 数、別々の行で更新される window ID と realtime FPS を表示します。少なくとも
1 つの pipeline が利用できる FFmpeg が必要で、`VHSDECODE_FFMPEG` と
`VHSDECODE_FFPROBE` で path を明示できます。

したがって compatible machine の native-rate 40 MSPS PAL/NTSC VHS は独立 GPU preview
path を自動選択します。次の形式で明示的に固定することもできます。

```powershell
decode.exe vhs --preview-server --dsp-backend cuda-fast --pal input.ldf
```

この path は 1 つの CUDA context を window 間で再利用し、anti-alias 付き 40→20
MSPS decimation、sync、FM/chroma/dropout processing、NV12 bob rendering、NVENC
H.264 encode を GPU 上で実行します。renderer は block-linear NV12 CUDA array へ直接
書き込み、NVENC はその array を register して pitch-linear conversion を省きます。各 bounded RF batch は一度だけ upload し、full luma/chroma/NV12 frame は host memory へ
download しません。host/device 境界を通るのは少量の sync/field-order control metadata
と圧縮済み H.264 packet だけで、FFmpeg は HLS/fMP4 への copy-mux のみを担当します。
明示的な `--dsp-backend cuda-fast` では compatible NVIDIA GPU が必須で、CPU preview や
別 encoder へ fallback しません。default の自動選択は GPU 起動失敗時だけ fallback し、
preview session 起動後に backend を途中変更しません。既存の GPU bob deinterlacer は変更していません。
preview のみ、bounded batch 内の clean な opposite-parity field で dropout を置換し、
seek window ごとに reset する 1-field 75/25 current/previous chroma blend を使います。

2026-08-20 の sustained local resource matrix は同じ real 40 MSPS PAL capture、Intel Core
Ultra 7 265K（20 logical processor）、RTX 4070 を使用しました。最初の 5 行は source
commit `41bfd92`、修正済み IPP preview 行は `1fb1455` から取得しました。各 row は独立
process launch 2 回の平均で、括弧内は 2 回の source-frame rate range です。

| Path | Source fps（range） | `decode.exe` CPU | System CPU | GPU SM avg/peak | NVENC avg/peak | Peak GPU FB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Full CUDA 40 MSPS | 35.30（35.28-35.33） | 10.81% / 2.16 cores | 33.89% | 32.44% / 72% | 0% / 0% | 7,038 MiB |
| Full IPP 40 MSPS | 23.79（23.71-23.87） | 22.15% / 4.43 cores | 28.76% | 0.10% / 4% | 0% / 0% | 3,102 MiB |
| Full CUDA 20 MSPS | 35.80（35.60-35.99） | 10.92% / 2.18 cores | 34.74% | 27.67% / 54% | 0% / 0% | 5,288 MiB |
| Full IPP 20 MSPS | 26.86（26.25-27.48） | 24.19% / 4.84 cores | 48.39% | 0% / 0% | 0% / 0% | 3,102 MiB |
| Preview CUDA 20 MSPS | 47.03（46.59-47.48） | 4.06% / 0.81 cores | 24.75% | 26.91% / 61% | 3.12% / 8% | 4,795 MiB |
| Preview IPP 20 MSPS | 34.33（34.05-34.61） | 24.66% / 4.93 cores | 43.85% | 1.52% / 9% | 1.48% / 4% | 3,292 MiB |

Full run は source frame 500 を要求し、exactly 1,000 output field を検証しました。
Preview run は別の cold W5 後に 20 個の異なる 2-second window、つまり source frame
1,000 を要求しました。fps は source-frame rate であり、2 output field/bob frame を
二重に数えません。process CPU は 20 logical processor 全体で正規化し、system CPU
には FFmpeg、driver、sampler、その他 machine work も含みます。GPU は global 100 ms
NVML sample です。最初の 5 行の idle baseline は system CPU 5.05%、GPU SM 0%、
NVENC 0%、GPU FB 3,103 MiB で、別途修正した IPP preview 行は system CPU 6.85%、
GPU FB 3,110 MiB を使用しました。CUDA の IPP 比 source throughput は full-40/full-20/
preview-20 で 1.48x/1.33x/1.37x です。詳細は
[performance reference](docs/README.detailed.ja.md#current-ippcuda-resource-matrix)を
参照してください。

final source の 5-window quality recheck は、修正済み IPP coordinate を使用しました。
1 field alignment のため IPP の最初の output frame を除くと、default CUDA preview の
SSIM Y/U/V/All 平均は 0.914657/0.957361/0.966698/0.930448 でした。強制
line-phase guard の combined 平均は 0.926692、cross-field dropout と chroma
stabilization を同時に無効化した場合は 0.922357 で、default は全 window で上回りました。
これは当該 capture/hardware に限定した preview 結果で、Exact equivalence ではありません。

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
| `cuda-fast` | NVIDIA CUDA 13 を使う experimental Windows x64 full-signal VHS path。独立した numerical contract を持ち、PAL/NTSC VHS を通常は 40 MSPS、`--decode-at-20msps` 指定時は GPU 40→20 または native 20 MSPS で decode し、CPU backend へ silent fallback しません。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
decode.exe vhs --dsp-backend ipp-fast --decode-at-20msps `
  --pal input.lds output-20msps
decode.exe vhs --dsp-backend cuda-fast --pal `
  --decode-at-20msps --start 100 --length 20 input.ldf output
```

`--decode-at-20msps` は VHS preview-quality mode で、Exact equivalence は保証しません。
40 MSPS source は anti-alias filter 後に内部 20 MSPS で decode し、native 20 MSPS
input は再度 decimate しません。TBC metadata の `fileLoc` は元の input sample 座標を
維持します。
これは preview-quality の sample-rate 選択であり、常に高速化する switch ではありません。
現在の startup-inclusive 500-frame gate では CUDA throughput が 1.40%、IPP が 12.91%
上がりました。以前の 100-frame short gate では fixed startup/reduction cost が支配し、
IPP は 6.83% 遅く見えました。full decode で使う前に対象 capture と実際の run length を
benchmark してください。

default Windows release は小型 CUDA-fast bridge を保持しますが、271 MiB の cuFFT DLL
は埋め込みません。明示的な `--dsp-backend cuda-fast`、または対象 default VHS preview
が軽量 driver/device preflight を通過した場合だけ compatible CUDA 13/cuFFT 12 を検索し、
見つからない場合は NVIDIA driver を先に確認してから NVIDIA の
pinned 202.2 MiB redistributable を download します。archive と DLL を個別に
SHA-256 検証し、`%LOCALAPPDATA%\vhs-decode-dotnet\cuda\cufft` へ一度だけ install
します。軽量 preflight が失敗した場合は resolver に入らず network access しません。
Exact、IPP、および自動 CUDA support surface 外の preview input も offline のままです。
offline/system runtime は `VHSDECODE_CUDA_RUNTIME_PATH`、cache root は
`VHSDECODE_CUDA_CACHE_PATH`、automatic
download の無効化は `VHSDECODE_CUDA_AUTO_DOWNLOAD=0` で指定できます。

LaserDisc の video、EFM、analog-audio full-complex FFT stage は IPP に接続済みです。
CVBS と HiFi は引き続き `ipp-fast` を拒否します。release-compatible な動作が
必要な場合は `exact` を使用してください。互換性を重視する用途では
[backend の詳細](docs/README.detailed.ja.md#パフォーマンス)を先に確認してください。
tested RTX 4070 と 1 本の real PAL capture では、画質修正後の FP32 CUDA-full path は
Exact の見え方に大幅に近づきました。上の sustained matrix では、40 MSPS は CUDA
35.30 source fps、IPP 23.79（1.48x）、20 MSPS は 35.80 対 26.86（1.33x）でした。
同じ source を使った別の短い session では CUDA throughput が大幅に高かったため、
これは environment snapshot であり、後続 code だけが以前の CUDA/IPP 結果を逆転した証拠ではありません。各 variant の 2 回の
luma/chroma/JSON output は、その variant 内で byte-identical でした。default の
export-side dropout correction
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
  --no-build --no-restore --minimum-expected-tests 1592
```

Visual Studio 2026 で `VHSDecodeDotNet.slnx` を開くと、build、debug、
Test Explorer からの xUnit v3 実行ができます。

Ubuntu 22.04 x64 で native build、全 test、multi-file publish、reproducible tar、
最終展開 tar smoke を一括実行する command は次のとおりです。

```bash
pwsh ./tools/build-linux-x64-release.ps1
```

<!-- SECTION: detail -->

## 詳細情報

- [日本語の詳細リファレンス](docs/README.detailed.ja.md)
- [互換性の根拠](docs/COMPATIBILITY_EVIDENCE.md)
- [English overview](README.md)
- [简体中文概览](README.zh-CN.md)

<!-- SECTION: license -->

## ライセンス

GPL-3.0。[`LICENSE`](LICENSE) を参照してください。
