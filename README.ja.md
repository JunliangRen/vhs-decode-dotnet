# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-11.01 -->

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
- Visual Studio 2026 の `.slnx` には **1,425** 件の標準 xUnit v3 test があり、
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
.NET の全 90 Release measurement は、同じ forward、reverse、mixed の 3-pass plan で
performance candidate を測定しました。candidate は merged main `cefdbaf` を基にし、final
ownership atomic gate は下で別に測定しています。両 binary identity と 3-run range は
詳細版に固定記録しています。互換性と速度は別々に評価します。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 45.414 s | 45.060 s | 12.483 s / 3.638x | 12.231 s / 3.684x | 11.235 s / 4.042x | 9.442 s / 4.772x |
| `--threads 1` | 52.323 s | 52.579 s | 32.014 s / 1.634x | 37.531 s / 1.401x | 23.312 s / 2.244x | 25.972 s / 2.024x |
| `--threads 5` | 45.991 s | 44.990 s | 12.606 s / 3.648x | 12.044 s / 3.736x | 11.247 s / 4.089x | 9.407 s / 4.783x |
| `--threads 10` | 47.385 s | 47.713 s | 10.257 s / 4.620x | 9.923 s / 4.808x | 9.499 s / 4.988x | 7.519 s / 6.346x |
| `--threads 20` | 48.459 s | 47.490 s | 8.309 s / 5.832x | 8.284 s / 5.733x | 8.045 s / 6.024x | 5.823 s / 8.155x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 segmented-envelope-release-ab-pairs=4 segmented-envelope-current-1000-ab-pairs=2 segmented-envelope-v040-160-ab-pairs=2 segmented-envelope-safety-ab-pairs=4 segmented-envelope-tests=2 segmented-envelope-intrinsic-modes=2 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する speedup の順で、
default は **5 workers** です。3-run range は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。ratio は
.NET absolute time と分母の Python time の両方で動き、別 fixture/window の過去表とも
直接比較できません。causal regression は、過去の ratio cell ではなく同時刻の .NET
revision A/B で判断します。

20 workers 以上の staged VHS path は、retained block-local RF Envelope を直接 scan して
exact float32 mean と dropout range を求め、full Envelope copy を省きます。低 worker 数は
contiguous path を維持します。arithmetic order、threshold、hysteresis、block lifetime、
output order は変わりません。1 個の atomic cache-operation gate が通常 read、cache
invalidation、staged acquisition の overlap を防ぎます。staged lease が active な間は
cache-mutating operation を拒否し、pooled block storage の早すぎる再利用を防ぎます。

order-reversed 1,000-frame Exact `current --threads 20` 2 pair は wall median を
43.00 から 42.88 seconds、CPU time を 342.50 から 324.50 seconds（5.3% 低下）、
peak working set を約 753 から 721 MiB へ動かしました。release-like 160-frame 4-pair A/B
は 2 勝ずつで wall/CPU の結論が不確定なため、short-window speedup は主張しません。
9 種類の compatibility surface はすべて一致しました。

publication 前の local read-only review でこの cache-ownership race を検出して修正しました。
修正前後の opposite-order 1,000-frame pair 4 組は 9 surface が一致しました。wall median は
39.770/39.932 seconds、pair change は +0.94%/+1.15%/-1.04%/-0.03% に分かれ、CPU median は
1.74% 減少したため、final atomic gate は performance-neutral と分類します。

merged Python PR341 は deterministic でした。Python v0.4.0 は 15 run で 15 種類の luma、
chroma、JSON、normalized-log hash を生成したため、strict oracle は引き続き Python
v0.4.0 `g4315520 --threads 0` です。完全な command、hardware、hash、memory bound、
過去の測定は[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。

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
  --no-build --no-restore --minimum-expected-tests 1425
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
