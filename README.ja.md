# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-03.01 -->

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
- Visual Studio 2026 の `.slnx` には **1,295** 件の標準 xUnit v3 test があり、
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

次の表は同じ private local 40 MHz PAL VHS `.ldf` fixture と同じ 40-frame
window を使用し、source filename は公開しません。2026-08-04、main
`714f5a6` を基にしたこの branch candidate で、影響を受ける 10 個の IPP-fast
cell を 3 回ずつ interleaved 測定しました（30 Release run）。この変更は Exact
に入らないため、Exact と Python は同じ window の audited measurement を維持します。
互換性判定は速度とは別に行います。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 15.207 s | 16.780 s | 4.332 s / 3.510x | 5.402 s / 3.106x | 3.414 s / 4.454x | 3.285 s / 5.108x |
| `--threads 1` | 17.694 s | 19.414 s | 9.867 s / 1.793x | 12.596 s / 1.541x | 7.163 s / 2.470x | 8.754 s / 2.218x |
| `--threads 5` | 15.719 s | 17.801 s | 4.268 s / 3.683x | 5.365 s / 3.318x | 3.530 s / 4.453x | 3.269 s / 5.445x |
| `--threads 10` | 16.037 s | 18.266 s | 3.579 s / 4.481x | 4.379 s / 4.171x | 3.007 s / 5.333x | 2.896 s / 6.307x |
| `--threads 20` | 16.405 s | 18.395 s | 2.951 s / 5.559x | 3.885 s / 4.735x | 2.618 s / 6.267x | 2.433 s / 7.559x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: ipp-refresh=30 repeats=3 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する
speedup の順で、default は **5 workers** です。今回の変更は長い VHS chroma
burst block に worker-local かつ bounded な IPP float32 SOS context を与えます。
fixed 200-frame `ipp-fast + current --threads 20` A/B では median wall time が
9.740 から 9.480 秒へ 2.67% 短縮（throughput 1.027x）、CPU time が 52.160 から
48.590 秒へ 6.84% 低下しました。Exact は両 profile の 1/default/20 workers で
main と byte-for-byte 一致しました。

更新した全 IPP-fast cell は deterministic で、`--threads 0`、default、20-worker
gate も一致しました。IPP-fast は明示的な numerically-close backend のままであり、
artifact が Exact または以前の IPP evaluation order と byte-for-byte 同一とは
主張しません。Python v0.4.0 は nonzero worker
count で output hash が変わる場合があるため、strict oracle は引き続き Python
v0.4.0 `g4315520 --threads 0` です。完全な command、hardware、hash、memory
bound、過去の測定は
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
  --no-build --no-restore --minimum-expected-tests 1295
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
