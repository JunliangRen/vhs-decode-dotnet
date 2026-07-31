# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-07-31.03 -->

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
- Visual Studio 2026 の `.slnx` には **1,141** 件の標準 xUnit v3 test があり、
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
| default（5） | 16.983 s | 14.414 s | 5.781 s / 2.938x | 7.216 s / 1.998x | 5.657 s / 3.002x | 7.092 s / 2.032x |
| `--threads 1` | 21.263 s | 19.881 s | 18.232 s / 1.166x | 20.627 s / 0.964x | 17.542 s / 1.212x | 20.201 s / 0.984x |
| `--threads 5` | 16.880 s | 14.329 s | 5.906 s / 2.858x | 7.463 s / 1.920x | 5.689 s / 2.967x | 7.459 s / 1.921x |
| `--threads 10` | 17.612 s | 15.149 s | 4.536 s / 3.883x | 5.887 s / 2.573x | 4.414 s / 3.990x | 5.537 s / 2.736x |
| `--threads 20` | 18.330 s | 15.447 s | 3.568 s / 5.138x | 5.049 s / 3.059x | 3.471 s / 5.281x | 4.496 s / 3.435x |
<!-- LATEST_PERFORMANCE_END -->

最新の Exact pass は、VHS RF stream block が全 cache から外れ、現在の span
assembly が完了した後にのみ output array を再利用します。public block result の
ownership と DSP 演算は変えず、idle retained pool は 48 set の hard limit を持ちます。
同条件の 100-frame trace では sampled allocation が 4.599 GB から
566.9 MB へ 87.7% 減少しました。

main/candidate の strict thread-profile gate 12 回と refreshed matrix 60 回は、
すべて各 reference と一致しました。交互順序の `current`/20-worker pair 10 組では、
decode-time median が 8.72 から 8.58 秒（1.61% 改善）、mean が 1.07% 改善しました。
throughput gain は modest ですが allocation pressure は大幅に低下しています。
1,000-frame gate も完全一致し、69.475 秒で完了、working set は bounded かつ
711.6 MiB 未満でした。

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

<!-- SECTION: build -->

## Build と test

固定 SDK は .NET `11.0.100-preview.6.26359.118` です。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1141
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
