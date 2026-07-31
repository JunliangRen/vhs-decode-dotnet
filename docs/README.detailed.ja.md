# vhs-decode-dotnet 詳細リファレンス

[プロジェクト概要](../README.ja.md)

[English](README.detailed.md) | [简体中文](README.detailed.zh-CN.md) | **[日本語](README.detailed.ja.md)**

<!-- README_SYNC: 2026-07-31.03 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 11 で再実装するプロジェクトです。現在は release
`v0.4.0`、commit `43155200da87c0d49eb37d8ec09b1372075ee8e4`
を互換性の基準としています。

> [!IMPORTANT]
> この互換移植は現在も開発中です。トップレベルのデコード経路は実装済みで
> 多数のテストがありますが、あらゆる実キャプチャとまれなオプションの組み合わせで
> バイト単位の一致を保証する段階にはまだ達していません。

> [!NOTE]
> `--dsp-backend ipp-fast` はこの .NET port が upstream に対して追加した
> experimental parameter であり、upstream `oyvindln/vhs-decode` v0.4.0 には
> 存在しません。2 つの 400-frame VHS 実 capture を `--threads 5` で測定した
> end-to-end speedup は約 5% でした。NTSC-J は luma、chroma、JSON、`fileLoc`、
> stdout、normalized log がすべて差異 0 でした。PAL も JSON と 800 個すべての
> `fileLoc` は差異 0 でしたが、luma sample の 0.000794%、chroma sample の
> 0.003226%（合計約 0.00201%）が異なり、変更 sample はすべて 800 field 中
> 1 つの深刻に損傷した field に限定されました。互換性重視では `exact` が
> 引き続き default です。

## 目次

