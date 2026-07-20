# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-07-20.3 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 11 で再実装するプロジェクトです。現在は release
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
| ソリューションとテスト | 実装済み | .NET 11 `.slnx`。標準 xUnit v3 テストは Visual Studio Test Explorer と `dotnet test` で利用できます。 |
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
- VHS は境界付き連続 RF pipeline を使用します。1 つの producer が順序付き input read
  を所有し、lookahead slot は最大 32、同時 block decode は最大 8 です。完了した block は
  個別に公開されるため、field は batch 全体ではなく必要な block だけを待ちます。
  seek、stream 変更、dispose では producer を cancel/drain してから別 reader が
  FFmpeg/GNU Radio stream に触れます。
- VSync envelope/minima 処理と harmonic power-ratio search は 1 つの read-only padded
  input 上で並行実行します。両 branch の完了後、candidate arbitration と detector
  state update は引き続き順序どおりに行います。
- worker 有効時、VHS field decode は luma TBC render と chroma field decode を
  並行実行します。同時に存在する chroma task は最大 1 つで、次の field へ進む前に
  calling thread 上で順序どおり state を commit します。
- 長い TBC sinc-resampling job は worker budget を共有し、出力順序を維持します。
  `--threads 0` と `--threads 1` は決定的な serial path を保持します。
- linear wow adjustment は一定の derivative を line ごとに 1 回だけ計算し、median/MAD
  repair 後に展開します。worker 有効時も source position と level preparation は固定 2-way
  のみで並行実行します。
- VHS heterodyne/carrier table は境界付きで並行構築します。carrier と phase parameter が
  一致する場合だけ phase-analysis workspace を field decode で再利用し、AFC 変更時は
  元の rebuild path に戻ります。
- HiFi は境界付き並列 block decode の後、順序どおりに後処理と書き込みを行います。
- Managed real FFT は pool 化した packing/scratch buffer を再利用します。float32 SOS の
  forward/backward filtering は拡張 buffer を 1 つ rent して in-place 実行し、呼び出し
  終了時に同期的に返却します。返される output array の通常の ownership は維持します。
- RF span assembly は block 境界の field array を作って再度 slice せず、要求された
  最終 output window へ直接書き込みます。
- little-endian host では、TBC/chroma sample を full-field byte copy を作らず `ushort`
  span から直接 stream へ書き込みます。big-endian fallback は返却される pooled buffer
  を 1 つ使うため、反復 write の memory 使用量も境界付きです。
- 標準 VHS field decode は、固定 read window が取り得る 2 種類の block 数に対応する
  exact-length RF span buffer set を最大 2 組だけ再利用します。同期 field decode 後に
  buffer を返却し、public `Read` result、deferred CVBS render、保持される LD VITS source
  はそれぞれ独立した array ownership を維持します。
- AVX/FMA kernel は正確な float32 conversion、VHS RF-envelope preparation、
  VHS Rust-style FM angle approximation、LD quantization、VHS chroma rotation、
  complex frequency filtering を高速化します。forward/inverse radix-4 FFT と 16-tap TBC
  sinc kernel は算術順序を変えずに pinned pointer indexing で bounds check を除去し、
  differential test で transform bit と output hash の一致を維持します。
- Recovery metadata は disk streaming され、snapshot queue の容量は 1、field-order
  history と RF cache にも hard limit があります。長時間 decode でも全 field を
  保持したり、将来の work を無制限に enqueue したりしません。
- CUDA/OpenCL は runtime dependency ではありません。現在の trace では、独立した
  32K FFT を host/device 間で往復させる根拠がありません。将来の任意 GPU backend は
  device-resident DSP stage を batch 化し、正確な CPU fallback を維持する必要があります。

ある Windows fixture 環境での Release 1 frame 計測値は次のとおりです。

| デコード | この移植 | Python v0.4.0 |
| --- | ---: | ---: |
| NTSC VHS | 2.346 s | 7.193 s |
| NTSC LaserDisc | 1.651 s | 5.865 s |

これは特定 fixture の値であり、一般的な benchmark ではありません。現在の VHS A/B は
すべて .NET SDK/runtime `11.0.100-preview.6.26359.118`、`--threads 20`、default chroma、
default resampling を使用しました。再現可能な 40-frame PAL probe では、保存した
continuous-pipeline 導入前 baseline の中央値が 11.60 秒、現在が 7.71 秒で、33.5% の
改善です。平均 active core は約 2.2-2.5 から 3.3-3.7 に増え、paired TBC、JSON、
chroma SHA-256 は一致しました。

現在の 40/160/320-frame sustained run は 7.65/26.58/52.51 秒で完了しました。
peak working set は 1.76/1.88/1.67 GiB、後半中央値は 1.42/1.30/1.28 GiB です。
320 frame はすべて書き込まれ、decode length に伴う memory 増加はありません。
以前の allocation pass では PAL LD 4-field probe も 5.12 GiB から 1.96 GiB に減少しました。

境界付き VHS field-stage overlap により、160-frame run は 20.13 秒から 18.55 秒
（7.8%）へ短縮しました。TBC、chroma、JSON の SHA-256 は完全に一致し、task は
current field 内で await されるため、memory は decode length とともに増加しません。

little-endian TBC writer の zero-copy write により、同じ 160-frame output 全体で約
455 MB の full-field temporary byte-array payload を除去しました。xUnit v3 allocation
probe は warm-up 後に 400,000 sample を thread-local allocation 1 KiB 未満で書き込みます。
新しい 160-frame run でも luma/chroma SHA-256 は完全に一致し、wall time は通常の
run-to-run noise の範囲内でした。

