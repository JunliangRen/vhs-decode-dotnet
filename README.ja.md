# vhs-decode-dotnet

[English](README.md) | [简体中文](README.zh-CN.md) | **[日本語](README.ja.md)**

<!-- README_SYNC: 2026-07-22.17 -->

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
- 周波数が厳密に 40.0 MHz の `.s16` input は native signed-16 loader を使い、
  実質的な変換を行わない FFmpeg pass-through を省きます。他形式と実際の
  resampling は従来の FFmpeg path を維持します。
- packed `.lds` input は Python-compatible な partial tail group を含め、要求された
  result array へ直接 decode します。完全な unpacked array の追加 allocation と copy は
  行いません。loader は 1,048,576 bytes 以下の private packed byte buffer を 1 個
  再利用します。並行 caller が貸し出し中の buffer を共有することはなく、それを超える
  read は保持しません。
- stream 単位の decoded RF cache により、重複する field read 間の FFT 再計算を
  避けつつメモリ使用量を制限します。
- VHS は境界付き連続 RF pipeline を使用します。1 つの producer が順序付き input read
  を所有し、lookahead slot は最大 32、同時 block decode は最大 8 です。完了した block は
  個別に公開されるため、field は batch 全体ではなく必要な block だけを待ちます。
  seek、stream 変更、dispose では producer を cancel/drain してから別 reader が
  FFmpeg/GNU Radio stream に触れます。完了した block は同じ worker 上限の下で、final
  RF span の重複しない trimmed range へ並行 copy します。serial path と stateful block
  path は順序付き assembly を維持します。
- VSync envelope/minima 処理と harmonic power-ratio search は 1 つの read-only padded
  input 上で並行実行します。両 branch の完了後、candidate arbitration と detector
  state update は引き続き順序どおりに行います。NumPy-compatible float64 median は
  small input で full sort を維持し、32K sample 以上で bit-exact introselect を使います。
- VSync の private forward/reverse envelope と harmonic BA-IIR chain は、それぞれが
  ownership を持つ array を in-place filter します。envelope branch は combined padded
  array を生成せず、reduced final result へ直接書き込みます。public IIR result の独立した
  ownership と bit-exact output は維持します。stateful detector は padded input が
  1,048,576 sample 以下のとき、exact-size の 6-array analysis workspace を 1 つ再利用
  します（上限時の保持量は約 48 MiB）。size 変更時はこの 1 entry を置き換え、それを
  超える input には保持しない temporary workspace を使います。
- VSync serration measurement は candidate window を read-only span で参照し、
  `Enumerable.Min`-compatible な float64 scan を使うため、full-window copy を 1 つ
  省きます。median scratch の ownership と NaN/signed-zero の bit semantics は不変です。
- worker 有効時、VHS field decode は luma TBC render と chroma field decode を
  並行実行します。同時に存在する chroma task は最大 1 つで、次の field へ進む前に
  calling thread 上で順序どおり state を commit します。
- 長い TBC sinc-resampling job は worker budget を共有し、出力順序を維持します。
  `--threads 0` と `--threads 1` は決定的な serial path を保持します。
- linear wow adjustment は一定の derivative を line ごとに 1 回だけ計算し、median/MAD
  repair 後に展開します。worker 有効時も source position と level preparation は固定 2-way
  のみで並行実行します。
- VHS heterodyne/carrier table は境界付き並行構築と session-owned one-entry cache を
  使用します。exact-key hit は元の array を再利用し、sample shape、carrier、phase、
  AFC の変更時は旧 entry を置き換えるため、保持 state は増加しません。phase analysis は
  field-owned resampled array を直接 read-only で使います。chroma prefilter が未設定なら
  decode も同じ read-only array を借用します。filter 設定時は owned result を返し、public
  prefilter API も independent-copy contract を維持します。
- 内部 VHS chroma comb と automatic gain は line-size の stack workspace を共有し、
  decode 専用 path は scale 済み sample を final `ushort[]` へ直接 map します。saturating
  body は AVX2/SSE4.1 を使い、未対応 CPU と末尾 sample は exact scalar fallback を
  維持します。public comb、gain、conversion API の independent-output contract は不変です。
- HiFi は境界付き並列 block decode の後、順序どおりに後処理と書き込みを行います。
- Managed real FFT は pool 化した packing/scratch buffer を再利用します。float32 SOS の
  forward/backward filtering は拡張 buffer を 1 つ rent して in-place 実行し、呼び出し
  終了時に同期的に返却します。返される output array の通常の ownership は維持します。
