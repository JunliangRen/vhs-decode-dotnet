# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-04.03 -->

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
- Visual Studio 2026 の `.slnx` には **1,349** 件の標準 xUnit v3 test があり、
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

次の表は同じ private local 40 MHz PAL VHS `.ldf` fixture と同じ 40-frame
window を使用し、source filename は公開しません。Python 列は audited measurement
を維持します。merged main `38b7d32` で、20 個すべての .NET cell を各 3 回
Release 測定しました。この oversized raw-FLAC fixture は
libsndfile 1.2.2 の exact-seek gate を超えるため、現在は正しく FFmpeg を使用します。
したがって、以前の `ced6afb` ベースの表とは直接比較できません。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 15.207 s | 16.780 s | 7.533 s / 2.019x | 7.725 s / 2.172x | 5.320 s / 2.858x | 5.171 s / 3.245x |
| `--threads 1` | 17.694 s | 19.414 s | 12.291 s / 1.440x | 14.181 s / 1.369x | 8.524 s / 2.076x | 9.719 s / 1.997x |
| `--threads 5` | 15.719 s | 17.801 s | 7.258 s / 2.166x | 7.626 s / 2.334x | 5.299 s / 2.966x | 5.150 s / 3.457x |
| `--threads 10` | 16.037 s | 18.266 s | 5.901 s / 2.718x | 6.861 s / 2.662x | 4.979 s / 3.221x | 4.702 s / 3.885x |
| `--threads 20` | 16.405 s | 18.395 s | 5.852 s / 2.803x | 6.058 s / 3.036x | 4.596 s / 3.569x | 4.345 s / 4.234x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: dotnet-full-refresh=60 repeats=3 cti-long-paired=8 determinism=60 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する
speedup の順で、default は **5 workers** です。multi-worker compact VHS path は
low-pass sync work と並行して `Video`、`Envelope`、`Chroma` を materialize します。
active staged span は 1 個に制限され、serial/stateful path は eager のままです。
reverse-order 1,000-frame Exact pair では v0.4.0 が 2.66%、`current` が 2.55%
短縮しました。600-frame IPP-fast pair は v0.4.0 が 1.85% 改善し、`current` は
実質 neutral（-0.03%）でした。

managed AVX は、元の float32 FMA 順序、scalar tail、no-AVX fallback を維持したまま、
8 個の独立した `current` CTI distance lane を評価します。reverse-order の
1,000-frame Exact 2 pair は 50.687/50.793 秒（baseline/candidate、-0.21%）で
neutral でした。IPP-fast の 2 pair は両方 candidate が勝ち、43.730/42.872 秒
（1.96% 短縮）、CPU time は 255.273 から 251.797 秒へ 1.36% 減少しました。
IPP pair の peak memory は増えていません。

更新した 60 matrix run はすべて deterministic でした。default-5 と
`--threads 1/5/10/20` は、profile ごとに luma、chroma、raw JSON、stdout、
normalized stderr/log の hash が 1 組だけでした。Exact/IPP-fast の CTI long gate
では baseline/candidate 間の ordered `fileLoc` も一致しました。
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

native-input route では、40 kHz mono PCM16 かつ total sample count が
`Int32.MaxValue` 以下の direct raw `fLaC` `.ldf`/`.flac` input に bundled
libsndfile を使います。それより大きい raw-FLAC capture は、libsndfile 1.2.2 が
境界後の exact random access を保証できないため FFmpeg を使います。eligible input
には default 40 MHz VHS `.ldf`、VHS `--no_resample`、`--inputfreq` なしの LD が
含まれます。default VHS `.flac`、全 CVBS input、Ogg/FLAC、stereo、PCM24、他の
sample rate、未完了 header は FFmpeg/PyAV-compatible path を維持します。

<!-- SECTION: build -->

## Build と test

固定 SDK は .NET `11.0.100-preview.6.26359.118` です。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1349
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
