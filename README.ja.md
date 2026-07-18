# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-07-18.1 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 10 で再実装するプロジェクトです。現在は release
`v0.4.0`、commit `43155200da87c0d49eb37d8ec09b1372075ee8e4`
を互換性の基準としています。

> [!IMPORTANT]
> この互換移植は現在も開発中です。トップレベルのデコード経路は実装済みで
> 多数のテストがありますが、あらゆる実キャプチャとまれなオプションの組み合わせで
> バイト単位の一致を保証する段階にはまだ達していません。

## 目次

- [対象範囲](#対象範囲)
- [現在の状態](#現在の状態)
- [互換性の範囲](#互換性の範囲)
- [パフォーマンス](#パフォーマンス)
- [ビルドとテスト](#ビルドとテスト)
- [使用方法](#使用方法)
- [出力とライブプレビュー](#出力とライブプレビュー)
- [検証](#検証)
- [今後の作業](#今後の作業)
- [詳細な根拠](#詳細な根拠)
- [ライセンス](#ライセンス)

<!-- SECTION: scope -->

## 対象範囲

この移植では、次のデコードアプリケーションのみを実装します。

- `decode.py vhs`
- `decode.py cvbs`
- `decode.py ld`
- `decode.py hifi`
- `vhs-decode`、`cvbs-decode`、`ld-decode`、`hifi-decode`
  と同等のスタンドアロンエイリアス

次の機能は意図的に対象外です。

- TBC ユーティリティと無関係な補助アプリケーション
- 元の decode をダブルクリックしたときに表示されるユーザー操作 GUI
- Matplotlib の `--debug_plot` ウィンドウと line-profiler の
  UI/レポート描画
- デコードパイプライン自体に不要なフィルター調整 UI

上流 CLI との互換性に必要な場合は、これらのツールを参照する
デコードオプションも引き続き解析されます。

<!-- SECTION: status -->

## 現在の状態

| 領域 | 状態 | 現在の境界 |
| --- | --- | --- |
| ソリューションとテスト | 実装済み | .NET 10 `.slnx`。標準 xUnit v3 テストは Visual Studio Test Explorer と `dotnet test` で利用できます。 |
| CLI と引数 | 実装済み、スナップショットテストあり | facade とスタンドアロンの help、alias、default、validation、diagnostic、exit 動作は v0.4.0 を対象とします。 |
| VHS とテープ形式 | 実装済み。まれなキャプチャ差分あり | VHS、S-VHS、Betamax、Video8/Hi8、U-matic、Type C、EIAJ、および対応 PAL/NTSC 形式は release 互換経路を共有します。 |
| CVBS | release 対応システムを実装済み | PAL/NTSC 経路は動作します。まれな vblank とオプション間のケースには実キャプチャ fixture の追加が必要です。 |
| LaserDisc | 実装済み。まれなキャプチャ差分あり | Video、VBI、EFM、analog audio、AC3、RF-TBC、metadata、recovery、PAL/NTSC 経路を接続済みです。 |
| HiFi | 実装済み。実キャプチャ検証が残る | 型付き v0.4.0 CLI、境界付き並列デコード、後処理、WAV/FLAC 出力、preview、GNU Radio mode を接続済みです。 |
| 入力 | 広範囲に実装済み | Raw input と一般的な FFmpeg/PyAV 相当の container 経路をカバーしています。まれな codec/timestamp は今後の対象です。 |
| 出力とリカバリー | 実装済み。edge case が残る | Streaming TBC/audio、JSON snapshot、SQLite、log、disk-space 処理、recovery 順序をカバーしています。 |
| 対話型 UI | 対象外 | デコード用 GUI と開発者向け plot/report ウィンドウは意図的に実装しません。 |

「実装済み」は、実行経路と対象を絞った互換性テストが存在することを意味します。
すべてのキャプチャで完全一致が証明済みという意味ではありません。

<!-- SECTION: coverage -->

## 互換性の範囲

### コマンドと引数

- 従来形式の `Program.Main` エントリポイントと `decode.py` 形式の dispatch。
- `decode.exe`、`vhs-decode.exe`、`cvbs-decode.exe`、
  `ld-decode.exe`、`hifi-decode.exe` apphost。
- Release 4.0 の option 名、alias、default、位置引数、help text、
  validation 順序、Python 形式の数値動作、error format。
- 対応するテープ形式とカラーシステム向けの VHS format catalog と parameter file。
- 標準入出力動作と上流互換の file validation。

### デコードパイプライン

- RF filter、FM demodulation、sync/level detection、line-zero recovery、
  field parity、HSync refine、TBC resampling、dropout detection、chroma、
  wow correction、AGC、metadata generation。
- `--use_saved_levels` は直前 field の sync level を再利用し、失敗時には
  再検出します。また現在の VHS field に 30 個以上の line-location error が
  ある場合、次の field で full detection を強制し、v0.4.0 の状態動作に合わせます。
- VHS/S-VHS/Betamax/Video8/Hi8/U-matic/Type C/EIAJ の routing と、
  上流 release 4.0 が対応する PAL、NTSC、PAL-M、PAL-N、MESECAM、
  NTSC-J、405-line、819-line の互換経路。
- LaserDisc VBI、CAV/CLV interpretation、analog audio、EFM/pre-EFM、
  AC3、automatic MTF、AGC、VITS、player-skip detection、recovery state。
- HiFi carrier decode、dropout compensation、head-switch interpolation、
  normalization、preview、GNU Radio transport、ordered output。

### 実行時と出力の動作

- 対応済み分岐では、recovery offset、field-order action、parameter-file log、
  partial output finalize を含む上流 diagnostic を完全一致または正規化して保持します。
- 必要に応じて streaming `.tbc`、`_chroma.tbc`、JSON、SQLite、
  PCM、EFM、pre-EFM、RF-TBC、AC3、WAV、FLAC 経路を提供します。
- 定期的な recovery JSON snapshot と上流形式の partial-file lifecycle。
- デコード中も TBC、chroma、JSON、raw audio sidecar を並行して読み取れるため、
  preview tool は完了を待つ必要がありません。

<!-- SECTION: performance -->

## パフォーマンス

性能改善は実装の一部ですが、決定的な出力と release 互換性を最優先します。

- `-t` / `--threads` は境界付き並列 RF demodulation/filtering を実行し、
  stream、FFmpeg、GNU Radio の読み取り順序を維持します。
- stream 単位の decoded RF cache により、重複する field read 間の FFT 再計算を
  避けつつメモリ使用量を制限します。
- 長い TBC sinc-resampling job は worker budget を共有し、出力順序を維持します。
  `--threads 0` と `--threads 1` は決定的な serial path を保持します。
- HiFi は境界付き並列 block decode の後、順序どおりに後処理と書き込みを行います。
- Managed FFT worker は scratch buffer と immutable root table を再利用し、
  安全な箇所で in-place transform を使い、aligned path の field 全体コピーを避けます。
- AVX/FMA kernel は LD の float32 quantization と complex frequency filtering を
  高速化し、検証済みの NumPy rounding と scalar fallback を保持します。

ある Windows fixture 環境での Release 1 frame 計測値は次のとおりです。

| デコード | この移植 | Python v0.4.0 |
| --- | ---: | ---: |
| NTSC VHS | 2.346 s | 7.193 s |
| NTSC LaserDisc | 1.651 s | 5.865 s |

これは特定 fixture の値であり、一般的な benchmark ではありません。
PAL LD 4-field Core probe では、検証済み出力を維持しながら managed allocation を
5.12 GiB から 1.96 GiB に削減しました。

<!-- SECTION: build -->

## ビルドとテスト

必要条件：

- .NET 10 SDK
- IDE として使用する場合は Visual Studio 2026
- FFmpeg 対応 container input では `ffmpeg` と `ffprobe` が `PATH` 上に必要
- デフォルトの HiFi FLAC 出力には `ffmpeg` が必要

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

現在の正式な Release build は warning 0、error 0 です。xUnit v3 project は
`dotnet test` と Visual Studio Test Explorer の両方で個別に検出できる
**736** tests を公開します。

<!-- SECTION: usage -->

## 使用方法

facade または standalone help を表示します。

```powershell
dotnet run --project src/VHSDecode.Cli -- vhs --help
dotnet run --project src/VHSDecode.Cli -- cvbs --help
dotnet run --project src/VHSDecode.Cli -- ld --help
dotnet run --project src/VHSDecode.Cli -- hifi --help
```

Release build 後は facade dispatch または apphost alias を使用できます。

```powershell
src\VHSDecode.Cli\bin\Release\net10.0\decode.exe vhs [upstream options] input output
src\VHSDecode.Cli\bin\Release\net10.0\vhs-decode.exe [upstream options] input output
```

対応する `cvbs`、`ld`、`hifi` command と上流 v0.4.0 の引数を使用します。
正確な引数一覧は `--help` で確認してください。

<!-- SECTION: preview -->

## 出力とライブプレビュー

Video decode output は、上流 Python と互換の read/write sharing で開かれます。
デコード実行中は次の操作が可能です。

- 増加中の `.tbc` と `_chroma.tbc` を開いて読み取れます。
- 別 process が公開済み `.tbc.json` recovery snapshot を解析できます。
- LD の `.pcm`、`.efm`、`.prefm` sidecar を並行して読み取れます。
- reader の許可によって write hot path に copy や lock は追加されません。
  実際の性能影響は preview tool と競合する storage I/O が主因です。

file length と snapshot 公開時点の正本は writer です。reader は増加中の TBC file を
処理し、JSON snapshot の置換後に再度開く必要があります。

<!-- SECTION: verification -->

## 検証

テストスイートは独自プログラムではなく、標準 xUnit v3 です。次を含みます。

- CLI/help/error snapshot と format/parameter matrix
- 決定的 DSP と floating-point compatibility fixture
- serial/worker output と state transition の比較
- TBC、chroma、JSON、SQLite、audio、sidecar lifecycle test
- recovery、seek、parity、field order、diagnostic order
- active output sharing と partial file readability
- 上流 release 4.0 との比較で作成した differential fixture

検証済み fixture には byte-exact output と安定した SHA-256 baseline が含まれます。
アルゴリズム別の全一覧と hash は、下記の共有 evidence document に保存しています。

<!-- SECTION: remaining -->

## 今後の作業

次は境界が明確な互換性・検証の差分であり、トップレベル command の欠落ではありません。

- 現在の fixture 外にあるまれな container codec/timestamp 動作
- HiFi 実キャプチャの end-to-end baseline 追加
- PAL LaserDisc、AC3、verbose VITS の実キャプチャ edge case
- まれな VHS/CVBS vblank、chroma track-phase、cross-option interaction
- まれな first-HSync/vblank recovery と完全な JSON/SQLite field metadata
- 残る TBC writer bit-compatibility edge と、全 format/option/実キャプチャの output parity
- fixture で互換性を保護した上での CPU utilization、allocation、SIMD、
  worker scheduling の継続的な profiling

対話型デコード UI と TBC utility はこの目標の対象外であり、
未完了のデコード互換作業としては追跡しません。

<!-- SECTION: evidence -->

## 詳細な根拠

以前の長い実装・差分検証一覧は
[`docs/COMPATIBILITY_EVIDENCE.md`](docs/COMPATIBILITY_EVIDENCE.md)
に保存されています。この共有文書には、3 言語の README が参照する詳細な
algorithm note、数値境界、output hash、fixture result が含まれます。

<!-- SECTION: license -->

## ライセンス

GPL-3.0。[`LICENSE`](LICENSE) を参照してください。