- double 精度 BA IIR の forward/backward filtering も 1 つの padded workspace 上で
  in-place 実行します。private pool は 4M sample まで、bucket ごとに最大 3 array だけを
  保持し、同期的に返却します。各 result は独立 ownership の exact-length array です。
- RF span assembly は block 境界の field array を作って再度 slice せず、要求された
  最終 output window へ直接書き込みます。
- default linear TBC resampling は field ごとの source-position/level-adjust workspace を
  rent し、正確な span だけを使用して、同期 serial/parallel resample の完了後に返却します。
- VHS diff-demod spike repair は、既存の 16-slot real-FFT workspace pool 内にある全長の
  complex scratch array を再利用します。返される analytic array は独立した ownership を
  維持し、非 VHS path は従来の allocation fallback を保持します。
- little-endian host では、TBC/chroma sample を full-field byte copy を作らず `ushort`
  span から直接 stream へ書き込みます。big-endian fallback は返却される pooled buffer
  を 1 つ使うため、反復 write の memory 使用量も境界付きです。
- 実際の multi-worker VHS session は capacity-one の専用 payload writer を使います。
  luma/chroma を並行 write しながら producer は次の field を decode し、payload、metadata
  snapshot、completion の順序は writer が単独で管理します。shutdown は queue を drain し、
  serial path と public custom-reader path は同期した順序付き write を維持します。
- 標準 VHS field decode は、固定 read window が取り得る 2 種類の block 数に対応する
  exact-length RF span buffer set を最大 2 組だけ再利用します。同期 field decode 後に
  buffer を返却し、public `Read` result、deferred CVBS render、保持される LD VITS source
  はそれぞれ独立した array ownership を維持します。
- VHS sync level の DC offset adjustment は exact-length low-pass workspace を最大 2 個
  再利用します。stateful pipeline がこの private buffer を所有し、元の video、public
  result、deferred-render input は変更せず、独立した array ownership を維持します。
- VHS は最後の block-local consumer の後に raw input、raw demodulation、analytic、
  RF high-pass result を破棄します。compact real-FFT block は分離した real/imaginary
  workspace を FM unwrap に直接渡し、未使用の RF high-pass inverse FFT、3 本の
  RF-span copy、全長 `Complex[]` 1 本を省きます。LD、CVBS、直接構築した decoder は
  従来の full-channel behavior を維持します。
- compact VHS stream block は、quantize 済みの SOS chroma も `float[]` のまま保持します。
  RF span assembly で AVX または exact scalar fallback により reusable field buffer へ
  一度だけ widen し、full/direct block は public `double[] Chroma` contract を維持します。
- AVX/FMA kernel は正確な float32 conversion、VHS RF-envelope preparation、
  VHS Rust-style FM angle approximation、LD quantization、VHS chroma rotation、
  complex frequency filtering を高速化します。forward/inverse radix-4 FFT は pinned
  pointer indexing を使用します。16-tap TBC sinc の interior window は独立した float
  weight/product を AVX/FMA で計算し、元の tap 順で加算します。clamped edge、短い input、
  非対応 hardware は scalar path を維持し、differential test で bit/hash を保ちます。
- Recovery metadata は disk streaming され、snapshot queue の容量は 1、field-order
  history と RF cache にも hard limit があります。長時間 decode でも全 field を
  保持したり、将来の work を無制限に enqueue したりしません。
- CUDA/OpenCL は runtime dependency ではありません。現在の trace では、独立した
  32K FFT を host/device 間で往復させる根拠がありません。将来の任意 GPU backend は
  device-resident DSP stage を batch 化し、正確な CPU fallback を維持する必要があります。

