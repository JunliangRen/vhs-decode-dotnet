# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-13.01 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 11 で再実装するプロジェクトです。互換性の対象は
upstream release `v0.4.0`、commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4` です。

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
- Visual Studio 2026 の `.slnx` には **1,437** 件の標準 xUnit v3 test があり、
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

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

LaserDisc の video、EFM、analog-audio full-complex FFT stage は IPP に接続済みです。
CVBS と HiFi は引き続き `ipp-fast` を拒否します。release-compatible な動作が
必要な場合は `exact` を使用してください。互換性を重視する用途では
[backend の詳細](docs/README.detailed.ja.md#パフォーマンス)を先に確認してください。

<!-- SECTION: performance -->

## 最新の性能

これは同じ private local 40 MHz PAL VHS `.ldf` fixture を使う、startup cost を含む
`--start 100 --length 160` snapshot です。source filename は公開しません。
2026-08-12 の固定条件測定を 84 run 保持し、影響を受ける 2 つの
`current --threads 20` cell を 2026-08-13 の 6 run で更新しました。これらの cell は
main `4416aaa` を基にした commit `e89df85` を使い、各 cell は 3 complete run を
持ちます。互換性と速度は別々に評価します。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 52.811 s | 54.243 s | 12.930 s / 4.084x | 12.477 s / 4.348x | 11.493 s / 4.595x | 9.673 s / 5.607x |
| `--threads 1` | 57.067 s | 56.762 s | 31.701 s / 1.800x | 36.035 s / 1.575x | 22.907 s / 2.491x | 25.402 s / 2.235x |
| `--threads 5` | 52.920 s | 55.722 s | 13.009 s / 4.068x | 11.803 s / 4.721x | 11.489 s / 4.606x | 9.433 s / 5.907x |
| `--threads 10` | 52.965 s | 54.949 s | 10.266 s / 5.159x | 9.335 s / 5.886x | 9.766 s / 5.424x | 7.727 s / 7.111x |
| `--threads 20` | 53.555 s | 54.842 s | 8.578 s / 6.244x | 7.325 s / 7.487x | 8.127 s / 6.590x | 5.863 s / 9.355x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-12 dotnet-current-date=2026-08-12 dotnet-current-t20-date=2026-08-13 phase18-table-runs=6 phase18-exact-200-ab-pairs=3 phase18-exact-1000-ab-pairs=1 phase18-ipp-200-ab-pairs=1 phase18-thread-gate-modes=2 phase18-tests=1435 phase18-final-signal-ab-pairs=3 phase18-final-memory-frames=1000 sinc-unroll-matrix-runs=90 sinc-unroll-exact-1000-ab-pairs=3 sinc-unroll-kernel-pairs=8 sinc-unroll-thread-profile-runs=24 sinc-unroll-memory-frames=2000 sinc-unroll-tests=34 sinc-unroll-intrinsic-modes=4 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する speedup の順で、
default は **5 workers** です。3-run range は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。ratio は
分子の Python time と分母の .NET time の両方で動き、別 fixture/window の過去表とも
直接比較できません。causal regression は、過去の ratio cell ではなく同時刻の .NET
revision A/B で判断します。

直前の isolated change は、nested ThreadPool companion inverse を decoder-owned の
bounded queue に置き換えます。`--threads 20` では、8 fixed companion worker が既存の
12-block prefetch cap を超える worker budget だけを使用します。interleaved 200-frame
Exact `current` 3 A/B pair は全 compatibility surface で一致し、paired wall-time change
の median は 1.61% 減少しました。1,000-frame pair は 34.732 から 34.000 秒へ
2.11% 高速化し、candidate working-set maximum は前半 352.2 MiB から後半
351.4 MiB へ微減しました。growth や OOM はありません。
final lifecycle follow-up も 3 interleaved pair と 1,000-frame run の全 surface で一致し、
first/second-half peak は 355.2/353.8 MiB でした。machine が idle ではなかったため、
その wall time は table から意図的に除外しています。

最新の allocation pass は、VHS sync analysis の escape しない scratch array 5 個を
exclusive detector workspace ごとに保持します。valid-pulse allocation benchmark は
38.2% 減少し、matched GC trace でも startup 後の detector 由来の recurring
`double[]` と `bool[]` allocation tick が消えました。opposite-order 1,000-frame Exact
`current --threads 20` 2 pair の combined wall time は -0.38% で throughput-neutral、
CPU は +1.65% だったため、CPU reduction や speedup は主張しません。Exact と
IPP-fast はそれぞれ、v0.4.0/`current` と `--threads 0`/default-five/`--threads 20` を
網羅する main/candidate 12 run を通過し、luma、chroma、raw JSON、stdout、normalized
diagnostic、ordered `fileLoc` が一致しました。今回の gate 中は無関係な foreground CPU
load があったため、table は最新の controlled throughput snapshot を維持します。

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

固定 SDK は .NET `11.0.100-preview.6.26359.118` です。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1437
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