<details>
<summary>Kernel と allocation の benchmark 履歴</summary>

pinned pointer を使う PAL サイズ TBC sinc 単体 A/B では、field あたりの中央値が
3.929 ms から 3.727 ms へ下がり、kernel は 5.1% 改善しました。続く interior-window
path は edge と短い input の clamp を維持し、serial probe をさらに 1.6% 改善しました。
新しい 160-frame run は 21.31 秒、private-memory の 4 分割中央値は
0.78/1.18/1.20/1.41 GiB、peak は 1.68 GiB で、TBC、JSON、chroma SHA-256 は一致しました。

AVX RF-envelope preparation は、単体 32K-block 中央値を 57.5 us から 13.3 us へ
短縮し、kernel は 76.9% 改善しました。40-frame 中央値は 7.55 秒から 7.39 秒、
160-frame run は 26.95 秒から 25.70 秒になりました。private-memory の四分位中央値は
1.34/1.48/1.50/1.45 GiB、peak は 1.72 GiB で、3 種類の hash は完全に一致しました。

4-lane AVX/SSE VHS Rust-style FM unwrap は、単体 32K-block 中央値を 610.1 us から
130.7 us へ短縮し、kernel は 78.6% 改善しました。5 組を交互に実行した 40-frame
full-path A/B では、wall-time 中央値が 7.43 秒から 7.41 秒、CPU-time 中央値が
27.88 秒から 26.36 秒となり、CPU time は 5.5% 減少しました。TBC、JSON、chroma
hash は完全に一致しました。160-frame run は 26.48 秒で完了し、private-memory の
四分位中央値は 1.45/1.47/1.40/1.23 GiB、peak は 1.79 GiB でした。

最新の FFmpeg stream 最適化では、read ごとの 16 MiB rewind buffer 再構築を、1 個の
bounded circular buffer に置き換えました。384-read 単体中央値は 695.4 ms から
48.7 ms、allocation は 4.31 GB から 142.6 MB へ減少しました。3 回の 40-frame
A/B では wall/CPU time 中央値が 8.98/28.47 秒から 7.40/22.33 秒となり、3 種類の
output hash は完全に一致しました。sampled `byte[]` allocation は 36.3 GB から
209 MB へ減少しました。160-frame run は 25.86 秒、private-memory 四分位中央値は
0.76/1.15/1.42/1.14 GiB、peak は 1.67 GiB でした。

最新の VHS real-FFT 最適化では、decoder 所有で最大 16 個を保持する workspace pool
により、正確な長さの half-spectrum、Hilbert buffer、raw envelope、rotation input を
再利用します。5 回の単体 384-block A/B では、中央値が 1,140.6 ms から 1,054.0 ms
（7.6%）、allocation が 2.216 GB から 906.8 MB（59.1%）、Gen2 中央値が 168 回から
56 回へ減少しました。160-frame full-path A/B の wall time は 24.54/24.57 秒で実質
同等でしたが、CPU time は 78.03 秒から 70.13 秒（10.1%）へ減少しました。current
run の peak は 1.68 GiB、private-memory 四分位中央値は 0.88/1.55/0.78/1.51 GiB で、
単調増加ではありません。TBC、JSON、chroma、単体 block の hash は完全に一致しました。

forward radix-4 kernel も inverse と同じ pinned indexing を使用するようになりました。
32768-point 単体中央値は 204.7 us から 195.9 us（4.3%）へ下がり、bit は完全一致です。
384-block RF composite は 841.96/841.19 ms で実質同等のため、block 全体の高速化は
主張しません。

続く float32 SOS 最適化では sample-major の演算順序を維持し、1、2、4-section cascade
の state をローカル変数に保持します。それ以外の section 数では flat で bounded な
state を使用し、32 section までは stack、それを超える場合は heap へ fallback します。
5 回の単体 32K two/four-section 中央値は 110.2/155.4 ms から 75.3/83.3 ms
（31.7%/46.4%）へ、5/8/10-section 中央値は 38.8%/40.2%/42.7% 改善しました。
2 組の 160-frame A/B では wall time 中央値が 21.22 秒から 20.57 秒（3.1%）、CPU
time が 73.31 秒から 68.73 秒（6.3%）へ減少し、TBC、JSON、chroma hash は完全に
一致しました。current 2 run の private-memory peak 中央値は 1.71 GiB で、四分位
ごとの memory は単調増加ではありませんでした。

続く最適化では float32 SOS の padded workspace を pool 化しました。同一条件の
40-frame GC trace で sampled allocation 全体は 16.772 GiB から 16.178 GiB、
`Single[]` allocation は 651.68 MiB から 47.25 MiB へ減少しました。5 組の交互
full-path A/B は wall-time 中央値 5.541/5.537 秒で実質同等、CPU-time 中央値は
20.000 秒から 19.438 秒になり、3 種類の output hash は完全に一致しました。現在の
fixture-limited 204-frame run は 23.39 秒で完了し、private-memory 四分位中央値は
1.147/0.886/0.888/0.917 GiB、peak は 1.755 GiB でした。

</details>

<!-- SECTION: build -->

## ビルドとテスト

必要条件：

- `.NET SDK 11.0.100-preview.6.26359.118`（`global.json` で固定）
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
**782** tests を公開します。

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
src\VHSDecode.Cli\bin\Release\net11.0\decode.exe vhs [upstream options] input output
src\VHSDecode.Cli\bin\Release\net11.0\vhs-decode.exe [upstream options] input output
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