現在の thread matrix は Intel Core Ultra 7 265K（20 logical processor）、
Windows 11 build 26220、.NET SDK/runtime `11.0.100-preview.6.26359.118`、
この移植の checkpoint `a45d433`、Python v0.4.0 commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`（表示は `g4315520`）で実行しました。
分離した Python 環境は NumPy 2.4.6、SciPy 1.18.0、Numba 0.66.0、
python-soxr 1.1.0 を使用しています。各値は 3 回の交互 Release run の median です。

| CLI mode | Effective worker | この移植 | Python | Speedup | Wall-time reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| default | 5 | 4.646 s | 13.112 s | 2.82x | 64.6% |
| `--threads 1` | 1 | 9.203 s | 14.111 s | 1.53x | 34.8% |
| `--threads 5` | 5 | 4.544 s | 12.799 s | 2.82x | 64.5% |
| `--threads 10` | 10 | 4.074 s | 13.560 s | 3.33x | 70.0% |
| `--threads 20` | 20 | 3.779 s | 14.046 s | 3.72x | 73.1% |

default は Release 4.0 CLI semantics に合わせて最終的に **5 worker** のままです。
この 20 logical processor fixture では、明示的な 20-worker mode が最速でした。
matrix は同じ local PAL `.lds` file と `--system pal
--detect_chroma_track_phase --ire0_adjust --tape_format VHS --frequency 40
--start_fileloc 620000000 -l 40 --overwrite` に各行の thread option を加えています。

この移植の 15 run は、すべての worker 数で同じ luma TBC、chroma TBC、JSON hash
set を生成しました。追加した 3 回の Python `--threads 0` control は互いに一致し、
この移植の全 run とも完全一致しました。上流 Python の default/nonzero thread mode は
byte-exact baseline として安定せず、15 回の matrix run は 2 種類の luma/chroma pair
になりました。12 run は serial reference と一致し、3 回の `--threads 5` run は別の
pair でした。そのため matrix は observed throughput の比較であり、strict compatibility
baseline は Python `--threads 0` です。

この 40-frame fixture の compatibility baseline は Python v0.4.0 `g4315520` の
`--threads 0` output です。

| Baseline artifact | SHA-256 |
| --- | --- |
| Luma TBC | `64C518A03B208F7CF950916BC01A997021CB0F76B3D6F131FBEE74E9035FD30C` |
| Chroma TBC | `70112719879FB64FA95DC8F3ED6E5FA335D4F8B62C50FC2AF3C26D2C2098F26F` |
| JSON | `C223671830D0105271F24172923B280A96C8D0D427567C49E9C0E562D38FA881` |

より長い exact-output checkpoint は Intel Core Ultra 7 265K（20 logical
processor）、Windows 11 build 26220、.NET SDK/runtime
`11.0.100-preview.6.26359.118` で実行しました。

| PAL VHS、1,000 frame / 2,000 field | Wall time | CPU time | Peak working set |
| --- | ---: | ---: | ---: |
| この移植、Release（2 run） | 218.00 / 218.63 s | 238.72 / 239.50 s | 829.6-838.2 MiB |
| Python v0.4.0（`g4315520`） | 417.37 s | 未取得 | 未取得 |

両方とも同じ local PAL `.lds` file と
`--system pal --detect_chroma_track_phase --ire0_adjust --tape_format VHS
--frequency 40 --start_fileloc 281303040 --threads 0 -l 1000 --overwrite`
を使用しました。この移植の 2 run はどちらも約 1.91 倍高速（wall time は
47.7-47.8% 減）で、3 種類の SHA-256 は Python と両方の run の間で byte 単位に
一致しました。両実装とも `--threads 0` は deterministic serial mode です。

独立した no-seek startup checkpoint では、別の local PAL `.lds` file に同じ PAL VHS
option と `--threads 0 -l 1000` を使用しました。Python とこの移植の luma SHA-256
`E6616B63BD7DD1DB6C093FC6D1DCA7D23AABEF34EFD52089338D992F2DDCD0CD`
および chroma SHA-256
`A292BD77A8EB3373B6C631CE4552F77B6D4E5AF2228A85F01C63EDBBBFB4C0EF`
は byte 単位で一致しました。2,000 field record、135 startup recovery step、
1,000 entry の file-frame sequence（`22..1021`）も一致しています。packaged Python
baseline は 8 文字の identity `g43155200`、この移植は `g4315520` を書くため、対応する
`gitCommit`/`version` identity string だけが JSON の差分です。この correctness run は
別の decode process と重なったため、その時間は benchmark result として扱いません。

これは特定 fixture の値であり、一般的な benchmark ではありません。以下の 40-frame
tuning A/B は .NET SDK/runtime `11.0.100-preview.6.26359.118`、`--threads 20`、
default chroma、default resampling を使用しました。再現可能な 40-frame PAL probe では、保存した
continuous-pipeline 導入前 baseline の中央値が 11.60 秒、最新値が 4.228 秒で、累積
63.6% の改善です。最新の exact-kernel checkpoint 単体では、paired wall/CPU/
peak-working-set 中央値が 4.434 秒/16.516 秒/1.314 GiB から
4.228 秒/15.328 秒/1.069 GiB（4.6%/7.2%/18.6%）へ低下しました。process CPU/wall は
約 3.63 active core のため、今後も state-safe な field-stage parallelism を優先します。
14 run の paired TBC、JSON、chroma SHA-256 はすべて一致しました。

以前の 40/160/320-frame sustained run は 7.65/26.58/52.51 秒で完了しました。
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

pinned pointer を使う PAL サイズ TBC sinc 単体 A/B は 3.929 ms から 3.727 ms へ
5.1% 改善し、続く interior-window path でさらに 1.6% 改善しました。AVX/FMA pass も
scalar clamp と順序付き double accumulation を維持します。5 組の交互 PAL-field A/B で
serial/20-worker 中央値は 21.588/5.579 ms から 18.741/5.330 ms（13.2%/4.5%）へ、
5 組の 40-frame full-path wall/CPU 中央値は 5.511/19.297 秒から 5.478/17.922 秒
（0.6%/7.1%）へ減少しました。実行順を反転した 2 組の 204-frame pair は 1.1-1.3%
高速で memory は境界内に保たれ、TBC、chroma、JSON、単体 field hash は完全一致です。

session-owned VHS chroma-table cache は exact-key の heterodyne set と burst-carrier set を
各 1 組だけ保持します。同一 40-frame GC trace で sampled allocation は 13.854 GiB から
12.579 GiB、`Double[]` は 12,611.83 MiB から 11,311.73 MiB、Gen2 は 38 回から 31 回へ
減少しました。5 組の交互 A/B で wall/CPU 中央値は 5.49/19.23 秒から
5.30/18.05 秒（3.5%/6.1%）へ短縮しました。実行順を反転した 2 組の 204-frame pair は
4.4% と 4.8% 高速で、memory は非単調、peak は 2.0 GiB 以下でした。409 field と全 output
hash は完全一致です。残る 2 回の read-only field copy も除去した結果、matched trace の
sampled allocation/`Double[]` は 12.580 GiB/11,309.71 MiB から
12.147 GiB/10,871.59 MiB へ減少しました。5 組の交互実行で wall/CPU 中央値は
5.209/18.188 秒から 5.175/17.094 秒（0.7%/6.0%）へ短縮しました。順序を反転した
2 組の 204-frame pair は 1.8% と 1.9% 高速で、memory は非単調、peak は 2.05 GiB 以下、
`--length 204` の 408-field output は完全一致です。

parallel RF span assembly は完了済みの immutable block だけを読み、final window の
重複しない range へ書き込みます。analog-audio phase 処理は順序どおりのままです。
5 組の交互 40-frame run で wall-time 中央値は 5.165 秒から 4.878 秒（5.6%）へ短縮し、
CPU time は 18.172 秒から 18.875 秒（3.9%）へ増え、core 使用率を throughput に
変換しました。順序を反転した 2 組の `--length 204` pair は 21.31/20.35 秒と
21.84/20.18 秒（4.5%/7.6% 高速）でした。current memory は非単調で peak は
1.93/2.06 GiB、408 field と全 hash は完全一致です。

parallel VHS payload output は、各 field の独立した luma/chroma stream write を重ね、
次の field の前に両方を join します。5 組の交互 40-frame run で wall-time 中央値は
4.98 秒から 4.87 秒（2.2%）へ短縮し、CPU 中央値は 18.20 秒から 19.50 秒へ増えて
未使用 capacity を利用しました。順序を反転した 2 組の `--length 204` pair は
20.451/20.181 秒と 20.483/20.353 秒（1.3%/0.6% 高速）でした。current memory は
非単調で peak は 2.03/2.06 GiB、408 field と全 hash は完全一致です。

compact VHS RF-channel path は cache 前に raw input、raw demodulation、RF high-pass の
block array を解放し、対応する field assembly と未使用の RF high-pass inverse FFT を
省きます。5 組の交互 40-frame A/B で wall/CPU 中央値は 6.01/18.86 秒から
5.02/17.45 秒（16.5%/7.5% 高速）へ短縮しました。順序を反転した 2 組の 204-frame
pair は baseline/current 20.48/20.28 秒と 20.61/19.87 秒、CPU は
79.88/68.91 秒と 77.17/72.44 秒でした。peak working set は 2.05-2.08 GiB から
1.58-1.67 GiB へ減少し、quarter sample は非単調でした。408 field と luma、chroma、
JSON hash は完全一致です。

compact analytic の follow-up は pooled real/imaginary array を VHS FM unwrap に直接渡し、
4 個の frequency difference を SIMD で同時に正規化し、完全な direct API の場合だけ
`Analytic` を materialize します。5 組の交互 40-frame pair は 5.02/5.03 秒で wall-time
neutral、CPU 中央値は 17.73 秒から 17.28 秒、peak working-set 中央値は 1.47 GiB から
1.26 GiB へ低下しました。順序を反転した 2 組の 204-frame pair も wall-time noise 内で、
current peak は 1.32-1.41 GiB、quarter sample は非単調、3 種の hash は完全一致です。

compact chroma の follow-up は float32 SOS output を RF field assembly まで narrow のまま
保持します。対応する 10-frame allocation trace では sampled managed allocation が
2.95 GiB から 2.89 GiB、`Double[]` が 2.75 GiB から 2.60 GiBへ減少し、`Single[]` は
0.03 GiB から 0.11 GiBへ増加しました。5 組の交互 40-frame pair で wall/CPU 中央値は
4.831/16.50 秒から 4.769/15.75 秒（1.3%/4.5% 改善）でした。順序を反転した 2 組の
204-frame pair は baseline/current 19.73/19.83 秒と 19.87/19.73 秒で wall-time neutral、
current peak は 1.46/1.39 GiB で既存の bounded working-set envelope 内に収まり、luma、
chroma、JSON hash は完全一致です。

bounded payload-writer の follow-up は capacity-one queue を通じて、次の VHS field decode と
現在 field の luma/chroma write を重ねます。payload は対応する recovery JSON snapshot より
常に先に完了し、completion は writer を drain し、worker failure は decode thread へ戻ります。
5 組の交互 40-frame pair で wall/CPU 中央値は 4.90/16.09 秒から 4.79/15.47 秒
（2.2%/3.9% 高速）へ短縮しました。順序を反転した 2 組の 204-frame pair は
baseline/current 20.23/19.54 秒と 20.05/19.19 秒（3.4%/4.3% 高速）でした。current quarter
peak は 1.35/0.74/0.96/1.14 と 1.27/0.95/0.97/1.09 GiB で単調増加せず、408 field と
luma、chroma、JSON hash は完全一致です。

native-rate `.s16` input は、宣言周波数が厳密に 40.0 MHz の場合だけ FFmpeg を
bypass します。新しい trace の inclusive 上位 300 method には FFmpeg pass-through も
input pump も現れませんでした。5 組の交互 40-frame pair で wall/CPU 中央値は
5.33/17.11 秒から 4.97/15.94 秒（6.8%/6.8%）へ、peak working-set 中央値は
1.23 GiB から 1.13 GiB へ低下しました。順序を反転した 2 組の 204-frame pair は
baseline/current 21.50/20.86 秒と 21.67/21.54 秒で、candidate peak は
1.39/1.35 GiB、すべての output hash は完全一致です。

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

次の最適化では default linear TBC resampler の field ごとの 2 つの double workspace を
pool 化しました。同一条件の 40-frame GC trace で sampled allocation 全体は
16.178 GiB から 14.892 GiB、`Double[]` allocation は 13.601 GiB から 12.316 GiB へ
減少しました。5 組の交互 A/B で wall-time 中央値は 5.684 秒から 5.571 秒（2.0%）、
CPU-time は 19.031 秒から 18.891 秒になり、3 種類の hash は完全に一致しました。
反復した 204-frame run の private-memory 四分位中央値は
1.025/1.047/1.007/1.042 GiB で平坦、peak は 1.869 GiB でした。

VHS diff-demod repair は、一時的な全長 `Complex[]` を既存の上限付き FFT workspace に
保持するようになりました。同一条件の 10-frame GC trace で sampled allocation 全体は
4.134 GiB から 3.861 GiB、`Complex[]` allocation は 622.63 MiB から 340.02 MiB へ
減少しました。10 組の交互 40-frame pair と、実行順を反転した 2 組の 204-frame pair は
wall time が run noise の範囲内で同等だったため、speedup は主張しません。長時間 run の
memory は境界内に保たれ、409-field の全 hash は完全に一致しました。

現在の double SOS/BA IIR pass は、一般的な 2/4-section double cascade を融合し、BA
filter の padded workspace を private bounded pool で再利用します。isolated
2/4-section SOS median は 37.5%/58.9% 改善しました。32K sample の high-pass order
4/9/20 では、現在の IIR path は旧 allocating reference より 23.7%/30.3%/26.6% 高速で、
warm thread allocation は約 1.05 MB から 262 KB へ減少しました。7 組の交互 40-frame
full-path pair は、上記の wall 4.6%、CPU 7.2%、peak working set 18.6% の改善を示しました。
fixture-limited 409-field run は 17.431 秒で完了し、25-50%、50-75%、75-100% の output
interval は 4.06/4.02/4.27 秒、後半の working/private memory 中央値は前半より
10.8/7.4 MiB 高いだけでした。記録した luma、chroma、JSON hash はすべて exact です。

packed `.lds` loader は decoded sample を要求された output へ直接書き込み、Python の
partial-tail-group behavior を維持します。交互に実行した 5 組の 40-frame real-capture
pair では、default の wall/CPU median が 4.687/12.422 秒から 4.610/12.188 秒へ、
20-worker は 3.813/14.469 秒から 3.743/13.109 秒へ低下しました。3 組の 160-frame
default pair は wall time が 15.281 秒から 14.993 秒へ低下しました。別の 5 組の
20-worker repeat では wall/CPU median が 12.655/46.297 秒から 12.601/46.156 秒へ、
peak working set が 1.319 GiB から 1.198 GiB へ低下しました。42 回の real-capture run は、
fixture ごとに exact な luma、chroma、JSON hash set を 1 組だけ生成しました。

続く packed-input pass は loader-owned read buffer を 1 個再利用します。1,024-block、
32K isolated probe の median は block あたり 68.20 us から 65.17 us（4.4% 高速）へ、
managed allocation は 310.49 MB から 268.52 MB（13.5% 削減）へ改善しました。
同じ 160-frame runtime counters では total allocation が 22.248 GiB から
22.113 GiB へ、約 139 MiB（0.61%）減少しました。5 組の 40-frame pair は default の
wall/CPU median が 4.380/12.016 秒から 4.325/11.594 秒へ、20-worker は
3.645/14.813 秒から 3.586/14.188 秒へ低下しました。3 組の 160-frame pair は
default/20-worker が 14.173/11.692 秒対 14.231/11.701 秒で wall-neutral でした。
逆順を含む 2 組の 400-frame pair は candidate/baseline 26.229/26.403 秒、
baseline/candidate 26.395/26.540 秒でした。この pass は長時間 run の allocation を
下げるため保持しますが、160/400-frame 結果は安定した full-path CPU speedup を
示すものではありません。記録した luma、chroma、JSON hash はすべて exact です。

VHS sync-reference の DC-offset pass は exact-length low-pass workspace を最大 2 個
再利用するようになりました。同一条件の 10-field GC trace で sampled managed
allocation は 2.639 GiB から 2.466 GiB、`Double[]` allocation は 2,469.42 MiB から
2,291.86 MiB、Gen2 collection は 17 回から 15 回へ減少しました。交互に実行した
5 組の 40-field pair は run noise の範囲内で wall time が同等でした
（default 4.473/4.522 秒、20-worker 3.736/3.778 秒）。CPU median は
12.719 秒から 11.969 秒、14.375 秒から 13.859 秒へ低下しました。3 組の
160-field pair では default/20-worker の wall median が 15.272/12.560 秒から
15.113/12.378 秒へ低下しました。400-field、20-worker の A/B では wall/CPU が
28.937/106.984 秒から 28.296/105.344 秒へ低下し、candidate の private-memory
quarter median は 1.076/0.766/1.025/0.726 GiB、peak は 1.463 GiB で、単調な増加は
ありませんでした。記録した luma、chroma、JSON の全 A/B hash は exact です。

VSync serration-window pass は level measurement 前の full-window copy を除去しました。
同一条件の 10-field GC trace で sampled managed allocation は 2.465 GiB から
2.434 GiB、`Double[]` allocation は 2,291.20 MiB から 2,266.54 MiB へ減少し、
24.7 MiB を削減しました。retained buffer は追加していません。交互に実行した 5 組の
40-field pair は wall/CPU が run noise の範囲内で同等でした（default は
4.508/12.188 秒から 4.556/12.422 秒、20-worker は 3.719/14.203 秒から
3.696/14.531 秒）。3 組の 160-field pair も同等でした（default は
14.847/40.484 秒から 14.904/40.406 秒、20-worker は 12.319/45.172 秒から
12.361/45.391 秒）。candidate を先に実行した保守的な 400-field、20-worker A/B では
wall/CPU が 28.015/107.828 秒から 27.865/108.547 秒、peak working set が
1.481 GiB から 1.465 GiB へ変化しました。この変更は CPU speedup の主張ではなく、
長時間 run の allocation pressure 低減のために保持します。記録した luma、chroma、
JSON hash はすべて exact です。

VHS chroma-prefilter ownership pass は prefilter 未設定時に immutable field input を
借用し、設定済み filter と public `ApplyChromaPreFilter` API は引き続き independently
owned array を返します。同一条件の 10-field GC trace で sampled managed allocation は
2.440 GiB から 2.384 GiB、`Double[]` allocation は 2,267.10 MiB から
2,207.39 MiB へ減少し、59.629 MiB の `ApplyChromaPreFilter` allocation stack が
完全に消えました。Gen2 collection は両 run とも 15 回です。交互に実行した 5 組の
40-field pair では default wall/CPU median が 4.475/12.312 秒から
4.433/12.219 秒、20-worker は 3.694/14.531 秒から 3.638/14.531 秒になりました。
3 組の 160-field pair では default が 15.104/41.297 秒から 14.732/40.344 秒へ、
20-worker wall time は 12.179/12.206 秒で実質同等、CPU time は 49.312 秒から
46.094 秒へ低下しました。実行順を反転した 2 組の 400-field pair は
candidate/baseline が 28.039/28.553 秒、baseline/candidate が
28.224/28.308 秒で、candidate peak は 1.474/1.475 GiB でした。記録した luma、
chroma、JSON hash はすべて exact です。

VHS chroma comb/gain pass は line-size の stack workspace 1 つで 2 つの内部 stage を
融合し、3 つの public stage API は変更しません。同一条件の 10-field GC trace で
sampled managed allocation は 2.360 GiB から 2.322 GiB、`Double[]` allocation は
2,197.06 MiB から 2,147.33 MiB へ減少しました。59.629 MiB の `ApplyComb`
allocation stack は消え、final gain-owned 59.629 MiB output は維持されました。Gen2
collection は両 run とも 14 回です。交互に実行した 5 組の 40-field pair では default
wall/CPU median が 4.455/12.250 秒から 4.366/12.125 秒、20-worker は
3.721/15.719 秒から 3.657/14.094 秒へ低下しました。別の 5 組の 160-field
20-worker run は wall/CPU median が 12.180/47.922 秒から 12.064/44.031 秒へ
低下しました。実行順を反転した 2 組の 400-field pair は candidate/baseline が
26.916/27.468 秒、baseline/candidate が 27.398/27.664 秒で、candidate peak は
1.484/1.481 GiB でした。記録した luma、chroma、JSON hash はすべて exact です。
以前の line-history in-place prototype は、160-field wall median が default で
15.20 秒から 15.53 秒、20-worker で 12.45 秒から 12.68 秒へ後退したため、完全に
削除しました。

続く VHS chroma gain-to-U16 pass は public gain/conversion API を変えず、内部 decode に
残っていた gain-owned double field を削除します。final implementation の同一条件
10-field GC trace では sampled managed allocation が 2.320069 GiB から
2.266559 GiB、`Double[]` allocation が 2,147.315 MiB から 2,086.828 MiB へ
減少しました。59.629 MiB の `ApplyAutomaticChromaGainWithComb` allocation stack は
消え、`UInt16[]` allocation は 29.815 MiB のまま、Gen2 collection は 15 回から
14 回へ減少しました。交互に実行した 5 組の 40-field pair では default wall/CPU
median が 4.461/12.781 秒から 4.403/12.047 秒、20-worker は 3.706/14.406 秒から
3.665/12.906 秒へ低下しました。別の 5 組の 160-field 20-worker run は wall/CPU
median が 12.196/46.047 秒から 11.985/45.625 秒へ低下しました。実行順を反転した
2 組の 400-field pair は、candidate/baseline wall 27.566/27.877 秒、CPU
107.531/105.828 秒、次に baseline/candidate wall 28.120/27.263 秒、CPU
105.422/107.594 秒で、candidate peak は 1.355/1.474 GiB でした。長時間 run は
total CPU を多く使う一方で早く終了し、記録した luma、chroma、JSON hash はすべて
exact です。最初の full-field neutral-fill form は 160-field wall median が default で
14.71 秒から 14.76 秒、20-worker で 12.05 秒から 12.26 秒へ後退したため作り直しました。
scalar line-span form も最初の 400-field candidate/baseline が 28.353/27.647 秒だったため
final にはせず、AVX2/SSE4.1 form のみが最終 long-run gate を通過しました。

VSync in-place BA-IIR pass は filtering arithmetic を変えず、各 private chain が ownership
を持つ array を再利用し、envelope blend を reduced final output へ直接書き込みます。
固定した PAL field fixture の isolated median は field あたり 6.610 ms から 5.080 ms
（23.1% 高速）へ、managed allocation は field あたり 15.60 MiB から 8.50 MiB
（45.5% 削減）へ改善しました。同一条件の 10-frame GC trace では sampled allocation が
2.264 GiB から 1.947 GiB（14.0% 削減）、Gen2 collection が 15 回から 11 回へ減少しました。
交互に実行した 5 組の 40-frame pair では default wall/CPU median が
4.455/12.547 秒から 4.319/12.156 秒、20-worker は 3.819/14.094 秒から
3.606/14.625 秒へ変化しました。5 組の 160-frame 20-worker pair では
wall/CPU/peak-working-set median が 12.059 秒/45.406 秒/1.475 GiB から
11.796 秒/45.922 秒/1.058 GiB へ変化しました。2 組の 400-frame pair は
candidate/baseline が 26.776/27.438 秒、baseline/candidate が 27.214/26.785 秒で、
candidate peak は 1.448/1.439 GiB でした。400-frame candidate は CPU を 1.4-5.0%
多く使う一方で 1.6-2.4% 早く完了し、記録した luma、chroma、JSON hash はすべて
exact です。

続く detector-owned VSync workspace pass は、6 個の exact-size analysis array を field
間で再利用します。同じ isolated fixture の median は field あたり 5.080 ms から
4.325 ms（14.9% 高速）へ、warm-call allocation は 8.50 MiB から約 3.8 KiB へ
減少しました。同じ 10-frame trace では sampled allocation が 1.947 GiB から
1.720 GiB、sampled `Double[]` allocation が 1,760.85 MiB から 1,524.33 MiB へ
減少しました。3 組の 160-frame default-worker pair は wall/CPU/peak median が
14.44 秒/40.94 秒/1.03 GiB から 14.21 秒/39.56 秒/0.77 GiB へ変化し、5 組の
20-worker pair は 11.63 秒/45.17 秒/1.19 GiB 対 11.67 秒/44.77 秒/1.21 GiB で
neutral でした。2 組の 400-frame 20-worker pair は 0.8-1.7% 早く完了し、candidate
peak 1.508/1.534 GiB、baseline 1.451/1.404 GiB の bounded range に収まりました。
luma、chroma、JSON hash はすべて exact です。

final field の共有 TBC resampling plan は source position と wow level adjustment を
一度だけ計算し、同じ read-only plan を chroma と luma で使用して、render 完了直後に
bounded buffer を `ArrayPool` へ返します。逆順を含む 2 組の 400-frame
default-worker pair では wall/CPU median が 33.690/97.734 秒から
32.805/93.609 秒へ低下しました（wall 2.6%、CPU 4.2% 減）。2 組の 20-worker pair
は wall 26.713 秒対 26.760 秒で neutral でしたが、CPU median は 106.563 秒から
105.266 秒へ低下し、candidate peak は bounded な 1.411/1.445 GiB でした。
luma、chroma、JSON hash はすべて exact です。

fallback serration-level search は field ごとに一度だけ decimation を行い、1 個の
bounded `ArrayPool` buffer に格納して、順序を保つ 30-step、5-IRE search 全体で同じ
pulse list を再利用します。最後の full-resolution retry、threshold sequence、scalar
comparison、pulse order は変わりません。main `4a67ae9` を baseline とし、同じ local
PAL `.lds` file（`--start_fileloc 620000000 -l 160`）で測定した 2 組の interleaved default-worker
pair は平均 wall/CPU が 13.991/41.492 秒から 13.595/39.773 秒へ低下しました
（2.8%/4.1% 減）。2 組の 20-worker pair は 11.152/48.508 秒から
10.838/47.180 秒へ低下しました（2.8%/2.7% 減）。これらの pair と最後の clean-source
replay を合わせても、candidate peak working set は 1.14 GiB 以下の bounded range に
収まり、全 10 run の luma、chroma、JSON hash は同一でした。160-frame gate を
通過しなかった AVX pulse-state prototype は削除しました。

default linear TBC の source position は output line 単位で一括生成するようになりました。
各 line の 2 つの location value を cache しつつ、sample ごとの元の division、
subtraction、multiplication、addition 順序を維持します。randomized test は生成した
すべての double を以前の scalar interpolation と bit-for-bit で比較します。`c51f059`
を baseline とし、同じ local PAL `.lds` file の 160-frame window で測定した 2 組の
interleaved default-worker pair は平均 wall/CPU が 14.060/40.164 秒から 13.598/40.438 秒へ
変化しました（wall 3.3% 減、CPU は run noise 内で 0.7% 増）。2 組の 20-worker pair
は 10.907/45.039 秒から 10.771/43.414 秒へ変化しました（wall 1.2%、CPU 3.6% 減）。
対応する default trace では sampled `BuildSourcePositions` self time が 711.35 ms から
257.61 ms へ低下しました（63.8% 減）。candidate peak working set は 1.13 GiB 以下に
収まり、全 8 run の luma、chroma、JSON hash は同一でした。

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
dotnet test --solution VHSDecodeDotNet.slnx -c Release --no-build --no-restore
```

現在の正式な Release build は warning 0、error 0 です。xUnit v3 project は
`dotnet test` と Visual Studio Test Explorer の両方で個別に検出できる
**822** tests を公開します。

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
