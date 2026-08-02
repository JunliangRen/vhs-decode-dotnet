# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-02.01 -->

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
- Visual Studio 2026 の `.slnx` には **1,234** 件の標準 xUnit v3 test があり、
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
| `ipp-fast` | Intel IPP を使う experimental Windows x64 VHS real-RF path。浮動小数点 bit が変化する可能性があり、`exact` へ silent fallback しません。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

CVBS、LaserDisc、HiFi は現在 `ipp-fast` を拒否するため、これらでは `exact`
を使用してください。互換性を重視する用途では
[backend の詳細](docs/README.detailed.ja.md#パフォーマンス)を先に確認してください。

<!-- SECTION: performance -->

## 最新の性能

次の表は、同じ private local 40 MHz NTSC `BETAMAX_HIFI` `.lds` sample と
同じ bounded frame range を全 run で使用します。sample filename は公開しません。
v0.4.0 の .NET profile は Python v0.4.0、`current` profile は merge 済みの
Python PR341 と同じ requested worker count で比較します。互換性判定は速度とは
別に行います。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 16.983 s | 14.414 s | 5.623 s / 3.020x | 6.730 s / 2.142x | 5.295 s / 3.207x | 6.688 s / 2.155x |
| `--threads 1` | 21.263 s | 19.881 s | 15.275 s / 1.392x | 17.979 s / 1.106x | 14.625 s / 1.454x | 17.008 s / 1.169x |
| `--threads 5` | 16.880 s | 14.329 s | 5.475 s / 3.083x | 6.514 s / 2.200x | 5.361 s / 3.149x | 6.841 s / 2.094x |
| `--threads 10` | 17.612 s | 15.149 s | 4.640 s / 3.795x | 5.890 s / 2.572x | 4.900 s / 3.594x | 5.631 s / 2.690x |
| `--threads 20` | 18.330 s | 15.447 s | 3.809 s / 4.812x | 4.388 s / 3.520x | 3.722 s / 4.924x | 4.819 s / 3.206x |
<!-- LATEST_PERFORMANCE_END -->

最新の Exact pass は PocketFFT radix-8 の direction dispatch を専用化し、
bit-exact な VHS chroma UInt16 conversion と `current` burst fit を vectorize し、
deterministic radix selection で `current` sync quantile を絞り込みます。scalar
fallback、data type、元の numerical operation order は維持されています。

native/scalar thread-profile gate と final matrix 60 run はすべて reference と
一致しました。balanced な `current`/20-worker pair 6 組では wall-time median が
9.637 から 8.627 秒（10.48% 減、throughput 11.71% 増）、serial pair 4 組では
50.377 から 37.300 秒（25.96% 減、throughput 35.06% 増）になりました。
反対順序の 1,000-frame pair 2 組では mean wall time が 72.405 から 62.752 秒
（13.33% 減、throughput 15.38% 増）になりました。allocation change は
0.18% に留まり、candidate working set は 706 MiB 未満で progressive slowdown
もありませんでした。

新しい direct raw `fLaC` input path は、上の固定 `.lds` table には影響しません。
同じ private local RF window の 100-frame、20-worker pair 1 組では、Release 1.4.4
FFmpeg baseline が 8.319 s、bundled-libsndfile candidate が 7.345 s でした
（wall time 11.71% 減、throughput 1.133x）。luma、chroma、raw JSON、stdout、
normalized stderr/log、ordered `fileLoc` 200 個はすべて一致しました。これは範囲を
限定した single-pair observation であり、decoder 全体への一般的な speed claim ではありません。

最新の direct raw-FLAC input 改善では、parallel decode/prefetch 用に最大 48 個の
正確な 32K RF input block を保持し、sequential decode は従来の allocation behavior
を維持します。同じ private local PAL VHS RF capture の逆順 1,000-frame
`current`/Exact/20-worker pair 2 組で、combined managed allocation は 43.96 から
16.63 GiB（62.18%）、GC pause は 0.380 から 0.253 秒（33.46%）へ減少しました。
combined wall time は 155.74 から 155.31 秒（0.28%）で、throughput は neutral と
分類します。全 output/diagnostic は一致し、candidate working set は 772 MiB 未満に
収まりました。上の固定 `.lds` matrix は影響を受けないため、数値は変更していません。

最新の Exact `current` VHS pass は、sync-level quantile に使う deterministic radix
histogram scan 2 回だけを parallelize します。最終 source-order selection と全 field
state は serial のままです。同じ private local PAL VHS RF capture の balanced
160-frame `--threads 20` pair 6 組はすべて candidate が高速で、median wall time は
18.622 から 17.739 秒（4.74% 減、throughput 4.97% 増）になりました。1 worker と
default-five は neutral、10 worker median は 1.31% 改善しました。反対順序の
1,000-frame pair 2 組では mean wall time が 78.559 から 76.495 秒（2.63% 減）となり、
luma、chroma、raw JSON、normalized stderr/log、ordered `fileLoc` が一致しました。
別の thread gate では stdout と thread 間 determinism も一致しました。candidate の
once-per-second working-set sample の最大値は 779.98 MiB で、progressive sampled
growth は観測されませんでした。固定 `.lds` table は、今回まったく同じ private matrix
sample を利用できなかったため、別 sample の比較不能な値で置換せず、前回の audited
snapshot を維持します。

各 .NET cell は wall-time median と profile が対応する Python 列に対する
speedup の順です。`1.000x` 未満は Python より遅いことを示します。Python
PR341 は merge commit `2f21e8ed6018b14561396cc95f1f6828054470b8` で、
`current` の upstream peer です。default は実際に **5 workers** です。Python
の nonzero-thread 行は throughput 比較専用で、strict compatibility の基準は
引き続き Python v0.4.0 `g4315520 --threads 0` です。

default worker の実数、完全な command、build hash、hardware、反復測定方法、
output hash、過去の測定は
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

native-input route では、40 kHz mono PCM16 の direct raw `fLaC` `.ldf`/`.flac`
input に bundled libsndfile を使います。対象は default 40 MHz VHS `.ldf`、VHS
`--no_resample`、`--inputfreq` なしの LD です。default VHS `.flac` と全 CVBS input、
Ogg/FLAC、stereo、PCM24、他の sample rate、未完了 header は FFmpeg/PyAV-compatible
path を維持します。

<!-- SECTION: build -->

## Build と test

固定 SDK は .NET `11.0.100-preview.6.26359.118` です。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1234
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
