# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-04.02 -->

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
- Visual Studio 2026 の `.slnx` には **1,319** 件の標準 xUnit v3 test があり、
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
window を使用し、source filename は公開しません。Python 列と 10 個の v0.4.0
.NET cell は audited measurement を維持します。main `89a0a09` を基にしたこの branch
では、10 個すべての `current` .NET cell を各 3 回再測定しました。互換性判定は
速度とは別です。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 15.207 s | 16.780 s | 4.267 s / 3.564x | 4.723 s / 3.553x | 3.608 s / 4.215x | 3.337 s / 5.029x |
| `--threads 1` | 17.694 s | 19.414 s | 9.732 s / 1.818x | 11.602 s / 1.673x | 7.089 s / 2.496x | 7.845 s / 2.475x |
| `--threads 5` | 15.719 s | 17.801 s | 4.167 s / 3.772x | 5.026 s / 3.542x | 3.607 s / 4.358x | 3.228 s / 5.515x |
| `--threads 10` | 16.037 s | 18.266 s | 3.274 s / 4.899x | 3.869 s / 4.721x | 3.069 s / 5.225x | 2.802 s / 6.518x |
| `--threads 20` | 16.405 s | 18.395 s | 2.919 s / 5.620x | 3.612 s / 5.093x | 2.667 s / 6.150x | 2.262 s / 8.134x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: prior-full-refresh=60 current-refresh=30 repeats=3 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する
speedup の順で、default は **5 workers** です。`current` VHS の 9-tap sync boxcar は
各 AVX vector で独立した 4 output を処理し、各 lane の従来の multiply/add order を
維持します。FMA、reassociation、worker、allocation、retained-buffer の変更は
ありません。40-frame 5-pair gate で IPP-fast/current 20-worker の wall median は
3.81%、CPU time は 8.55% 減少しました。最後の 200-frame pair では wall time が
1.43%、CPU time が 4.93% 減少し、peak memory は同じでした。更新した 20-worker
cell は 2.262 秒、profile-matched Python PR341 measurement の 8.134x です。

更新した 30 run は backend ごとに deterministic でした。baseline/candidate
gate は luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が一致しました。
IPP-fast は明示的な numerically-close backend のままで、Exact と byte-for-byte
同一とは主張しません。Python v0.4.0 は nonzero worker count で output hash が
変わる場合があるため、strict oracle は Python v0.4.0
`g4315520 --threads 0` です。完全な command、hardware、hash、memory bound、
過去の測定は
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
  --no-build --no-restore --minimum-expected-tests 1319
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
