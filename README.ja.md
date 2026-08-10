# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-08-10.02 -->

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
- Visual Studio 2026 の `.slnx` には **1,416** 件の標準 xUnit v3 test があり、
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
160-frame snapshot です。source filename は公開しません。Python と .NET の全 90 Release
run を、この candidate 上で forward、reverse、mixed の 3 pass により interleave して
測定しました。candidate は merged main `0306db8` を基にしています。測定した production
blob と 3-run range は詳細版に固定記録しています。互換性と速度は別々に評価します。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 44.296 s | 43.805 s | 12.763 s / 3.471x | 11.858 s / 3.694x | 11.251 s / 3.937x | 9.487 s / 4.617x |
| `--threads 1` | 53.404 s | 53.632 s | 32.537 s / 1.641x | 38.751 s / 1.384x | 22.896 s / 2.332x | 26.221 s / 2.045x |
| `--threads 5` | 44.283 s | 43.593 s | 12.600 s / 3.514x | 12.468 s / 3.496x | 11.196 s / 3.955x | 9.393 s / 4.641x |
| `--threads 10` | 45.330 s | 45.943 s | 9.941 s / 4.560x | 9.959 s / 4.613x | 9.385 s / 4.830x | 7.611 s / 6.036x |
| `--threads 20` | 46.551 s | 46.697 s | 8.336 s / 5.584x | 7.608 s / 6.138x | 8.084 s / 5.759x | 5.806 s / 8.043x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 fused-spectrum-microbench-pairs=12 fused-spectrum-short-ab-pairs=8 fused-spectrum-long-ab-pairs=2 fused-spectrum-thread-gate-runs=12 fused-spectrum-scalar-runs=2 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する speedup の
順で、default は **5 workers** です。3-run range は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。全列を同じ
interleaved batch で測定していますが、ratio は分母となる Python 時間でも変動します。
.NET regression の判断には古い表の倍率ではなく、同じ fixture と範囲による .NET revision
A/B を使用します。今回の refresh では全 .NET median が以前の表より短くなりましたが、
対応する Python median の低下率がさらに大きい cell では表示 speedup が下がっています。
たとえば default IPP-fast/current の .NET は 9.778 s から 9.487 s に短縮しましたが、
対応する Python は 53.288 s から 43.805 s になったため、.NET wall time が短くても ratio は
5.450x から 4.617x に変化しました。

Exact VHS は 2 回の complex RF filter と Hilbert real scale を、1 回の ordered spectrum
traversal で実行します。binary64 expression と stage order は変更せず、non-finite value と
alias input は従来の scalar/sequential behavior を維持します。12 alternating 1M-element
microbenchmark pair で kernel は 4.280 ms から 2.976 ms（1.438x）へ改善しました。4 組の
400-frame pair は end-to-end median を 16.50 から 16.33 秒へ短縮し、profile-matched の
1,000-frame run は `current` で 0.65%、v0.4.0 で 2.10% lower wall time を観測しました。
各 profile 1 pair の観測であり、sustained gain の claim ではありません。A/B、6 thread mode、
fully scalar fallback の全 artifact/log surface が一致し、working set は bounded のまま、
最初と最後の 3 分の 1 の間では小幅な変動に留まりました。

merged Python PR341 も deterministic でしたが、Python v0.4.0 は 15 run で 15 種類の luma、
chroma、JSON、log hash を生成したため、strict oracle は引き続き Python v0.4.0
`g4315520 --threads 0` です。完全な command、hardware、hash、memory bound、過去の測定は
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
  --no-build --no-restore --minimum-expected-tests 1416
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
