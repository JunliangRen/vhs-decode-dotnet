# vhs-decode-dotnet 詳細リファレンス

[プロジェクト概要](../README.ja.md)

[English](README.detailed.md) | [简体中文](README.detailed.zh-CN.md) | **[日本語](README.detailed.ja.md)**

<!-- README_SYNC: 2026-08-17.02 -->

[`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode) の
デコード関連部分を .NET 11 で再実装するプロジェクトです。現在は release
`v0.4.0`、commit `43155200da87c0d49eb37d8ec09b1372075ee8e4`
を互換性の基準としています。

現在の .NET port release は `v0.4.0-2.3.0`（application version `2.3.0`）です。

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
| 入力 | 広範囲に実装済み | Raw input、bundled libsndfile へ厳密に限定した direct raw FLAC、一般的な FFmpeg/PyAV 相当の container 経路をカバーしています。まれな codec/timestamp は今後の対象です。 |
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

private local PAL VHS RF capture の natural-start 20-frame gate でも、
merged Python PR 341 と Exact `current` は全 40 field で一致しました。対象は
luma/chroma byte、ordered `fileLoc`、stdout、timing-normalized stderr、
754 行の timestamp-normalized log、および source identity だけを正規化した
JSON です。Exact `current` は `--threads 0`、default、`--threads 20` でも
deterministic です。

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

DSP backend は `--dsp-backend exact|ipp-fast|cuda-fast` で明示的に選択します。この
parameter はこの .NET port の experimental extension であり、upstream
`oyvindln/vhs-decode` v0.4.0 CLI には含まれません。`exact` が default で、
既存の managed 互換経路を維持し、Intel IPP の probe や load は行いません。
`ipp-fast` は opt-in Windows x64 backend です。Intel CPU が official support
target であり、compatible non-Intel x64 CPU はこの project の best-effort
experimental path です。IPP が返す feature mask に SSE4.2 が含まれる場合のみ、
正の non-Intel vendor warning を受け入れます。静的 link 済みの
`vhsdecode_ipp.dll` を load し、IPP version と選択された ISA を表示します。
bridge、ABI、CPU が利用できない場合は明確に失敗し、`exact` へ暗黙に fallback
しません。現在の IPP routing は VHS real-RF FFT、`current` profile の
color-under Super-Gaussian DFT、および LaserDisc video、EFM、analog audio が使う
power-of-two double-precision full-complex FFT stage を含みます。LD path は native
bridge ABI v1.3 を使用します。CVBS と HiFi は未対応の `ipp-fast` を明示的に拒否し、
Exact kernel を実行した結果を誤って benchmark として扱うことはありません。

`cuda-fast` は独立した Windows x64 NVIDIA CUDA 13 full-signal VHS backend です。
`vhsdecode_cuda_fast.dll` と cuFFT を load し、cuVHS commit
`c55e72073f44b27e8839efb842e4345af39887f7` に固定した RF demodulation、
sync/line location、time-base correction、color-under、dropout graph を実行します。
CPU code は input callback、少量の control/metadata work、output publication を担当しますが、
Exact または IPP field engine は実行しません。RF sample、cuFFT R2C/C2C/C2R storage、
demodulation、geometry、colour、dropout image data は FP32 です。直接 FP32 で係数を
構築すると不安定だったため、1 回だけ行う high-order host filter design のみ double
precision を保ち、完成した係数を GPU upload 前に明示的に量子化します。native bridge
ABI v4 は raw signed-16、eligible libsndfile、FFmpeg PCM16 input で direct PCM16 callback
を優先します。FP32 の半分の transfer size で upload し、deterministic GPU kernel が
FP32 で正確に表現できる各 signed-16 value を FP32 signal plane へ変換します。managed
FP32 callback は fallback として維持します。bridge は persistent chroma workspace、
自動的に 16 field に制限する batch、parallel deterministic sync-pulse scan も使用します。
ABI v4 は要求 output-field limit と scan 可能な RF 量を分離します。そのため native
pipeline は weak/no-signal leader を通過し、horizontal cadence が連続して安定してから
fallback field order を開始し、invalid field と先頭の不完全な second field を捨て、
要求された完全な alternating field 数で正確に停止します。cuVHS の optional host-side
K4 source reconstruction は compile 時に無効化され、この route は常に GPU source を
使います。未対応 option/profile や CUDA component 不足は明示的に失敗し、別 DSP
backend へ fallback しません。初期 contract は Windows x64 の native-rate 40 MSPS
PAL/NTSC VHS SP/LP/EP のみです。CVBS、LaserDisc、HiFi、S-VHS、その他の video
system、packed `.lds`、preview-server routing、明示的な compatibility profile
selection は近似処理しません。

この backend は独自の numerical contract を持ち、`v0.4.0` または `current` の hash
compatibility を主張しません。local RTX 4070 12 GiB development machine の current
same-session interleaved comparison では、同じ fixed private 40 MHz real PAL `.ldf`
request `--start_fileloc 320000000 --length 500` を使いました。CUDA は
15.605154/15.747770 秒、`ipp-fast --threads 20` は 14.108349/14.064289 秒でした。
median は 15.676462 対 14.086319 秒（31.8950 対 35.4954 output fps）で、この限定比較
では CUDA の wall time が 11.29% 長く、throughput は IPP の 0.8986x です。両 path
とも同じ
14,745.6/59,417.6 black/white metadata level を持つ、exactly 1,000 個の ordered
alternating 1135x313 field を出力しました。最初の synchronized `fileLoc` は
322,212,917 と 320,563,200 です。独立した sync/TBC algorithm が異なる field boundary
を選ぶためです。これは same-request performance comparison であり、output equality の
主張でも、tape、system、driver、NVIDIA model をまたぐ一般的な speed guarantee でも
ありません。

別の startup-inclusive A-B-B-A comparison は直前の CUDA executable を baseline にし、
old build 22.083587/21.753076 秒、final build 16.355230/15.758866 秒を測定しました。
median は 21.918332/16.057048 秒で、wall time は 26.74% 減、throughput は 36.50% 増、
output speed は 22.812 から 31.139 fps へ上昇しました。2 つの measurement sequence 間で
machine 全体の throughput が変化したため、この数値は CPU comparison と分けて記録します。

process resource sampling 付きの 2 回目の A-B-B-A でも同じ方向を確認しました。old/new
wall median は 20.765240/15.313171 秒（26.26% 減、throughput 35.60% 増）、CPU median
は 36.3125/33.0000 秒（9.12% 減）でした。median peak working set は 246.34 から
256.38 MiB へ増加しました。old control 自体の peak private bytes は
4,449.98-4,586.33 MiB と noisy で、descriptive median は 4,518.16 から 4,616.46 MiB、
final run 2 回は 4,617.25/4,615.67 MiB でした。retained prefetch は PCM16 batch 1 個に
bounded です。これは measured memory tradeoff であり、unbounded lookahead queue では
ありません。

final fixed window を使った CUDA run 2 回の luma/chroma/JSON は byte-identical で、
それぞれ 40,705 dropout segment を持ちます。SHA-256 は
`647967E99822994A69DBDEB0E0B5824755FE204F650277106D2B4F234109C456`、
`CBC40C00452C3AFA8CE15CAEB97DBDD009ED2FA7803AAA3D51813B2F498E6E99`、
`745F4F570D8EB2D480FBB38BFD4513EA17F3D28583553BB69CF120EE7D27DB3D` です。
global `fileLoc` は 322,212,917 から 1,121,408,422 まで strictly increasing でした。
これは 1 台の GPU/driver 上の repeatability evidence で、cross-device determinism を
保証しません。

80-frame Exact/CUDA comparison は同じ fixed RF request を使いました。CUDA が約 1 frame
遅い boundary を選んだため、lossless FFV1 export は Exact の最初の frame を除いて
79 pair を alignment しました。default の export-side dropout correction 使用時の
SSIM Y/U/V/All は 0.954905/0.988109/0.991285/0.972301、PSNR Y/U/V/average は
33.196867/41.243137/43.586266/35.699053 dB でした。export-side correction なしでは
SSIM は 0.949393/0.987529/0.990573/0.969222、PSNR は
31.286140/41.027876/43.313528/33.944493 dB でした。alignment した全 158 field の
active-picture dropout mask は Exact に対し precision 97.512%、recall 99.818%、IoU
97.339% で、whole-field IoU は 94.186% でした。contact sheet の manual review でも
scene content、colour、motion は非常に近く、従来の重大な diagonal/rainbow phase
failure は解消しました。sync、TBC geometry、pixel には差が残るため、これは
viewing-quality gate であり Exact compatibility ではありません。refined K4 horizontal-
sync stage を bypass する低コスト実験は luma SSIM を 0.949393 から 0.942450 に下げた
ため、採用しませんでした。

pinned CUDA graph には build 時に source-match guard 付き patch を適用し、upstream の
ordering/lifecycle hazard を除去します。FM analytic difference は別 destination を使い、
chroma burst accumulation は固定順序で reduction し、writer parity は decode 間で漏れず
各 pipeline instance に属します。upstream の in-decode dropout concealment は、この
project で保守する Exact-style metadata detector に置き換えます。dynamic per-field source
offset と field mean を使い、0.18 threshold、1.25 hysteresis、30-sample merge gap、strict
greater-than-10 minimum run を適用し、PAL parity geometry を Exact と同様に map します。
decoder は metadata のみを記録し、optional correction は export-side operation のままです。
GPU は ballot operation で 32-sample warp mask を parallel classification し、その後 serial
Exact state machine が transition bit のみを消費します。threshold precedence、merge
distance、minimum length、sample position は維持されます。real 500-frame window の独立
A-B-B-A comparison は median wall time を 13.64% 短縮し、throughput を 15.79% 増加させ、
16-field dropout stage 自体は約 43.22 から 7.41 ms へ低下しました。この変更では
luma/chroma/JSON が byte-identical のままです。

burst-source DC mean も deterministic fixed-tree FP64 reduction の後に sample-parallel roll
を行います。旧 serial accumulation order から raw CUDA chroma bit は意図的に変わりますが、
final run 2 回は相互に byte-identical で、old/new lossless rendered output の SSIM は
1.000000、Exact-aligned の表示精度で各 SSIM 値は不変でした。average PSNR は
35.699052 から 35.699053 dB です。bounded pageable host buffer は次の PCM16 RF batch
1 個だけを prefetch し、current GPU/output tail と overlap します。最初の read も
CUDA/cuFFT initialization と overlap します。internal A-B-B-A control は 500-frame median
を 11.649328 から 9.175967 秒へ短縮しました（wall time 21.24% 減、throughput 26.96%
増）。final file は同一です。JSON recovery checkpoint は約 1 秒に 1 回までに制限し、
final publication は常に complete document を書きます。paired 500-frame median は 2.96%
改善し、crash snapshot が約 1 秒 stale になる可能性があります。luma/chroma の 4 MiB
stdio buffer はさらに median wall time を 1.68% 改善しました。shared burst FFT storage
は 0.48% slower、16M-sample managed read block は noise range、page-locked input は
0.84% slower、asynchronous dropout allocation は約 12% slower だったため採用しません。

parallel pulse kernel の real-GPU test は boundary、NaN、variable offset、overflow semantics
を含みます。direct GPU dropout test は dynamic offset、field parity、hysteresis、merge、
minimum-run semantics を含みます。full in-process synthetic NTSC pipeline test は同じ
48-field RF source を FP32 で 1 回、PCM16 で 2 回 decode し、45 個すべての 910x263
output field について luma/chroma/JSON の byte equality を要求します。alternating field
parity、strictly increasing `fileLoc`、parity と整合する `1,4,3,2` NTSC `fieldPhaseID` の
cyclic rotation も検証します。native build は normal 16-field batch と forced five-field
batch の両方でこの contract を実行し、phase/head-track state を non-aligned batch boundary
の先まで運びます。NTSC evidence は geometry、lifecycle、determinism のみを対象とし、
real NTSC VHS capture は local にないため colour/quality certification は未実施です。
これらの結果も GPU/driver 間の universal determinism を保証しません。

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
  4K sample 未満で full sort を維持し、4K sample 以上で bit-exact introselect を使います。
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
- `current` color-under path の phase analysis は、burst probe が各 output line から
  実際に読み得る先頭 `BurstStart + BurstEnd` sample だけを resample します。linear
  interpolation は compact な pooled source-position/level-adjust array を使いますが、
  wow smoothing は省略 sample も original FMA recurrence で逐次更新し、non-linear
  interpolation は full-plan fallback を維持します。
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

### VHS sync-analysis workspace reuse

exclusive な VHS sync-detector workspace は、escape しない scratch array 5 個を保持する
ようになりました。sync/porch candidate、sort/MAD/slope に順番に使う 1 個の共有
statistics buffer、grid-support count、final mask です。sort は現在の logical length
だけを処理し、grid count は全要素を初期化し、mask は reuse 前に clear します。
演算順序、threshold、pulse ownership、独立した result array ownership は変わりません。

valid-pulse allocation benchmark は 1 call あたり 46,362.2 から 28,642.2 bytes へ
38.2% 減少しました。matched 200-frame `gc-verbose` trace では、main の
`DetectFiltered` に recurring `double[]` tick 76 回、合計 7.71 MiB と、`bool[]` tick
2 回、合計 0.20 MiB がありましたが、candidate は両方の recurring call site を除去
しました。retained workspace array は second zero の初回 capacity growth にだけ現れ、
escape する edge/pulse result array は独立 ownership を維持します。

interleaved 200-frame Exact `current --threads 20` 3 pair と opposite-order 1,000-frame
2 pair は、luma、chroma、raw JSON、stdout、normalized stderr/log、field count、すべての
ordered `fileLoc` で一致しました。long pair 2 組の main/candidate wall time 合計は
67.258/67.004 秒で 0.38% 減少にとどまり throughput-neutral、CPU time は
569.563/578.985 秒だったため、CPU reduction や固定 speedup は主張しません。
candidate の peak working set は両方の long run で 355.1-357.0 MiB に収まりました。

追加の 24-run gate は Exact/IPP-fast、v0.4.0/`current`、`--threads 0`、default-five、
`--threads 20` を網羅しました。candidate は同じ configuration の main と luma、chroma、
raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が一致し、Exact の 3 thread
mode は各 profile の main `--threads 0` oracle とも一致しました。full xUnit v3 suite は
1,437 tests を公開し、dirty large-small-large workspace reuse、shared-detector concurrency、
valid-pulse warm-allocation test を含みます。gate 中は無関係な foreground CPU load があり、
matched long result も throughput-neutral だったため、public 6-path table は更新しません。

### current VHS radix final bucket の parallel collection

compact 3-stage `current` VHS level selector は final worker-local histogram を再利用し、
選択した 2 個の radix bucket を parallel に収集します。fixed worker order の prefix sum が
final bucket count を stable scratch offset に変換します。各 worker は contiguous source
partition 1 個だけを scan し、互いに重ならない 2 個の destination range にだけ書き込むため、
worker range を連結した scratch order は serial source order と完全一致します。
sortable-prefix conversion、float64 value、bucket/rank selection、final quickselect expression、
すべての v0.4.0、serial、dense、exceptional-value path は変更していません。

focused regression は 2、3、4、5、10、20 workers と 2 種類の data distribution で、
選択結果 2 個と collection 後の scratch 全体の bit pattern を比較します。IPP runtime を
含む standard xUnit v3 full suite は 1,437/1,437 で通過しました。全 hardware intrinsic を
無効にした 36 current VHS sync test も通過し、AVX-only の 1 case だけは設計どおり skip
されました。24-run real-RF gate は Exact/IPP-fast、v0.4.0/`current`、`--threads 0`、
default-five、`--threads 20` を網羅しました。luma、chroma、raw JSON、stdout、normalized
stderr/log、すべての ordered `fileLoc` が main と一致し、各 candidate profile は 3 thread
mode 間でも deterministic でした。

interleaved 200-frame Exact `current --threads 20` 3 pair の wall-time change は -4.54%、
-3.51%、+0.36% で、paired reduction median は 3.51% でした。opposite-order の
1,000-frame 2 pair は 34.911 から 33.184 秒、34.461 から 33.263 秒へ移動し、4.95% と
3.48% 高速化しました。long pair 全体の average active core は 8.34 から 8.87 へ増え、
aggregate CPU time は 1.9% 増加しました。すべての output/diagnostic surface は exact です。

別の 2,000-frame baseline/candidate resource gate は 4,000 field を完了し、luma、chroma、
raw JSON、stdout が一致しました。wall time は 60.097 から 59.450 秒（1.08% 減）、CPU は
501.750 から 503.234 秒、average active core は 8.349 から 8.465 でした。candidate の
private-memory quarter median は 370/621/796/691 MiB、main は 364/364/364/413 MiB、peak は
それぞれ 890.5/425.8 MiB でした。candidate memory は単調増加せず、終了時は peak 未満で
OOM もありません。これは bounded-run evidence であり、memory reduction や unlimited
duration を保証するものではありません。

### pooled IPP VHS envelope SOS

IPP-fast は full-length の one-section VHS RF envelope SOS を bounded
`IppSos32FilterPool` で処理します。Exact は従来の managed float32 expression と
演算順序を維持します。pool は native context を最大 12 個保持し、RF pipeline と
ともに dispose されます。専用 xUnit v3 integration test は pipeline routing、context
creation、retention bound、dispose を検証します。

固定 private PAL VHS 200-frame trace では、RF envelope filtering に属していた約
1.50 秒の managed SOS CPU が消え、残る約 0.44 秒は chroma processing でした。
opposite-order 1,000-frame IPP-fast pair 2 組は全 artifact/log surface が一致しました。
v0.4.0 profile の wall time は 46.282/46.331 秒で neutral、average CPU は
199.695 から 198.227 秒へ 0.74% 減少しました。`current` は 34.463 から
34.224 秒へ 0.69% 高速化し、CPU は 216.430 から 210.336 秒へ 2.82% 減少しました。

別の 1,000-frame runtime-counter run は working set を 34 回 sample しました。
first/final-third median は 386.8/387.6 MiB、peak は 390.8 MiB、total allocation は
677.1 MiB、GC pause は合計 35 ms でした。12-run deterministic gate は Exact/
IPP-fast、v0.4.0/`current`、`--threads 0`、default-five、`--threads 20` を網羅し、
各 profile 内の luma、chroma、raw JSON、stdout、normalized stderr/log、ordered
`fileLoc` はすべて一致しました。

### allocation-free internal VHS burst-probe result

Phase 22 candidate は public `ChromaBurstDemodulationResult` を従来の sealed record class の
まま保ちます。direct public class-to-struct prototype は CLR signature、binary binding、
`null`/`default`、identity、boxing、generic behavior を変えるため review で却下しました。
final path は 11 scalar field に別の internal readonly record struct を使い、実際の public API
boundary だけで public class に変換します。focused xUnit test は両 type category、signed zero、
NaN payload、infinity、init-only field、exact conversion、非破壊的な `with` behavior を固定します。

reverse-order focused 7 pair は exact checksum を維持し、median allocated bytes を serial で
114,244,800 から 67,256,448（41.13%）、4-worker で 183,595,176 から 137,502,920
（25.11%）へ削減しました。matched 200-frame Exact `current --threads 20` GC trace では total
allocation tick が 2,099 から 1,998（4.81%）、sampled
`ChromaBurstDemodulationResult` tick が 108 から 0 になりました。この trace でも luma、
chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` は一致しました。

reverse-order 200-frame 20 pair、opposite-order 1,000-frame 8 pair、以下の 60-run matrix は
captured surface がすべて exact でした。long-run paired wall change は 0.37% faster から
1.01% slower の範囲だったため、allocation/GC-pressure improvement として採用し、
end-to-end throughput や peak-memory win は主張しません。

### bounded libsndfile PCM16 overlap rewind

parallel VHS block batch は overlap する 32,768-sample RF window を読みます。direct
libsndfile path は従来、reusable block ごとに backward seek し、overlap 部分を再度
decode していました。Phase 24 は各 libsndfile loader に lazy allocation の
1,048,576-frame circular PCM16 rewind window を 1 個追加します。サイズは固定で 2 MiB です。
`ReadReusable` は cached prefix をコピーし、fresh tail だけを libsndfile に要求します。
ordinary `Read` は従来の seek behavior を維持し、mapped restart、実際の native seek、
fallback、dispose は ring を clear します。PCM16 conversion も同じ short-to-double
expression と AVX2 conversion path を使います。

Exact/IPP-fast の interleaved 200-frame 6 pair は luma、chroma、raw JSON、stdout、
normalized stderr/log、すべての ordered `fileLoc` が一致しました。Exact の wall median は
9.231 から 9.223 秒、CPU median は 88.141 から 84.906 秒になりました。IPP-fast の wall
median は 7.749 から 7.757 秒、CPU median は 45.953 から 45.406 秒になりました。short
wall delta は throughput-neutral な screening evidence として扱います。

opposite-order 1,000-frame 4 pair でも同じ surface が exact でした。Exact の
baseline/candidate mean wall time は 35.383/35.027 秒で 1.00% 減、mean CPU time は
293.797/290.023 秒で 1.28% 減でした。IPP-fast の mean wall time は
33.112/32.830 秒で 0.85% 減、mean CPU time は 197.367/196.844 秒で 0.27% 減でした。
candidate working-set peak は 353.0-357.5 MiB、private-memory peak は
366.9-371.5 MiB に収まりました。ring は fixed-size で、fallback/dispose 時に解放され、
decode length とともに増えません。

別の baseline/candidate 12-run gate は Exact v0.4.0/`current` の `--threads 0`、省略した
default-five、`--threads 20` を網羅しました。すべての captured surface は main と一致し、
各 profile は worker count 間で deterministic でした。zero-warning Release build、標準
xUnit v3 1,442 tests、libsndfile class 64 tests、native IPP smoke 15 tests が通過しました。

### strict analytic preparation の early staging

既存の 12-worker threshold を超える Exact VHS `current` では、main worker が real RF FFT を
開始する前に、strict NumPy-compatible full-complex analytic preparation を bounded companion
へ queue します。companion と main path は重ならない workspace array を使い、既存の FFT
plan、data type、expression、padding、conversion point を維持します。join は real inverse の
exception priority と ordered block commit を保持します。v0.4.0、default-five、
`--threads 1/5/10`、GNRC、sharpness、IPP-fast は以前の path のままです。

Exact `current --threads 20` の interleaved 1,000-frame pair 3 組は exit status、luma、chroma、
raw JSON、stdout、normalized stderr/log、すべての ordered `fileLoc`、その他の保存 metadata が
一致しました。baseline/candidate median は 37.876/37.403 秒で wall time は 1.25% 減でした。
CPU time median は 301.375 から 319.250 秒へ 5.93% 増え、effective core use は 7.96 から
8.54 へ増えました。median peak working set は 402.34 から 392.10 MiB、median peak private
bytes は 414.81 から 403.58 MiB へ減りました。candidate は bounded のままで、per-block
allocation を追加しません。

startup-heavy な public 160-frame window は引き続き noisy でした。interleaved pair 3 組の
baseline/candidate median は 6.930/7.437 秒で candidate は 7.30% 遅い一方、range は
6.598-7.618/6.635-7.467 秒で重なり、pair ごとの方向も異なりました。別の candidate-only
3-run refresh は 6.710-7.900 秒、median 6.782 秒で、7 種類の captured compatibility
surface はそれぞれ 1 hash でした。table はこの refresh を使いますが、cross-date host state は
causal evidence とせず、同時刻の long A/B を candidate gate とします。別の 12-run gate は
Exact v0.4.0/`current` の `--threads 0`、default-five、
`--threads 20` を網羅し、全 surface が一致して各 profile は deterministic でした。
zero-warning Release build と 1,443-test standard xUnit v3 suite gate が通過しました。
1,439 tests が pass、failure は 0、local test output に native runtime がなかった IPP-only
4 cases は skip されました。

### managed real-FFT inverse preparation の並行化

最新の Exact VHS `current` path は、大きな managed real-FFT inverse の独立した
conjugate-pair preparation を requested worker に分割します。各 pair は serial loop と
同じ float32 input、twiddle lookup、arithmetic expression、write location、center-bin
overwrite を使います。transform、normalization、packet scheduling、bounded worker-owned
workspace は変更しません。small transform と one-worker call は serial のままです。

Exact `current --threads 20` release binary の interleaved 1,000-frame pair 3 組は exit
status、luma、chroma、raw JSON、stdout、normalized stderr/log、すべての ordered
`fileLoc` が一致しました。candidate は 6.25%、5.29%、1.04% 高速でした。
baseline/candidate median は 35.822/33.715 秒で、wall time は 5.88% 減、throughput は
1.063x でした。median CPU time は 298.891 から 284.063 秒へ 4.96% 減り、effective
core use は 8.34 から 8.43 へ増えました。median peak working set は 359.28 から
348.45 MiB へ 3.01%、median peak private bytes は 373.59 から 359.74 MiB へ
3.71% 減りました。

startup-heavy な 160-frame apphost matrix は普遍的な gain とせず、counterexample として
残します。同時刻の baseline/candidate pair 15 組では、candidate median は default、1、5、
10 workers で 0.08%、0.44%、1.85%、2.59% 高速でしたが、20 workers では 4.14%
遅くなりました。7 種類の captured surface はすべて exact でした。別の one-worker
3-pair audit では旧 `bdccd58` binary が 41.77 秒、current main が 41.04 秒でした。
したがって前回の cross-date snapshot より低い ratio は host-state drift によるもので、
Phase 24/25 revision regression ではありません。別の 12-run gate は Exact v0.4.0/current
の `--threads 0`、omitted/default-five、`--threads 20` を網羅し、全 surface が main と
一致して各 profile は deterministic でした。

3-case xUnit v3 theory は supported real-FFT length 32,256、32,768、33,600 を使い、
8,192-pair parallel threshold の below、at、above を網羅します。20-worker workspace
inverse は 3 length すべてで serial result と byte-identical でした。full local suite は
1,446 tests を discover し、1,442 pass、failure 0、native runtime がない IPP-only 4 cases は
skip されました。

### bounded な cross-field VHS wavefront

2.1.0 release は production VHS sequence decode に capacity-two wavefront を追加します。
同期と ordered state planning は sequence thread に残し、luma rendering、prepared chroma の
completion、tape-dropout mapping は並行して完了できます。ただし、現在の RF span を参照する
task がすべて完了するまで、次 field の RF read は開始しません。output、JSON、`fileLoc`、
recovery、diagnostic は input 順に commit され、field ごとの diagnostic capture も次の ordered
commit の前に元へ戻します。

大きな leased RF span は lookahead 前に返却され、field boundary を越えるのは独立所有の
chroma tail と小さな pooled field output だけです。chroma、render、output pool の capacity は
すべて 2 です。500-frame memory pair の baseline/candidate peak working set は
354.4/473.2 MiB、private bytes は 365.7/525.0 MiB でした。1,000 frames ではそれぞれ
353.6/468.9 MiB と 367.2/502.8 MiB であり、per-field growth ではなく固定 window であることを
確認しました。最初の prototype は complete RF span を 2 個保持して約 884 MiB に達したため、
publication 前に破棄しました。

Exact `current` はこの wavefront から明示的に除外しています。gate 後の interleaved
500-frame pair 3 組は 19.74/19.86 秒の median で、有効な gain がありませんでした。
残した path には改善があり、Exact v0.4.0 は 33.55 から 32.31 秒へ短縮（wall time 3.7% 減）、
IPP-fast `current` は 19.77 から 17.26 秒へ短縮（12.7% 減）しました。後者は CPU time を
約 1.5% 増やしながら effective core use を 5.66 から 6.58 へ高めました。

最終 1,000-frame `--threads 20` release-binary gate は、Exact/IPP-fast と
v0.4.0/`current` の全 4 組で luma、chroma、raw JSON、ordered `fileLoc`、stdout、
normalized stderr、normalized log が一致しました。Exact v0.4.0 は 46.047 から
42.575 秒、IPP-fast v0.4.0 は 44.980 から 42.084 秒、IPP-fast `current` は
31.009 から 25.260 秒になりました。最後の path は CPU time が 188.20 から 197.34 秒へ
変化し、effective core use は 6.07 から 7.81 へ上がりました。別の 24-run gate は
`--threads 0`、省略時の default-five、20 workers を網羅し、standard xUnit v3 suite は
全 1,459 tests に成功しました。

### Exact-current sync-candidate scratch audit

diagnostic full cross-field Exact-current wavefront は技術的には動作しましたが、有効化せず
却下しました。順序を反転した 1,000-frame `--threads 20` pair 2 組の
baseline/candidate median は 33.91/35.96 秒で、candidate は 6.05% slower、CPU time は
6.13% 増加し、effective core use は 8.51/8.52 のままでした。追加並列性ではなく競合を
示したため、production の Exact-current gate は維持します。

採用した変更は、VBlank candidate ごとの temporary `List<ClassifiedSyncPulse>` を最大
26 entry の bounded stack span に置き換え、state machine 完了時だけ accepted entry を
copy します。同じ 500-frame runtime-counter pair で managed allocation は
1,058,682,656 から 572,202,104 bytes（46.0% 減）、Gen0 collection は 60 から 30、
GC pause は 44.4 から 24.2 ms へ減少しました。peak working set は 415.3 から
411.9 MiB、CPU time は 161.5 から 158.9 秒へ移動しました。interleaved 160-frame
6 pair と順序反転 1,000-frame 2 pair は wall throughput を neutral と分類したため、
public Python/.NET speed table は更新していません。

24-run release-binary gate は Exact/IPP-fast、v0.4.0/`current`、`--threads 0`、
省略時 default-five、20 workers を網羅しました。luma、chroma、raw JSON、ordered
`fileLoc`、stdout、normalized stderr/log は全 run で一致しました。10,000 個の rejected
candidate を使う focused allocation test と standard xUnit v3 1,460 tests はすべて成功しました。

### field-local sync-pulse workspace reuse

次の Exact-current pass は、`TbcFieldDecodePipeline` に caller-owned の classified/refined
pulse list を保持します。public `SyncAnalyzer` method は従来どおり owned result を返し、
internal sequential field path だけが 2 個の list を clear して再利用します。pulse の順序と値、
rescue mutation、VBlank state、diagnostic、全 cross-field transition は変更していません。
damaged field が backing array を 65,536 entry より大きくしても、次の normal-sized field は
reuse 前にその oversized retention を解放します。

この変更の前に worker-local PocketFFT scratch prototype も試しました。bit-exact は維持しましたが、
順序を反転した 1,000-frame 2 pair の baseline/candidate group median は 32.62/32.64 秒、
CPU time は 297.09/299.14 秒で、安定した end-to-end value がなかったため完全に戻しました。

採用した pulse-list change では、同じ 160-frame `gc-verbose` trace の sampled managed
allocation が 680,536,552 から 621,643,144 bytes（8.65% 減）になり、sampled
65,614,400-byte の `ClassifiedSyncPulse[]` hotspot が消えました。short 6 pair は
scheduling noise を含みましたが、順序を反転した 1,000-frame `--threads 20` 2 pair は
wall-time median を 32.95 から 32.11 秒（2.55% 減）、CPU time を 298.40 から
288.02 秒（3.48% 減）へ短縮し、effective core use は 9.06 から 8.97 へ移動しました。

順序を反転した memory pair はどちらも candidate peak が低くなりました。保守的な
reverse-order comparison では peak working set の baseline/candidate が
390.8/360.5 MiB、private bytes が 409.9/374.4 MiB でした。baseline-first pair の
baseline には 628.7 MiB の GC peak があったため、大きい差は percentage claim に使いません。
24-run release-binary gate は 4 backend/profile と `--threads 0`、default-five、20 workers
を網羅し、全 surface が一致しました。更新した Preview 7 candidate の 60-run public matrix は全 cell で surface
ごとに 1 hash を維持し、standard xUnit v3 1,500 tests はすべて成功しました。

### 最新の 6-path thread matrix

最新の overview は startup cost を含む `--start 100 --length 160` snapshot で、同じ private
local 40 MHz PAL VHS `.ldf` fixture 上の Python v0.4.0、merge 済みの Python PR341、Exact
v0.4.0、Exact `current`、IPP-fast v0.4.0、IPP-fast `current` を比較します。filename は
公開しません。active table は 2026-08-12 の固定 Python reference 30 run を保持します。
全 60 回の .NET 測定は 2026-08-15 に commit `21b8b01` から build した
self-contained .NET 11 Preview 7 candidate でまとめて更新しました。この documentation/toolchain
refresh では新しい tag や Release を公開しません。各 .NET cell は wall-time median、
profile が対応する Python 列に対する speedup、wall-time reduction
の順です。別 batch、format、fixture を使った過去の matrix とは直接比較できません。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI mode（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 52.811 s | 54.243 s | 13.977 s / 3.778x / 73.53% | 12.556 s / 4.320x / 76.85% | 12.214 s / 4.324x / 76.87% | 9.419 s / 5.759x / 82.64% |
| `--threads 1` | 57.067 s | 56.762 s | 36.434 s / 1.566x / 36.16% | 40.155 s / 1.414x / 29.26% | 25.468 s / 2.241x / 55.37% | 27.561 s / 2.060x / 51.45% |
| `--threads 5` | 52.920 s | 55.722 s | 14.055 s / 3.765x / 73.44% | 12.655 s / 4.403x / 77.29% | 12.244 s / 4.322x / 76.86% | 9.104 s / 6.121x / 83.66% |
| `--threads 10` | 52.965 s | 54.949 s | 11.795 s / 4.491x / 77.73% | 10.198 s / 5.388x / 81.44% | 10.535 s / 5.027x / 80.11% | 7.467 s / 7.359x / 86.41% |
| `--threads 20` | 53.555 s | 54.842 s | 10.667 s / 5.021x / 80.08% | 9.533 s / 5.753x / 82.62% | 9.216 s / 5.811x / 82.79% | 6.991 s / 7.845x / 87.25% |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 dotnet-current-runs=30 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-15 dotnet-current-date=2026-08-15 phase22-200-ab-pairs=20 phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 phase22-gc-traces=2 phase22-tests=1438 phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 phase24-thread-gate-runs=12 phase24-tests=1442 phase25-public-cell-runs=15 phase25-public-ab-pairs=15 phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 phase25-tests=1446 phase26-kernel-ab-pairs=8 phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 phase26-public-cell-runs=30 phase26-tests=1447 phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 phase27-thread-backend-runs=24 phase27-public-cell-runs=60 phase27-tests=1448 phase28-kernel-ab-pairs=8 phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 phase28-intrinsic-runs=3 phase28-public-cell-runs=60 phase28-tests=1448 phase30-burst-kernel-runs=14 phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 phase30-memory-runs=2 phase30-public-cell-runs=60 phase30-tests=1448 phase31-interleaved-ab-pairs=9 phase31-long-gate-runs=8 phase31-thread-backend-runs=24 phase31-memory-runs=4 phase31-public-cell-runs=60 phase31-tests=1459 phase32-vblank-short-ab-pairs=6 phase32-vblank-long-ab-pairs=2 phase32-thread-backend-runs=24 phase32-gc-traces=2 phase32-counter-runs=2 phase32-tests=1460 phase33-sync-list-short-ab-pairs=6 phase33-sync-list-long-ab-pairs=2 phase33-thread-backend-runs=24 phase33-gc-traces=1 phase33-memory-runs=4 phase33-public-cell-runs=60 phase33-tests=1463 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

3-run wall-time range は次のとおりです。

<!-- LATEST_PERFORMANCE_RANGES_BEGIN -->
| CLI mode | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| default（5） | 52.583-62.222 s | 53.893-58.195 s | 13.935-14.207 s | 12.236-13.228 s | 12.100-13.038 s | 9.255-10.015 s |
| `--threads 1` | 56.709-60.521 s | 56.335-58.991 s | 35.013-38.936 s | 39.121-41.993 s | 25.070-25.741 s | 27.009-28.035 s |
| `--threads 5` | 52.845-53.977 s | 53.696-58.437 s | 13.713-14.517 s | 12.153-13.959 s | 12.130-12.540 s | 8.997-10.933 s |
| `--threads 10` | 51.797-53.088 s | 52.649-56.775 s | 11.687-11.808 s | 9.358-11.685 s | 10.010-11.632 s | 7.166-8.026 s |
| `--threads 20` | 52.967-55.987 s | 53.005-55.618 s | 9.375-11.492 s | 8.772-10.610 s | 8.393-9.501 s | 6.108-7.452 s |
<!-- LATEST_PERFORMANCE_RANGES_END -->

保持する 30 Python measurement は 2026-08-12 固定条件 campaign の reference です。
全 20 個の .NET cell は、同じ .NET 11 Preview 7 candidate による 2026-08-15 の
完全な 60 run です。各 3-run cell 内で luma、chroma、raw JSON、stdout、normalized
stderr/log、ordered `fileLoc` の hash set はそれぞれ 1 つでした。
Python v0.4.0 は 15 run で 15 種類の luma、chroma、JSON、normalized-log hash set を
生成したため、strict oracle は `g4315520 --threads 0` のままです。

更新した全 .NET cell は commit `21b8b01` から build した self-contained Preview 7 candidate を
使いました。product version は
`2.1.0+21b8b01998fb7519cf3616820e181dba93f23d10`、single-file `decode.exe` SHA-256 は
`9426C7693B63BCB7661946BD856B790EA0207A48AD8257791197AC450DF3161B` です。
host は Intel Core Ultra 7 265K（20 logical processor）、Windows 11 build 26220 です。
repository pin と host CLI の .NET SDK はともに `11.0.100-preview.7.26381.103` です。raw directory は private fixture path を
含むため local にのみ保持し、public に独立再現可能な benchmark corpus とは主張しません。

直前の Preview 6 release snapshot と今回の Preview 7 candidate は別々の campaign で測定しました。
差は descriptive reference に限ります。run order、thermal/scheduler state、background load を
same-moment A/B として制御していないため、Preview 7 が原因の speedup/regression とは主張しません。

3-run range は通常の startup、thermal、scheduler、system variation を示します。ratio cell は
Python numerator と .NET denominator の両方で動きます。revision の因果的な regression/speedup
判断には使わず、以下の同時刻 interleaved revision A/B を gate とします。

### managed AVX Super-Gaussian spectrum mask

managed Exact chroma final filter は、Super-Gaussian spectrum mask を 4 個の `Complex32`
value に AVX で同時適用します。各 float component を double に拡張し、元の scalar multiply、
subtract/add、最後の double-to-float conversion point を lane ごとに維持します。loop に FMA
や reduction はありません。vector を store する前に両方の double result を確認し、1 lane
でも NaN または infinity なら、その vector と残りの tail は元の scalar JIT shape で処理します。
unaligned Span と 1、2、3 value の tail も同じ scalar behavior を維持します。IPP mask path は
変更していません。

alternating process-level kernel 8 pair は、それぞれ 178,201-point mask を 2,000 回適用しました。
全 16 run は同じ SHA-256 でした。scalar wall-time median は 1,210.850 ms、AVX は
167.083 ms で、86.20% reduction、7.247x throughput gain です。CPU-time median は
86.18% 減少しました。release disassembly は `vmulpd`、`vsubpd`、`vaddpd` を含み、fused
multiply-add はありません。focused test は 539 finite combination、全 vector-tail length、
unaligned slice と前後 sentinel、さらに 12 x 12 x 12 の exceptional-value cross を網羅します。
default JIT、`TieredCompilation=0`、forced AVX-off、all-hardware-intrinsics-off の evidence は、
それぞれの baseline scalar result と一致しました。

interleaved 1,000-frame Exact `current --threads 20` release-binary 6 pair は、exit status、luma、
chroma、raw JSON、stdout、normalized stderr/log、全 ordered `fileLoc` で一致しました。
candidate は 5 pair で高速、残る 1 pair は 0.03% だけ低速でした。独立 median は wall time
35.879 から 35.608 秒（0.76%、1.008x）、CPU time 298.148 から 295.141 秒（1.01%）、
effective core 8.18 から 8.38（2.43%）へ移りました。peak working set median は 352.5 から
345.9 MiB、private bytes は 364.8 から 357.9 MiB へ低下しました。

final release binary は Exact 12 gate、IPP-fast 12 gate に加え、native、AVX-disabled、
all-intrinsics-disabled の Exact gate を通過しました。7 artifact/log surface と ordered `fileLoc`
はすべて一致しました。fresh 60-run public matrix は backend/profile/thread cell ごとに 3 complete
run を持ち、各 cell の hash set は 1 つでした。先の batch は無関係な high-CPU process が現れた
ため破棄し、採用 batch は各 decode 前に external CPU sample が低いことを必須にしました。
full xUnit v3 suite は 1,448 tests すべてに成功しました。

### real inverse 最終 radix-4 vectorization と analytic copy 除去

analytic difference repair は、全 buffer を 2 番目の array に copy してから上書きする
処理を廃止しました。descending in-place pass により各 subtraction が読む source value は
同じで、allocation、padding、downstream ownership も変わりません。interleaved 1,000-frame
Exact `current --threads 20` 4 pair は全 compatibility surface で一致しました。wall-time
median は 39.174 から 39.152 秒で中立（0.06%）、CPU-time median は 315.130 から
310.140 秒へ 1.58% 減りました。

32K real inverse FFT で一般的な最終 `ido == 1` stage は、4 個の独立 radix-4 butterfly を
AVX で同時処理します。load は元の butterfly layout に transpose され、各 lane は scalar
version と同じ operand と multiplication order を保持します。FMA と horizontal reduction は
使わず、scalar tail と AVX-disabled fallback も不変です。専用 xUnit v3 oracle は signed zero、
subnormal、maximum finite value、infinity、signaling/quiet NaN を含む full inverse output を
scalar implementation と bit 単位で比較します。

alternating process-level kernel 8 pair はそれぞれ warmed 32K inverse を 4,000 回実行し、
1 SHA-256 を生成しました。wall-time median は 697.821 から 676.900 ms へ 3.00% 減りました。
続く interleaved 1,000-frame Exact `current --threads 20` release-binary 4 pair は luma、chroma、
raw JSON、stdout、normalized stderr/log、全 ordered `fileLoc` で一致し、candidate が 4/4 で
高速でした。wall-time median は 32.971 から 32.323 秒へ 1.96% 減少（1.020x）、CPU time は
278.195 から 267.500 秒へ 3.84% 減少しました。

別の 1,000-frame sampling run は peak working set 394.2 MiB、private bytes 404.5 MiB でした。
first-third と last-third の median は増加せず、retained buffer や OOM の傾向はありません。
Exact/IPP-fast、v0.4.0/`current`、`--threads 0`/default-five/`--threads 20` は native/scalar FFT
path を含む 24 release-binary compatibility gate を通過しました。更新した 60-run public
matrix は 5 worker mode 全体で profile ごとに 1 hash set を保持し、full xUnit v3 suite は
1,448 tests すべてに成功しました。

### AVX2 CTI reciprocal-table gather

`current` CTI distance stage は pinned 2,048-entry reciprocal mantissa table と同じ
float32 bit construction を維持します。AVX2 host では、独立した 8 bucket index を 1 回の
`GatherVector256` で読み出します。sign/exponent field、zero/subnormal から infinity、high
exponent から signed zero、quiet 化した NaN payload は lane ごとの integer bit operation で
再構成します。FMA、reduction、reassociation、shared scratch、新しい sample-level allocation は
なく、元の scalar lookup が fallback のままです。

focused xUnit v3 test は 2,048 bucket 全件、両 sign、zero、subnormal、exponent boundary、
infinity、signaling/quiet NaN payload、scalar tail を網羅し、native AVX2 と forced
AVX2-disabled run の両方で通過しました。40 iteration の alternating kernel 8 pair は
16 run 全体で 1 SHA-256 でした。wall-time median は 1,763.352 から 912.073 ms へ
48.28% 減少（1.933x）、CPU-time median は 1,710.938 から 906.250 ms へ 47.03% 減少しました。

interleaved 1,000-frame Exact `current --threads 20` release-binary 4 pair は、exit status、
2,000 fields、luma、chroma、raw JSON、stdout、normalized stderr/log、すべての ordered
`fileLoc` で一致し、candidate が 4/4 で高速でした。wall-time median は 34.068 から
33.462 秒へ 1.78% 減少（1.018x）、CPU time は 291.781 から 286.734 秒へ 1.73% 減少し、
effective core は 8.56 から 8.57 へ移りました。median peak working set は 345.9 から
347.4 MiB、private bytes は 358.1 から 359.1 MiB で、4 independent run の observed peak は
bounded でした。

final release-binary matrix は Exact/IPP-fast、v0.4.0/`current`、`--threads 0`、default-five、
`--threads 20` を native/forced AVX2-disabled candidate path の両方で検証しました。36 run
すべてが 9 compatibility surface と cross-thread determinism で一致しました。更新した
30-run `current` public matrix は 5 worker mode 全体で profile ごとに 1 hash set を保持しました。
native IPP runtime を利用した full xUnit v3 suite は 1,447 tests すべてに成功しました。

### bounded current VHS companion inverse scheduling

high-worker VHS `current` は以前、各 RF block worker から `Task.Run` で analytic
companion inverse を起動し、その後同期的に待機していました。sampling では nested
ThreadPool scheduling と block された outer worker が確認されました。この isolated
change は decoder-owned bounded queue と固定 background worker を使い、2 つの inverse
FFT implementation、expression、buffer、exception precedence、ordered commit は変更しません。
input processor がなく、既存の 12-block prefetch cap を超える場合だけ有効になるため、
`--threads 20` は 8 companion worker を設定します。worker は最初の eligible inverse で
lazy start し、stateful sharpness の sequential block path では 1 worker に制限します。
queue/worker creation が失敗した場合は、元の serial real-then-companion order を維持します。

interleaved 200-frame Exact `current --threads 20` 3 A/B pair は exit status、luma、chroma、
raw JSON、stdout、normalized stderr/log、すべての ordered `fileLoc` で一致しました。paired
wall-time change の median は 1.61% 減少しました。別の 1,000-frame pair も同じ surface
で一致し、34.732 から 34.000 秒へ 2.11% 高速化しました（1.022x）。CPU time は
284.766 から 279.500 秒、effective core は 8.199 から 8.221 へ移りました。candidate の
first/second-half working-set maximum は 352.2/351.4 MiB で、progressive growth や OOM
はありませんでした。

default-five と `--threads 0` の Exact run も全 surface で一致しました。200-frame
IPP-fast pair は全 surface で一致し、7.137 から 7.102 秒へ移りました。この 0.49% の
screening delta は noise とみなし、IPP speedup には帰属しません。native IPP runtime を
利用した状態で xUnit v3 1,435 test がすべて成功しました。

final review follow-up は worker creation を lazy にし、stateful sharpness を 1 companion
に制限し、各 completion signal を dispose する前に `Set` の return を待ちます。table
candidate に対する interleaved 200-frame 3 pair は再び全 surface で一致し、wall delta は
-4.47%、+2.53%、-2.31% でした。別の foreground workload が active だったため、これらの
time は regression の否定だけに使い、table から除外します。final 1,000-frame run は保存済み
artifact と normalized diagnostic のすべてに一致し、first/second-half working-set peak は
355.2/353.8 MiB、final sample は 353.6 MiB でした。

### ordered AVX TBC sinc accumulation

16-tap interior TBC sinc path は、interpolated weight と product の計算にすでに AVX/FMA
を使います。この isolated change は instruction、float32 product、double accumulation、
cast point、left-to-right addition order を維持します。16 addition は scalar SSE2 intrinsic
で直接展開し、各 new tap を left operand に固定して baseline の NaN payload order と一致させ、
直後に完全上書きされる stack buffer の冗長な clear も省きます。scalar/boundary path は不変です。

interleaved single-thread kernel 8 pair は同じ SHA-256 を保ち、median batch time を
259.624 から 233.064 ms へ 10.23% 短縮しました（1.114x）。candidate は 8/8 で高速でした。
product decision は引き続き full decoder evidence を使います。interleaved 1,000-frame Exact
`current --threads 20` 3 pair は exit status、field count、luma、chroma、raw JSON、stdout、
normalized stderr/log、ordered `fileLoc` が一致し、candidate は 3/3 で高速でした。mean wall
time は 35.242 から 35.179 秒へ 0.18% 減少（1.002x）、mean CPU time は 281.84 から
286.22 秒へ 1.55% 増え、mean active core は 8.00 から 8.14 へ移りました。

24-run deterministic gate は Exact/IPP-fast、v0.4.0/`current`、`--threads 0`、default-five、
`--threads 20` を網羅し、全 product surface が baseline と thread mode 間で一致しました。
default、no-FMA、no-AVX、fully scalar の real-RF run も完全一致しました。異なる正負の
NaN payload、infinity、signed zero も保存済み main binary と bit-exact でした。34 focused TBC
test は normal intrinsic、AVX disabled、全 hardware intrinsic disabled で通過しました。
full xUnit v3 suite は 1,429 test が pass し、local IPP runtime に依存する expected skip は
4 件でした。

別の 2,000-frame counter gate は 4,000 field を完全一致で完了し、wall time は 68.724 から
68.473 秒へ 0.37% 改善しました（1.004x）。candidate working set は median 353.1 MiB、
maximum 359.6 MiB、first/final-third median は 352.0/357.2 MiB でした。startup 後の 9 個の
200-frame interval は 6.446-6.699 秒に収まり、throughput は progressive に低下しませんでした。
total allocation は 1,302.4 MiB、GC pause は 73 ms でした。

### managed AVX CTI line snapshot

CTI は 4 pass の各開始前に float64 chroma line の float32 snapshot を更新します。この
isolated change は検証済みの managed AVX conversion helper を再利用し、1 iteration で
8 double を 8 float に変換します。cast、padding、scalar tail、destination buffer、pass
boundary、non-AVX behavior は同じで、CTI arithmetic、state、scheduling、allocation は
変更しません。

18 focused CTI test は通常 intrinsic、AVX disabled、全 hardware intrinsic disabled で
通過しました。conversion special-value test も native/AVX-disabled path で、signed zero、
subnormal、infinity、NaN、unaligned destination、scalar tail の float bit を完全比較して
通過しました。full xUnit v3 suite は 1,428 test が pass し、local IPP runtime に依存する
expected skip は 4 件です。

interleaved long CTI kernel 8 pair は 1 output hash と同じ allocation count を維持し、
candidate が 8/8 で高速でした。median wall time は 2,522.737 から 2,396.830 ms
（4.99% 減、1.053x throughput）、median CPU time は 2,515.625 から 2,390.625 ms
（4.97% 減）となりました。

順序を反転した 1,000-frame Exact `current --threads 20` 3 pair は exit status、field count、
luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` ですべて一致し、
candidate が 3/3 で高速でした。mean wall time は 36.603 から 35.631 seconds
（2.66% 減、1.027x throughput）、process CPU time は 297.333 から 283.583 seconds
（4.62% 減）となりました。別の 24 run は Exact/IPP-fast、v0.4.0/`current`、
`--threads 0`/default-five/`--threads 20` を網羅し、各 baseline/candidate pair と
profile ごとの worker mode は全 compatibility surface で一致しました。

別の candidate 2,000-frame run は 69.119 seconds で全 4,000 field を完了しました。
peak working set は 355.75 MiB、final は 353.52 MiB、first/final-quarter mean は
341.80/350.72 MiB で、progressive peak growth や OOM はありませんでした。これは
local compute reduction であり、multicore utilization の claim ではありません。

### 以前の managed AVX double-complex inverse FFT normalization

この retained historical candidate は merged main `ea1bb8e` を基にしています。

inverse transform は各 real/imaginary component に同じ double-precision normalization factor
を乗算します。candidate は scalar operand order を維持しながら、2 個の独立した complex value
を 1 つの `Vector256<double>` で処理します。FMA、reduction、reassociation、shared state、
new allocation はありません。元の scalar loop は tail と AVX 非対応 path に残ります。JIT
disassembly には 1 個の `vmulpd` があり、FMA instruction はありませんでした。

専用 bit-equivalence test は transform length 2、4、8、32、512 で、signed zero、subnormal、
minimum normal、signed one、maximum finite、infinity、異なる NaN payload を網羅します。
5 storage test は通常 intrinsic、AVX disabled、全 hardware intrinsic disabled で通過しました。
full xUnit v3 suite は 1,426 test が pass し、IPP runtime 不在による expected skip は 4 件です。

順序を反転した length-32,768 inverse-transform kernel 8 pair は、各 measurement で 3,000
transform を実行しました。median wall time は 986.10 から 727.61 ms（26.2% 減）、median
CPU time は 1,000 から 750 ms（25.0% 減）でした。output hash は一致し、両 revision の
allocation は 616 bytes でした。

順序を反転した 1,000-frame Exact `current --threads 20` 3 pair は、luma、chroma、JSON、
stdout、normalized stderr/log、ordered `fileLoc`、frame count、metadata の 9 compatibility
surface ですべて一致しました。mean wall time は 39.867 から 39.515 seconds（0.88% 減）、
CPU time は 315.260 から 305.969 seconds（2.95% 減）でした。3 pair の wall direction は
-2.10%、-0.20%、-0.35% です。average active core は 7.91 から 7.74 となり、これは local
compute reduction であって multicore scaling の claim ではありません。peak working set は
bounded で、observed maximum は baseline 650.6 MiB、candidate 396.2 MiB、OOM はありません。

### managed AVX/AVX2 radix-11 PocketFFT butterfly

managed float32 PocketFFT の radix-11 pass は、4 個の contiguous frequency index を
AVX で同時に処理します。radix 11 が final stage の場合、AVX2 gather で 4 個の独立した
packet を読み、output を contiguous に書きます。各 lane は元の scalar pair、coefficient
multiply、add/subtract、complex twiddle、conversion order を維持します。conservative
overflow bound を超える finite input、non-finite input、scalar tail、必要な ISA がない host
は元の scalar path を実行します。FMA、reduction、reassociation、shared state、新しい
sample-sized allocation はなく、unsafe code は pinned packet gather/store に限定します。

61 mixed-radix compatibility test は通常 intrinsic、AVX2 disabled、AVX disabled、全 hardware
intrinsic disabled で通過しました。専用 direct-plan test は forward/backward の両方で
length 55、121、363 を網羅し、signed zero、subnormal、minimum normal、maximum finite、
infinity、異なる NaN payload を含みます。独立した 2 回の length-363 kernel process
（各 10 pair）は output bit を完全に維持し、median-of-medians を 103.415 から 34.925 ms
（66.23% 減）へ短縮しました。これは isolated-kernel evidence であり end-to-end claim では
ありません。

main `dbc617e` に対する順序を反転した 1,000-frame Exact `current --threads 20` 3 pair は
9 compatibility surface ですべて一致し、candidate が 3/3 で高速でした。mean wall time は
37.328 から 36.734 seconds（1.59% 減、1.016x throughput）、process CPU time は 295.823
から 293.208 seconds（0.88% 減）、average active core は 7.925 から 7.982 になりました。
別の candidate 2,000-frame run は 70.139 seconds で 4,000 field と 4,000 unique ordered
`fileLoc` を完了しました。peak working set は 647.43 MiB、final は 604.34 MiB、3 区間の
working-set mean は 590.37/598.75/601.05 MiB、先頭 20% を除いた linear slope は
-24.81 MiB/min で、progressive growth や OOM はありませんでした。

### 以前の managed AVX radix-3 PocketFFT butterfly

managed float32 PocketFFT の radix-3 pass は、4 個の独立した complex index を 1 つの
`Vector256<float>` で処理します。各 lane は元の scalar pair、coefficient multiply、
add/subtract、complex twiddle order を維持します。non-finite value または conservative
overflow bound を超える magnitude を含む packet は元の 4 scalar index を実行し、tail と
AVX-disabled host も scalar path を維持します。vector path に FMA、reduction、
reassociation、shared scratch state、allocation、cross-transform state はありません。

60 mixed-radix compatibility test は通常 intrinsic、AVX disabled、全 hardware intrinsic
disabled で通過しました。専用 direct-plan test は forward/backward の両方で length 33、
726、990 を検証します。AVX-safe な signed zero、subnormal、minimum normal、signed one と、
scalar fallback を起動する maximum finite、infinity、異なる NaN payload を個別に含みます。
optimized JIT disassembly の butterfly/twiddle arithmetic は分離した `vmulps`、`vaddps`、
`vsubps` のみで、FMA instruction はありません。

順序を反転した 6 kernel pair では、length-726 の mean time が 712.391 から 705.693 ms
（0.94% 減）、length-990 が 740.479 から 709.507 ms（4.18% 減）となり、output hash は
一致しました。順序を反転した 1,000-frame Exact `current --threads 20` 3 pair は 9
compatibility surface ですべて一致しました。mean wall time は 37.288 から 36.909 seconds
（1.02% 減）、CPU は 301.984 から 292.766 seconds（3.05% 減）、average active core は
8.10 から 7.93 でした。各 pair の wall direction は -0.79%、+0.21%、-2.46%、bounded
maximum working set は baseline 443.4 MiB、candidate 355.6 MiB です。3 `--threads 0` pair
も全 surface で一致し、mean wall time は 41.030 から 40.219 seconds になりました。
v0.4.0 と IPP-fast profile はこの managed current-profile FFT path を呼びません。

### 以前の managed AVX radix-5 PocketFFT butterfly

managed float32 PocketFFT の radix-5 pass は、4 個の独立した complex index を 1 つの
`Vector256<float>` で処理します。各 lane は scalar の add/subtract、coefficient multiply、
complex twiddle、conversion order を維持します。non-finite value または conservative overflow
bound を超える magnitude を含む packet は 4 scalar index を使います。tail と AVX 非対応 host
も scalar path を維持します。FMA、reduction、reassociation、shared scratch state、新しい
allocation、cross-transform state はありません。

59 mixed-radix compatibility test は通常 intrinsic、AVX disabled、全 hardware intrinsic disabled
で通過しました。専用 scalar-reference test は direct length-55 plan を使い、AVX-safe な signed
zero、subnormal、minimum normal、signed one と、scalar fallback を起動する maximum finite、
infinity、異なる NaN payload を forward/backward で別々に検証します。既存の double radix-8
AVX path も plan-specific overflow bound で input を preflight し、extreme/non-finite value は
fallback します。4 storage/overlap test が 3 intrinsic mode で vector/fallback path を網羅します。
radix-5 storage butterfly の JIT disassembly は 4 `vmulps`、4 `vaddps`、1 `vsubps` を含み、
FMA instruction はありませんでした。

production-size forward/back kernel loop 128 iteration は、wall time が 757.745 から 600.026 ms
（20.8% 減）、CPU が 765.625 から 671.875 ms（12.2% 減）となり、hash は一致し GC は
ありませんでした。順序を反転した 1,000-frame Exact `current --threads 20` 2 pair は 9
compatibility surface ですべて一致しました。mean wall time は 38.483 から 37.800 seconds
（1.78% 減）、CPU は 308.664 と 308.188 seconds で実質 flat、average active core は 8.02
から 8.15、average peak working set は 361.6 から 353.1 MiB でした。各 pair の wall direction
は -0.54% と -3.01% なので、conservative end-to-end claim は aggregate 1.78% とします。
memory は bounded で OOM はありませんでした。

### 以前の managed AVX radix-8 PocketFFT butterfly

managed float32 PocketFFT の radix-8 pass は、4 個の独立した complex index を 1 つの
`Vector256<float>` で処理します。各 lane は scalar の add、subtract、multiply、rotate、twiddle
order を維持し、tail と AVX 非対応 host は変更前の scalar loop を使います。vector path に
FMA、reduction、reassociation、shared scratch state、allocation、cross-transform state はありません。

58 mixed-radix compatibility test は通常 intrinsic、AVX disabled、全 hardware intrinsic disabled
で通過しました。新しい frozen scalar-oracle test は 3,840-value transform を使用し、positive/
negative zero、最小の positive/negative subnormal と normal、positive/negative one third、
positive/negative one を含みます。forward と backward SHA-256 はすべての intrinsic mode で一致します。

順序を反転した 1,000-frame Exact `current --threads 20` 2 pair の combined mean は、wall time が
36.800 から 36.149 seconds（1.77% 低下）、CPU time が 308.508 から 298.828 seconds
（3.14% 低下）になりました。interleaved 160-frame Exact 2 pair も candidate が勝ち、IPP-fast
2 pair は wall-neutral でした。opposite-order `--threads 0` と `--threads 1` check は output-exact で、
combined single-worker CPU は実質 flat だったため、serial speedup は claim しません。

更新した 30 matrix run は、各 backend の default、1、5、10、20 workers 間で luma、chroma、raw
JSON、stdout、normalized stderr/log、ordered `fileLoc` がすべて一致しました。candidate の
1,000-frame peak は 352.1/450.5 MiB、baseline は 370.6/370.2 MiB でした。matrix maximum は
両 revision の single-worker case における 817.0 MiB で、progressive growth や OOM はありません。

### AVX current chroma ACC segment scaling

`current` VHS automatic chroma gain path は、独立した 4 sample を managed AVX で scaling します。
gain recurrence は scalar のまま元の順序で進め、その後に 4-value vector を構築します。finite group
は従来の double-to-float32 conversion、double multiply、result-to-float32 conversion point を維持し、
non-finite group と AVX 非対応 host は元の scalar expression を実行します。FMA、reduction、
reassociation、state boundary change、新しい array、追加の pooled lifetime はありません。

15 focused xUnit v3 test は通常 intrinsic、AVX disabled、全 hardware intrinsic disabled で通過しました。
bit-pattern test は vector tail、signed zero、subnormal、maximum finite value、infinity、異なる NaN
payload を含みます。JIT inspection は scalar gain addition、AVX conversion/multiply、FMA instruction
なしを確認しました。

独立した 2 session の interleaved 1,000-frame Exact `current --threads 20` 5 pair のうち、candidate
は 4 pair で勝ちました。2-pair session は wall median を 34.782 から 34.446 seconds（0.97% 低下）、
CPU を 295.266 から 291.602 seconds（1.24% 低下）へ移動しました。3-pair session は wall を
35.255 から 35.052 seconds（0.57% 低下）へ移動しましたが、CPU median は noise により 2.20%
高く、Exact の CPU-efficiency claim は行いません。IPP-fast 2 pair は wall を 31.764 から
31.247 seconds（1.63% 低下）、CPU を 202.539 から 195.578 seconds（3.44% 低下）へ移動しました。

すべての pair で exit status、luma、chroma、raw JSON、stdout、normalized stderr/log、ordered
`fileLoc` が一致しました。追加 gate は Exact/IPP-fast の `--threads 1`、explicit zero、default five、
`--threads 20` を含みます。candidate と baseline の memory peak は既存の約 715 MiB observed process
window 内に留まり、progressive growth や OOM はありませんでした。

### Worker-local current VHS radix scan

3-stage `current` VHS level-quantile path は、各 worker の private radix histogram を fixed source/
histogram pointer で scan します。worker partition、sortable-prefix conversion、exceptional-value
fallback、bucket selection、integer increment は変えず、candidate は managed array の bounds
check と address calculation の繰り返しだけを除去します。

1,048,576 value と 4 worker の long-process microbenchmark 6 pair は、scan median を 1.2953 から
1.1646 ms（10.1% 低下）へ移動し、result bit は一致しました。interleaved 320-frame Exact
`current --threads 20` 4 pair はすべて candidate が勝ち、wall median は 16.4905 から
16.2279 seconds（1.59% 低下）、CPU は 132.99 から 127.28 seconds（4.29% 低下）、median
peak working set は 403.6 から 401.4 MiB へ移動しました。opposite-order 1,000-frame 2 pair も
両方 candidate が勝ち、wall は 40.321 から 39.842 seconds（1.19% 低下）、CPU は 325.28 から
317.73 seconds（2.32% 低下）、candidate peak working set は約 406 MiB に bounded でした。
default-five の 320-frame 1 pair は 31.483/31.506 seconds で wall-neutral、CPU は 2.50% 低下です。

全 pair の exit status、luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` は
一致しました。hardware intrinsic enabled では 34 focused xUnit v3 test がすべて通過しました。
disabled では 33 test が通過し、AVX-specific edge-scan test 1 件を設計どおり skip しました。baseline/candidate gate は
`--threads 0`、default-five、`--threads 20` を含み、repeated high-worker run はその mode 内で
deterministic でした。異なる requested worker mode を 1 つの shared hash oracle とは扱いません。

### VHS Envelope の直接 segmented scan

20 workers 以上を要求した場合、staged VHS payload materialization は full RF Envelope を
span workspace へ先に copy しません。materializer は既存 decoder block を存続させ、その
Envelope segment を直接読み取ります。mean は既存の 128-sample recursive split、8-lane
float32 leaf sum、conversion point、addition order を保持します。dropout scan も index order、
threshold、hysteresis、merge distance、minimum length、final range order を維持します。低 worker
count は従来の contiguous copy/scan path を使い、既存 caller は明示的な full materialization
を引き続き要求できます。shared idle/ordinary/staged atomic state は通常 cache operation と
staged acquisition の同時進入を防ぎます。staged lease の dispose 前は通常 read と明示的
cache invalidation を拒否し、segmented analysis が参照中の pooled block の早期返却を防ぎます。

high-worker test は block boundary をまたぎ、payload assembly 中の target Envelope が未初期化の
ままであること、mean の bitwise 一致、複数 window/threshold の contiguous detector との一致、
最後の deferred full materialization を検証します。active lease 中は通常 read/cache invalidation が
拒否され、dispose 後に再開することも検証します。blocking-loader regression は通常 read を先に
開始し、staged acquisition が拒否され loader concurrency が 1 のままであることを確認します。
threshold 直下の worker test は従来 path を
固定します。標準 1,425 test はすべて通過し、全 .NET hardware intrinsic を無効化した 13 reduction
test と 34 block-cache test も通過しました。

opposite-order 1,000-frame Exact `current --threads 20` pair 2 組は各 2000 ordered field を生成し、
exit status、luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が一致しました。
wall-time median は 43.001 から 42.880 秒へ 0.28% 改善し、CPU は 342.50 から 324.50 秒へ 5.26%
減少、peak working set は 753.0 から 721.2 MiB へ低下しました。160-frame v0.4.0 pair 2 組では
wall time が 10.893 から 10.670 秒へ 2.05% 改善し、CPU は 78.44 から 77.49 秒、peak working set
は 795.1 から 752.9 MiB へ低下しました。

publication 前の local read-only review で cache-ownership race を検出しました。atomic gate 前後の
opposite-order 1,000-frame pair 4 組は 9 compatibility surface が一致しました。
baseline/candidate wall median は 39.770/39.932 seconds、pair change は +0.94%、+1.15%、
-1.04%、-0.03% で 2 勝ずつでした。CPU median は 320.23 から 314.67 seconds へ 1.74% 減少し、
median peak working set は 405.25 から 399.84 MiB へ低下しました。したがって final atomic gate は
wall-time speedup/regression ではなく performance-neutral と分類します。

別の正式 package style による 160-frame Exact `current` A/B 4 組は baseline/candidate が各 2 勝で、
wall-time median は 7.691/7.909 秒、CPU は 67.50/68.02 秒でした。pair ごとの方向が矛盾するため、
短い window は inconclusive とし、speedup も regression も主張しません。公開 matrix は Exact
`current --threads 20` median をそのまま示しますが、cross-batch cell を因果 evidence にはしません。
60 .NET run と 15 Python PR341 run は各 profile 内で 1 hash set でした。Python v0.4.0 は 15 run
すべてで luma、chroma、JSON、normalized log hash が異なったため、strict oracle は引き続き
`g4315520 --threads 0` です。

### Caller output へ直接書く managed double PocketFFT

managed double-precision complex FFT plan は factor-pass parity から caller output または
worker-local scratch を initial source として選びます。各 pass は同じ 2 span を交互に使い、
final pass が必ず caller output へ着地します。これにより final full-array copy と 2 個目の
worker-local `Value[]` buffer を除去しました。factorization、twiddle、radix order、data
type、normalization、各 floating-point expression は変わらず、FMA/reassociation も追加して
いません。complex input/output overlap は memmove-equivalent span copy を維持します。
real input storage と complex output が overlap する場合は旧 2-buffer result path を使い、
全 input 消費後にのみ copy します。

標準 xUnit v3 test 3 件は length 2、4、8、64、512、32768、131072 の forward、inverse、
real-forward hash と、length 8、64、512 の exact/forward-shifted/backward-shifted overlap を
固定します。special-value path-consistency case は signed zero、subnormal、maximum finite、
infinity、異なる NaN payload を repeated、caller-output、in-place storage path 間で bitwise
比較し、real case は guarded 2-buffer overlap fallback も通過します。NaN payload selection は
CPU 間で変わり得るため、これは machine-specific な absolute special-value oracle では
ありません。native hardware、AVX disabled、全 .NET hardware intrinsic disabled で通過します。

default 32768-sample RF block の product-shape microbenchmark 10 pair は real-forward と
complex-inverse の median を 1768.90 から 1515.88 ms（14.30% 低下、1.167x throughput）へ
動かし、candidate は 10 pair すべてで勝ち、hash は一致しました。overlap-aware final build
は後に 1498.46、1516.92、1560.97 ms を記録し、以前の candidate median を挟んだため、
normal non-overlap hot path の gain が維持されたことを確認しました。

final candidate の 160-frame Exact `current` 5-worker 4 pair は exit status、field count、
luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が一致し、
candidate は 3 pair で勝ちました。median wall time は 20.72 から 20.36 seconds
（1.73% 低下）、process CPU time は 84.20 から 83.51 seconds（0.83% 低下）、median
peak working set は 370.5 から 364.2 MiB へ移動しました。この same-moment A/B が
5-worker path の causal gate であり、public table の ratio 低下は cross-batch の
denominator/timing movement で、candidate regression ではありません。

normal production path の order-reversed 1000-frame Exact `current --threads 20` 2 pair は
各 2000 ordered field を生成しました。median wall time は 41.10 から 40.12 seconds
（2.38% 低下）、CPU time は 336.77 から 328.91 seconds（2.33% 低下）、peak working
set は約 439.8 から 416.7 MiB へ移動し、9 compatibility surface はすべて一致しました。
public 90-run matrix も .NET profile/mode と Python PR341 profile/mode ごとに 1 hash set
でした。Python v0.4.0 は 15 run すべてで異なる luma、chroma、JSON、normalized-log hash
を生成したため、strict oracle は `g4315520 --threads 0` のままです。

### Bounds-check を除いた managed float32 SOS state access

managed Exact float32 SOS の generic kernel は、検証済みの section ごとに 2 float という state
layout を維持し、recursive state を GC-tracked span reference 経由で参照します。JIT disassembly
では forward と backward の inner loop から 2 個の state bounds branch が消え、scalar multiply、
add、subtract の順序が変わっていないことを確認しました。FMA、SIMD reassociation、conversion、
padding、lifetime、ownership の変更はありません。

interleaved 160-frame Exact `current --threads 20` 6 pair は、9 種類の artifact、metadata、console、
normalized-log surface がすべて一致し、candidate は 5 pair で勝ちました。median wall time は
11.499 から 11.373 seconds へ 1.10% 減少し、throughput は 1.12% 向上、CPU time は 92.133
から 90.695 seconds へ 1.56% 減少しました。default-five 4 pair は 21.651 から 21.555
seconds へ 0.44% 減少し、candidate は 3 pair で勝ちました。0.74% の CPU 増加と working-set
movement は short-run noise の範囲内です。
これらの A/B script は `--start` を省略して CLI default の `--start 0` を使用するため、
160-frame time を frame-100 matrix と直接比較してはいけません。

1,000-frame Exact `current --threads 20` pair は 42.463 から 42.221 seconds へ短縮し、wall time
は 0.57%、CPU time は 351.297 から 347.672 seconds へ 1.03% 減少しました。peak working
set は 439.2 から 437.7 MiB へ移動しました。これはこの gate で bounded behavior を示すもので、
一般的な memory reduction は主張しません。baseline/candidate の 6-run thread gate は
`--threads 0`、default-five、`--threads 32` を対象とし、9 種類の surface がすべて一致しました。

独立した checked-index float32 reference test は fixed input bit pattern を使い、section count
5、8、10、31、32、33、64 を対象にします。aggregate SHA-256 は
`61A8966C55B1331509E0A60D6B731FA125F9406185C2C4AAF4E4CBC42F16C80D` で、通常時と全 .NET
hardware intrinsics 無効時の両方で通過します。

### Full-span reverse を除いた managed float32 SOS second pass

managed Exact float32 forward/backward SOS path は以前、forward filter、work span 全体の
reverse、再度の forward filter、最後の reverse を順に実行していました。現在の second
pass は元の span の最後の sample から最初へ進み、結果を同じ index に書き戻します。
したがって同じ sample sequence と endpoint initial condition を観測しつつ、2 回の full-buffer
reverse を除去します。1、2、4 section および generic kernel は per-sample section order、
expression、conversion point、recursive-state update を維持し、FMA、SIMD reassociation、
precision change は導入していません。
この以前の candidate は merged main `73dd014` を基にし、測定 source blob は
`c3aac76922ad525884cf26779b2035db76248547`、baseline blob は
`766e0926fec7c07bb2bc0a20c0c39824b8f05996` です。

2、4、5 SOS section を対象に、opposite-order の 32K microbenchmark を 21 pair 実行しました。
baseline/candidate median はそれぞれ 154.540/151.793 microseconds（1.78% lower）、
195.178/190.025 microseconds（2.64% lower）、278.516/275.540 microseconds（1.07% lower）で、
全 output SHA-256 が一致しました。40 件の focused DSP test は通常時と全 .NET hardware
intrinsics 無効時に各 1 回通過し、標準 xUnit v3 execution は合計 80 件です。

interleaved 160-frame Exact `current` 8 pair では candidate が 6 pair で勝ち、paired-median
wall time は 2.43% 減少、throughput は 2.49% 向上、CPU time は 2.69% 減少しました。
別の Exact v0.4.0 160-frame pair も一致しました。全 pair で luma、chroma、raw JSON、
stdout、normalized stderr/log、ordered `fileLoc` が同一です。

1,000-frame Exact `current` pair は 39.242 から 37.908 seconds へ短縮し、wall time は
3.40%、CPU time は 330.656 から 310.141 seconds へ 6.20% 減少しました。baseline の
first/final-third working-set median は 385.7/387.7 MiB、peak 391.3 MiB、candidate は
380.9/384.2 MiB、peak 387.8 MiB です。これはこの gate で bounded behavior を示すもので、
一般的な memory reduction は主張しません。6-run thread gate は `--threads 0`、default-five、
`1`、`5`、`10`、`20` を対象とし、全 mode の 7 compatibility surface が完全一致しました。

### 入力を保持する real FFT first pass

入力を保持する real FFT path は以前、complete input を pooled packed array へ copy した後、
最初の radix pass で 2 番目の pooled array へ copy と transform を行っていました。現在は
2 個の work array を先に rent し、caller の read-only span から first pass を直接実行します。
後続 radix pass、packed-spectrum materialization、binary64 expression、arithmetic order は不変で、
owned-input path も変更していません。従来の hot-buffer reuse order を保つため、最終 packed
array は pool へ最後に返します。

最終 2 組の opposite-order pair は、process ごとに 20,000 回の warmed 32K forward+inverse を
実行しました。preserving forward の平均は 111.773 から 107.062 microseconds へ短縮し、
time reduction は 4.22% です。forward+inverse 全体は 250.744 から 248.181 microseconds へ
1.02% 短縮しました。全 run で forward と inverse の SHA-256 は完全一致です。focused bitwise
test は length 2、4、4,096、32,768 を対象とし、caller input の byte-for-byte 保持、output tail
sentinel、既存 owned-input path との一致も確認します。

最終 v0.4.0 160-frame A/B 2 pair は baseline median 8.282 から 8.237 秒へ 0.54% lower、
最終 `current` 400-frame 1 pair は 16.184 から 16.010 秒へ 1.08% lower でした。全 pair で
luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が一致しました。
別の 1,000-frame `current` run も同じ 7 surface が一致し、memory は bounded でした。baseline
first/final-third working-set median は 649.1/643.9 MiB、peak 681.9 MiB、candidate は
644.3/641.5 MiB、peak 686.2 MiB です。一般的な memory reduction は主張しません。

12-run determinism matrix は Exact v0.4.0 と `current` の `--threads 0`、default-five、`1`、
`5`、`10`、`20` を対象とし、各 profile 内で全 surface が stable でした。AVX と AVX2 を
無効化した 4 fixed-hash test も通過しました。full Release suite は xUnit v3 test 1,416 件を
すべて通過しました。

### Exact VHS analytic-spectrum traversal の融合

Exact VHS analytic path は以前、full complex spectrum を RF video filter、MTF filter、real
Hilbert scale のためにそれぞれ 1 回ずつ走査していました。現在は同じ順序の 3 stage を 1 回の
traversal で実行します。2 回の complex multiply は既存の binary64 FMA expression と rounding
point を維持し、その後に同じ real multiply を実行します。non-finite pair は従来の scalar
sequence を使い、Span が alias する場合は元の 3-pass implementation に fallback します。

1,048,576 element に対する 12 alternating pair は、warmed kernel median を 4.280 から
2.976 ms へ短縮しました。time reduction は 30.47%、throughput は 1.438x で、output は
bit-identical、steady-state allocation は zero です。4 組の 400-frame interleaved pair は
全 captured surface が一致し、end-to-end wall-time median は 16.50 から 16.33 秒へ約 1.0%
改善しました。
alias guard の review 後、最終 source で 12-pair recheck を 1 回だけ実施し、4.246 対
2.833 ms（1.4984x）でした。これは通常の non-alias kernel が維持されたことの確認であり、
上記の固定した full-batch measurement を置き換えるものではありません。

long compatibility observation は Exact の両 behavior profile について baseline/candidate の
1,000-frame `--threads 20` pair を 1 組ずつ実行しました。`current` は 38.198 対 37.951 秒
（0.65% lower）、v0.4.0 は 48.146 対 47.135 秒（2.10% lower）でした。これは single-pair
observation であり、sustained throughput claim ではありません。candidate の first/final third
working-set median は `current` で 462.4/462.9 MiB、v0.4.0 で 434.5/437.5 MiB で、bounded かつ
ほぼ stable ですが小幅な増加があります。CPU と単発の peak working set は run ごとに変動する
ため、一般的な CPU または memory reduction は主張しません。

全 A/B で luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が一致しました。
12-run candidate matrix は Exact の両 profile と `--threads 0`、default-five、`1`、`5`、`10`、
`20` を対象とし、さらに 2 run で全 hardware intrinsic を無効化して通常の `--threads 1`
artifact と一致しました。更新した 6-path table の 60 .NET run も、profile/thread group ごとの
全 captured surface で 1 hash を維持しました。Release build は標準 xUnit v3 test 1,416 件を
すべて通過し、focused 28-test class も fully scalar process で再度通過しました。

### Managed complex real-input direct staging

caller-buffer managed complex FFT は以前、real double から完全な `Complex` destination を作り、
その destination を plan の thread-local working value へコピーしていました。real-input entry
point は現在、double を同じ working value へ直接書き込んでから、変更のない FFT execute path
を呼び出します。factorization、butterfly arithmetic と順序、normalization、scratch selection、
最後の `Complex` output conversion は変更していません。

8 opposite-order microbenchmark pair は、measurement ごとに 512 回の warmed 32K transform を
steady-state allocation なしで実行しました。baseline median は 437.804 us/call、candidate は
424.065 us/call で、3.14% 減少しました。checksum と sample output bit は一致しました。
4 組の 200-frame interleaved pair も全 surface が一致し、wall-time median は 9.93 から
9.52 秒へ減少しましたが、因果的な claim には長い gate を使用します。

opposite-order の 1,000-frame Exact `current --threads 20` pair 2 組は、各 run で 2,000 ordered
fields を生成しました。wall-time median は 40.030 から 39.448 秒へ 1.45%、CPU-time median は
323.492 から 317.922 秒へ 1.72% 減少しました。sampled peak working-set median は baseline
444.3 MiB、candidate 406.0 MiB でした。個々の peak は process placement で変動するため、
一般的な memory reduction ではなく bounded-memory evidence として扱います。

全 short/long run で luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が
一致しました。別の 24-run gate は Exact/IPP-fast、両 behavior profile、`--threads 0`、
default-five、`--threads 20` を対象としました。さらに default、no-FMA、no-AVX、fully scalar の
hardware-intrinsic setting 4 種も一致しました。native bridge smoke は system DLL dependency
のみで通過し、標準 xUnit v3 test は 1,403/1,403 で通過しました。

### Managed real-FFT owned input

compact VHS block は raw demodulation output を公開しません。sharpness、chroma trap、optional
video clipping が final source を決めた後、managed Exact real FFT はその stage が排他的に
所有する array を in-place で消費します。arithmetic、packing、spectrum write order、filter、
inverse transform は変更していません。diagnostic/raw-output call は copying API を維持し、
IPP-fast は同じ native forward FFT を呼び出します。

12 alternating microbenchmark pair は、measured batch ごとに 512 個の独立した 32K input を
それぞれ 1 回だけ transform しました。owned path は 11 pair で勝ち、median は 54.953 から
53.732 ms へ 2.22% 減少しました。4 pair の 160-frame end-to-end check は throughput-neutral
でした。sustained gate は opposite-order の 1,000-frame Exact `current --threads 20` pair 2 組で、
combined wall time は 79.259 から 78.562 秒へ短縮しました（0.88% lower、1.0089x throughput）。
2 run とも candidate が勝ちましたが、combined CPU time は 0.63% 増えたため CPU-efficiency
improvement とは主張しません。

4 long run はすべて 2,000 ordered fields を生成し、luma、chroma、raw JSON、stdout、normalized
stderr/log、ordered `fileLoc` が一致しました。candidate 2 run の first/final-third working-set
median は 441.6/443.7 MiB と 579.7/563.4 MiB でした。peak は process placement により大きく
変動したため memory reduction は主張せず、どちらにも progressive growth はありません。
別の 24-run gate は Exact/IPP-fast、両 behavior profile、`--threads 0`、default-five、
`--threads 20` を対象とし、全 captured surface が baseline と一致して thread setting 間でも
deterministic でした。native bridge smoke は system DLL dependency のみで通過し、IPP case を
skip せずに全 1,402 xUnit v3 tests が通過しました。

### Managed AVX real radix-4 FFT

Exact real-FFT の forward/backward radix-4 stage は、AVX block ごとに独立した complex
point を 2 つ処理します。各 lane は scalar の multiply/add/subtract order を維持し、
FMA は使いません。scalar tail と従来の no-AVX path も残しています。length 4 から
131,072 までの 96 forward/inverse bit-pattern case は baseline と byte-for-byte で一致
しました。JIT disassembly には個別の `vmulpd`、`vaddpd`、`vsubpd` があり、fused
multiply-add instruction はありませんでした。

length 131,072、3,000 forward/inverse transform の opposite-order microbenchmark 6 組で、
wall-time median は 3.7935 から 3.6182 秒へ短縮しました（4.62% lower、1.0485x
throughput）。160-frame Exact `current --threads 20` の 4 pair では wall-time median が
4.27%、CPU time が 12.49% 減少し、対応する v0.4.0 と IPP-fast check は neutral から
slightly faster でした。全 run で capture した 9 compatibility surface が一致しました。
上の独立した 30-run matrix を public absolute timing に使い、因果 A/B の percentage を
各 cell にそのまま当てはめてはいません。

opposite-order 1,000-frame pair 2 組はより neutral に近く、combined wall time は 80.222
から 79.549 秒へ 0.84% 減少した一方、combined CPU time は 651.172 から 658.203 秒へ
1.08% 増加しました。したがって 160-frame result は short-window hotspot evidence であり、
sustained throughput が同じ percentage で改善するという主張ではありません。

1,000-frame sampled run は 2,000 fields を完了し、luma、chroma、JSON hash が一致しました。
peak working set は 415.3 MiB、final-third median は 409.9 MiB で、first-third median より
2.3 MiB 高いだけでした。progressive memory growth はありません。標準 xUnit v3 suite は
1,401 tests が通過し、そのうち固定した radix-4 hash 4 件は AVX disabled でも通過します。

直前の 40-frame table は fixed startup cost、特に Python の startup cost を過大に反映
していました。例えば default IPP-fast `current` は window を 160 frames に広げると
6.351x から 5.450x になりました。Python は 19.791 から 53.288 秒への増加に対し、
.NET は 3.116 から 9.778 秒へ増えています。これは startup cost の希釈であり、
candidate の regression ではありません。以前の NTSC Betamax HiFi table は別の private
fixture を使うため比較できません。今後は 160-frame、3-pass method を維持し、因果的な
性能判断には引き続き matched long A/B を使います。

全 60 .NET matrix run は、capture した全 surface で deterministic でした。merged Python
PR341 の 15 run も deterministic でした。Python v0.4.0 は
15 run で 15 種類の luma、chroma、raw JSON、normalized-log hash を生成しましたが、
stdout、normalized stderr、ordered `fileLoc` は安定していました。したがって strict
oracle は任意の multi-worker run ではなく、Python v0.4.0 `g4315520 --threads 0` のままです。

### split-input VHS Rust phase-difference AVX

caller-buffer の real/imaginary VHS Rust FM path は、AVX block ごとに 8 個の有限な
float32 lane を処理します。double-to-float conversion、signed-minimum adjustment、
atan polynomial の multiply/add order、floor-based tau wrapping、frequency scaling、
float-to-double output conversion は変更していません。non-finite lane を 1 つでも含む
block は scalar fallback を使います。AVX が利用可能な場合、remainder は既存の 4-lane
loop を使い、AVX のない host は scalar path を使います。interleaved `Complex` path は
意図的に 4-lane のままです。
同じ構造の 8-lane 実験は CPU time を減らした一方、Exact `current --threads 20` の
wall-time median を 6.83% 悪化させました。

tiered compilation を無効にした 6 組の alternating-process microbenchmark では、
32,768 samples、30,000 calls の split-kernel median が 1.675 から 1.131 秒へ短縮
（32.5% lower、1.481x throughput）し、checksum は一致しました。final split-only
6-pair 160-frame IPP-fast `current --threads 20` check は near-neutral で、candidate は
4 勝 2 敗、通常の median は 6.541 から 6.504 秒へ 0.57% 短縮し、median CPU time は
0.89% 増加しました。

より安定した opposite-order 1,000-frame pair 2 組では、combined wall time が 71.280
から 70.645 秒へ 0.89% 短縮（1.0090x throughput）、CPU time が 416.906 から
415.891 秒へ 0.24% 減少し、allocation が 1.326 から 1.319 GiB へ減少しました。
candidate の maximum sampled working set は 383.5 MiB、main は 384.6 MiB で、
first/final-third sample に progressive growth はありませんでした。24-run Exact/
IPP-fast thread gate は両 behavior profile の `--threads 0`、default-5、`--threads 20`
を対象とし、capture した luma、chroma、raw JSON、stdout、normalized stderr/log、
ordered `fileLoc` はすべて一致しました。focused xUnit v3 13 cases は native hardware、
AVX2 disabled、all hardware intrinsics disabled の各 mode で通過し、全 1,385-test suite
と warning 0 の Release build も通過しました。

### compact Exact VSync radix staging

Exact `current` の parallel VSync quantile selector は、2 段の 16-bit stage の代わりに
11+11+10-bit radix stage で同じ 32-bit sortable prefix を決定します。最終 candidate
prefix、source-order filter、Quickselect input、floating-point expression、detector state
は変わらず、sequential path も変更していません。worker-private histogram の最大 storage
は 512 KiB から 16 KiB、detector 内部の 4-worker cap では 2 MiB から 64 KiB になります。

IPP-fast は以前の 16+16-bit route を明示的に維持します。両 backend に compact route を
適用した初期候補では、IPP の 4-pair gate が wall time で 1.6% 遅く、CPU time で 1.8%
高くなりました。Exact だけを compact stage に route した最終形では、IPP の 4-pair
check は neutral で、wall time は 1.31% 短縮、CPU time は約 0.4% 増加しました。
IPP contract 内の baseline/candidate artifact は exact です。

fixed production-size selection probe では、8 paired batch の全てで compact form が勝ち、
median wall time は 0.966511 から 0.891624 秒へ 7.75% 短縮（throughput 約 1.084x）、
checksum `000000002704317B` は不変でした。これは hotspot evidence であり、end-to-end
claim ではありません。4 組の interleaved 160-frame Exact pair は balanced total wall
time を 34.113 から 33.114 秒へ 2.93%、CPU time を 303.11 から 278.11 秒へ 8.25%
短縮しました。個別には 2/4 pair の勝利なので、short result には noise があります。

causal end-to-end gate は opposite-order の 1,000-frame Exact `current --threads 20` pair
2 組です。combined wall time は 82.260 から 80.387 秒へ 2.28% 短縮（1.0233x
throughput）、CPU time は 644.594 から 640.391 秒へ 0.65% 減少しました。candidate の
working-set sample は各 run 内で bounded でした。process placement のばらつきにより
observed peak は 387.5 から 418.7 MiB でしたが、継続的増加や OOM behavior はありません。

最終 strict gate は 44 baseline/candidate run です。8 short interleaved pair、2
opposite-order long pair、2 behavior profile の `--threads 0`、default-5、
`--threads 20` を横断する 24 Exact/IPP-fast thread run を含みます。luma、chroma、raw
JSON、ordered `fileLoc`、stdout、normalized stderr/log はすべて一致しました。focused
detector suite は 25/25、全 1,382 xUnit v3 test は native hardware、AVX2 disabled、all
hardware intrinsics disabled の各 mode で通過しました。Release solution build は warning
0、error 0 でした。

### contiguous AVX2 VSync radix histogram merge

この merge 済み historical candidate は main `845d8d1` を基にし、executable の
SHA-256 は `7BAC056495F42BF4327F6E9D99AF4168F0FA585319BC9CF9F9D09E84B4A3E632` です。
`current` VSync quantile path は各 worker-private radix histogram を contiguous span として
merge します。最初の worker を destination に copy し、残りを元の worker order で加算
します。AVX2 は 8 個の exact integer bucket を一度に処理し、tail と AVX2 非対応 CPU
では同じ順序の scalar path を使います。floating-point expression、quantile target、
detector state、ordered commit boundary は変わりません。

isolated production-size radix probe では、cache-local merge が old bucket-major merge
との 8 pair 全てで勝ち、wall median は 1.169541 から 1.034975 秒へ 11.51% 短縮しました。
explicit AVX2 integer loop も cache-local scalar form との 8 pair 全てで勝ち、1.043777
から 0.971152 秒へ 6.96% 短縮し、checksum は exact でした。この kernel result は
hotspot の確認用で、end-to-end speedup としては扱いません。

stable end-to-end result は opposite-order の 1,000-frame IPP-fast
`current --threads 20` pair 2 組です。combined wall time は 72.913607 から 71.789921 秒へ
1.54% 短縮（1.0157x throughput）、CPU time は 426.328125 から 422.593750 秒へ 0.88%
減少しました。peak working set は約 373 MiB で、first-/last-third sample に継続的な
増加はありません。4 long run の luma、chroma、raw JSON、ordered `fileLoc`、normalized
stderr、normalized log は 1 hash set でした。

complete strict gate は 48 baseline/candidate run です。short interleaved 10 pair、
opposite-order long 2 pair、そして 2 behavior profile の `--threads 0`、default-5、
`--threads 20` を横断する 24 Exact/IPP-fast thread-matrix run を含みます。luma、chroma、
raw JSON、ordered `fileLoc`、normalized stderr/log はすべて一致し、stdout を capture
した short/thread-matrix gate でも一致しました。更新した 30 `current` matrix run は
cross-thread deterministic でした。全 1,382 xUnit v3 test は native hardware、AVX2
disabled、all hardware intrinsics disabled の各 mode で通過しました。

### deterministic PAL VSync median selection

この historical candidate は merged main `ae3722d` を基にし、executable の SHA-256 は
`AAAB4B0A884D0F22B361E369A55A2C475DD2D042806043B25EAFB5DF188B7860` です。
`NumpyReduction` は 32,768 values まで待たず、4,096 values 以上を既存の deterministic
introselect へ送るようになりました。これにより約 6K/15K samples の PAL VSync MAD
group で full sort を避けます。threshold 未満の input と positive/negative zero を両方
含む array は full sort のままです。NaN は selection 前にそのまま返し、even-length
input は upper middle value を選択し、lower partition の maximum を scan して、同じ
順序で `(lower + upper) / 2.0` を評価します。focused test は allocation API と
caller-scratch API の両方で 4,095、4,096、4,097 values を検証します。

固定 160-frame IPP-fast `current --threads 20` の 4 組の interleaved
baseline/candidate pair は wall-time median を 7.253 から 6.542 秒へ短縮し
（9.80% lower、10.87% higher throughput）、全 4 pair で candidate が勝ちました。
CPU median は 39.453 対 39.469 秒で実質同じです。固定 1,000-frame gate は
38.478 から 35.487 秒へ 7.77% 短縮し、CPU time は 215.016 から 212.422 秒へ
1.21% 減少、peak working set は 368.3 対 368.1 MiB で bounded のままでした。
candidate の first-/last-third working-set median は 361.6/366.3 MiB で、継続的な
増加はありませんでした。

Exact と IPP-fast の baseline/candidate matrix は 2 compatibility profile の
`--threads 0`、default-5、`--threads 20` をカバーしました。その 24 run、8 short A/B
run、2 long-gate run は luma、chroma、raw JSON、ordered `fileLoc`、stdout、normalized
stderr/log がすべて一致しました。60 回の .NET performance-matrix run は全 thread
mode で profile ごとに 1 hash でした。merged Python PR341 は deterministic でしたが、
Python v0.4.0 は 15 run で 15 種類の luma、chroma、JSON hash を生成し、明示的
non-default worker の 12 run も 12 種類でした。strict oracle は引き続き Python
v0.4.0 `g4315520 --threads 0` です。

以前 1,000-frame `--fallback_vsync` gate に使用した private NTSC VHS fixture は今回
利用できませんでした。この gate は再実行せず、古い fallback evidence から今回の
claim を推定していません。

### oversized raw-FLAC mapped seeking

raw-FLAC parser は signed 32-bit sample range を超え、metadata chain が complete、
single fixed block size と fixed blocking strategy を使い、seektable を持たない 40 MHz
mono PCM16 stream だけを mapped libsndfile read の対象にします。first-frame header は
STREAMINFO と一致し CRC-8 を通過する必要があります。mapper は pinned FFmpeg/PyAV
path の first-frame PTS と RF-position rounding を integer arithmetic で再現します。
この path は ordinary parallel VHS decode だけで有効です。`--threads 0/1`、debug plot、
GNU Radio AFE input、nonzero `--sharpness`、resampling、variable-block stream、seektable
付き stream、unknown layout は FFmpeg のままです。mapping、seek、read、
length-boundary failure は同じ logical sample から一方向に FFmpeg fallback します。
先頭、中間、signed 32-bit boundary、near EOF の direct probe は FFmpeg と reference
FLAC decoder の両方に一致しました。

36 回の interleaved baseline/candidate real-RF run は luma、chroma、raw JSON、ordered
`fileLoc`、stdout、timing-normalized stderr、timestamp-normalized log がすべて一致
しました。Exact `current --threads 20` の 200 frames は 14.876 から 11.135 秒へ
25.15% 短縮し、effective cores は 6.99 から 8.06、peak working set は 803.4 から
407.0 MiB になりました。reverse-order 1,000-frame 2 pair は 53.230 から 44.055 秒へ
17.24%、effective cores は 6.76 から 7.41、peak working set は 773.2 から
402.4 MiB です。32 workers/100 frames は 10.030 から 6.679 秒へ 33.41% 短縮し、
effective cores は 6.80 から 8.88 へ増えました。変更していない single-worker route
は 13.901 対 13.877 秒で neutral です。

別の 200-frame gate は Exact v0.4.0 を 22.24%、IPP-fast `current` を 24.64%
短縮しました。この mapped-seeking change は `v0.4.0-1.5.3` で release 済みで、
これらの historical timing は上の最新 table に引き継いでいません。

### managed current CTI quotient/finish AVX

既存の 8-lane AVX/FMA CTI distance path は、同じ lane を pinned reciprocal
refinement、threshold gate、lower/upper weighting、target-delta construction、rounded
output まで処理するようになりました。各 lane は元の float subtraction/FMA sequence、
float-to-double conversion point、double-precision weighting/FMA sequence、double として
保存する前の final float rounding を維持します。scalar tail と AVX、FMA、SSE4.1 の
いずれかに非対応の host は以前の path を使います。JIT disassembly では 2 個の
4-lane finish group が `vfmadd*`、`vblendv*`、conversion instruction として hot loop
に inline され、8 回の scalar `FinishSample` call が消えたことを確認しました。

production-size kernel の interleaved 8 pair は同一 SHA-256 を維持し、wall median を
416.746 から 336.922 ms へ 19.16%、process CPU median を 2578.125 から
1742.188 ms へ 32.42% 削減しました。160-frame Exact
`current --threads 20` 6 pair は全 surface を一致させ、wall median を 14.08 から
13.78 秒へ 2.1% 削減し、candidate は 5 勝しました。reverse-order 1,000-frame
Exact 2 pair の combined median は 54.42 から 52.76 秒へ 3.05%、CPU time は
377.59 から 373.18 秒へ減少し、effective core は 6.94 から 7.07 へ増加しました。
candidate sampled peak working set median は 17.3 MiB 高いものの 799 MiB 未満で、
progressive growth はありませんでした。

同条件の 160-frame IPP-fast 6 pair は 3 勝 3 敗で、paired mean wall-time change は
+0.13% でした。そのため IPP path は throughput-neutral と分類し、causal IPP
speedup は主張しません。表の新しい IPP cell は current-build snapshot であり、
以前の表との差全体をこの patch に帰属させるものではありません。

24 baseline/candidate RF gate は Exact/IPP-fast、v0.4.0/`current`、`--threads 0`、
default-5、`--threads 20` をカバーしました。luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、ordered `fileLoc`、cross-thread
determinism はすべて一致しました。1,349 個の xUnit v3 test はすべて pass し、
pinned CTI 18 case は AVX 無効時と SSE4.1 無効時にも pass しました。Release solution
は warning/error なしで build しました。

### managed radix-8 PocketFFT AVX pair

double-precision radix-8 kernel は managed AVX で独立した butterfly 2 個を同時に
評価します。各 lane は元の add、subtract、multiply、conversion order を維持し、
FMA と reassociated reduction は使用しません。odd tail と AVX 非対応 host は元の
scalar path を実行します。

reverse-order の 1,000-frame Exact 2 pair では、v0.4.0 が 64.106 から 63.742 秒へ
0.57% 短縮し、CPU time は 342.547 から 336.578 秒へ 1.74% 減少しました。
`current` は 52.405 から 52.259 秒へ 0.28% 短縮し、CPU time は 392.438 から
374.859 秒へ 4.48% 減少しました。candidate の sampled peak working set は両
profile とも約 778 MiB でした。short matrix は現在の throughput snapshot であり、
この direct main/candidate long pair が変更自体の causal evidence です。

1,349 個すべての xUnit v3 test は通常実行と AVX 無効実行の両方で pass しました。
24 strict baseline/candidate run は Exact、IPP-fast、両 profile、`--threads 0`、
default-5、`--threads 20` をカバーし、luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、ordered `fileLoc`、cross-thread
determinism がすべて一致しました。60-run matrix executable の SHA-256 は
`33F39E01AD16CB2053AB6A4AF1F27064D90981AD1661F315C51B86E99E9F6E79` です。

最新の compact VHS path は、完全な low-pass sync reference を先に組み立て、
low-pass のみを使う field sync work と並行して `Video`、`Envelope`、`Chroma` を
materialize します。stream decoder ごとに decoded block と pooled destination
buffer を所有できる active staged span は 1 個だけです。serial、single-block、
diagnostic、stateful sharpness path は eager のままです。copy expression、float32
chroma widening point、DC-offset order、field-state boundary、ordered commit path は
変更していません。

reverse-order 1,000-frame Exact A/B 2 pair では、v0.4.0 が 49.195 から
47.886 秒へ短縮し（2.66% lower、1.027x）、`current` が 43.986 から 42.862 秒へ
短縮しました（2.55% lower、1.026x）。average active core はそれぞれ
5.70 から 5.82、7.38 から 7.81 へ上昇しました。reverse-order 600-frame
IPP-fast A/B 2 pair では v0.4.0 が 28.758 から 28.226 秒へ 1.85% 改善し、
`current` は 23.415 対 23.422 秒で neutral（-0.03%）でした。long pair の
candidate peak working set は main と同じ 0.38-0.71 GB の観測範囲に収まり、
active staged span は 1 個で retained-growth path はありません。

すべての long pair で luma、chroma、raw JSON、stdout、timing-normalized stderr、
timestamp-normalized log、ordered `fileLoc` が一致しました。別の 100-frame matrix
では Exact/IPP-fast と両 behavior profile について `--threads 0`、default-5、
`--threads 20` が一致し、更新した 40-frame matrix 60 run はすべて deterministic
でした。focused sequence test は eager/staged `current`、fallback VSync、saved
levels、clamp/DC offset、retry ownership、dispose も検証します。

managed `current` CTI distance stage は AVX/FMA で 8 個の独立した float32 lane を
評価します。各 lane は従来の subtract、multiply、fused multiply-add、square root、
threshold、reciprocal、weight、write order を維持します。vector tail と AVX/FMA
非対応 host は従来の scalar expression を使います。pinned PR341 xUnit v3 test 18 個は
hardware path と AVX を無効にした別 process の両方で pass しました。production-size
kernel の alternating 6-pair は同一 SHA-256 を維持し、wall median を 4,969.497 から
4,387.421 ms へ 11.71%、CPU median を 4,812.500 から 4,289.063 ms へ 10.88%
削減しました。

reverse-order 1,000-frame Exact `current --threads 20` の 2-pair は neutral で、
baseline/candidate wall median は 50.687/50.793 秒（-0.21%）、CPU median は
378.680/381.266 秒でした。そのため Exact end-to-end speedup は主張しません。
同条件の IPP-fast 2-pair は両方 candidate が勝ち、wall median は 43.730 から
42.872 秒へ 1.96%、CPU median は 255.273 から 251.797 秒へ 1.36% 減少しました。
この IPP pair の maximum sampled working set は 736.8/734.6 MiB です。
8 measured run の luma、
chroma、raw JSON、ordered `fileLoc`、stdout、normalized stderr、timestamp-normalized
log はすべて一致しました。更新した 60-run matrix は deterministic で、default-5 と
`--threads 1/5/10/20` の backend/profile ごとに hash set は 1 組だけでした。

現在の Super-Gaussian staging path は AVX を使い、reflect padding を構築する中央部の
float64-to-float32 変換、既存の IPP spectrum mask、float32-to-float64 output expansion を
高速化します。各 lane は元の conversion point と、mask の multiply、明示的な
multiply-by-zero、add、subtract の順序を維持します。FMA、reassociation、allocation、
retained buffer は追加しません。vector tail と AVX 非対応 host は元の scalar
expression を使います。focused test は独立した scalar reference に対して NaN
payload、infinity、subnormal、signed zero、unaligned destination、non-vector length を
検証します。

production-size filter microbenchmark の alternating 7-pair は同一の output SHA-256 を
維持し、median を 1.95 から 1.18 ms へ短縮しました（39.5% lower、throughput
1.653x）。fixed 200-frame `ipp-fast + current --threads 20` の alternating 3-pair は
wall median を 9.064 から 8.503 秒へ 6.19%、process CPU median を 48.312 から
46.609 秒へ 3.52% 削減し、effective core use は 5.33 から 5.48 へ上昇しました。
すべての pair で exit status、field count、luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、ordered `fileLoc` が一致しました。
更新した 30 matrix run は backend ごとに deterministic で、直前の audited matrix
hash と一致しました。Release solution は warning/error 0、1,324 個すべての xUnit
v3 test が pass しました。

`current` VHS の 9-tap sync boxcar は、各 AVX vector で隣接する 4 output sample を
計算します。各 lane は従来の ascending source order で同じ 9 回の multiply と add
を実行します。FMA、reassociation、worker-cap change、allocation、retained buffer
は追加しません。vector tail と AVX 非対応 host は従来の scalar loop を使います。
standard xUnit v3 test は独立した scalar reference を使い、2、3、4 workers で
length 9、10、10,003 の bit pattern を検証し、NaN payload、正負の infinity、
subnormal、minimum normal、signed zero も網羅します。CI は別 process で
`DOTNET_EnableHWIntrinsic=0` を設定し、focused sync test 25 個を再実行して scalar
fallback を検証します。

fixed 40-frame `ipp-fast + current --threads 20` の alternating 5-pair は wall median
を 2.452 から 2.359 秒へ短縮し（3.81% lower、throughput 1.040x）、process CPU
median を 12.063 から 11.031 秒へ削減しました（8.55% lower）。candidate は 4 勝
1 敗で、sampled peak working-set median は 356.4 対 355.6 MiB と実質的に同じです。
最後の 200-frame pair は wall time を 8.904 から 8.777 秒へ（1.43% lower）、CPU
time を 47.219 から 44.891 秒へ削減し（4.93% lower）、peak は両方 363.8 MiB でした。
すべての pair で exit status、field count、luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、ordered `fileLoc` が一致しました。
更新した 30 matrix run は default、1、5、10、20 workers の backend ごとに 1 つの
hash set のみを生成しました。Release solution は warning/error 0、1,319 個すべての
xUnit v3 test が pass しました。

先に merge 済みの IPP inverse-staging optimization は requested worker が 12 を
超える場合に適用されます。pooled IPP VHS workspace ごとに private な
companion IPP real-FFT plan を 1 個所有します。これにより、stateful native plan を
共有せず real RF inverse と独立した Hilbert imaginary inverse を並行実行できます。
DSP expression、transform input、output order、16-workspace retention limit は変更
していません。低 worker count、v0.4.0、GNRC、nonzero RF high boost は serial staging
のままです。alternating fixed 100-frame 5-pair では mean wall time が 9.347 から
9.035 秒へ短縮しました（3.33% lower、throughput 1.034x）。paired reduction median
は 4.22% で、candidate は 4 勝 1 敗でした。mean effective core use は 4.74 から
4.88 へ上昇し、mean peak working set は 402.1 から 414.7 MiB へ増えました。
companion plan の bounded cost は 12.5 MiB です。

その checkpoint では documented 40-frame 3-pair はすべて高速で、decoder-reported
median は 2.290 から 2.180 秒へ 4.80% 短縮しました。この historical cell は
profile-matched Python PR341 より 8.438x 高速で、wall time を 88.15% 削減しました。
別の no-start 1,000-frame candidate
run は 46.359 秒、5.27 effective core で完了しました。sampled working set は
414.9 MiB で peak に達し、early mean 400.8 MiB に対し final quarter mean は
387.9 MiB で、progressive growth はありませんでした。

先行する reduction kernel は VHS RF processing で使われる NumPy-compatible float32
mean を高速化します。各 leaf は従来どおり 128-element pairwise boundary、recursive
split point、8 個の独立 accumulator、最後の addition tree を維持します。AVX は 8 個の
float64 input を変換し、同じ 8 lane を一緒に進めるだけです。FMA、reassociation、
allocation、新しい retained buffer は使いません。production size の alternating
microbenchmark 8 run では median が 82.23 から 42.30 ms へ短縮しました
（48.6% lower、throughput 1.944x）。fixed 160-frame
`ipp-fast + current --threads 20` の 3-pair interleaved A/B は wall median を
11.855 から 11.620 秒へ 1.98% 短縮（throughput 1.020x）し、candidate は 2 pair で
勝ち、1 pair で負けました。process CPU median は 55.297 から 57.422 秒、median
effective core use は 4.66 から 4.94 へ増えたため、wall-time gain は測定可能な有用な
CPU occupancy 上昇を伴います。全 paired run で luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、ordered `fileLoc` が一致しました。

6 個の candidate real-RF gate は IPP-fast v0.4.0 と `current` の explicit zero、
default-five、20 workers を網羅しました。profile 内の luma、chroma、raw JSON、
stdout、timing-normalized stderr、timestamp-normalized log、ordered `fileLoc` は
worker count をまたいで一致しました。すべての baseline/candidate 100-frame pair と
documented 40-frame pair も同じ surface で一致しました。Release solution は
warning/error 0、**1,319** 個すべての xUnit v3 test が pass しました。

先に merge 済みの parallel `current` VHS sync kernel は固定 9-tap boxcar を専用化します。
9 個の multiply-add statement は従来の ascending source order と float64 conversion
point を維持し、SIMD、FMA、reassociation、worker-cap change、新しい retained
buffer は導入しません。fixed 160-frame
`ipp-fast + current --threads 20` の 3-pair interleaved A/B は wall median を
12.667 から 12.512 秒へ 1.22% 短縮（throughput 1.012x）し、全 candidate run が
高速でした。CPU median は 58.438 から 58.141 秒、median effective core use は
4.61 から 4.65 になりました。全 run で luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、ordered `fileLoc` が一致しました。
24-cell refresh は両 current backend 内で deterministic でした。Exact v0.4.0 の
zero/default/20 baseline-candidate gate も exact を維持し、current/default は paired
gate、current/20 は長い A/B gate を通過しました。`--threads 0` はこの kernel を
dispatch しません。Release solution は warning/error 0、1,302 xUnit v3 test は
すべて pass し、3 個の focused bit-identity case は AVX2-disabled と
all-hardware-intrinsics-disabled でも pass しました。

先に merge 済みの PCM16 candidate は native libsndfile input を AVX2 で 8 sample ずつ
`double` へ変換します。scalar tail と non-AVX2 fallback は元の signed integer
conversion を exact に維持し、追加 buffer、multiply、reduction、FMA、sample
reordering はありません。focused xUnit v3 test は 65,536 個すべての PCM16 値と
全 vector tail を網羅し、31 loader test は native、AVX2-disabled、
all-intrinsics-disabled execution のすべてで pass しました。8-pair conversion microbenchmark
の median は 72.241 から 44.311 ms へ 38.66% 短縮（throughput 1.630x）しました。fixed
100-frame `ipp-fast + current --threads 20` の 3-pair interleaved A/B は baseline
が 5.02/5.00/5.01 秒、candidate が 4.91/5.22/4.96 秒でした。candidate は 2 win/
1 loss、median は 5.01 から 4.96 秒、arithmetic mean は 5.01 から 5.03 秒へ変化
しました。この noisy end-to-end result は near-neutral とし、一般的な speedup
claim には使用しません。6 run すべてで luma、chroma、raw JSON、stdout、timing-normalized stderr、
timestamp-normalized log、ordered `fileLoc` が一致しました。さらに 12 個の strict
Exact baseline/candidate gate が両 profile の zero/default/20 workers と cross-thread
determinism を一致させました。その checkpoint の IPP-fast/current 20-worker
matrix cell は 2.378 秒で、この fixed window の profile-matched Python PR341
measurement より 7.735x 高速でした。

前回の `current` CTI optimization は、固定 RCPSS mantissa approximation を process-wide
の 2,048-entry read-only table（payload 8 KiB）へ事前計算し、conversion point と
arithmetic result を変えずに per-sample integer division/remainder を除去します。
21-pair の production-sized CTI microbenchmark は 1.590 から 1.401 ms へ 11.88%
短縮（throughput 1.135x）しました。fixed 100-frame
`current --dsp-backend ipp-fast --threads 20` window で build ごとに 3 回
interleaved 測定した decoder-reported median は 5.14 から 5.07 秒へ 1.36% 短縮
（throughput 1.014x）しました。luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log はすべて一致しました。Exact
matrix hash は以前の strict current gate、IPP matrix hash は merge 済み SOS32
baseline と一致しました。

SOS32 candidate は長い VHS chroma-burst block に worker-local IPP single-precision
biquad context を与え、idle context は最大 12 個だけ保持します。fixed 200-frame
`current --dsp-backend ipp-fast --threads 20` window で build ごとに 3 回
interleaved 比較した結果、median wall time は 9.740 から 9.480 秒へ 2.67% 短縮
（throughput 1.027x）、process CPU time は 52.160 から 48.590 秒へ 6.84% 低下
しました。v0.4.0/`current` の 40-frame gate は zero/default/20 workers で profile
ごとに 1 組の artifact、stdout、`fileLoc` hash を生成し、sampled peak working set
は 281.2-707.4 MiB の bounded range でした。両 profile の 1/default/20 workers
を対象にした 6 組の Exact main/candidate pair は luma、chroma、raw JSON、stdout、
timing-normalized stderr、timestamp-normalized log、すべての ordered `fileLoc` で
byte-for-byte 一致しました。

IPP BiQuad は以前の managed SOS loop と異なる float32 evaluation order を使うため、
これは意図的に `ipp-fast` に限定した numerical change です。200-frame window の
main 比較では、142,102,000 luma sample のうち 245,115（0.172492%）が異なり、
mean absolute error は 0.031664、RMS error は 11.216850、maximum 26,666 は 1 field
の dropout decision 変更によるものです。chroma は 18,467,547 sample（12.995980%）
が異なり、mean absolute error 0.154614、RMS error 1.817706、maximum 1,661 でした。
field count、`seqNo`、stdout、normalized stderr、ordered `fileLoc` は一致し、raw JSON
はその field の `dropOuts` だけが異なり、数値 IRE log line も変化しました。これは
Exact compatibility claim ではありません。

DFT32 candidate は previous release と fixed 200-frame
`current --dsp-backend ipp-fast --threads 20` pair でも比較しました。wall time は
10.815 から 9.470 秒（12.43% 短縮、throughput 1.142x）、process CPU time は
65.797 から 51.047 秒（22.42% 低下）、peak working set は 352.9 から 351.2 MiB
へ低下しました。field count、stdout、ABI-normalized stderr/log、ordered `fileLoc`
は一致し、luma と raw JSON hash も一致しました。142,102,000 unsigned chroma
sample の numerical comparison では 0.1627% に差があり、全 sample の 99.99987%
は差 1 以下、RMSE は 0.063、差 4 超は 124 sample でした。これは `ipp-fast` の
numerical result であり、Exact byte-compatibility claim ではありません。別の
40-frame Exact v0.4.0/`current` release-candidate pair は byte、metadata、console、
log、`fileLoc` の 9 gate をすべて通過しました。

Python v0.4.0
commit は `43155200da87c0d49eb37d8ec09b1372075ee8e4`、merge 済み
PR341 commit は `2f21e8ed6018b14561396cc95f1f6828054470b8`
（`v0.4.0-40-g2f21e8ed`）です。Python
3.14.0 は NumPy 2.4.6、SciPy 1.18.0、Numba 0.66.0、python-soxr 1.1.0 を
使用しました。共通引数は次のとおりです。

```text
--system pal --detect_chroma_track_phase --ire0_adjust
--tape_format VHS --frequency 40 --length 40 --start 100 --overwrite
```

共通の `--start 100` は各 profile で同じ bounded frame window を選択します。
`--start_fileloc` は使用していません。両実装の default は **5 workers** です。

sync-scan candidate は short public matrix とは別に、Exact `current`、20 workers、
start offset なしでも gate しました。160/400-frame の interleaved median は
17.23 から 16.80 秒、33.14 から 32.57 秒へ短縮し、active core median は
5.27 から 5.60、5.25 から 5.51 へ増えました。実行順を反転した 2 回の
1,000-frame run は 72.01/72.03 秒から 70.57/70.59 秒へ短縮しました。maximum
working set は 435.8-437.4 MiB、allocation は 3.02-3.04 GiB の範囲に留まり、
luma、chroma、JSON、stdout/stderr、normalized log、順序付き 2,000 個すべての
`fileLoc` は変更前 build と一致しました。

後続の Exact 専用 precise-scan stage は、2 回目の serial million-sample threshold
pass を parallel crossing extraction に置き換え、元の state machine と grid decision
を input order で再構築します。逆順を含む 2 回の 1,000-frame run は
70.06/69.32 秒から 68.26/68.67 秒へ短縮し、0.9-2.6% の削減でした。allocation は
3.02-3.05 GiB、maximum working set は 435.5-437.2 MiB の範囲です。40-frame pair
は結果が混在したため、固定の short-run gain は主張しません。IPP-fast の 6 pair
160-frame check は 11.64/11.67 秒で中立だったため、元の serial second scan を維持します。

別の 40-frame `--threads 0` gate では、Exact v0.4.0 が Python `g4315520` と
luma、chroma、raw JSON、順序付き 80 個すべての `fileLoc`、stdout、normalized
stderr、timestamp-normalized log で完全一致しました。Exact `current` も、予期
される build-identity JSON field を除外したうえで Python PR341 と同じ surface が
一致しました。v0.4.0 strict-oracle artifact は次のとおりです。

| Baseline artifact | SHA-256 |
| --- | --- |
| Luma TBC | `37B799282A82770461AD9DB8EC2E471AB86F9C05F145D411C2FCA5A6D695CACE` |
| Chroma TBC | `DC2E3C6FAC3323F05080F22CBEB1236A9EBFB3F0A8CB58B6D498F42EA1AFD794` |
| JSON | `9FB6DC1FAE18024B63B93E1165C5C3F7858AC6A01A786043F7A0E4BF5EAEC30C` |

各 .NET profile と Python PR341 は mode ごとに 1 つの deterministic hash set
だけを生成しました。Python v0.4.0 の各 default/nonzero mode は 3 回の反復で
luma/chroma/JSON/log hash を 3 種類生成しましたが、ordered `fileLoc`、stdout、
normalized stderr は安定していました。そのため、これらの Python 行は throughput
比較専用で、strict oracle は Python v0.4.0 `g4315520 --threads 0` のままです。
上記の SOS32 最適化より前は、IPP-fast がこの fixture で対応する Exact の
luma/chroma hash と一致していました。この過去の sample 固有の結果は、一般的な
byte-compatibility 保証ではありません。

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

### Complex32 second-pass packet の in-place transform

Exact float32 PocketFFT の large-transform layout は、second pass で互いに重ならない
contiguous source packet をすでに所有しています。各 source packet を in-place transform
して別の destination buffer へ scatter するようにし、temporary `Complex32` packet の
allocation/rent と source packet 全体の先行 copy を除去しました。parallel packet は重なりません。
factorization、root、twiddle、transform arithmetic、normalization、scatter order、data
type、final buffer ownership は変更していません。

3 組の frozen forward/backward baseline は長さ 11,025、119,790、131,072 を、要求 worker
数 1 と 20 で検証します。4 番目の test は old-main の正負 zero、subnormal、minimum
normal の forward/backward hash を固定し、1/20 workers の preserving、owned、
owned+scratch storage path を網羅します。maximum finite、infinity、異なる NaN payload
は同一 process 内で 3 storage path を bitwise 比較し、non-finite payload selection を
machine-specific な absolute hash には固定しません。これら 4 個の independently
discoverable xUnit v3 case は native hardware と全 .NET hardware intrinsic 無効の両方で
通過します。

merged main `a059580` に対する 6 組の interleaved 160-frame Exact
`current --threads 20` pair は、luma、chroma、raw JSON、stdout、normalized
stderr/log、ordered `fileLoc`、field count、exit behavior がすべて一致しました。median
wall time は 11.59 から 11.27 seconds（2.8% 低下）、throughput は 2.8% 向上、CPU time
は 1.9% 減少し、candidate は 5 pair で勝ちました。1-worker の 4 pair は 69.56 から
68.98 seconds（0.8% 低下）へ移動し、CPU time と peak memory は実質 neutral でした。

20-worker の 1,000-frame 3 pair は exact を維持しましたが、host scheduling により
勝敗が入れ替わりました。paired-median wall change は +0.14%、paired-median CPU change
は +1.18% なので、long-run throughput と CPU efficiency は明示的に neutral とします。
candidate peak working set は約 443 MiB に bounded で、進行に伴う増加はありません。

補助 sampled trace では `MemmoveNative` attribution が 8.391 から 5.454 CPU-seconds
（35.0% 低下）、mean effective core が 7.72 から 8.43、P90 が 9.83 から 11.22 へ
移動しました。この trace の古い baseline は `a059580` より前なので、hotspot removal と
multicore shape の確認だけに使います。因果性能 gate は上記 exact-main interleaved A/B
です。更新した 90-run matrix は 6 個すべての Python/.NET profile と default、1、5、10、
20 workers を網羅しました。60 .NET run と 15 Python PR341 run は deterministic でした。
Python v0.4.0 の 15 run は各 run で異なる luma、chroma、JSON、normalized-log hash を
生成したため、strict oracle は引き続き `g4315520 --threads 0` です。

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

native-input route の direct raw `fLaC` `.ldf`/`.flac` input は、最初の metadata
block が完全な 34-byte STREAMINFO で、40 kHz mono PCM16、既知の nonzero sample
count が `Int32.MaxValue` 以下なら bundled libsndfile を使います。handle は lazy-open、
sequential read は seek-free、random read は exact frame seek で、pooled PCM16
workspace から従来どおり `short` を `double` へ変換します。

通常の parallel VHS decode に限り oversized-input gate も使えます。metadata chain が
complete、STREAMINFO の nonzero block size が固定、最初の audio frame が
fixed-blocking、SEEKTABLE なし、sample count が `Int32.MaxValue` 超であることが条件です。
decoder restart ごとに integer time-base/block 演算で logical RF sample を、固定した
FFmpeg/PyAV path と同じ最初の native FLAC sample へ map します。既存の 2 MiB rewind
window と 40 MiB byte-distance restart threshold も維持します。`--threads 0/1`、
debug-plot/GNU Radio AFE mode、nonzero `--sharpness`、LD、CVBS、gate 外 input は
FFmpeg のままです。native open unavailable/unsupported、seek/decode error、mapping/length boundary failure は、
同じ logical sample から既存 FFmpeg/PyAV-compatible loader へ一度だけ切り替えます。
FFmpeg 未導入時も正常な reported EOF は EOF のままです。default VHS `.flac`、
Ogg/FLAC、stereo、PCM24、他 rate、unknown total、rejected header、`.vhs`、`.wav`、
`raw.oga` も FFmpeg を維持します。

同じ private local RF window で、Release 1.4.4 の FFmpeg path と candidate の
libsndfile path は default、`--threads 0`、`--threads 20` の luma、chroma、raw
JSON、stdout、normalized stderr/log、ordered `fileLoc` がすべて一致しました。
20-worker の interleaved 20-frame pair 3 組では wall median が 3.88 秒から
2.99 秒になりました。default-worker median は 4.36 秒と 4.30 秒で range が
重なります。より長い single 100-frame/200-field、20-worker pair は 8.319 s から
7.345 s（11.71% 減、throughput 1.133x）となり、sampled aggregate `decode` plus
FFmpeg peak working set は 797.0 から 724.9 MiB になりました。long-window result は
範囲を限定した single-pair observation で、一般的な speed percentage ではありません。

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
assembly が必要とする eviction は copy 完了まで返却を遅延します。serial
assembly でも full cache 内で low-numbered block が自分自身を evict する場合は
返却を遅延します。deferred diagnostic callback が例外を投げた場合は harvested
block を返してから例外を再送出し、in-flight prefetch cancellation test は後続の
input read を block した状態で completed output がすべて回収されることを確認します。idle pool の
retained hard limit は 48 sets で、decoded cache の maximum configured capacity、
すなわち base 16 entries と最大 32 prefetch allowance entries に一致します。
active decoded/prefetched block と current span lease は retained count に含まれず、
total live sets は一時的に 48 を超え得ます。DSP type、coefficient、expression、
operation order、padding、field commit order は変わりません。

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

reviewed candidate は local で warning 0 の Release build と xUnit v3 test 1,169 件を
すべて pass しました。GitHub Actions command は discoverable test 1,169 件以上を
要求し、clean runner に external AC3 tools がない場合は optional LD AC3
reference-pipeline test を skip できます。strict main/candidate run
12 回は Exact v0.4.0/`current` の
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

### 採用した Exact hot-path specialization

次の Exact pass は、独立 gate を通過した 4 つの変更をまとめたものです。
PocketFFT radix-8 kernel は forward/inverse direction を hot loop の外で選択します。
VHS chroma UInt16 conversion は AVX2/SSE4.1 を使いながら、`+32767`、finite check、
saturation、truncation、scalar tail の規則を維持します。`current` burst fitter は
検証済み scalar form と同じ lane expression/reduction tree を持つ固定 16-lane
AVX/FMA dot-product shape を使用します。`current` sync quantile は partition を
再利用し、deterministic 16+16 radix selection で 32-bit sortable prefix を絞って
から既存の final Quickselect を実行します。signed zero、infinity、NaN は元の
sequential path を使います。worker-local histogram は workspace ごとに約
768 KiB の固定サイズです。cross-field state、output order、data type、numerical
operation order は移動していません。

performance commit は native hardware、AVX2 disabled、all hardware intrinsic
disabled の各環境で xUnit v3 test 1,169 件をすべて pass しました。native 12 run
と scalar 12 run の strict profile/thread gate は Exact v0.4.0/`current` の
`--threads 0`、default-five、`--threads 20` を対象とし、luma、chroma、raw JSON、
stdout、normalized stderr/log、ordered `fileLoc` が一致しました。native/scalar
cross comparison 84 項も一致し、final reviewed source は warning 0 の Release
build と 1,169 tests を pass しました。

release `v0.4.0-1.4.2` に対する balanced 100-frame Exact
`current`/20-worker pair 6 組では、median wall time が 9.637 から 8.627 秒
（10.48% 減、throughput 11.71% 増）となり、candidate が 6 回すべて勝ちました。
serial pair 4 組では 50.377 から 37.300 秒（25.96% 減、throughput 35.06% 増）
となり、candidate が 4 回すべて勝ちました。比較した artifact/diagnostic surface
はすべて exact です。

反対順序の 1,000-frame pair 2 組はそれぞれ 2,000 fields を完了し、luma、chroma、
raw JSON、normalized log、全 ordered `fileLoc` が一致しました。mean wall time は
72.405 から 62.752 秒（13.33% 減、throughput 15.38% 増）になりました。mean
integrated allocation は 3.799 から 3.806 GiB（+0.18%）で、GC pause は実質
neutral、candidate working set は 706 MiB 未満でした。両 run order で後半 5 個の
100-frame interval が前半 5 個より速く、bounded-memory gate に progressive
slowdown はありませんでした。

final six-path table は `d526ef5` から build した executable
`7F3434744E2120282C9888CF66AF730A184A103465561DE5A2B3F63B0022202F`
を使用します。final 60 run はすべて complete profile/backend reference と一致
しました。Python column は同じ固定 host の測定値を維持し、v0.4.0 の strict
oracle は引き続き `g4315520 --threads 0`、merged Python PR341 は `current` の
profile peer です。

### Double PocketFFT packet buffer の再利用

double-precision DUCC packet path は first/second pass の `Complex[]` packet を
worker thread ごとに再利用します。各 buffer はその worker が要求した最大 packet
までだけ増加し、呼び出しごとに正確な active length へ slice されます。gather、
transform、twiddle、scatter、normalization、data type、arithmetic order は変更せず、
decoder/field state も worker boundary を越えません。

latest main の candidate は warning 0 の Release build と、native hardware および
AVX2 disabled の全 1,224 xUnit v3 tests を通過しました。private local PAL VHS RF の
12 runs は Exact v0.4.0/`current` の `--threads 0`、default、`--threads 20` を対象にし、
baseline/candidate/worker count 間で luma、chroma、raw JSON、stdout、normalized
stderr/log、ordered `fileLoc` がすべて一致しました。

反対順序の 1,000-frame Exact `current`/20-worker pair 2 組では build ごとに合計
4,000 fields を完了し、比較 surface はすべて一致しました。combined allocation は
0.18%、GC pause は 7.94% 減少しました。combined wall time は 156.090 から
156.173 秒（+0.05%）で、throughput は明示的に neutral と分類します。candidate の
working set は 834 MiB 未満で、前半/後半の 100-frame median は 7.07～7.09 秒を
維持し、progressive slowdown、OOM、unbounded growth はありませんでした。

### Bounded libsndfile RF input reuse

compact streaming pipeline は packed LDS と direct raw-FLAC loader に共通の
internal reusable-input ownership contract を提供します。libsndfile implementation
が保持するのは、正確に 32,768 samples の `double[]` block 最大 48 個、約 12 MiB
です。これは PAL field 1 個の parallel block batch と prefetch を収容し、oversized
array は保持しません。native PCM16 sample は従来と同じ
`output[i] = samples[i]` で変換します。fallback output は pool に入る前に
loader-owned storage へ copy され、public/diagnostic read は独立 array のままです。

最初の candidate は sequential decode でも libsndfile block を再利用しましたが、
default-worker pair 4 組で約 6% の regression が出たため却下しました。採用した実装は
parallel block decode/prefetch だけで libsndfile reuse を有効にします。guard 後の
default-worker pair 4 組は方向が混在し、median wall time は 61.43 から 61.61 秒
（+0.29%）で neutral と分類します。packed LDS の既存 sequential reuse policy は
維持します。

同じ private local PAL VHS RF capture の同一 parameter 100-frame allocation trace は
3,561,003,024 から 986,114,768 sampled bytes（72.31%）へ減少し、luma、chroma、
raw JSON、normalized log、ordered `fileLoc` は一致しました。逆順 1,000-frame
`current`/Exact/20-worker pair 2 組では build ごとに 4,000 fields を完了し、全 surface
が一致しました。combined allocation は 43.96 から 16.63 GiB（62.18%）、GC pause
は 0.380 から 0.253 秒（33.46%）、Gen2 collection は 151 から 40 へ減少しました。
combined wall time は 155.74 から 155.31 秒（0.28%）で throughput は neutral です。
candidate working set は 772 MiB 未満、steady 100-frame interval は約 7.0 秒で、
progressive slowdown、OOM、unbounded growth はありませんでした。

baseline/candidate gate 12 組は Exact v0.4.0/`current` の explicit `--threads 0`、
thread option 省略時の default、`--threads 20` を対象にしました。luma、chroma、raw
JSON、stdout、normalized stderr/log、ordered `fileLoc` は pre-change build と各
worker-count reference に一致しました。

### Deterministic parallel current VHS sync quantile

Exact `current` VHS detector は 524,288 samples 以上の field で high/middle-prefix
radix histogram scan を最大 4 個の worker-local slab に分配します。reduction は固定
worker order です。exceptional-value fallback、最後の source-order prefix collection、
Quickselect、floating-point expression、全 cross-field state は従来どおり serial の
ままです。同時に active または retained となる parallel workspace ごとに最大約
2 MiB 増加し、reuse 前に clear します。product concurrency は既存 field-worker
scheduler で bounded です。小さい field と one-worker decode は従来の serial path
を使います。

新しい focused test 3 件は dirty workspace を one/two-bucket case 間で連続 reuse
した bit-exact result、worker boundary の exceptional value、active length 外で
無視される poison tail、warm caller-thread allocation を検証します。zero-warning
Release build と 1,234 件すべての xUnit v3 test は native、AVX2 disabled、all
hardware intrinsics disabled の 3 環境で通過し、各 summary は別の local log に
保存しました。real-RF
gate 12 回は Exact v0.4.0/`current` の explicit `--threads 0`、omitted/default、
`--threads 20` を対象にし、luma、chroma、raw JSON、stdout、normalized stderr/log、
ordered `fileLoc`、thread 間 determinism が pre-change build と一致しました。

同じ private local PAL VHS RF capture で one-worker short gate と 160-frame
default-five pair 4 組は throughput-neutral でした。10-worker pair 4 組では candidate
が 3 組で勝ち、median wall time は 20.029 から 19.766 秒（1.31% 減）でした。
20-worker pair 6 組はすべて candidate が高速で、median は 18.622 から 17.739 秒
（4.74% 減、throughput 4.97% 増）でした。sampled trace では従来の serial selector
work が 2 個の worker-local histogram loop に分かれ、final selection は serial の
ままであることを確認しました。

反対順序の 1,000-frame/2,000-field pair 2 組では luma、chroma、raw JSON、
normalized stderr/log、全 ordered `fileLoc` が一致し、mean wall time は 78.559 から
76.495 秒（2.63% 減、throughput 2.70% 増）でした。別の thread gate では stdout
と thread 間 determinism も一致しました。combined sampled allocation は 16.570
から 16.661 GiB、GC pause は 0.247 から 0.259 秒となったため、allocation/GC
improvement は主張しません。candidate の once-per-second working-set sample の
最大値は 779.98 MiB、Gen2 collection は 42 から 40 で、両 run は progressive
sampled growth や OOM なしで完了しました。

### Decoder-owned PAL chroma upconversion

最新の Exact `current` PAL path は decoder-owned の resampled chroma field に
heterodyne multiplier を直接適用します。public read-only API は独立 output を従来
どおり allocation します。internal path は最初の書き込み前に phase-table length を
検証し、normalize 後の NumPy-style line range が non-overlap で、sorted または
合法な tail-to-head wrap 1 回だけであることを確認します。非対応 sequence は入力を
変更する前に fallback します。multiplication、`(double)(float)` conversion point、
line order、gap zeroing、後続 filter/comb/gain、cross-field state は変わりません。

focused xUnit v3 test は sorted range、gap、実際の PAL single-wrap layout について
各 output `double` の bit を allocating reference と比較します。duplicate range は
fallback が input を変更しないことも確認します。production-size の完全 PAL decode
は copying path と一致し、warm-up 後に caller-owned UInt16 output を使う場合の
allocation は 256 KiB 未満です。zero-warning Release build と 1,236 tests は native、
AVX2 disabled、all hardware intrinsics disabled の 3 環境ですべて通過しました。

同じ private local PAL VHS RF sample の matched 80-frame allocation trace では、
sampled object bytes が 872,958,736 から 415,617,272（52.39% 減）、sampled
allocation amount が 2,065,610,264 から 1,606,599,696（22.22% 減）になりました。
baseline の 235,891,312-byte `UpconvertChroma` と 221,894,168-byte field-copy
allocation stack は消えました。balanced 80-frame Exact `current`/20-worker pair
6 組は candidate 4 勝 2 敗で、mean wall time は 11.942 から 11.776 秒（1.39% 減）
でした。このため short throughput は near-neutral と分類します。

matched 1,000-frame/2,000-field counter pair は managed allocation を 8.254 から
3.009 GiB（63.54% 減）、GC pause を 0.134 から 0.113 秒（15.63% 減）、Gen2
collection を 20 から 2、maximum sampled working set を 773.29 から
439.86 MiB（43.12% 減）へ移しました。candidate first/last-quarter working-set
median は 405.84/406.51 MiB で progressive growth や OOM はありません。wall time
は 74.130 から 73.217 秒（1.23% 減、throughput 1.25% 増）ですが、範囲を限定した
single-pair observation とします。luma、chroma、raw JSON、normalized stderr/log、
2,000 個すべての ordered `fileLoc` が一致しました。別の gate 12 件は Exact
v0.4.0/current の explicit zero、omitted/default、20 workers を対象にし、stdout と
thread 間 determinism も一致しました。

### Bounded parallel current burst-prefix analysis

`current` chroma phase pass は state-independent な line prefix を固定 contiguous
range に分け、最大 4 worker で probe します。最後の 16-line track-rotation check、
phase-sequence assembly、color-killer summary、全 reduction、全 cross-field state
transition は decode thread が input order で実行します。worker exception が起きた
場合は speculative prefix を破棄して元の serial path を再実行し、従来の exception/
recovery behavior を維持します。v0.4.0 と one-worker decode は serial のままです。

初期の 20-way prototype は short-run CPU use を上げましたが 40-frame wall time を
悪化させたため却下しました。4-versus-8-worker の direct 10-pair test は throughput
neutral で、`--threads 20` の RF/FFT work を妨げない 4-worker internal cap を採用
しました。160-frame Exact `current`/20-worker pair 6 組はすべて candidate が速く、
median wall time は 11.94 から 10.68 秒（10.6% 減）、median active core は 6.11
から 6.69、median process CPU time は 72.98 から 71.43 秒になりました。

反対順序の 1,000-frame/2,000-field Exact `current`/20-worker 比較 2 回は wall time
を 64.36/65.15 から 56.31/56.05 秒へ短縮しました（12.5-14.0%）。luma、chroma、
raw JSON、stdout、normalized stderr/log、全 ordered `fileLoc` は baseline、candidate、
explicit-zero/default/20-worker gate、更新した Exact/IPP-fast matrix で一致しました。
final matrix でも current backend ごとに default/5/10/20 worker 全体で deterministic
hash set は 1 つです。xUnit v3/Microsoft.Testing.Platform test は 1,244 件すべて
通過しました。

allocation improvement は主張しません。sampled baseline allocation は
2.079/2.085 GiB、candidate は 2.125/2.237 GiB でした。candidate maximum working-set
sample は 393.8/515.0 MiB ですが、first/last-third median は 386.4/390.1 と
490.0/493.6 MiB に留まりました。両 long run は progressive growth や OOM なしで
完了し、追加の parallel result と scheduling work は field ごとに bounded です。

### Bounded parallel current ACC segments

`current` automatic chroma-gain pass は、独立した単調増加の chroma segment を固定
contiguous range に分け、最大 8 worker で処理します。raw-gain construction、
outlier limit、smoothing、final noise FMA reduction、mean amplitude、cross-field state、
output submission は入力順のままです。各 sync-tip window が自身の segment 内に完全に
収まり、scratch が 4,096 sample 以下の場合だけ parallel path を使用します。異常または
overlap する入力、v0.4.0、one-worker call は serial path を維持します。従来の public
CLR method signature は変更していません。worker exception は捕捉し、input partition
順に再送出します。worker-local float/double median scratch は output mutation 前に rent
し、すべての exit path で返却します。

160-frame Exact `current`/20-worker の interleaved pair 6 組では、median wall time が
11.15 から 10.47 秒へ 6.1% 短縮し、median active core は 6.67 から 7.58 に増えました。
別の 1,000-frame/2,000-field pair は 55.851 から 53.003 秒へ 5.1% 短縮しました。
luma、chroma、raw JSON、stdout、該当する normalized stderr/log、全 ordered `fileLoc`
は一致しました。candidate allocation は 1.918 GiB、first/last-third working-set median
は 382.9/383.0 MiB、maximum は 386.7 MiB で、progressive growth や OOM はありません。

cap-8 audit 当時の `current` matrix は Exact と IPP-fast の default/1/5/10/20 worker を
各 6 回 interleaved 測定し、合計 60 Release run です。compatibility hash set はすべて 1 値
でした。zero-warning Release build と 1,262 件すべての xUnit v3/
Microsoft.Testing.Platform test が通過しました。

### Bounded current Super-Gaussian FFT parallelism

`current` Super-Gaussian chroma final filter の既存 PocketFFT packet-independent
stage は、requested worker count が許す場合に最大 12 internal worker を使用します。
packet decomposition、padding、mask、arithmetic、transform order、output order、serial
path は変更していません。filter は bounded instance-local workspace を 1 つ保持し、
concurrent call は互いに分離した temporary workspace を使用します。

matched cap-4/cap-8 short pair 12 組の combined median wall time は 14.67 から
14.29 秒へ 2.57% 短縮し、median active core は 6.50 から 6.88 に増えました。
反対順序の 1,000-frame/2,000-field comparison 2 回では combined median が
54.345 から 52.408 秒へ 3.56% 短縮しました。luma、chroma、raw JSON、stdout、
normalized stderr/log、全 ordered `fileLoc` は一致しました。2 回の candidate の
first/last-third working-set median は 428.6/433.1 MiB と 382.4/382.7 MiB、maximum
は 435.9 と 386.5 MiB で、progressive growth や OOM はありません。

旧 pipeline に対する初期 cap-12 experiment は一度却下されました。その後の bounded
pipeline と output-buffer 変更を含む最新 HEAD で cap-8/cap-12 audit を再実行し、cap 12
を採用しました。160-frame interleaved pair 6 組中 5 組で candidate が速く、median wall
time は 14.43 から 13.88 秒へ 3.8% 短縮し、median process CPU time も 109.33 から
103.95 秒へ低下しました。median peak working set は 787.95 と 787.69 MiB で実質同等です。
全 run の luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` が
一致し、更新した 30-run `current` matrix の全 cell でも各 compatibility surface は
1 hash でした。

### Decoder-local current burst-probe buffer

`current` chroma phase analyzer は、exact-length padded-burst array を 4 個の
decoder-local lock-free slot から再利用します。4-slot bound は既存 burst-probe
worker cap と一致します。active sample は毎回すべて上書きしてから同じ SOS filter と
fitter を実行し、arithmetic、filter order、worker count、cross-field state、exception
order は変更しません。buffer はすべての exit path で返却され、保持は最大 4 array です。

80-frame Exact `current --threads 20` baseline/candidate short screen の wall time は
9.393/9.371 秒で実質 neutral でした。process CPU time は 74.406 から 67.922 秒、
peak working set は 435.1 から 430.5 MiB へ低下しました。この single pair は
CPU/allocation evidence としてのみ扱い、新しい throughput claim にはしないため、
上の full performance matrix は変更していません。luma TBC、chroma TBC、raw JSON、
stdout、normalized stderr/log、zero/default/20-worker determinism はすべて一致しました。

### Value-type classified sync pulse

`ClassifiedSyncPulse` は immutable record struct になりました。kind、pulse geometry、
ordering flag、list order、および downstream の数値式は変更せず、pulse classification
と VBlank refinement で accepted pulse ごとの managed object allocation を除去します。
focused xUnit v3 test が value-type contract と保存値を固定します。

matched 80-frame Exact `current --threads 20` baseline/candidate short screen では、
wall time が 10.309 から 10.003 秒へ 2.97% 短縮しました。process CPU time は
69.453 から 70.797 秒へ 1.93% 増加し、peak working set は 435.3/435.0 MiB で
横ばいでした。luma TBC、chroma TBC、raw JSON、stdout、normalized stderr/log、
すべての ordered `fileLoc` が一致しました。この single pair は recent optimization
evidence であり、上の full matrix median を置き換えるものではありません。

### High-worker VHS inverse staging の並行化

Exact VHS `current` request が既存の 12-worker outer prefetch cap を超える場合も、
NumPy-compatible analytic spectrum は従来と同じ順序で serial preparation します。
その後、complex inverse FFT と独立した real inverse FFT を並行実行します。両処理は
異なる source spectrum を読み、異なる worker-local destination に書き込みます。
default、1 から 12 worker、v0.4.0、non-VHS、`ipp-fast` path は serial staging を
維持し、`--gnrc` も serial path のままです。sample-length buffer は追加せず、outer
block concurrency は 12 のままです。serial path における real inverse exception の
優先順も維持し、task の同期 scheduling failure は元の serial sequence に戻します。

同じ private local PAL VHS fixture の fixed 200-frame Exact
`current --threads 20` pair で main `eec3658` と branch candidate を比較しました：

| Metric | main `eec3658` | Branch candidate | 変化 |
| --- | ---: | ---: | ---: |
| Wall time | 11.945 s | 11.480 s | 3.89% 短縮 / throughput 1.041x |
| Process CPU time | 95.359 s | 90.828 s | 4.75% 低下 |
| Active cores | 7.98 | 7.91 | 実質同等 |

exit status、field count、luma TBC SHA-256、chroma TBC SHA-256、raw JSON
SHA-256、stdout SHA-256、normalized stderr、normalized log、全 ordered `fileLoc`
が一致しました。harness が有効な peak-working-set 値を返さなかったため、実測 memory
値は主張しません。scope の異なるこの 200-frame pair は反復 40-frame matrix と
分けて記録します。

### float32 FFT root table の再利用

`PocketFftReal32.Plan` は immutable unity-root table を保持し、factor construction
および complexified forward/inverse recombination ごとに再利用します。
`PocketFftComplex32` は large multipass transform と rooted packet plan の間で、
immutable `SinCos2PiByN` table を root length ごとに共有します。この cache は
32-entry FIFO 上限を持ち、eviction は cache reference だけを外すため、実行中の
transform は immutable table を安全に保持します。scratch array、transform state、
data type、operation order、float conversion point は変更していません。root key は
既存 plan の dimension と一致します。

matched final 200-frame Exact `current --threads 20` trace では、sampled allocation
amount が 579,283,536 から 541,701,824 bytes へ 6.49%、sampled object bytes が
391,712,736 から 373,960,808 へ 4.53% 減少しました。final bounded-cache candidate
と main `ced6afb` の 1,000-frame 3 pair は mixed result のため throughput-neutral
と扱います。1/3 pair のみ高速で、main/candidate の median wall time は
44.924/44.985 秒（candidate +0.14%、0.999x）、median CPU time は
332.672/333.734 秒（+0.32%）でした。median peak working set は
391.9/396.5 MiB ですが、candidate の 1 run は 614.3 MiB まで達したため、これらの
短時間 process sample から memory reduction は主張しません。全 run で 9 compatibility
surface が一致し、final 60-run matrix と 12-run determinism matrix も通過しました。
focused 16 KiB unit-test threshold は warmed calling thread の allocation だけを測定し、
上記 process-wide allocation は unit test ではなく matched trace に基づきます。

### managed precise VHS threshold-scan AVX

current VHS の scalar second-pass precise threshold scan は、ordered non-signaling comparison
を使い、AVX step ごとに隣接 double 4 組を分類します。falling/rising mask は引き続き
scalar index の昇順で commit し、変更のない valid-grid predicate は candidate order、
modulo expression、threshold、early exit を維持します。scalar tail と no-AVX fallback は
元の expression を使い、FMA、reduction reorder、worker、retained sample buffer は追加
していません。

focused scalar/AVX oracle は 8 種類の length に加え、quiet/signaling NaN、infinity、
signed zero、正確な crossing index を含む固定 vector/tail case を網羅します。34-case
detector class は通常 hardware で全件 pass し、all hardware intrinsics disabled では
33 件 pass、AVX-only case 1 件が明示的に skip されます。matched 400-frame IPP-fast
trace では、sampled
`VhsSyncDetector.DetectFiltered` CPU time が 1263.307 から 1173.588 ms へ 7.1% 減少
しました。opposite-order の 400-frame pair 4 組は baseline/candidate wall median が
16.98/16.47 秒でしたが、保守的な paired-difference gain は 1.3% のため、より大きい
unpaired gap は因果的な gain として扱いません。

opposite-order の 1,000-frame IPP-fast `current --threads 20` pair 2 組では、median wall
time が 36.341 から 35.822 秒へ 1.43% 短縮（1.0145x throughput）、median process CPU
time が 222.078 から 220.789 秒へ 0.58% 減少し、median active core は 6.11 から 6.16
になりました。4 回の 2,000-field run は luma、chroma、raw JSON、stdout、normalized
stderr/log、すべての ordered `fileLoc` が一致し、progressive memory growth や OOM は
ありませんでした。ただし 2 pair だけでは memory reduction を主張しません。更新した
30-run current matrix でも、各 backend と worker setting の 7 compatibility surface は
それぞれ 1 hash のみでした。

### Complex32 final workspace の直接 return

float32 mixed-radix packet plan は、radix pass ごとに 2 個の worker-local `Value[]` array
を交互に使います。最終 pass が scratch に入る場合、`Execute` はその array を直接返し、
従来と同じ `Complex32` writeback の前に行っていた全体 copy を省きます。input load、
factor/packet order、root、twiddle、すべての arithmetic expression、float32 conversion
point、output ownership、thread-static workspace bound は変更していません。1,000-frame
CPU trace では、削除した `Plan.Execute` memmove に 6,432 samples、全 sample の約 1.0%
が帰属していました。

mixed-radix xUnit v3 の 53 cases は通常実行と all hardware intrinsics disabled の両方で
pass しました。SciPy fixture hash、奇数/偶数 radix count、serial/parallel transform、
owned buffer、repeatable warm workspace、239,580-point field transform を含みます。順序を
反転した 200-transform microbenchmark 8 run は 1 output hash を維持しましたが、schedule
noise が大きいため throughput-neutral と分類し、end-to-end claim には使いません。

interleaved 160-frame Exact `current --threads 20` pair 4 組は、luma、chroma、raw JSON、
stdout、normalized stderr/log、すべての ordered `fileLoc` が一致しました。勝敗は 2 対 2
でしたが、candidate wall median は 1.93% lower、CPU median は 12.85% lower でした。
最終判定の opposite-order 1,000-frame pair 2 組は両方 candidate が勝ち、combined wall
time は 79.767 から 78.634 秒へ 1.42% 短縮（1.0144x throughput）、combined process CPU
time は 650.047 から 629.078 秒へ 3.23% 減少しました。candidate peak working set は
387.8-393.7 MiB、main は 391.8-395.3 MiB で、progressive growth や OOM はありません。
これは bounded-memory evidence であり、resident-memory reduction claim ではありません。

short gate 24 run は Exact/IPP-fast、v0.4.0/`current`、explicit zero/default-five/20 workers
を網羅し、baseline/candidate と cross-thread の artifact/log surface がすべて一致しました。
更新した current matrix 30 run は default、1、5、10、20 workers を順序を入れ替えた
3 pass で実行し、backend ごとに 1 deterministic hash set のみでした。変更のない Python
と .NET v0.4.0 の列は以前の same-host measurement を維持し、その checkpoint の
2 current 列は当時の candidate single-file executable を使いました。

### AVX2 Hilbert real-spectrum scaling

VHS/LD analytic-signal preparation pass は、double-precision complex spectrum の各値に
real Hilbert multiplier を掛けます。managed AVX2 path は 4 個の multiplier を load し、
対応する real/imaginary lane に複製して、独立した 4-double multiply を 2 回実行します。
FMA、reduction、expression reordering はありません。group 内の complex component または
real multiplier が `NaN`/infinity の場合、4-value group 全体が従来の
`Complex * double` expression を使い、.NET exceptional-value semantics を維持します。
AVX2 のない host と scalar tail も同じ expression を使います。

final committed isolated 32,768-value kernel の median は、順序を反転した 8 trial で
711.718 から 47.936 ms へ短縮（14.847x）し、output bit は exact、warm allocation は
0 でした。range は scheduling-noisy で、これは kernel evidence のみです。
opposite-order 1,000-frame Exact `current --threads 20` pair 2 組は 1 勝 1 敗でした。
combined wall time は 83.678 から 83.419 秒へ 0.31% 短縮（1.0031x throughput）、
combined process CPU time は 661.375 から 660.375 秒へ 0.15% 減少したため、
end-to-end throughput は neutral と分類します。candidate peak working set は最大
393.6 MiB に収まり、progressive growth や OOM はありません。resident-memory
reduction は主張しません。

focused xUnit v3 15 cases は native AVX2、AVX2 disabled、all hardware intrinsics disabled
のすべてで pass しました。final real-input intrinsic gate 3 run は native candidate と
2 disabled mode を比較して一致しました。Exact/IPP-fast gate 24 run は v0.4.0/current の
explicit zero、default-five、20 workers を網羅し、baseline/candidate と cross-thread の
luma、chroma、raw JSON、stdout、normalized stderr/log、ordered `fileLoc` がすべて一致
しました。candidate matrix 45 run は Exact v0.4.0、Exact `current`、IPP-fast
`current` の default、1、5、10、20 workers を順序を入れ替えた 3 pass で更新し、
各 profile/backend set は 1 deterministic hash set のみでした。IPP-fast v0.4.0 は
影響を受けない prior measurement を維持します。final executable は `8409b1f` を
基にした commit `3740bf1` から build され、SHA-256 は
`0F119B82507E8ACB5FF0CF8EE4C407436671828B1981CC9FCDC824B2F34ACD19` です。

### current chroma-burst fitter の loop fusion

opt-in `current` の chroma-burst least-squares fitter は、cosine、sine、residual、
Jacobian を 5 pass ではなく 1 個の ascending-index loop で準備します。4 個の
non-vector dot sum も従来と同じ昇順で同じ loop 内に蓄積し、一時 theta/sine buffer を
削除しました。data type、sample ごとの expression、conversion point、
OpenBLAS-compatible dot reduction、solver order、worker boundary、state transition は
変更せず、FMA や reassociation も導入していません。

complete `Fit` を 100,000 回実行する alternating process run 9 組では、median が
1,625.603 から 1,489.642 ms へ 8.36% 短縮し、throughput は 1.091x になりました。
全 run は同じ checksum と 2,080-byte process-level allocation count を維持しました。
これは isolated-kernel evidence であり、end-to-end claim ではありません。

同じ private local 40 MHz PAL VHS sample の 1,000-frame Exact
`current --threads 20` interleaved pair 3 組は数値上すべて candidate が勝ちました。
mean wall time は 39.572 から 39.416 秒へ 0.39% 短縮しましたが、paired difference の
95% interval は -0.011 から 0.322 秒で zero を含みます。mean process CPU time も
322.464 から 326.474 秒へ 1.24% 増加したため、end-to-end throughput は neutral と
分類し、CPU-efficiency improvement も主張しません。
sampled peak working set は既に確認済みの 741 MiB bound 内で、unbounded allocation
path も追加していません。noise の大きい sample から resident-memory reduction は
主張しません。

paired long run 6 件と final-source confirmation run 1 件は luma、chroma、raw JSON、
stdout、timing-normalized stderr、timestamp-normalized log、2,000 個すべての
ordered `fileLoc` が一致しました。
追加の short gate 12 件は Exact v0.4.0/current の explicit zero、default-five、
20 workers を網羅し、baseline/candidate と cross-thread の全 surface が exact でした。
pinned fitter test 3 件は通常実行と AVX disabled の両方で pass し、full Release suite は
1,397 pass、IPP runtime が build directory にないための skip 4 件で完了しました。
homepage table は前回の complete five-path matrix のままです。この異なる
1,000-frame window を代入すると startup-inclusive ratio の比較可能性が失われるためです。

### Bounded sync と chroma phase container

sync analyzer は、既知の input/slice bound から 4 個の
`List<ClassifiedSyncPulse>`/`List<double>` capacity を設定するようになりました。
pulse order、filter、VBlank state、数値式は変更していません。initial capacity は
65,536 entry を上限とし、malformed high-noise input が全 raw pulse 数に比例した
classified-pulse backing array を先に確保することを防ぎます。実際に大きい accepted
result は通常どおり grow します。既存の focused xUnit v3 case は 100 万 rejected pulse
を入力し、classification を 2 MiB 未満、必要な raw-pulse copy を含む refinement を
12 MiB 未満に gate します。`current` chroma phase
builder は final `ChromaPhaseLine[]` を 1 回だけ確保し、独立な parallel prefix をその
配列へ直接書いた後、元の ordered state machine が同じ配列を継続して埋めます。以前の
prefix array、List backing array、最後の `ToArray()` copy はなくなりました。parallel
probe の exception slot は実際に例外が起きた場合だけ作成され、最小 input-line の例外を
再送出する順序も維持します。

10,000 iteration の sync microbenchmark 7 組では、median wall time が 264.306 から
251.985 ms（4.66%）へ、allocation が 739,600,040 から 401,680,040 bytes
（45.69%）へ減少し、全 run の checksum は 1 種類でした。順序を入れ替えた 1,000-frame
Exact `current --threads 20` 3 組は scheduling noise が大きいため、end-to-end throughput
は neutral と分類します。chroma phase の 100,000-call microbenchmark 5 組では median
allocation が 6,547,233,136 から 5,881,975,184 bytes（10.16%）へ減り、candidate は
3/5 組で勝ち、paired median wall-time は 1.23% 低下しました。

順序を逆にした 1,000-frame chroma pair 2 組は両方 candidate が勝ち、combined wall
time は 79.645 から 79.056 秒（0.74% 低下）でした。combined CPU time は 0.43%
増えたため CPU-efficiency claim は行いません。全 long run で luma、chroma、raw JSON、
stdout、normalized stderr/log、2,000 個の ordered `fileLoc` が一致しました。補助的な
12-second fixed-duration steady trace では sampled allocation amount が 247,776,232 から
228,539,336 bytes（7.76%）へ減り、classified-pulse `AddWithResize` leaf も主要 site
から消えました。ただし trace window は同一 field count に正規化していないため、この
割合を complete-pipeline allocation claim には使いません。

より広い sync scratch-workspace 実験は microbenchmark allocation を 36.62% 減らした
一方、逆順の real pair 2 組で 1.02% と 1.74% 遅くなったため、完全に revert しました。
allocation だけを良く見せるために throughput を交換していません。今回の小幅な
same-revision 結果は full 5-profile refresh ではないため、homepage の Python comparison
matrix は変更しません。

final Release build は warning/error ともに 0 でした。標準の xUnit v3/
Microsoft.Testing.Platform run は全 1,401 test を検出し、1,397 pass、0 fail、IPP runtime
依存の 4 case のみ explicit skip でした。

### 8-way current burst probe と double path の Exact SOS reuse

最新の `current` chroma phase prefix は、独立した burst worker を 4 個ではなく最大 8 個
使用できます。decoder-local の exact-length burst cache も hard bound は 8 slot です。
使用前に active sample をすべて上書きし、全 exit path で buffer を返却するため、保持領域は
file length に比例して増えません。以下の CLI 実測 gain はこの bounded worker-cap change
によるものです。production CLI は本 phase より前から owned float32 burst buffer を
in-place filter していました。別の改善として、public double-precision Exact
`AnalyzeFieldPhase` path は既存の destination API を通じて final burst SOS を exclusive
ownership の buffer へ書き戻し、その API path の temporary result allocation を 1 個
避けます。double-path change に CLI speed/memory claim は帰属しません。coefficient、odd
extension、forward/reverse order、floating-point expression、fitter order、exception
priority、すべての cross-field state transition は変更していません。

build ごとの process-level burst-prefix trial 7 回は同じ checksum を維持しました。median wall
time は 197.780 から 153.813 ms へ 22.2% 短縮しました。初期の 8-worker memory run 1 件は
peak working set 617.3 MiB に達しましたが、controlled final source では再現しなかったため
non-representative として破棄しました。採用した baseline/candidate sampled pair は peak
working set 382.6/400.7 MiB、peak private bytes 398.9/405.9 MiB、wall time 30.169 から
28.426 秒でした。約 7 MiB の private-byte 増加は bounded scheduling tradeoff であり、
memory reduction claim ではありません。

interleaved 1,000-frame Exact `current --threads 20` release-binary pair 3 組はすべて candidate
が勝ちました。独立 median は wall time が 29.576 から 28.673 秒（3.05%、throughput
1.031x）、process CPU time が 273.375 から 275.297 秒（+0.70%）、effective core が
9.24 から 9.60（+3.87%）へ移りました。6 回の long run は luma TBC、chroma TBC、raw
JSON、stdout、timing-normalized stderr、timestamp-normalized log、全 ordered `fileLoc` で
一致しました。

別の 6-run gate は未変更の `--threads 0` path、省略時の default-five workers、
`--threads 20` を網羅しました。baseline/candidate hash は一致し、3 worker mode もすべて
deterministic でした。更新した 60-run Exact/IPP-fast、v0.4.0/`current`、
default/1/5/10/20-worker matrix は、各 cell の 7 captured surface で hash が 1 種類でした。
focused SOS workspace test は 45/45、current chroma parallel test は 10/10、標準 xUnit v3
suite は全 1,448 test に成功しました。

</details>

<!-- SECTION: build -->

## ビルドとテスト

必要条件：

- `.NET SDK 11.0.100-preview.7.26381.103`（`global.json` で固定）
- IDE として使用する場合は Visual Studio 2026
- optional Intel IPP bridge の build には Visual Studio C++ Build Tools と Windows SDK
- 厳密に限定した direct 40 kHz mono PCM16 raw-FLAC native-input route 以外
  （`Int32.MaxValue` samples を超える stream を含む）の
  container input、および native open/seek/decode failure または reported-length
  boundary 後の recovery fallback では `ffmpeg` と `ffprobe` が `PATH` 上に必要
- native-input route の正常かつ size-eligible な raw-FLAC RF input、default HiFi FLAC output、
  LD `--write-test-ldf` は bundled libsndfile を直接使えます。各 path は文書化した
  fallback または compatibility boundary を維持します

```powershell
.\tools\build-ipp-native.ps1
.\tools\build-cuda-fast-native.ps1
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release --no-build --no-restore --minimum-expected-tests 1550
dotnet test --project tests\VHSDecode.Tests\VHSDecode.Tests.csproj -c Release --no-build --no-restore --coverage --coverage-output coverage.cobertura.xml --coverage-output-format cobertura
```

最初の command は optional `ipp-fast` native artifact を含めるためのものです。
この script は `vswhere` で MSBuild を検出し、
固定した `intelipp.static.win-x64` NuGet package を restore して sequential static
bridge を build します。外部 IPP、OpenMP、oneTBB、Visual C++ runtime DLL 依存が
あれば失敗します。development/deployment PC のどちらにも Intel oneAPI の install
は不要です。binary-only single-file release は `vhsdecode_ipp.dll` と必要な
third-party notice を埋め込み、license sidecar file は追加しません。

2 番目の command は optional `cuda-fast` bridge を build します。CUDA 13 Toolkit、
NVIDIA GPU/driver、CMake/Ninja、MSVC 14.44 以前の host toolset が必要です。local
checkout を指定した場合、script は clean な pinned cuVHS commit であることを確認し、
bridge を compile/smoke-test してから `vhsdecode_cuda_fast.dll`、cuFFT runtime、
third-party notice を stage します。不要な native backend の command は省略でき、
Exact-only build では両方を省略できます。bridge は MSVC runtime と CUDA runtime を
static link し、dependency audit は意図しない dynamic CRT/cudart dependency を拒否します。
deployment には staged cuFFT sidecar と compatible NVIDIA driver が引き続き必要です。
default command はすべての native GPU test を実行します。GPU のない CI では
`-SkipRuntimeTests` を指定できます。この mode でも bridge の compile、audit、stage は
行いますが、GPU runtime validation にはなりません。

現在の正式な Release build は warning 0、error 0 です。xUnit v3 project は
`dotnet test` と Visual Studio Test Explorer の両方で個別に検出できる
**1,550** tests を公開します。

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
decode.exe vhs --dsp-backend cuda-fast --pal [supported options] input.ldf output
```

最後の 2 形式は独立した numerical contract の optional backend を明示的に選択します。
`cuda-fast` は文書化した subset だけを受け付け、未対応 option を無視せず失敗します。

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
