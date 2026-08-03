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
- Visual Studio 2026 の `.slnx` には **1,283** 件の標準 xUnit v3 test があり、
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

次の表は、同じ private local 40 MHz PAL VHS `.ldf` fixture と同じ 40-frame
window を全 run で使用します。source filename は公開しません。各値は
2026-08-02 に main commit `c92af1d` で実行した 3 回の interleaved run の
median が基礎です。2026-08-03、bounded ACC segment と Super-Gaussian FFT の
parallelization 後の final cap-12 branch candidate で全 `current` cell を 3 回ずつ
再測定しました。Python と .NET v0.4.0 cell は従来の audited value を維持します。
さらに mixed-radix Super-Gaussian transform を IPP DFT32 に移した後、15 回の
final-candidate run で `IPP-fast + current` 列を更新しました。互換性判定は速度とは
別に行います。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 15.207 s | 16.780 s | 4.389 s / 3.465x | 7.030 s / 2.387x | 3.609 s / 4.213x | 3.801 s / 4.415x |
| `--threads 1` | 17.694 s | 19.414 s | 10.065 s / 1.758x | 13.871 s / 1.400x | 7.215 s / 2.453x | 9.527 s / 2.038x |
| `--threads 5` | 15.719 s | 17.801 s | 4.282 s / 3.671x | 7.428 s / 2.396x | 3.568 s / 4.406x | 3.583 s / 4.968x |
| `--threads 10` | 16.037 s | 18.266 s | 3.494 s / 4.589x | 6.040 s / 3.024x | 3.098 s / 5.177x | 3.027 s / 6.035x |
| `--threads 20` | 16.405 s | 18.395 s | 3.235 s / 5.071x | 5.118 s / 3.594x | 2.654 s / 6.182x | 2.545 s / 7.229x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: base=90 current-refresh=30 ipp-current-refresh=15 repeats=3 -->

採用した IPP candidate は、`ipp-fast + current` の 356400-point managed float32
transform だけを arbitrary-length IPP DFT32 に置き換えます。同じ private local
PAL VHS sample の fixed 200-frame `--threads 20` pair では wall time が `10.815`
から `9.470` 秒（12.43% 短縮、throughput 1.142x）、process CPU time が
`65.797` から `51.047` 秒へ低下し、peak working set も 1.7 MiB 減少しました。
luma と raw JSON は byte-identical です。1 億 4210.2 万 chroma sample の 0.1627%
に bit difference がありましたが、全 sample の 99.99987% は差 1 以下、RMSE は
0.063 で、documented approximate `ipp-fast` contract 内です。別の real-sample
Exact v0.4.0/`current` check は両方とも 9 gate 全一致でした。

その後の 80-frame Exact `current --threads 20` short screen では、上の full matrix を
維持しています。decoder-local burst-probe buffer reuse の wall time は実質 neutral
（`9.393` から `9.371` 秒）でしたが、process CPU time は `74.406` から `67.922`
秒、peak working set は `435.1` から `430.5` MiB へ低下しました。この single pair
を新しい throughput claim には使用しません。TBC、chroma、JSON、stdout、normalized
stderr/log、および zero/default/20-worker determinism はすべて一致しました。

2026-08-03 の最新 short screen では、classified sync pulse を immutable value として
保持し、pulse ごとの object allocation を除去しました。同じ 80-frame Exact
`current --threads 20` gate で wall time は `10.309` から `10.003` 秒へ `2.97%`
短縮し、process CPU time は `69.453` から `70.797` 秒へ `1.93%` 増加、peak working
set は `435.3`/`435.0` MiB で横ばいでした。9 項目の compatibility comparison は
すべて一致しました。この single pair は最新 optimization evidence の更新であり、
上の full matrix median を置き換えるものではありません。

今回採用する high-worker Exact VHS candidate は、serial spectrum preparation 後の
独立した 2 つの inverse FFT stage を並行実行します。同じ private local PAL VHS
fixture に対する fixed 200-frame Exact `current --threads 20` pair の結果です：

| 最新の採用 optimization | main `eec3658` | Branch candidate | 変化 |
| --- | ---: | ---: | ---: |
| Wall time | 11.945 s | 11.480 s | 3.89% 短縮 / throughput 1.041x |
| Process CPU time | 95.359 s | 90.828 s | 4.75% 低下 |
| Active cores | 7.98 | 7.91 | 実質同等 |

exit status、field count、luma、chroma、raw JSON、stdout、normalized stderr、
normalized log、ordered `fileLoc` の 9 gate はすべて一致しました。測定 harness が
有効な peak-working-set 値を返さなかったため memory 数値は主張しません。実装は
sample-length buffer を追加せず、既存の 12-block outer pipeline bound を維持します。
scope の異なるこの 200-frame pair は上の 40-frame repeated-run matrix を置き換えません。

各 .NET cell は wall-time median と profile が対応する Python 列に対する
speedup の順です。`1.000x` 未満は Python より遅いことを示します。Python
PR341 は merge commit `2f21e8ed6018b14561396cc95f1f6828054470b8` で、
`current` の upstream peer です。default は実際に **5 workers** です。matrix
は 30 個の mode/profile cell を 3 回ずつ繰り返した 90 run です。更新した 10 個の
`current` cell は final candidate の interleaved run 30 回、final IPP DFT32 列は
さらに 15 run を追加しました。

各 .NET profile と Python PR341 は mode ごとに 1 つの deterministic hash set
だけを生成しました。Python v0.4.0 は各 default/nonzero mode の 3 run で
luma/chroma/JSON/log hash を 3 種類生成しましたが、ordered `fileLoc`、stdout、
normalized stderr は安定していました。そのため、これらの Python 行は throughput
比較専用です。別の 40-frame `--threads 0` gate では Exact v0.4.0 と Exact
`current` が output byte、metadata、stdout/stderr、normalized log で各 Python
peer と一致しました。strict oracle は引き続き Python v0.4.0
`g4315520 --threads 0` です。

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
  --no-build --no-restore --minimum-expected-tests 1283
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