- [対象範囲](#対象範囲)
- [現在の状態](#現在の状態)
- [互換性の範囲](#互換性の範囲)
- [上流動作プロファイル](#上流動作プロファイル)
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
| 上流動作プロファイル | 段階的に実装 | `v0.4.0` が引き続き default です。`current` は opt-in で、merged PR 341 の 5 stage、VHS HSync detection、VSync level refinement、NTSC chroma group-delay correction、asymmetric Super-Gaussian final filtering、current color-under burst/ACC/CTI chain を実装しています。 |
| VHS とテープ形式 | 実装済み。まれなキャプチャ差分あり | VHS、S-VHS、Betamax、Video8/Hi8、U-matic、Type C、EIAJ、および対応 PAL/NTSC 形式は release 互換経路を共有します。 |
| CVBS | release 対応システムを実装済み | PAL/NTSC 経路は動作します。まれな vblank とオプション間のケースには実キャプチャ fixture の追加が必要です。 |
| LaserDisc | 実装済み。まれなキャプチャ差分あり | Video、VBI、EFM、analog audio、AC3、RF-TBC、metadata、recovery、PAL/NTSC 経路を接続済みです。 |
| HiFi | 実装済み。実キャプチャ検証がさらに必要 | 型付き v0.4.0 CLI、境界付き並列デコード、後処理、WAV/FLAC 出力、preview、GNU Radio mode を接続済みです。境界付き 4 秒 NTSC Betamax explicit-carrier gate で Python の WAV byte と decoded FLAC PCM に一致します。 |
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

<!-- SECTION: upstream-behavior -->

## 上流動作プロファイル

VHS decode は `--compat-version v0.4.0|current` を受け付けます。

| Profile | Default | 固定した upstream source | 現在有効な動作 |
| --- | --- | --- | --- |
| `v0.4.0` | はい | release v0.4.0、commit `43155200da87c0d49eb37d8ec09b1372075ee8e4` | 既存の release 互換 decode path。 |
| `current` | いいえ | merged PR 341、commit `2f21e8ed6018b14561396cc95f1f6828054470b8` | exact-first VHS HSync candidate extraction、MAD rejection、multi-grid lock、level calibration、subpixel pulse synthesis、robust VSync level refinement、NTSC chroma group-delay correction、asymmetric zero-phase Super-Gaussian final filtering、fitted burst frequency/DC tracking、phase-compensated upconversion、current ACC/noise estimation、four-pass CTI。 |

`current` は段階導入される opt-in profile です。color-under format では
PR 341 の official CTI default、`--cti_mix 1` と `--cti_width 2` を
使用します。`--cti_mix 0` で CTI を無効にできます。upstream と同様に
width 0 は無効化ではなく、minimum 4-sample sweep radius と zero noise
threshold を維持します。embedded baseline catalog はこの stage を明示します。

HSync、VSync-level、group-delay stage は original upstream function と直接比較し、
deterministic synthetic fixture で検証します。chroma stage は burst/track-phase
analysis を shift せず、final 16-tap chroma resampling の source coordinate にだけ
固定した float64 shift を加えます。test は Numba output bit、zero-shift legacy
path、実際の parallel threshold、upstream の全状態で正になる phase-truthiness
behavior をカバーします。Super-Gaussian stage は final color-under SOS filter
を置き換えます。deterministic SciPy 1.18 oracle は symmetric reflection
padding、`next_fast_len`、DUCC-compatible float32 mixed-radix rFFT/irFFT、
float64 response、complex64 multiplication、および production NTSC、PAL、
PAL-M/NLINE、MESECAM field length をカバーします。current chroma stage は
pinned four-parameter Gauss-Newton burst fit、per-line frequency/DC phase
compensation、MAD-clamped interpolated gain、sync-tip noise estimation、CTI
を追加します。component test は PR 341 の Numba output と比較し、BLAS の
影響を受ける fit intermediate には exact downstream float32 gate と厳密な
float64 tolerance を使い、portable float64 bit identity は主張しません。
HSync は upstream PAL fixture gate も維持します。固定した VSync behavior は
upstream の back-porch assignment quirk も意図的に保持し、修正には別の
profile change が必要です。別の end-to-end gate により、default `v0.4.0`
profile は `--threads 0`、default、`--threads 20` のすべてで同一に保たれます。

### 1,000-frame release gate

同じ private local NTSC `.ldf` capture を、両 behavior profile で Exact backend
と default 5 worker を使って 2 回ずつ decode しました。

| Profile | Python oracle | この移植、2 run | Peak working set | Result |
| --- | ---: | ---: | ---: | --- |
| default `v0.4.0` | 499.80 s | 95.37 / 93.17 s | 1.14 / 1.11 GiB | 6 comparison が完全一致し deterministic |
| `current` | 459.34 s | 141.85 / 143.13 s | 923 / 942 MiB | 6 comparison が完全一致し deterministic |

各 run で luma TBC、chroma TBC、JSON/`fileLoc`、stdout の SHA-256 が一致し、
stderr は elapsed-time line だけを除いた後、log は timestamp normalization
後に一致しました。すべての run が中断せず 1,000 frame を完了しました。
default oracle は upstream v0.4.0 `--threads 0` です。pinned PR 341 の
`current` source は 438 frame 後に自身の float-slice `TypeError` に到達するため、
残りは 6 個の slice bound だけを `int` に変換し、ほかは source-identical な
extended oracle を使用しました。この release gate では IPP を除外しています。

<!-- SECTION: performance -->

## パフォーマンス

性能改善は実装の一部ですが、決定的な出力と release 互換性を最優先します。

DSP backend は `--dsp-backend exact|ipp-fast` で明示的に選択します。この
parameter はこの .NET port の experimental extension であり、upstream
`oyvindln/vhs-decode` v0.4.0 CLI には含まれません。`exact` が default で、
既存の managed 互換経路を維持し、Intel IPP の probe や load は行いません。
`ipp-fast` は opt-in Windows x64 backend です。Intel CPU が official support
target であり、compatible non-Intel x64 CPU はこの project の best-effort
experimental path です。IPP が返す feature mask に SSE4.2 が含まれる場合のみ、
正の non-Intel vendor warning を受け入れます。静的 link 済みの
`vhsdecode_ipp.dll` を load し、IPP version と選択された ISA を表示します。
bridge、ABI、CPU が利用できない場合は明確に失敗し、`exact` へ暗黙に fallback
しません。v1 で IPP を使うのは VHS real-RF FFT stage のみです。CVBS、LD、HiFi
は未対応の `ipp-fast` を明示的に拒否し、Exact kernel を実行した結果を誤って
benchmark として扱うことはありません。IIR/SOS と HiFi/LD の高速化は段階的な
今後の作業であり、現在の active path ではありません。

`ipp-fast` は数値的に近い performance mode であり、byte-compatible mode では
ありません。FFT と vector math の評価差は floating-point bit を変え、threshold
decision、metadata、recovery、log、output file に影響する可能性があります。
release-compatible な hash または動作が必要な場合は `exact` を使用してください。

各 real capture で 1 warm-up pair の後、interleaved/reverse-order A/B を 5 pair、
400 frame、`--threads 5` で測定しました：

| Capture | end-to-end wall-time median gain | Compatibility result |
| --- | ---: | --- |
| Local NTSC-J VHS capture | 4.73% | Luma、chroma、JSON、800 個すべての `fileLoc`、stdout、normalized log が一致しました。 |
| Local PAL VHS capture | 5.00% | JSON と 800 個すべての `fileLoc` が一致しました。Luma sample の 0.000794%、chroma sample の 0.003226%（合計約 0.00201%）が異なり、すべて 1 つの深刻に損傷した field 内でした。normalized log も異なりました。 |

これらは tested capture と machine の結果であり、一般的な speed/compatibility
guarantee ではありません。PAL の zero-output-difference 実験には Exact inverse
FFT への fallback が必要で、paired median gain は -1.05% まで低下したため、
その fallback は有効化していません。

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
  1,048,576 sample 以下のとき、最近使用した 2 個の exact-size 6-array analysis
  workspace を保持します（上限では 1 entry 約 48 MiB、合計約 96 MiB）。exact-shape
  hit は entry を昇格させ、3 番目の shape は最も古い entry を破棄します。上限を
  超える input には保持しない temporary workspace を使います。
- VSync serration measurement は candidate window を read-only span で参照し、
  `Enumerable.Min`-compatible な float64 scan を使うため、full-window copy を 1 つ
  省きます。median scratch の ownership と NaN/signed-zero の bit semantics は不変です。
- pulse detection は threshold transition のない sample 区間を飛ばす部分だけに AVX
  compare を使います。各 state transition、pulse-length validation、result append は
  引き続き順序付き scalar code で行い、AVX 非対応 CPU は元の scalar path を使います。
- worker 有効時、VHS field decode は luma TBC render と chroma field decode を
  並行実行します。同時に存在する chroma task は最大 1 つで、次の field へ進む前に
  calling thread 上で順序どおり state を commit します。Exact mode の tape-envelope
  dropout detection も同じ bounded field overlap を共有し、read-only task は最大 1 つで、
  field が return する前に必ず join されます。
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
- stateful VHS CLI sequence path は、luma rendering と chroma decode が重なるため、
  exact-length の luma field workspace と別の chroma field workspace を保持します。
  public `Decode()` と retained `DecodeFields()` の result は独立した
  `ChromaBurstSamples` を所有し続け、internal non-retaining CLI path だけが省略します。
  direct UInt16 path は double field を必要とせず、public resampling API も
  independent-output ownership を維持します。
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

dropout overlap の strict Exact-mode benchmark は、同じ synthetic packed PAL RF
fixture で 160 frame を要求し、各 mode について 1 組の warm-up 後に 4 組の
alternating Release A/B を実行しました。変更前の main と比べ、end-to-end wall-time
median は `--threads 5` で 2.8%、`--threads 20` で 4.5% 短縮し、candidate は全 8 組で
高速でした。Luma TBC、chroma TBC、JSON、stdout、normalized stderr/log、および
順序付き 320 個すべての `fileLoc` は一致しました。変更のない serial path の
`--threads 0` も同じ gate を通過しました。default-worker の 200-frame sustained run
では後半の slowdown がなく、field ごとの dropout task は最大 1 つで、sampled memory
は最後の quarter で再び低下しました。

続く owned-buffer VHS chroma SOS pass は、現在の stage が `double[]` を排他的に
所有する場合に限り、同じ float32 forward/backward filter を in-place で実行します。
conversion point、odd-extension padding、section order、public ownership は変更して
いません。同条件の 40-frame trace では sampled managed allocation が 3.143 から
2.865 GiB（8.9%）、`Double[]` allocation が 2,637.6 から 2,351.7 MiB（10.8%）へ
減少し、Gen2 collection は 18 回から 17 回になりました。160-frame の alternating
4 pair では wall-time median が `--threads 5` で 2.2%、`--threads 20` で 3.5%
短縮し、serial 2 pair も 1.0% 短縮しました。candidate は全 10 pair で高速でした。
Luma TBC、chroma TBC、JSON、順序付き `fileLoc`、stdout、normalized stderr/log、
cross-thread hash はすべて一致しました。monitor 付き 200-frame default-worker run
にも後半の slowdown や単調な memory growth はありませんでした。

VSync serration detector は、最大 2 個の exact-shape workspace のそれぞれに固定
60-line の median scratch buffer を保持するようになりました。public static
measurement API は独立 allocation の result path を維持します。同条件の 40-frame
default-5 `gc-verbose` trace では、この median に由来する
`ReadOnlySpan<double>.ToArray()` sample 213 回（77.234 MiB）がすべて消え、
total sampled allocation は 2.863362 GiB から 2.790004 GiB（2.6%）へ減少し、
Gen2 collection は 16 回から 15 回になりました。160-frame の observed median は
default-5 で 0.2%、20 worker で 1.5%、clean serial retry で 1.4% 改善しましたが、
20-worker pair の勝敗は混在したため、ここで確認した結果は固定 throughput 率ではなく
allocation reduction です。`--threads 0`、default-5、`--threads 20` の luma、
chroma、JSON、順序付き 320 個すべての `fileLoc`、stdout、normalized stderr/log、
cross-thread hash は完全一致しました。default-worker の 200-frame matched check は
candidate 15.303 s、main 15.385 s で、candidate の前半/後半は
77.22/70.87 ms per frame、peak は 0.993 GiB でした。retained median storage は
detector ごとに 60-line buffer 2 個までに制限されます。

### 最新の 5-path thread matrix

最新の overview は、同じ private local 40 MHz NTSC `BETAMAX_HIFI` `.lds`
sample で Python v0.4.0、Exact v0.4.0、Exact `current`、IPP-fast v0.4.0、
IPP-fast `current` を比較します。filename は公開しません。各 cell は 3 回の
interleaved Release run の wall-time median、同じ行の Python に対する speedup、
wall-time reduction の順です。

| CLI mode（workers） | Python v0.4.0 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: |
| default（5） | 16.983 s | 6.416 s / 2.647x / 62.2% | 7.678 s / 2.212x / 54.8% | 5.667 s / 2.997x / 66.6% | 7.184 s / 2.364x / 57.7% |
| `--threads 1` | 21.263 s | 20.547 s / 1.035x / 3.4% | 22.961 s / 0.926x / 8.0% 遅い | 20.129 s / 1.056x / 5.3% | 22.320 s / 0.953x / 5.0% 遅い |
| `--threads 5` | 16.880 s | 6.173 s / 2.735x / 63.4% | 7.802 s / 2.164x / 53.8% | 5.802 s / 2.909x / 65.6% | 7.186 s / 2.349x / 57.4% |
| `--threads 10` | 17.612 s | 4.880 s / 3.609x / 72.3% | 6.118 s / 2.879x / 65.3% | 4.636 s / 3.799x / 73.7% | 5.744 s / 3.066x / 67.4% |
| `--threads 20` | 18.330 s | 3.997 s / 4.586x / 78.2% | 4.767 s / 3.845x / 74.0% | 3.966 s / 4.622x / 78.4% | 4.919 s / 3.726x / 73.2% |

benchmark host は Intel Core Ultra 7 265K（20 logical processor）、
Windows 11 25H2 build 26220.8925、.NET SDK/runtime
`11.0.100-preview.6.26359.118` です。Python 自体は変更されていないため、
Python 列は以前の fixed matrix median を保持し、4 つの .NET 列は 60 回の
interleaved run で再測定しました。candidate executable は `d0508f9` の
production code change を含み、single-file `decode.exe` SHA-256 は
`98ADB0ED3F5EF086AC2A189F302101C751C68F0390123817EAF5452DD83BE7A1`
でした。fixed 40-frame matrix には startup cost と run ごとの spread が含まれ、
下記の反対順序 1,000-frame pair が stable whole-pipeline A/B を示します。
Python 3.14.0 は NumPy 2.4.6、SciPy 1.18.0、Numba 0.66.0、
python-soxr 1.1.0 を使用しました。共通引数は次のとおりです。

```text
--system ntsc --NTSCJ --detect_chroma_track_phase --ire0_adjust
--tape_format BETAMAX_HIFI --frequency 40 --sub_deemphasis
--start 100 --length 40 --overwrite
```

両実装の default は **5 workers** です。独立した 3 回の Python
`--threads 0` control は median 30.253 s で、互いに完全一致しました。保持した
すべての Exact v0.4.0 run は luma、chroma、JSON、stdout、normalized
stderr/log、順序付き 80 個すべての `fileLoc` でこの oracle と一致しました。
再測定した Exact-current と IPP-current の各列は 15 run 全体でそれぞれ 1 つの
deterministic hash set を生成しました。別の candidate gate でも
`--threads 0`、default、`--threads 20` の Exact v0.4.0 が Python oracle と
完全一致しました。この sample では IPP-fast の luma、chroma、JSON、
`fileLoc` は対応する Exact profile と一致しましたが、明示的な IPP diagnostic
により normalized stderr/log は異なります。この sample 固有の結果は
byte-compatibility の保証ではありません。

今回の Python nonzero/default 15 run は、この sample では同じ
luma/chroma/JSON set を保ちましたが、normalized log hash は 7 種類でした。
以前の fixed matrix では worker count 間の Python artifact hash 不安定性も確認
されています。したがって nonzero-thread Python 行は throughput 比較専用で、
strict oracle は upstream `g4315520 --threads 0` のままです。

以前の Exact-only thread matrix は Intel Core Ultra 7 265K（20 logical processor）、
Windows 11 build 26220、.NET SDK/runtime `11.0.100-preview.6.26359.118`、
Python v0.4.0 commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`（表示は `g4315520`）で実行しました。
分離した Python 環境は NumPy 2.4.6、SciPy 1.18.0、Numba 0.66.0、
python-soxr 1.1.0 を使用しています。各値は 3 回の交互 Release run の median です。

| CLI mode | Effective worker | この移植 | Python | Speedup | Wall-time reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| default | 5 | 3.861 s | 12.021 s | 3.114x | 67.9% |
| `--threads 1` | 1 | 8.052 s | 13.700 s | 1.701x | 41.2% |
| `--threads 5` | 5 | 3.964 s | 11.924 s | 3.008x | 66.8% |
| `--threads 10` | 10 | 3.379 s | 12.344 s | 3.653x | 72.6% |
| `--threads 20` | 20 | 3.152 s | 12.649 s | 4.013x | 75.1% |

default は Release 4.0 CLI semantics に合わせて最終的に **5 worker** のままです。
この 20 logical processor fixture では、明示的な 20-worker mode が最速でした。
matrix は同じ local PAL `.lds` file と `--system pal
--detect_chroma_track_phase --ire0_adjust --tape_format VHS --frequency 40
--start_fileloc 620000000 -l 40 --overwrite` に各行の thread option を加えています。

この移植の 15 run は、すべての worker 数で同じ luma TBC、chroma TBC、JSON hash
set を生成しました。追加した 3 回の Python `--threads 0` control は互いに一致し、
この移植の全 run とも完全一致しました。上流 Python の default/nonzero thread mode は
byte-exact baseline として安定せず、15 回の matrix run は 14 種類の luma/chroma
pair と 10 種類の JSON hash を生成し、serial luma/chroma reference と一致したのは
2 run だけでした。そのため matrix は observed throughput のみを比較し、hash、
metadata、console output、timestamp-normalized log の strict compatibility baseline は
Python `--threads 0` です。

この 40-frame fixture の compatibility baseline は Python v0.4.0 `g4315520` の
`--threads 0` output です。

| Baseline artifact | SHA-256 |
| --- | --- |
| Luma TBC | `6F4DD4ABE1D05A5030846DEA550758A79E7737D680A2B06024CFA06C83BF5185` |
| Chroma TBC | `BB91833B7575C003AEC9853ED75D4CFF82C1125690B226E0A79D539B6594169C` |
| JSON | `2F4C27FB9F3A9F4E8467BB49E89D660132DA5A2DCCC99AE897A072B1DD099EE5` |

より長い exact-output checkpoint は Intel Core Ultra 7 265K（20 logical
processor）、Windows 11 build 26220、.NET SDK/runtime
`11.0.100-preview.6.26359.118` で実行しました。

| PAL VHS、1,000 frame / 2,000 field | Wall time | CPU time | Peak working set | Python 比 speedup |
| --- | ---: | ---: | ---: | ---: |
| Python v0.4.0（`g4315520`、`--threads 0`） | 405.63 s | 402.88 s | 0.74 GiB | 1.00x |
| この移植、default（5 worker） | 76.78 s | 215.66 s | 1.11 GiB | 5.28x |
| この移植、`--threads 20` | 60.58 s | 244.95 s | 1.45 GiB | 6.70x |

3 run はすべて同じ local PAL `.lds` file と
`--system pal --detect_chroma_track_phase --ire0_adjust --tape_format VHS
--frequency 40 --start_fileloc 620000000 -l 1000 --overwrite` に各行の thread option
を加えて実行しました。この移植の両 mode は Python `--threads 0` と luma、chroma、
JSON、stdout の SHA-256、`fileLoc` で整列した全 metadata、timestamp を除いた 5,132
log line のすべてで完全一致しました。最初と最後の `fileLoc` は全 run で
`620421120` と `2219612160` です。

長時間 run でも進行に伴う低速化はありませんでした。default mode の前半/後半
500 frame は 38.03 秒/37.72 秒、`--threads 20` は 30.42 秒/29.37 秒で、両 mode の
peak working set は run 全体で bounded のままでした。

別の native-container checkpoint では、同じ local NTSC-J `.ldf` file の大きな
nonzero position から 1,000 frame / 2,000 field を decode しました。

| NTSC-J VHS mode | Wall time | Python 比 speedup |
| --- | ---: | ---: |
| Python v0.4.0（`g4315520`、`--threads 0`） | 397.158 s | 1.00x |
| この移植、`--threads 0` | 175.531 s | 2.26x |
| この移植、default（5 worker） | 80.761 s | 4.92x |
| この移植、`--threads 20` | 58.527 s | 6.79x |

この移植の 3 mode はすべて、strict Python baseline と luma、chroma、JSON、stdout
の SHA-256、順序付き 2,000 `fileLoc`、timestamp を除いた 3,473 log line の全項目で
完全一致しました。この checkpoint は、大きな seek 後の native `.ldf` loader が
upstream PyAV の first-frame PTS behavior を保持することも検証します。

AVX pulse-transition pass の fresh strict recheck でも、同じ local NTSC-J `.ldf`
capture を使いました。現在の Python v0.4.0 `--threads 0` は 390.077 秒、この移植の
`--threads 20` は 57.609 秒（6.77x、wall time 85.2% 減）でした。luma、chroma、
JSON、stdout、順序付き 2,000 `fileLoc`、timestamp-normalized 3,413 log line はすべて
exact で、この移植の peak working set は 1.323 GiB でした。Python の直接再実行と
clean merged main はどちらも現在の 3,413-line log を生成したため、上記 3,473-line
record は historical checkpoint として残しています。Python launcher は処理を child
process に委譲するため、Python の CPU/memory 値は掲載しません。

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

これは特定 fixture の値であり、一般的な benchmark ではありません。同一 binary の
`--threads 20` による 3-run 160-frame NTSC-J scalar/AVX A/B では、wall median が
12.029/11.854 秒（1.5% 高速）、CPU median が 46.984/46.250 秒（1.6% 低下）でした。
luma、chroma、JSON、stdout、normalized-log hash、`fileLoc` range はすべて一致しました。
以下の 40-frame tuning A/B は .NET SDK/runtime `11.0.100-preview.6.26359.118`、`--threads 20`、
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

VSync analysis は、通常の field length が交互に現れるたびに唯一の workspace を置き換える
代わりに、2-entry の exact-shape LRU を保持します。array type、実際に書き込む range、
padding、filter arithmetic、detector-state の commit 順序は変更せず、各 entry は従来の
1,048,576-sample 上限を維持します。
同じ実 PAL 10-frame GC trace で sampled managed allocation は 1.633 GiB から
1.463 GiB（10.4% 減）、sampled `Double[]` は 1,464.83 MiB から 1,295.74 MiB
（11.5% 減）、`AnalysisWorkspace` allocation は 205.69 MiB から 34.28 MiB
（83.3% 減）になりました。5 組の interleaved 160-frame `--threads 20` pair では wall
median が 10.188 秒から 10.029 秒（1.6% 高速）、mean が 10.217 秒から 10.030 秒
（1.8% 高速）になり、全 pair が 0.9-3.7% 高速でした。CPU median は 2.1% 増えましたが、
peak-working-set median は 1.375 GiB から 0.936 GiB へ低下しました。400-frame gate は
wall/CPU/peak を 24.032 秒/101.969 秒/1.455 GiB から
23.722 秒/97.828 秒/0.958 GiB へ改善しました。candidate の quarter working-set median は
0.705/0.752/0.776/0.654 GiB で、進行に伴う増加はありません。PAL の serial/default/20/64-worker
run は 6 種類の output/normalized diagnostic hash が一致し、既存の NTSC-J large-seek
1,000-frame gate も Python v0.4.0 `--threads 0` と luma、chroma、JSON、stdout、
normalized stderr/log、2,000 個すべての `fileLoc`、52 件の startup recovery diagnostic が
完全一致しました。

一般的な order-3、order-4、order-5 BA-IIR path は fixed-order scalar kernel を使うように
なりました。coefficient type、sample ごとの expression、arithmetic order、state update、
public buffer ownership は変更せず、その他の filter shape は generic implementation を
使い続けます。isolated kernel median は managed allocation を変えずに 1.77-1.88 倍へ
改善しました。同じ local PAL RF capture の 5 組の interleaved 160-frame
`--threads 20` pair では wall median が 14.116 秒から 12.897 秒（8.6% 減）、CPU median が
53.156 秒から 49.813 秒（6.3% 減）となり、すべての candidate run が高速でした。全 10
run の luma、chroma、JSON、stdout、normalized stderr、timestamp-normalized log hash は
完全一致しました。順序を反転した 2 組の 400-frame pair は 7.0-7.7% 高速で、peak working
set は 1.551 GiB 以下に収まり、同じ 6 artifact が一致しました。candidate の
serial/default-5/20/64-worker run も 6 artifact すべてで exact です。同じ local NTSC-J RF
capture の新しい 160-frame large-seek gate でも、main baseline の 20 worker と candidate の
serial/default-5/20/64 worker の間で 6 artifact がすべて一致しました。PAL と NTSC-J は
同じ 3 種類の specialized filter order を構築し、独立した scalar xUnit oracle が全 path を
検証し、848 test はすべて pass しています。上記の既存 1,000-frame NTSC-J gate に変更は
ありません。

native `.ldf` PCM16 loader は、2 MiB の rewind history を 1 個の fixed circular buffer に
保持し、fresh byte を requested block へ直接読み込み、forward-discard buffer を再利用する
ようになりました。FFmpeg launch、seek、padding、byte order、rewind boundary、partial EOF
後の position advancement、sample conversion の意味は変えていません。同じ private local
NTSC `.ldf` capture に対する machine-idle の controlled 400-frame `--threads 20` pair では、
baseline/candidate の wall time が 26.242 秒から 24.741 秒（5.7% 減）、CPU time が
145.516 秒から 136.484 秒（6.2% 減）、peak working set が 1,204.5 MiB から
1,190.6 MiB になりました。luma、chroma、JSON、stdout、normalized stderr、
timestamp-normalized log はすべて一致しました。candidate は default `v0.4.0` と opt-in
`current` の両 profile でも中断せず 1,000 frame / 2,000 field を完了し、同じ 6 comparison
すべてで保存済みの各 Python oracle と完全一致しました。IPP は gate から除外しています。

RF nonlinear/sub-deemphasis は、`RfDemodulator` ごとに exact-key の read-only high-pass
response を 1 個ずつ保持するようになりました。key は block length と immutable high-pass
parameter で構成され、miss 時は lock 内で唯一の entry を置き換えます。このため concurrent
block は完成済み response を共有し、任意の block shape が蓄積しません。同じ private
local 40 MHz NTSC `BETAMAX_HIFI` `.lds` capture で、baseline `846ad28` に対する 5 組の
interleaved/reversed 160-frame `--threads 20` pair は、wall median を 16.294 秒から
16.220 秒（0.5% 減）、mean を 16.603 秒から 16.257 秒（2.1% 減）へ移し、candidate は
5 組中 4 組で高速でした。CPU median は 116.641 秒から 114.234 秒（2.1% 減）、mean は
117.633 秒から 113.417 秒（3.6% 減）となり、全 5 組で candidate が勝ちました。peak
working-set median は 1.868 GiB から 1.753 GiB に下がりましたが、個々の peak には
依然として noise があります。

同じ fixture の single serial/default-worker check は、wall/CPU time をそれぞれ
94.518/96.641 秒から 83.767/86.547 秒、26.180/108.828 秒から 24.063/99.781 秒へ
減らしました。これは single-pair observation であり、一般的な percentage では
ありません。10 回の 20-worker run と serial/default candidate は、luma、chroma、
JSON、stdout、normalized stderr、timestamp-normalized log の同じ 1 set に一致しました。
別の opt-in `current` pair も profile 内の同じ 6 surface で一致し、CPU time は
122.750 秒から 115.141 秒へ低下しました。2 回の 400-frame candidate run は
deterministic で、計測 run は 37.012 秒、peak 1.949 GiB、quarter working-set median
0.879/1.208/1.266/1.187 GiB となり、終了側で progressive growth はありませんでした。

opt-in `current` VHS HSync boxcar は、大きな独立 output range を最大 4 worker に分割し、
各 sample の元の left-to-right float64 multiply-add order を維持するようになりました。
同じ private local 40 MHz NTSC `BETAMAX_HIFI` capture で、反転順序の 160-frame
default-worker pair 2 組は 0.5% と 0.9% 高速で、平均は 26.260 秒から 26.075 秒に
なりました。idle capacity が使われたため、candidate CPU time は平均 102.290 秒から
104.045 秒に増えました。`--threads 20` pair 2 組は 10.1% と 5.6% 高速で、平均
17.655 秒から 16.255 秒、CPU 平均は 120.745 秒から 114.130 秒になりました。
candidate working set は 1.344 GiB 以下に収まりました。`--threads 0`、`1`、
default 5、`20` を含む 16 short run と 8 long run で、luma、chroma、JSON、stdout、
normalized stderr、timestamp-normalized log はすべて exact です。

opt-in `current` の Super-Gaussian float32 real-FFT path は、新しく生成した
`Complex32[]` の ownership を large multipass transform へ渡し、field ごとの
冗長な whole-buffer clone を 3 回削減するようになりました。input-preserving API、
FFT plan、float32 conversion point、packet layout、arithmetic order は変更して
いません。同じ private local 40 MHz NTSC `BETAMAX_HIFI` `.lds` sample で、4 組の
interleaved 160-frame `--threads 20` pair は candidate 16.30 秒、baseline
16.52 秒を平均し、pair gain は 2.85%、-0.63%、0.89%、2.38%、median は
1.64% でした。candidate-first の single 400-frame pair は candidate/baseline
35.84/39.54 秒、CPU 260.45/269.59 秒でした。この order-sensitive な single
observation は一般的な percentage ではありません。candidate quarter working-set
peak は 1.459/1.231/0.820/1.422 GiB で、progressive growth はありませんでした。
両 gate は luma、chroma、JSON、stdout、normalized stderr、
timestamp-normalized log が一致し、320/800 個の ordered `fileLoc` もすべて一致
しました。`current` は `--threads 0`、`1`、default 5、`20` で deterministic で、
別の `v0.4.0` profile regression も同じ 6 surface に一致しました。

high worker count の RF prefetch は、従来の bounded lookahead 計算を維持したまま、
最大 12 個の active block worker を許可するようになりました。lookahead は
`min(effectiveWorkers + min(effectiveWorkers, 8), 32)` で計算され、
`effectiveWorkers = min(requestedThreads, logicalProcessorCount)` です。20 logical
processor の benchmark host では、`--threads 20` の prefetch slot は 28 のままで、
cache は拡大しません。同じ private local 40 MHz NTSC `BETAMAX_HIFI` sample で、
4 組の interleaved 160-frame
`current --threads 20` pair はすべて candidate が高速でした。平均 wall time は
16.401 秒から 15.650 秒（4.57% 減）、paired median gain は 3.47%、平均 CPU time は
117.664 秒から 116.902 秒（0.65% 減）でした。反転順序の 400-frame pair 2 組は
0.63% と 3.11% 高速で、baseline/candidate 平均は 37.053/36.362 秒でした。
effective occupancy が 7.03 core から 7.78 core に上がったため、平均 CPU time は
260.422 秒から 283.047 秒へ増加しました。candidate peak は 1.542-1.572 GiB に
収まり、quarter sample は非単調で、decode progress に伴う増加はありません。
luma、chroma、JSON、stdout、normalized stderr、timestamp-normalized log、
320/800 個の ordered `fileLoc` はすべて一致しました。candidate の `current` output は
`--threads 0`、`1`、default 5、`20` で deterministic で、別の 160-frame
`v0.4.0` baseline regression も同じ 6 surface と 320 個すべての `fileLoc` に
一致しました。

VHS full-spectrum analytic path は、RF block ごとに同じ配列を再構築せず、
read-only Hilbert multiplier を FFT length ごとに cache するようになりました。
multiplier の値、float64 type、consumer、element evaluation order は変更して
いません。同じ private local 40 MHz NTSC `BETAMAX_HIFI` sample の独立した
80-frame `gc-verbose` trace では、sampled managed allocation が
43.073 GiB から 42.195 GiB（0.878 GiB、2.04% 減）へ、Gen2 collection が
134 回から 126 回へ減りました。6 組の 40-frame gate は noise が大きく、
4 組で baseline が高速だったため、short-run gain は主張しません。4 組の
interleaved 160-frame `current --threads 20` pair はすべて candidate が高速で、
paired median wall-time gain は 4.85%、average gain は 5.24%、average CPU time
は 3.59% 減り、median peak working set は 1.520 GiB から 1.479 GiB へ
下がりました。反転順序の 400-frame pair 2 組は 3.82% と 3.80% 高速で、
average CPU time は 1.50% 減り、median peak working set は 1.555 GiB から
1.502 GiB へ下がりました。すべての A/B gate で luma、chroma、JSON、stdout、
normalized stderr、timestamp-normalized log、すべての ordered `fileLoc` が
一致しました。candidate の `current` output は `--threads 0`、`1`、default 5、
`20` でも deterministic で、別の 160-frame `v0.4.0` regression も同じ surface
に一致しました。

2 か所の VHS full-spectrum `ForwardReal` は、RF block lifetime のその時点で
未使用だった worker-owned の full-length array へ書き込むようになりました。
FFT plan、float64 conversion、element evaluation order、output ownership は
変更していません。同じ private local 40 MHz NTSC `BETAMAX_HIFI` sample の
matched 80-frame `gc-verbose` trace では、sampled managed allocation が
42.1717 GiB から 35.1848 GiB へ減りました（6.9869 GiB、16.57% 減）。
Gen2 collection は 116 回と 115 回でした。6 組の 40-frame pair は noise が
大きく、candidate は 3 組のみ勝ち、paired median throughput は 1.63% 低かった
ため、short-run gain は主張しません。4 組の interleaved 160-frame
`current --threads 20` pair はすべて candidate が高速で、paired median
throughput gain は 2.99%、average gain は 2.57%、average CPU time は
2.15% 減り、median peak working set は 1.510 GiB から 1.484 GiB へ
下がりました。反転順序の 400-frame pair 2 組は run order の影響が強く、
-5.20% と +8.94% でした。両 order を balanced aggregate にすると throughput は
1.37% 高く、average CPU time は 2.95% 減り、median peak working set は
1.550 GiB から 1.529 GiB へ下がりました。すべての A/B gate で luma、
chroma、JSON、stdout、normalized stderr、timestamp-normalized log、すべての
ordered `fileLoc` が一致しました。candidate の `current` output は
`--threads 0`、`1`、default 5、`20` で deterministic で、別の 160-frame
`v0.4.0` regression も同じ surface に一致しました。

VHS complex high-boost path は、互いに重ならない phase で worker-owned の
full-length analytic FFT buffer 2 個を再利用し、analytic signal を構築するたびに
spectrum copy と inverse output を割り当てないようになりました。FFT plan、
float64 arithmetic、expression order、padding、returned-output ownership は
変更していません。同じ private local 40 MHz NTSC `BETAMAX_HIFI` sample の
matched 80-frame `gc-verbose` trace では、sampled managed allocation が
35.1777 GiB から 28.1943 GiB へ減りました（6.9834 GiB、19.85% 減）。
`Complex[]` allocation は 17,135.251 MiB から 9,974.767 MiB へ 41.79%
減り、Gen2 collection は 108 回から 88 回へ減りました。6 組の 40-frame pair
では candidate が 5 組で高速となり、paired throughput gain は median 1.67%、
average 1.75% でした。4 組の interleaved 160-frame
`current --threads 20` pair では 3 組で高速となり、paired throughput gain は
median 3.06%、average 2.73%、balanced aggregate 2.71% でした。aggregate
CPU time は 2.16% 減り、median peak working set は 1.491 GiB から
1.457 GiB へ下がりました。反転順序の 400-frame pair 2 組はいずれも candidate
が高速で、1.96% と 1.86% でした。balanced aggregate throughput は 1.91%
高く、aggregate CPU time は 3.19% 減り、median peak working set は
1.529 GiB から 1.445 GiB へ下がりました。すべての A/B gate で luma、
chroma、JSON、stdout、normalized stderr、timestamp-normalized log、すべての
ordered `fileLoc` が一致しました。candidate の `current` output は
`--threads 0`、`1`、default 5、`20` で各 2 回とも同一で、別の 160-frame
`v0.4.0` regression も同じ surface に一致しました。

RF deemphasis は最終的な sample ごとの減算を decoder-owned video array へ
書き込むようになり、public helper は引き続き独立した array を返します。VHS RF
high-band scaling も、以前の sample が不要になった後に filter output を再利用します。
data type、FFT/filter work、sample ごとの expression order、public ownership は
変更していません。同じ private local 40 MHz NTSC `BETAMAX_HIFI` sample の
matched 80-frame `gc-verbose` trace では、sampled managed allocation が
28.183423 GiB から 26.445196 GiB へ減りました（1.738227 GiB、6.17% 減）。
`Double[]` allocation は 14,999.178 MiB から 13,209.475 MiB へ 11.93% 減り、
Gen2 collection は 86 回から 66 回へ減りました。4 組の 40-frame pair は noise が
大きく、candidate は 2 組で高速でしたが、paired median throughput は -1.49%、
balanced aggregate は -3.82% だったため、short-run gain は主張しません。
4 組の 160-frame pair も 2 組ずつの勝敗で、median throughput は +0.15%、
balanced aggregate は +0.82%、aggregate CPU time は 0.27% 増でした。
反転順序の 400-frame pair 2 組は -0.77% と +1.89% で、balanced throughput は
0.54% 高く、aggregate CPU time は 0.32% 減りました。sampled peak working set は
改善しなかったため、resident-memory reduction は主張しません。すべての A/B gate で
luma、chroma、JSON、stdout、normalized stderr、timestamp-normalized log、
すべての ordered `fileLoc` が一致しました。candidate の `current` output は
`--threads 0`、`1`、default 5、`20` で各 2 回とも同一で、別の 160-frame
`v0.4.0` regression も同じ surface に一致しました。

complex VHS high-boost path は、以前の phase が終了した後、boost 前の analytic-real
と filtered-real の intermediate を既存の worker-owned workspace array 3 個へ
格納するようになりました。同じ out-of-place
`PocketFftComplex.Inverse(input, output)` implementation を使用しており、data
type、FFT arithmetic、expression order、returned-block ownership、workspace
pool cap は変更していません。同じ private local 40 MHz NTSC `BETAMAX_HIFI`
sample の matched 80-frame `gc-verbose` trace では、sampled managed allocation
が 26.446234 GiB から 22.918228 GiB へ減りました（3.528006 GiB、13.34% 減）。
`Double[]` allocation は 1,791.025 MiB（13.56%）、`Complex[]` allocation は
1,793.958 MiB（17.98%）減り、Gen2 collection は 82 回から 80 回へ減りました。
1 組の 40-frame smoke pair では baseline が 9.16% 高速だったため、short-run gain
は主張しません。4 組の interleaved 160-frame pair では candidate が 3 組で
高速となり、paired median throughput は 2.44%、balanced aggregate は 2.43%
向上しました。aggregate CPU time は 0.27% 減り、median peak working set は
1.429 GiB から 1.303 GiB へ下がりました。反転順序の 400-frame pair 2 組は
candidate が 1.98% と 1.55% 高速で、balanced throughput は 1.77% 高く、
aggregate CPU time は 0.22% 減り、median peak working set は 1.423 GiB から
1.350 GiB へ下がりました。candidate-first の 1,000-frame pair は wall time が
ほぼ同等ながら 0.32% 低速でしたが、candidate CPU time は 0.31% 減り、peak
working set は 1.437 GiB から 1.381 GiB へ下がりました。candidate の 250-frame
区間 4 個は 28.44/26.40/25.83/26.22 秒で、progressive slowdown や memory growth
はありませんでした。すべての A/B gate で luma、chroma、JSON、stdout、
normalized stderr、timestamp-normalized log、すべての ordered `fileLoc` が
一致しました。candidate の `current` output は `--threads 0`、`1`、default 5、
`20` で各 2 回とも同一で、別の 160-frame `v0.4.0` regression も同じ surface に
一致しました。

VHS real-FFT sub-deemphasis は、以前の値の lifetime が終了した後、high
spectrum、inverse high part、analytic magnitude を worker-owned workspace
array 3 個へ書き込むようになりました。public helper は引き続き独立した array を
返し、同じ PocketFFT transform、SOS filter、data type、sample ごとの expression
order を維持します。matched 80-frame `gc-verbose` trace では sampled managed
allocation が 22.934976 GiB から 20.311490 GiB へ
2.623486 GiB（11.44%）減り、`Double[]` は 11,420.375 MiB から
9,629.972 MiB へ 15.68%、`Complex[]` は 8,179.967 MiB から
7,285.327 MiB へ 10.94% 減りました。Gen2 collection は 79 回から 81 回へ
増えたため、GC count の改善は主張しません。

interleaved 160-frame `current --threads 20` pair 4 組は noise が大きく
（-4.44% から +7.97%）、paired throughput median は +0.02%、balanced aggregate
throughput は +0.92%、aggregate CPU time は 0.12% 高く、median peak working
set は 1.201 GiB から 1.122 GiB へ下がりました。400-frame pair 4 組は paired
median -0.78%、balanced -1.08%、CPU はほぼ同じながら 0.03% 高く、median peak
working set は 1.414 GiB から 1.134 GiB へ下がりました。candidate-first の
1,000-frame `current` pair は wall throughput が 0.78% 高く、CPU time は
0.91% 減り、peak working set は 1.318 GiB から 1.127 GiB へ下がりました。
candidate の 250-frame 区間 4 個は 20.53/18.39/17.78/17.77 秒、sampled working
set は 0.597/0.761/0.621/0.788 GiB で、progressive slowdown や growth は
ありませんでした。

短い 160-frame v0.4.0 pair は実行順に敏感だったため、short-run gain は主張
しません。順序を反転した 1,000-frame v0.4.0 pair 2 組は candidate が
4.28% と 9.78% 高速で、balanced throughput は 7.03% 高く、aggregate CPU time
は 2.90% 減りました。candidate peak working set は 1.14-1.34 GiB、baseline は
1.75-1.78 GiB でした。すべての A/B gate で luma、chroma、JSON、stdout、
normalized stderr/log、すべての ordered `fileLoc` が一致しました。最終
5-path matrix でも Exact v0.4.0 は Python `--threads 0` と一致し、4 つの .NET
profile/backend combination は default、1、5、10、20 workers でそれぞれ
deterministic でした。

linear-TBC plan preparation は per-line MAD scratch 3 個を rent し、median
scratch を再利用して、補正済み factor を pooled level-adjust buffer へ直接
書き込むようになりました。derivative、median/MAD、threshold、smoothing、
position、conversion、16-tap sinc、ownership semantics は変更していません。
273 line の plan を 200 回準備する allocation probe は 2,240,768 bytes から
30,400 bytes へ減少しました（98.64%）。release v1.3.3 を baseline とした、
実行順を反転した interleaved 160-frame Exact v0.4.0 `--threads 20` A/B pair
3 組では、wall-time median が 12.084 s から 11.766 s（2.63% 減、throughput
2.71% 増）、average CPU time が 102.385 s から 100.432 s（1.91% 減）へ
変化しました。peak-memory noise から memory reduction は主張しません。
luma、chroma、JSON、stdout、normalized stderr/log、順序付き 320 個すべての
`fileLoc` は完全一致しました。別の 40-frame `--threads 0`、default、
`--threads 20` gate も exact で、thread 間で deterministic でした。

current mode の VHS multi-grid support count は、対称な ordered pair を別々に
調べる代わりに、各 unordered pulse pair を 1 回だけ評価して両方の count を
更新するようになりました。pulse location の順序、tolerance comparison、integer
count、candidate selection、floating-point stage、detector state の順序は変更
していません。release v1.3.4 を baseline とした、実行順を反転した interleaved
160-frame Exact current `--threads 20` pair 5 組では、wall-time median が
15.012 s から 14.755 s（1.71% 減）になり、balanced aggregate throughput は
1.06% 増えました。candidate は 5 組中 4 組で勝ちました。aggregate CPU time は
0.63% 増えたため、CPU-time reduction は主張しません。matched 80-frame sampling
trace では inclusive `VhsSyncDetector.Detect` time が 2,493 ms から 2,346 ms
（5.89% 減）になりましたが、これは hotspot の変化を示すもので、別の
end-to-end gain ではありません。すべての A/B run で luma、chroma、JSON、
stdout、normalized stderr/log、全 ordered `fileLoc` が一致しました。別の
current/v0.4.0 `--threads 0`、default、`--threads 20` gate も deterministic
かつ exact で、v0.4.0 serial result は全 surface で Python oracle と一致しました。

current mode の Super-Gaussian chroma final filter は、instance-local FFT
workspace を 1 個だけ再利用するようになりました。concurrent caller には別々の
workspace が渡され、完了後に保持するのは最大 1 個です。reflection padding、
float32 conversion point、DUCC/PocketFFT packet order、mask arithmetic、
inverse normalization、output ownership は変更していません。dirty-buffer reuse
と concurrent-call gate を含む 52 件の focused FFT/filter xUnit v3 test がすべて
pass しました。

同じ private local capture の matched 80-frame runtime-counter run では、
sampled managed allocation が 20.241 GiB から 17.580 GiB、Gen2 collection が
75 回から 73 回へ減りました。sampled peak working set は一貫して改善しなかった
ため、resident-memory reduction は主張しません。interleaved 160-frame pair
5 組は short-run noise でわずかに baseline 側だったため、short-run speedup も
主張しません。順序を反転した 400-frame pair 2 組は candidate が 2.82% と
3.00% 高速で、balanced aggregate throughput は 3.00% 高く、aggregate CPU time
は 1.66% 減りました。400-frame counter series は progress とともに増えず、
non-monotonic でした。すべての A/B gate で luma、chroma、JSON、stdout、
normalized stderr/log、全 ordered `fileLoc` が一致しました。別の `current` と
`v0.4.0` の `--threads 0`、default 5、`--threads 20` check も 7 surface すべてで
一致し、Exact v0.4.0 は Python serial oracle と一致しました。

mixed-radix float32 PocketFFT Plan は、thread-local value workspace と scratch
workspace を 1 個ずつ保持し、より大きい Plan に遭遇した場合だけ拡張するように
なりました。transform output は引き続き caller-owned です。input conversion、
factorization、packet order、sample ごとの arithmetic、inverse normalization、
copy boundary は変更していません。production length 239,580-point の xUnit v3
probe では、warm call ごとの allocation が 11,594,232 bytes から
3,878,568 bytes（66.55% 減）になり、output SHA-256 は完全一致しました。

release v1.3.6 を baseline とした、順序を反転した interleaved 160-frame Exact
`current` `--threads 20` pair 6 組では、wall-time median が 15.009 s から
14.553 s（3.04% 減、throughput 3.13% 増）、CPU-time median が 110.820 s
から 107.227 s（3.24% 減）になりました。matched 400-frame runtime-counter
run では、sampled managed allocation が 91.712 GiB から 85.902 GiB
（6.34% 減）、GC pause が 1.066 s から 0.760 s、Gen0 collection が 765 回
から 306 回、Gen2 collection が 327 回から 313 回へ減りました。retained
worker-local buffer により sampled median working set は 674 MiB から
742 MiB へ増えたため、resident-memory reduction は主張しません。candidate の
first-third median は 757 MiB、final-third median は 699 MiB で、progress に
伴う増加はありませんでした。startup 後の連続する 100-frame interval は
8.350、7.628、7.495、7.599 s でした。

すべての 160-frame A/B run で luma、chroma、JSON、stdout、normalized
stderr/log、全 ordered `fileLoc` が一致しました。400-frame pair でも luma、
chroma、JSON、normalized log、順序付き 800 個すべての `fileLoc` が一致しました。
別の Exact `current`/v0.4.0 `--threads 0`、default 5、`--threads 20` gate も
7 surface すべてで一致し、deterministic でした。

Betamax FSC notch は、以前の内容が不要になった decoder-owned video array を
in-place で filter するようになりました。public helper は引き続き独立した
array を返します。notch design、padding choice、pooled odd-extension buffer、
forward/backward IIR arithmetic、reverse order、final copy-back は変更して
いません。release v1.3.7 を baseline とした、順序を反転した interleaved
160-frame Exact `current --threads 20` pair 6 組では candidate が 5 組で勝ち、
wall-time median は 14.667 s から 14.495 s（1.17% 減、throughput 1.19% 増）、
CPU-time median は 107.227 s から 106.398 s（0.77% 減）、balanced aggregate
throughput は 1.02% 増えました。

matched 80-frame `gc-verbose` trace では sampled managed allocation が
18.245 GiB から 17.388 GiB（4.70% 減）、sampled `Double[]` allocation が
9.690 GiB から 8.820 GiB（8.98% 減）、Gen2 collection が 73 回から 64 回へ
減りました。matched 400-frame runtime-counter run では total allocation が
84.082 GiB から 79.278 GiB（5.71% 減）、Gen2 collection が 325 回から
294 回へ減りました。baseline/candidate の wall time は 33.179/33.382 s、
median working set は 647.4/649.1 MiB で、candidate には 1.392 GiB の一時的な
sample もあったため、この pair から long-run speedup や resident-memory
reduction は主張しません。startup を含む candidate の 100-frame interval は
10.365/7.632/7.594/7.638 s で、progressive slowdown や growth はありません。

すべての 160-frame A/B run で luma、chroma、JSON、stdout、normalized
stderr/log、順序付き 320 個すべての `fileLoc` が一致しました。400-frame
output でも luma、chroma、JSON、normalized log、順序付き 800 個すべての
`fileLoc` が一致しました。追加の Exact gate 12 run は `current` と v0.4.0 の
`--threads 0`、default 5、`--threads 20` を対象とし、7 surface すべてで一致し、
cross-thread deterministic でした。更新した Exact/IPP overview matrix 60 run
も、記録済み compatibility reference をすべて通過しました。

VHS real-FFT sub-deemphasis の analytic stage は、以前の内容が不要になった
後で worker-owned の full-length complex buffer 2 個を再利用するように
なりました。新しい preallocated DUCC real-to-full-spectrum overload は、
同じ input packing、Plan/packetized transform 選択、root multiplication、
conjugate mirroring、negative-zero 処理、Hilbert mask、inverse transform、
data type、sample ごとの magnitude 式を維持します。public helper は引き続き
独立配列を返します。focused xUnit v3 coverage は 1,024-point Plan と
32,768-point packetized の両 branch、無効または重複する buffer、SciPy bit
hash、warm allocation を確認します。

release v1.3.8 に対する一致した 80-frame `gc-verbose` trace では、sampled
managed allocation が 17.388 GiB から 13.900 GiB へ 20.06% 減り、
sampled `Complex[]` allocation が 7.122 GiB から 3.622 GiB へ 49.14%
減り、Gen2 collection が 64 回から 57 回へ 10.94% 減りました。順序を反転して
interleave した 160-frame Exact `current --threads 20` 6 組では、
baseline/candidate の wall median は 13.523/13.654 秒でした。balanced
aggregate wall/CPU time の変化は +0.14%/+0.08% にとどまり、candidate の
wall 勝利は 6 組中 4 組だったため、再現可能な throughput 向上は主張しません。

順序が逆の 400-frame runtime-counter 2 組を合計すると、allocation は
161.302 GiB から 125.900 GiB へ 21.95% 減り、Gen2 collection は 656 回から
487 回へ 25.76% 減りました。candidate の aggregate wall time は 1.52%
高かったため、long-run speedup も主張しません。candidate の 1 run には
1,381 MiB の一時的 working-set sample がありましたが、reverse-order run では
再現せず 731 MiB が最大でした。別の 1,000-frame candidate run は 2,000
fields を 73.267 秒で完了し、sampled working set の median/max は
648/1,032 MiB でした。first/last-third median は 664/642 MiB で、9.247 秒の
startup interval 後、9 個の 100-frame interval は 7.038 から 7.152 秒に
収まりました。これは bounded memory と progressive slowdown がないことを
支持しますが、resident-memory reduction の主張ではありません。

すべての A/B で luma、chroma、raw JSON、stdout、normalized stderr/log、
ordered `fileLoc` が一致しました。追加の Exact gate 12 件は v0.4.0 と
`current` を `--threads 0`、default 5、`--threads 20` で網羅し、7 surface
すべてで baseline と一致して cross-thread deterministic でした。再測定した
Exact/IPP matrix run 60 件も、記録済みの compatibility reference をすべて
通過しました。

続く VHS complex-RF filtered-spectrum workspace pass は、full filtered
spectrum を既存の上限付き worker workspace に保持し、optional RF MTF multiply
を in-place で実行します。per-block の `Complex[]` output を最大 2 本削除しながら、
complex-multiply の式と fallback、full-complex FFT/Hilbert lifetime、係数、
data type、ordered output state を維持します。通常値と特殊値の in-place test は
長さ 0、1、2、3、32,769 を網羅します。warm PAL RF block の allocation は
2,098,184 bytes で、新しい上限は 2,400,000 bytes です。

formal release v0.4.0-1.3.9 に対する matched 80-frame `gc-verbose` trace では、
sampled managed allocation が 13.900 GiB から 10.415 GiB へ 25.1% 減り、
sampled `Complex[]` allocation が 3.622 GiB から 135 MiB へ 96.4% 減り、
Gen2 collection が 57 回から 44 回へ 22.8% 減りました。順序を反転した
160-frame 6 組は 7 compatibility surface すべてで一致し、
baseline/candidate wall median は 14.069/14.167 秒、CPU median の変化は
-0.44% だったため、この pass は throughput-neutral と判断します。

順序が逆の 400-frame runtime-counter 2 組では、aggregate allocation が
127.255 GiB から 93.795 GiB へ 26.29% 減り、Gen2 collection が 496 回から
261 回へ 47.38% 減りました。counter 計測下の aggregate wall time は 3.57%
高かったため、long-run speedup は主張しません。candidate の sampled working
set 最大値は 1.23 GiB で、reverse-order run の first/last-third median は
905/908 MiB でした。別の 1,000-frame candidate run は luma、chroma、raw JSON、
normalized log、2,000 個すべての ordered `fileLoc` で前 release checkpoint と
一致し、allocation を 160.525 GiB から 119.650 GiB へ 25.46%、Gen2 を
628 回から 353 回へ 43.79% 減らしました。first/last-third working-set median は
747/782 MiB、最大値は 1,198 MiB で、startup 後の 100-frame interval 9 個は
7.239 から 7.493 秒に収まりました。workspace は bounded で progressive
slowdown もありませんが、GC pressure の低下により periodic resident-memory
sample は高くなるため、resident-memory reduction は主張しません。

次の Exact pass は double precision SOS の padded odd-extension workspace
を pool で再利用します。使用前に logical range をすべて初期化し、padding、
double precision arithmetic、section/sample order、forward/reverse order、
独立した exact-length result は変更しません。temporary array は `finally`
で返却し、no-padding path は従来の owned copy を維持します。既存の
section-major bit-exact test は、warm 4,096-sample call の allocation が
40,000 bytes 未満であることも検証します。

formal release v0.4.0-1.4.0 に対する matched Exact
`current --threads 20` 80-frame `gc-verbose` trace では、sampled managed
allocation が 10.415 GiB から 8.659 GiB へ 16.9%、sampled `Double[]` が
8.837 GiB から 7.083 GiB へ 19.8% 減り、Gen2 は 44 回から 26 回に
なりました。以前の 1.054 GiB `OddExtension` allocation caller は消えました。
順序を反転した 160-frame 6 組は luma、chroma、raw JSON、stdout、
normalized stderr/log、すべての ordered `fileLoc` で一致し、
baseline/candidate wall median の 13.682/13.602 秒は throughput-neutral
と判断します。追加の 12 gate は v0.4.0 と `current` の `--threads 0`、
default 5、`--threads 20` を網羅しました。

別の 1,000-frame candidate run も luma、chroma、raw JSON、normalized
stderr/log、2,000 個すべての ordered `fileLoc` で formal release checkpoint
と一致しました。total allocation は 119.650 GiB から 98.679 GiB へ 17.5%、
GC pause は 1.124 秒から 1.029 秒へ減り、Gen2 は 353 回から 319 回に
なりました。first/last-third working-set median は 680/727 MiB で、
一時的な 1,420 MiB peak は後に 900 MiB 未満へ戻りました。startup 後の
100-frame interval 9 個は 6.800 から 6.989 秒に収まっています。これは
bounded memory と progressive slowdown がないことを示しますが、
resident-memory reduction や stable throughput improvement の主張ではありません。

次の Exact pass は double SOS forward/backward filtering に internal-only の
destination form を追加し、public API の independently owned result は
変更しません。VHS RF high-boost path では worker-owned `RawEnvelope` が
envelope input の役割を終え、後の demodulation stage まで再利用されないため、
SOS result と式を変えない float32-envelope scaling をそこへ書き込んでから
既存 FFT に渡します。padding、double arithmetic、section/sample order、
reversal order、FFT input、output ownership は不変で、retained array も
追加しません。destination path は section-major reference と bit-exact で、
warm 4,096-sample call の allocation は 4,096 bytes 未満に gate されます。

main `a184450` に対する matched Exact `current --threads 20` 80-frame
`gc-verbose` trace では、sampled managed allocation が 8.667 GiB から
7.797 GiB へ 10.0%、sampled `Double[]` が 7.091 GiB から 6.221 GiB へ
12.3% 減り、Gen2 は 36 回から 33 回になりました。約 0.9 GiB の
high-boost SOS result-allocation chain は消えました。順序を反転した
160-frame 6 組は 7 compatibility surface すべてで一致し、
baseline/candidate wall median 13.297/13.375 秒、CPU median
101.750/101.273 秒は throughput-neutral と判断します。追加の 12 gate は
v0.4.0 と `current` の `--threads 0`、default 5、`--threads 20` を
網羅しました。

matched 1,000-frame counter run も luma、chroma、raw JSON、normalized
stderr/log、2,000 個すべての ordered `fileLoc` で一致しました。total
allocation は 98.021 GiB から 88.428 GiB へ 9.8% 減り、GC pause は
0.994/0.998 秒、Gen2 は 289/286 回でした。baseline/candidate wall time は
72.129/71.352 秒ですが、単一の ordered pair から speedup は主張しません。
candidate の first/last-third working-set median は 676/874 MiB で、終盤の
1,484 MiB peak は collection timing の影響を受けるため、resident-memory
reduction は主張しません。startup 後の 100-frame interval 9 個は
6.787 から 7.035 秒に収まり、bounded memory と progressive slowdown が
ないことを示します。

最新の Exact pass は numeric semantics を変えず、さらに 1 つの worker-local
buffer を再利用します。sub-deemphasis は high-pass signal を `Real`、analytic
magnitude を `Imaginary`、FFT scratch を既存の complex workspace に保持します。
demod input が `demodSpectrum` に変換された後、以前の `RawEnvelope`/compact
demod 内容は不要です。そのため新しい full-block `Double[]` を割り当てず、
既存の destination API で double-precision amplitude SOS result を
`RawEnvelope` に書き込みます。non-workspace public API、padding choice、
section/sample と reversal order、post-SOS expression、returned output
ownership は変更しません。xUnit v3 warm-block allocation test がこの lifetime
と deterministic output を検証します。

main `583d062` に対する matched Exact `current --threads 20` 80-frame
`gc-verbose` trace では、sampled managed allocation が 7.797 GiB から
6.926 GiB へ 11.2%、sampled `Double[]` が 6.221 GiB から 5.342 GiB へ
14.1% 減り、Gen2 は 33 回から 29 回になりました。sub-deemphasis SOS
result-allocation caller は消えました。順序を反転した 160-frame 6 組は luma、
chroma、raw JSON、stdout、normalized stderr/log、すべての ordered `fileLoc`
で一致しました。baseline/candidate wall median は 13.845/13.404 秒、CPU
median は 103.258/100.133 秒でしたが、以下の長時間 run では stable
throughput gain を再現しませんでした。追加の 12 gate は v0.4.0 と `current`
の `--threads 0`、default 5、`--threads 20` を網羅しました。

反対順序の 1,000-frame counter pair 2 組も luma、chroma、raw JSON、
normalized stderr/log、2,000 個すべての ordered `fileLoc` で一致しました。
allocation は 11.87-12.12%、GC pause は 1.28-7.44%、Gen2 は
21.50-24.35% 減りました。candidate wall time は 0.13% と 1.35% 高かったため、
この pass は throughput-neutral と判断します。candidate first/last-third
working-set median は 679/773 MiB と 804/659 MiB、peak は
1,517/1,516 MiB で、sampled resident memory は改善しませんでした。combined
post-startup 100-frame interval は 6.924 から 7.195 秒に収まり、どちらの run
にも progressive growth はありません。これは bounded memory の根拠であり、
resident-memory reduction の主張ではありません。

続く Exact PocketFFT pass は、各 complex transform の最後にあった full-buffer
copy を 1 回削除します。mixed-radix plan は最後の radix pass が実際に書き込んだ
worker-local value buffer を返し、既存の sample ごとの `Complex` conversion が
直ちに使用します。radix selection、packet order、arithmetic expression、
normalization order、data type、thread-local ownership は変更しません。
deterministic な 32,768-point odd-pass xUnit case は forward/inverse bit hash を
それぞれ
`950264D00BFBB9E577539DD1CD8BAE660B3EA9EAC82DD131794CAA108341061B`
と
`3CC982F0D601FD7B484FECAF26FC912F0DDF77593B378E1E45ED9B9EBB1EF5B5`
に固定します。

real gate 12 件は v0.4.0 と `current` の 1、default 5、20 workers を網羅し、
interleaved 160-frame pair 6 組も luma、chroma、raw JSON、stdout、normalized
stderr/log、すべての ordered `fileLoc` で一致しました。CPU trace では以前の
final copy に由来する 374.725 ms の `Memmove` caller が消えています。順序を
反転した 1,000-frame counter pair 2 組の combined process CPU time は
1,122.171 秒から 1,111.078 秒へ 0.99% 減り、combined wall time は
151.782/151.650 秒で実質的に同じでした。sampled allocation も
7.437/7.420 GiB で実質同じで、新しい allocation type はありません。この
pass は CPU と memory-bandwidth の削減として保持し、throughput-neutral と
判断します。

次の Exact PocketFFT pass は、radix-8 zero-frequency butterfly の integer
indexing だけを変更します。`Pass8FirstIndex` は caller が 1 回だけ計算した
input/output base と stride を受け取り、各 load/store で `InputIndex` と
`OutputIndex` を再計算せず、加算で 8 つの location を進みます。`i > 0` の
twiddle 付き loop、すべての floating-point expression、butterfly と rotation
order、normalization、data type、buffer ownership は変更しません。既存の
64-point と 32,768-point forward/inverse bit-hash test が even/odd pass の
mixed-radix plan を網羅します。

preallocated 32,768-point transform を 2,000 回実行する、順序を反転した
independent-process microbenchmark 8 組はすべて candidate が速く、
baseline/candidate median は 1,445.939/1,296.320 ms（10.35% 減）でした。
checksum は同一で timing overhead は双方 40 bytes です。real gate 12 件は
v0.4.0 と `current` の 1、default 5、20 workers を網羅しました。順序を
反転した 160-frame pair 6 組は luma、chroma、raw JSON、stdout、normalized
stderr/log、すべての ordered `fileLoc` で一致し、wall median は
15.613/15.586 秒、CPU median は 107.594 秒から 103.797 秒へ 3.53%
減りました。CPU trace は `Execute` と `Pass8` の間で attribution が移動した
ため、安定した aggregate を使います。mixed-radix Plan self-time は
22.338 秒から 20.553 秒へ 8.0%、double-precision PocketFFT total self-time
は 5.0% 減りました。

反対順序の 1,000-frame counter pair 2 組も luma、chroma、raw JSON、
normalized stderr/log、2,000 個すべての ordered `fileLoc` で一致しました。
combined process CPU time は 1,056.06 秒から 1,017.89 秒へ 3.61%、
combined wall time は 138.962 秒から 138.308 秒へ 0.47% 減りました。
sampled allocation は 154.186/153.769 GiB で実質同じです。GC と resident
memory sample は collection timing によって変動するため、allocation または
resident-memory reduction は主張しません。candidate の startup 後の
100-frame interval は両方の順序で 6.607 から 6.794 秒に収まり、
progressive growth はありません。この pass は repeatable な CPU reduction
として保持し、whole-pipeline-throughput-neutral と判断します。

続く Exact allocation pass は、float32 SOS initial state を 2 つの flat span
に保持します。32 sections までは steady-state と scaled-state span を stack
backed とし、それを超える uncommon filter は bounded heap fallback を維持します。
backward pass では scaled span を上書きして再利用し、2 個目の scaled matrix を
割り当てません。SOS coefficient、float32 conversion point、steady-state
expression、scale operation、sample-major section order、reverse order、output
ownership は変更しません。既存の 1、2、4、generic-section bit-hash test は
exact のままで、warm in-place allocation gate は 4,096 から 512 bytes へ
厳格化しました。

順序を反転した independent-process microbenchmark 8 組は、それぞれ
preallocated 4,096-sample two-section filter を 10,000 回実行しました。6 組で
candidate が速く、baseline/candidate median は 238.658/234.193 ms
（1.87% 減）、allocation は 2,400,040/720,040 bytes、つまり約
240/72 bytes per call でした。real gate 12 件は v0.4.0 と `current` の 1、
default 5、20 workers を網羅しました。順序を反転した 160-frame pair 6 組は
luma、chroma、raw JSON、stdout、normalized stderr/log、すべての ordered
`fileLoc` で一致しました。wall median は 13.134/13.257 秒、CPU median は
98.500/100.969 秒のため、whole-pipeline speedup は主張しません。

matched 80-frame allocation trace では、以前の sampled `System.Single[,]`
182 events、合計 18.511 MiB が消えました。unrelated large array が支配的なため、
total sampled allocation は 6.918/6.920 GiB で neutral、Gen2 start は 27/28
でした。反対順序の 1,000-frame counter pair 2 組も、すべての applicable
artifact/log surface と 2,000 個の ordered `fileLoc` で一致しました。combined
counter-reported allocation は 155.200 GiB から 154.364 GiB へ 0.54% 減り、
Gen0/Gen2 collection は 1,515/492 から 1,491/486、GC pause は
1.857 秒から 1.777 秒へ変化しました。combined CPU time は
1,078.188/1,077.734 秒で実質同じです。combined wall time は
145.978/144.539 秒でしたが、short pair は stable throughput gain を確認して
いません。candidate の startup 後 100-frame interval は両方の順序で
6.877 から 7.121 秒に収まりました。working-set sample は collection timing
で変動するため、resident-memory reduction は主張しません。この pass は
bounded small-object/GC reduction として保持し、
whole-pipeline-throughput-neutral と判断します。

次の Exact allocation pass は、float32 SOS coefficient を destination span
へ直接変換します。32 sections までは coefficient span を stack-backed とし、
それを超える uncommon filter は以前の heap allocation を bounded fallback
として維持します。6 個の coefficient cast、steady-state expression、scale
operation、sample-major section order、reverse order、output ownership、
filter object lifetime は変更しません。focused test は既存の 1、2、4、
generic-section bit hash と 33-section fallback を網羅し、warm in-place
allocation gate は 512 から 64 bytes へ厳格化しました。

順序を反転した independent-process microbenchmark 8 組は、それぞれ
preallocated 4,096-sample two-section filter を 10,000 回実行しました。
baseline/candidate allocation は 720,040/40 bytes、つまり約
72/0 bytes per call です。median は 231.419/231.810 ms で、candidate が
速かったのは 2 組だけのため、timing は neutral と判断します。real gate
12 件は v0.4.0 と `current` の 1、default 5、20 workers を網羅しました。
interleaved 160-frame pair 6 組は luma、chroma、raw JSON、stdout、
normalized stderr/log、すべての ordered `fileLoc` で一致しました。
baseline/candidate wall median は 13.390/13.410 秒、CPU median は
101.609/101.477 秒でした。

matched 40-frame/80-field allocation trace では、baseline の sampled
`FloatSosSection[]` 25 events、合計 2.538 MiB が candidate で消えました。
total sampled allocation は 3.723/3.714 GiB、Gen0/Gen1/Gen2 start は同じ
30/1/18 ですが、unrelated large array がこの sample を支配します。反対順序の
1,000-frame counter pair 2 組も、すべての applicable artifact/log surface と
2,000 個の ordered `fileLoc` で一致し、combined wall time は
145.840/145.455 秒でした。counter-reported allocation は
154.112/154.379 GiB で、消えた小配列よりはるかに大きい scale で変動するため、
whole-pipeline allocation、resident-memory、speedup は主張しません。
candidate の startup 後 100-frame interval は 6.905 から 7.159 秒に収まり、
progressive slowdown はありません。この pass は bounded small-object
elimination として保持し、whole-pipeline-throughput-neutral と判断します。

次の Exact PocketFFT pass は、mixed-radix packet の変換結果を既存の
worker-local `Complex32` packet span へ書き戻します。plan は入力を従来どおり
thread-static `Value[]` workspace へコピーし、同じ `Execute` を実行してから
packet へ出力します。root、twiddle、arithmetic、packet order、normalization、
data type、ownership は変更していません。既存 SciPy fixture、serial/parallel、
owned-output hash は exact のままで、warm large-multipass allocation gate は
4 MiB から 64 KiB へ厳格化しました。

順序を反転した independent-process microbenchmark 8 組は、それぞれ 239,580-point
FFT を 200 回、20 workers で実行しました。baseline/candidate allocation median
は 776,701,680/5,062,408 bytes（99.35% 減）、output hash は一致しました。
wall median は 649.18/631.10 ms でしたが、勝敗は 4 対 4 のため isolated timing
は neutral と判断します。v0.4.0/current の 1、default 5、20 workers を網羅する
real gate 12 件と interleaved 160-frame pair 6 組も、luma、chroma、raw JSON、
stdout、normalized stderr/log、すべての ordered `fileLoc` で一致しました。
short-pair wall median は 14.03/13.66 秒、勝敗は 3 対 3、CPU median は
102.97/103.95 秒のため、short timing も neutral と判断します。

matched 40-frame/80-field allocation trace では、sampled total allocation が
3.706 から 3.366 GiB、events が 15,543 から 12,026、Gen0 start が 30 から 6
へ減りました。sampled `Complex32[]` は 364.328 MiB/3,559 events から
2.747 MiB/3 events になり、baseline の `Plan.Transform` caller
163.263 MiB/1,606 events は消えました。Gen1 は 1/1、Gen2 は 14/17 で、
collection timing の差から Gen2 改善は主張しません。この trace も全 output、
JSON、ordered `fileLoc`、normalized log surface で一致しました。

反対順序の 1,000-frame pair 2 組は、combined wall time を
144.526 から 136.988 秒（5.22% 減、1.055x throughput）、counter allocation を
154.553 から 140.373 GiB（9.17% 減）、GC pause を 1.688 から 0.920 秒
（45.49% 減）、Gen0 collection を 1,483 から 304（79.5% 減）へ移しました。
全 artifact/log surface と 2,000 個の ordered `fileLoc` は exact です。
candidate の startup 後 100-frame interval は 6.500 から 6.860 秒で、sampled
working set は bounded のまま progressive slowdown はありません。Gen2 timing と
resident-memory reduction は主張しません。この pass は allocation/GC と stable
long-run throughput の改善として保持します。上記 five-path overview は 60 回の
.NET run で更新し、全 run が同じ compatibility gate を通過しました。

次の Exact `current` chroma allocation pass は、NTSC burst deemphasis が返す
exclusive `double[]` を保持し、その buffer を `current` phase compensation へ
直接渡します。従来は同じ array を read-only span として扱った後、in-place
upconversion の直前に field 全体を clone していました。削除した処理は純粋な
ownership copy であり、filtering、float32 conversion point、phase arithmetic、
sample order、caller-owned input は変わりません。PAL、v0.4.0、phase correction
disabled、field parity がない path も従来どおりです。production-size の xUnit v3
allocation gate は field-size `double[]` output 2 個と final `ushort[]` だけが
budget 内に収まることを確認し、旧実装の 3 個目の field copy は上限を超えます。

local Release solution は warning/error 0 で build され、1,118 tests がすべて
pass しました。final GitHub Actions も同じ 1,118 tests を discover し、
1,117 pass、optional AC3 dependency を必要とする 1 test が skip、failure 0
でした。real gate 12 件は v0.4.0/current の 1、default 5、20 workers を
網羅します。idle interleaved 160-frame pair 6 組は luma、chroma、raw JSON、
stdout、normalized stderr/log、すべての ordered `fileLoc` で一致しました。
baseline/candidate wall median は 13.513/13.224 秒、CPU median は
103.375/101.617 秒です。candidate は 6 組中 4 組で速かったものの、short-run
timing は whole-pipeline speedup の根拠にはしません。

idle opposite-order 1,000-frame counter pair 2 組も、すべての artifact/log surface
と 2,000 個の ordered `fileLoc` で一致しました。combined counter allocation は
140.055 から 132.532 GiB へ 7.523 GiB（5.37%）減りました。combined wall time は
142.323/142.893 秒、GC pause は 1.100/1.116 秒で neutral、sampled working set は
bounded のままです。candidate の startup 後 100-frame interval は 6.794 から
6.981 秒で、progressive slowdown はありません。この pass は deterministic な
full-field allocation reduction として保持し、whole-pipeline-throughput-neutral
に分類します。そのため five-path overview は直前の valid idle 60-run matrix を
維持します。

続く Exact/current pass は、decoder-owned field buffer に 1H または 2H chroma
comb を直接適用します。public `ApplyNtscComb` と `ApplyPalComb` API は caller
input を引き続き copy します。internal path は forward order で field を上書き
しても元の delayed line を読めるよう、`ArrayPool<double>` に NTSC は 1 line、
PAL は 2 line だけ保持します。float32 conversion point と PAL/NTSC の subtraction
order は変えません。bit-exact xUnit v3 test は両 system と両 precision mode を
網羅し、production-size allocation gate は field-size `double[]` 1 個と final
`ushort[]` だけを許容します。従来の追加 comb field はこの budget を超えます。

local Release solution は warning/error 0 で build され、1,119 tests がすべて
pass しました。real gate 12 件は v0.4.0/current の 1、default 5、20 workers を
網羅し、baseline/candidate と全 thread count の luma、chroma、raw JSON、stdout、
normalized stderr/log、すべての ordered `fileLoc` が一致しました。interleaved
160-frame current pair 6 組も exact です。baseline/candidate wall median は
13.299/13.318 秒、CPU median は 101.664/103.000 秒で、candidate win は 2 組の
ため short timing は neutral とします。

反対順序の 1,000-frame current counter pair 2 組では、4 run すべての checked
artifact/log surface と 2,000 個の ordered `fileLoc` が一致しました。combined
counter allocation は 132.089 から 125.320 GiB へ 6.769 GiB（5.12%）減少し、
combined wall time は 142.414 から 140.016 秒へ 1.68% 減、throughput は
1.017x、GC pause は 1.067 から 1.000 秒になりました。candidate の startup 後
100-frame interval は 6.68 から 6.89 秒です。maximum sampled working set は
1,481.6 MiB、baseline は 1,467.4 MiB なので resident-memory reduction は主張
しませんが、どちらも bounded で progressive slowdown はありません。refreshed
five-path overview は 60 回の interleaved .NET run を使い、全 60 run が既存の
compatibility reference を通過しました。

最新の Exact field-resampling allocation pass は destination-buffer form を追加
しますが、16-tap sinc expression、float32 conversion point、sample order、
resampling plan は変えません。stateful VHS CLI sequence decoder は、bounded
chroma task が luma rendering と重なるため、exact-length の luma workspace と
別の chroma workspace を所有します。public `Decode()` と retained
`DecodeFields()` は独立した `ChromaBurstSamples` を引き続き allocation して返し、
internal non-retaining CLI sequence result だけが省略します。direct UInt16 path は
double field を allocation せず、public resampling API も independent-output
contract を維持します。各 role で保持する buffer は 1 個だけで、shape change は
置換されるため、retained memory は decode length に比例せず bounded です。

Release solution は warning/error 0 で build され、1,120 件の xUnit v3 test が
すべて pass しました。real profile/thread gate 6 組は Exact v0.4.0 と `current`
の `--threads 0`、default-five、`--threads 20` を網羅しました。candidate は
baseline の luma、chroma、raw JSON、stdout、normalized stderr/log、すべての
ordered `fileLoc` と一致し、連続する public `Decode()` result も異なる chroma
burst array を保持します。refreshed five-path matrix は 4 つの Exact/IPP profile
combination を default、1、5、10、20 workers で各 3 回実行し、全 60 run が既存
compatibility reference と一致しました。

反対順序の Exact-current/20-worker 1,000-frame pair 2 組では、combined counter
allocation が 126.226 から 110.873 GiB（12.16% 減）、GC pause が 1.015 から
0.885 秒（12.83% 減）、wall time が 141.015 から 140.405 秒
（0.43% 減、throughput 1.004x）になりました。反対順序の current/default
500-frame pair 2 組では allocation が 62.638 から 55.606 GiB（11.23% 減）、
GC pause が 11.91%、wall time が 1.05% 改善しました。対応する
v0.4.0/default pair は allocation 10.80%、GC pause 22.65%、wall time 0.88%
を削減し、v0.4.0/20-worker 1,000-frame pair は allocation 10.40%、
wall time 2.11% を削減しました。

candidate-only の v0.4.0/20-worker 3,000-frame run は 164.593 秒で完了しました。
4 つの working-set quarter median は 793.94、799.89、747.98、772.25 MiB、
maximum は 1,400.8 MiB、final sample は 957.3 MiB でした。最初と最後の
steady 100-frame interval 10 個の median は 5.492 と 5.440 秒です。これは
retained memory が bounded で progressive slowdown がないことを支持しますが、
collection-timing peak を resident-memory reduction とは表現しません。

次の Exact complex-demodulation workspace pass は、compact path の escape しない
raw FM result を worker の exact-length `RawEnvelope` buffer に書き、optional
diff-demod repair result をその時点で不要な `Real` buffer に書きます。retained
`DemodRaw` と analytic diagnostic は引き続き独立した array を所有します。data
type、atan approximation、sample expression、SIMD lane order、FFT behavior、
repair order、pool cap、serial state/output commit は変わりません。2 件の focused
xUnit v3 test は destination-buffer repair を独立に再構築した allocating oracle
と比較し、workspace を繰り返し lease した後の retained array も bit 単位で
確認します。完全な Release suite は 1,122 tests すべてに pass しました。

短い real profile/thread gate 6 組は Exact v0.4.0 と `current` の
`--threads 0`、default-five、`--threads 20` を網羅しました。current/20-worker
500-frame run 8 回、current/20-worker 1,000-frame run 4 回、さらに
v0.4.0/20-worker、current/default、v0.4.0/default の 500-frame run 各 4 回は、
baseline の luma、chroma、raw JSON、stdout、normalized stderr/log、すべての
ordered `fileLoc` と一致しました。refreshed five-path matrix も 20 cell 各 3 回、
合計 60/60 compatible run を完了しました。同じ production code から構築した
self-contained validation `decode.exe` の SHA-256 は
`333D051E361FE425EA893EE819129BB1CFC9249CF77E29746C94252F263D19D0` です。
measured `98ADB0...7A1` executable との 100-frame strict gate も、7 つの
artifact/log surface と ordered `fileLoc` すべてで一致しました。

反対順序の current/20-worker 500-frame pair 4 組では median wall time が
37.905 から 38.002 秒（+0.26%）へ動き、median CPU は 294.055 から
288.711 秒（-1.82%）へ減りました。より長い v0.4.0/20-worker、
current/default、v0.4.0/default pair median はそれぞれ 30.834/31.169、
67.850/67.632、59.756/59.871 秒です。wall change は -0.32% から
+1.10% の範囲なので、この pass は speedup ではなく throughput-neutral と
判断します。

反対順序の current/20-worker 1,000-frame counter pair 2 組では、allocation が
111.461 から 88.022 GiB（21.03% 減）、GC pause が 0.994 から 0.791 秒
（20.43% 減）になりました。instrumented wall time は 147.734 から
150.428 秒（+1.82%）、maximum sampled working set は
1,472.19/1,507.75 MiB であるため、resident-memory または wall-time reduction
は主張しません。candidate-only current/20-worker 3,000-frame run は
209.767 秒、allocation 133.2 GiB、GC pause 1.146 秒でした。最初/中間/最後の
1,000-frame total は 72.00/69.11/68.50 秒、working-set-quarter median は
721.87/1,304.73/601.61/864.18 MiB、maximum は 1,462.11 MiB です。working
set は単調増加せず final third がより速いため、bounded memory と progressive
slowdown がないことを支持します。

最新の Exact NTSC chroma ownership pass は、internal non-retained
sequence-resampling buffer を burst deemphasis に直接移譲します。public decode
API と retained diagnostic path は引き続き input を copy して独立した array を
返し、in-place deemphasis と後続 phase/comb stage は明示的に internal-owned
である buffer にだけ適用されます。data type、multiplication expression、loop
order、phase/comb order、ordered field commit は変わりません。

candidate commit `0270f101aa21d2f2a3c5679365eb4a4e9655d77c` から構築した
self-contained `decode.exe` の SHA-256 は
`2626A0F82B89D7F4025F41600C034048E61F89F399B08746071968B6E3E619B5`、
測定した baseline executable は
`FFCB821C0E46885B7735A9ADCA1AA1ACD6454DC43C84ED671EC7B2EB31DA261C`
です。Release build は warning 0、error 0 で、標準 xUnit v3 test 1,126 件が
すべて pass しました。Exact v0.4.0 と `current` はいずれも `--threads 0`、
省略/default-five、`--threads 20` の strict gate を通過しました。thread 間でも
luma、chroma、raw JSON、stdout、normalized stderr/log、すべての ordered
`fileLoc` が一致しました。homepage の refreshed five-path matrix も candidate
run 60 回すべてで compatibility reference と一致しました。

同条件の 100-frame allocation trace では、total sampled allocation が
4,994,031,752 から 4,611,843,896 bytes（7.65% 減）、sampled `Double[]` が
4,061,498,384 から 3,679,431,952 bytes（9.41% 減）になりました。baseline
trace は 2 つの burst-deemphasis call site で、約 1.9 MiB の full-field
`double[]` allocation event を 202 回記録しましたが、candidate trace では
両方とも消えました。100-frame output 自体は 200 fields です。

反対順序の current/20-worker 160-frame pair 6 組では median wall time が
13.398 から 13.451 秒（+0.40%）へ動き、CPU time は 103.133 から
101.398 秒（-1.68%）へ減ったため、short-run throughput は neutral と判断します。
対応する v0.4.0 pair は wall time を 10.672 から 10.563 秒（1.02%、1.010x）、
CPU time を 97.453 から 95.141 秒（2.37%）へ改善しました。各 profile の
反対順序 1,000-frame pair 2 組も完全一致し、中断せず完了しました。`current`
は 72.151 から 70.557 秒（2.21%、1.023x）へ改善し CPU time は実質 neutral、
v0.4.0 は 55.700 から 55.200 秒（0.90%、1.009x）へ改善し CPU time を
2.00% 削減しました。この pass は主に allocation reduction のため採用し、
modest な long-run throughput benefit も確認しています。refreshed homepage
matrix は新しい candidate run 60 回で構成し、Python column は既存の固定 sample
oracle measurement を維持します。

最新の Exact PocketFFT cache pass は、cold key の重複 plan construction を
防ぎます。`ConcurrentDictionary.GetOrAdd` は、複数 worker が missing key で
競合すると、最終的に 1 つの value だけを公開する場合でも expensive value
factory を複数回呼び出すことがあります。4 つの PocketFFT implementation は
shared single-creation cache を使い、既存 value の read は concurrent fast path、
cold miss は独立した per-key gate で調整されるため、異なる length は並列に
build できます。same-key factory reentrancy は拒否され、factory exception は
cache されず、後続 call で retry できます。cache key、retained plan cardinality、
FFT data type、coefficient、arithmetic order、thread-local scratch は変わりません。

同条件の 100-frame allocation trace では、complex plan construction が
30 calls/9,746,000 sampled bytes から 1 call/458,672 bytes、real plan
construction が 30 calls/3,031,704 sampled bytes から 1 call/131,120 bytes
になりました。duplicate sampled construction を 12,187,912 bytes
（12.19 MB）除去し、total sampled allocation は 4,611,843,896 から
4,598,751,544 bytes に減りました。float-real plan build 2 回は実際の
2 lengths に対応するため変わりません。

candidate は標準 Release build と xUnit v3 test 1,130 件をすべて pass し、
concurrent factory、failure 後の retry、mixed-radix、workspace、FFT の focused
test を含みます。strict main/candidate run 12 回は Exact v0.4.0/`current` の
`--threads 0`、省略/default-five、`--threads 20` を対象とし、各 profile 内の
luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が
一致しました。refreshed Exact/IPP-fast、v0.4.0/`current`、
default/1/5/10/20-worker matrix 60 回も、各 backend/profile reference と一致
しました。この round は current main binary を byte-exact oracle とし、Python
自体は再実行していません。main の既存 direct Python v0.4.0
`g4315520 --threads 0` evidence を transitive upstream reference としています。

交互順序の 100-frame Exact `current`/20-worker pair 5 組では、decode-time
median が 9.12 から 8.58 秒（5.9% 減）になりました。run 間の variance は
残りましたが、mean は 9.150 から 8.756 秒（4.3% 減）であり、modest な
cold-start/end-to-end gain と判断します。別の candidate 1,000-frame run は
2,000 fields を 69.862 秒で完了し、current-main の luma、chroma、raw JSON、
normalized stderr/log、ordered `fileLoc` reference と一致しました。sampled
working set は maximum 1,479.6 MiB、first/final-third median 659.9/593.2 MiB
で、progressive growth のない bounded memory を支持します。

### RF stream output buffer reuse

現在の Exact candidate では、VHS stream decoder が demodulated video、
RF envelope、low-pass video、float32 chroma の 4 つの block-sized output
ownership を管理します。block が cache 内にある間、または現在の span assembly
に参加している間は array を排他的に保持し、両方の利用が終了した後だけ
concurrent pool に返します。public `DecodePreparedBlock` result は独立 allocation
のままです。worker failure、cancelled prefetch、cache invalidation、replacement、
eviction、stream change、disposal は eligible set を返し、active parallel
assembly が必要とする eviction は copy 完了まで返却を遅延します。pool の retained
hard limit は 48 sets で、decoded block 16 と prefetch block 32 の ceiling に一致
します。DSP type、coefficient、expression、operation order、padding、field commit
order は変わりません。

同じ 100-frame Exact `current`/20-worker trace で total sampled allocation は
4,598,751,544 から 566,944,304 bytes へ減少し、4,031,807,240 bytes、
87.67% を削減しました。final trace は initial concurrency で output set を
60 組作成した後に安定して再利用し、retained subset は 48 を超えません。
対応する 1,000-frame counter run では integrated allocation が 40.641 から
3.844 GiB へ減りました。この最適化は decoder の numerical path ではなく
GC pressure を削減します。

反対順序の 100-frame Exact `current`/20-worker pair 10 組では、decode-time
median が 8.72 から 8.58 秒（1.61% 減）、mean が 8.710 から 8.617 秒
（1.07% 減）になり、wall-time median は 1.72% 減りました。run 間 variance が
残るため throughput gain は modest と明示し、allocation reduction を主結果と
します。

final candidate は warning 0 の Release build と xUnit v3 test 1,136 件をすべて
pass しました。strict main/candidate run 12 回は Exact v0.4.0/`current` の
`--threads 0`、default-five、`--threads 20` を対象にし、luma、chroma、raw JSON、
stdout、normalized stderr/log、ordered `fileLoc` が一致しました。refreshed
Exact/IPP-fast、v0.4.0/`current`、default/1/5/10/20-worker matrix 60 回も、
各 profile/backend reference と一致しました。

final 1,000-frame Exact `current`/20-worker run は 2,000 fields を 69.475 秒で
完了し、current-main artifact、normalized diagnostic、ordered `fileLoc` と一致
しました。working set は maximum 711.6 MiB、first/final-third median
627.5/703.7 MiB で、progressive unbounded growth なしとして bounded-memory
gate を pass しました。前 round と同じく current main が direct A/B oracle で、
verified Python v0.4.0 `g4315520 --threads 0` evidence を transitive upstream
reference とします。

</details>

<!-- SECTION: build -->

## ビルドとテスト

必要条件：

- `.NET SDK 11.0.100-preview.6.26359.118`（`global.json` で固定）
- IDE として使用する場合は Visual Studio 2026
- optional Intel IPP bridge の build には Visual Studio C++ Build Tools と Windows SDK
- FFmpeg 対応 container input では `ffmpeg` と `ffprobe` が `PATH` 上に必要
- default HiFi FLAC output は bundled libsndfile を使い、FFmpeg は不要

```powershell
.\tools\build-ipp-native.ps1
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release --no-build --no-restore --minimum-expected-tests 1136
```

最初の command は optional `ipp-fast` native artifact を含めるためのものです。
Exact-only build では省略できます。この script は `vswhere` で MSBuild を検出し、
固定した `intelipp.static.win-x64` NuGet package を restore して sequential static
bridge を build します。外部 IPP、OpenMP、oneTBB、Visual C++ runtime DLL 依存が
あれば失敗します。development/deployment PC のどちらにも Intel oneAPI の install
は不要です。binary-only single-file release は `vhsdecode_ipp.dll` と必要な
third-party notice を埋め込み、license sidecar file は追加しません。

現在の正式な Release build は warning 0、error 0 です。xUnit v3 project は
`dotnet test` と Visual Studio Test Explorer の両方で個別に検出できる
**1,136** tests を公開します。

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
decode.exe vhs --dsp-backend ipp-fast [upstream options] input output
```

最後の形式は approximate output を許容できる場合に optional fast backend を
明示的に選択します。

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

snapshot の公開で一時的な sharing/access conflict が発生した場合は、
100 ms、500 ms、2 秒後に再試行します。1 回の checkpoint failure で後続の
snapshot worker が停止することはありません。最終 canonical JSON を置換できない
場合、decode は `OUTPUT INCOMPLETE` と非ゼロ exit code で失敗し、append-only の
`.tbc.json.fields.tmp` journal を保持します。完全な snapshot を生成済みなら
`.tbc.json.final`（または番号付き `.final.N`）として保存し、以前の canonical
snapshot は変更しません。成功時の最終 JSON byte と v0.4.0 completion lifecycle
は変わりません。

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
- default へ昇格する前に、ほかの format/option combination でも opt-in
  `current` profile 全体の capture-wide certification を継続
- まれな first-HSync/vblank recovery と完全な JSON/SQLite field metadata
- 残る TBC writer bit-compatibility edge と、全 format/option/実キャプチャの output parity
- fixture で互換性を保護した上での CPU utilization、allocation、SIMD、
  worker scheduling の継続的な profiling

対話型デコード UI と TBC utility はこの目標の対象外であり、
未完了のデコード互換作業としては追跡しません。

<!-- SECTION: evidence -->

## 詳細な根拠

以前の長い実装・差分検証一覧は
[`COMPATIBILITY_EVIDENCE.md`](COMPATIBILITY_EVIDENCE.md)
に保存されています。この共有文書には、3 言語の README が参照する詳細な
algorithm note、数値境界、output hash、fixture result が含まれます。

<!-- SECTION: license -->

## ライセンス

GPL-3.0。[`LICENSE`](../LICENSE) を参照してください。
