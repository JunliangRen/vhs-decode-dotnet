# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-12.03 -->

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
- Visual Studio 2026 の `.slnx` には **1,431** 件の標準 xUnit v3 test があり、
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
`--start 100 --length 160` snapshot です。source filename は公開しません。Python と
.NET Release の全 90 measurement は、main `1b6f315` を基にした candidate commit
`c8c2835` を使い、forward、reverse、mixed pass を含む同じ 2026-08-12 batch で
完了しました。以前の snapshot は本表で置き換えます。互換性と速度は別々に評価します。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 46.165 s | 45.821 s | 13.780 s / 3.350x | 12.896 s / 3.553x | 11.739 s / 3.933x | 9.869 s / 4.643x |
| `--threads 1` | 56.050 s | 55.754 s | 34.138 s / 1.642x | 39.839 s / 1.399x | 24.408 s / 2.296x | 26.950 s / 2.069x |
| `--threads 5` | 46.278 s | 45.804 s | 13.277 s / 3.486x | 12.726 s / 3.599x | 11.659 s / 3.969x | 9.859 s / 4.646x |
| `--threads 10` | 47.111 s | 48.017 s | 10.526 s / 4.476x | 9.648 s / 4.977x | 9.858 s / 4.779x | 7.744 s / 6.200x |
| `--threads 20` | 48.283 s | 48.622 s | 8.441 s / 5.720x | 7.787 s / 6.244x | 8.284 s / 5.829x | 6.018 s / 8.079x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-12 dotnet-current-date=2026-08-12 radix3-avx-matrix-runs=90 radix3-avx-exact-1000-ab-pairs=3 radix3-avx-kernel-pairs=12 radix3-avx-tests=60 radix3-avx-intrinsic-modes=3 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する speedup の順で、
default は **5 workers** です。3-run range は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。ratio は
.NET absolute time と分母の Python time の両方で動き、別 fixture/window の過去表とも
直接比較できません。causal regression は、過去の ratio cell ではなく同時刻の .NET
revision A/B で判断します。

最新の isolated change は managed AVX で 4 個の独立した float32 radix-3 PocketFFT index を
同時に計算し、scalar operation と twiddle order を維持して FMA や reassociation を使いません。
production plan ごとの順序を反転した kernel 6 pair では、length 726 と 990 の mean time が
それぞれ 0.94% と 4.18% 減り、hash は一致しました。順序を反転した 1,000-frame Exact
`current --threads 20` 3 pair は 9 compatibility surface ですべて一致しました。mean wall time
は 37.288 から 36.909 seconds（1.02% 減）、CPU time は 301.984 から 292.766 seconds
（3.05% 減）となり、memory は bounded でした。

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
  --no-build --no-restore --minimum-expected-tests 1431
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
