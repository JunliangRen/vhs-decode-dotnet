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
- Visual Studio 2026 の `.slnx` には **1,417** 件の標準 xUnit v3 test があり、
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
`--start 100 --length 160` snapshot です。source filename は公開しません。Python と .NET の全 90 Release
run を、この candidate 上で forward、reverse、mixed の 3 pass により interleave して
測定しました。candidate は merged main `3e01fdd` を基にしています。測定した production
blob と 3-run range は詳細版に固定記録しています。互換性と速度は別々に評価します。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 46.064 s | 45.418 s | 13.109 s / 3.514x | 12.868 s / 3.530x | 11.499 s / 4.006x | 9.725 s / 4.670x |
| `--threads 1` | 55.170 s | 55.257 s | 33.228 s / 1.660x | 39.682 s / 1.393x | 23.625 s / 2.335x | 26.775 s / 2.064x |
| `--threads 5` | 46.247 s | 45.834 s | 13.377 s / 3.457x | 12.807 s / 3.579x | 11.552 s / 4.003x | 9.842 s / 4.657x |
| `--threads 10` | 47.318 s | 48.504 s | 10.567 s / 4.478x | 9.664 s / 5.019x | 9.637 s / 4.910x | 7.933 s / 6.114x |
| `--threads 20` | 48.349 s | 48.631 s | 8.675 s / 5.574x | 8.134 s / 5.979x | 8.290 s / 5.832x | 6.110 s / 7.959x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 sos-state-current-160-ab-pairs=6 sos-state-default-160-ab-pairs=4 sos-state-current-1000-ab-pairs=1 sos-state-thread-gate-runs=6 sos-state-jit-disassemblies=2 sos-state-focused-test-runs=2 sos-reverse-32k-microbench-pairs=21 sos-reverse-current-160-ab-pairs=8 sos-reverse-v040-160-ab-pairs=1 sos-reverse-current-1000-ab-pairs=1 sos-reverse-thread-gate-runs=6 sos-focused-test-runs=80 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

各 .NET cell は wall-time median と profile が対応する Python 列に対する speedup の
順で、default は **5 workers** です。3-run range は
[詳細な性能リファレンス](docs/README.detailed.ja.md#パフォーマンス)にあります。全列を同じ
interleaved batch で測定していますが、ratio は分母となる Python 時間でも変動します。
.NET regression の判断には古い表の倍率ではなく、同じ fixture と範囲による .NET revision
A/B を使用します。

managed Exact float32 SOS の generic kernel は、section ごとの 2 個の recursive state を
GC-tracked span reference 経由で参照します。arithmetic order を変えず、forward と backward
の inner loop からそれぞれ 2 個の bounds branch を除去しました。interleaved 160-frame
`current --threads 20` 6 pair では median wall time が 11.499 から 11.373 seconds（1.10%）へ
短縮し、throughput は 1.12% 向上、CPU time は 1.56% 減少しました。candidate は 5 pair で
勝ち、9 種類の compatibility surface はすべて一致しました。別の 1,000-frame pair は
42.463 から 42.221 seconds（0.57%）へ短縮し、CPU time は 1.03% 減少、memory は bounded の
ままです。

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
  --no-build --no-restore --minimum-expected-tests 1417
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
