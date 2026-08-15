# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-13.01 -->

这是 [`oyvindln/vhs-decode`](https://github.com/oyvindln/vhs-decode)
中解码相关部分的 .NET 11 重写，兼容目标为上游 release `v0.4.0`、commit
`43155200da87c0d49eb37d8ec09b1372075ee8e4`。

> [!IMPORTANT]
> 这仍是持续进行中的兼容性移植。顶层解码路径已经实现并经过大量测试，但尚未声称
> 每一种真实采集和罕见参数组合都已做到逐字节一致。

完整兼容矩阵、实现细节、历次性能数据、验证证据和剩余差距请查看
**[中文详细说明](docs/README.detailed.zh-CN.md)**。

## 目录

- [项目概览](#项目概览)
- [快速开始](#快速开始)
- [行为配置与后端](#行为配置与后端)
- [最新性能](#最新性能)
- [兼容性状态](#兼容性状态)
- [构建与测试](#构建与测试)

<!-- SECTION: overview -->

## 项目概览

- 只实现解码部分：VHS、CVBS、LaserDisc 和 HiFi。
- 以 release 4.0 的命令、参数、别名、默认值、诊断和输出生命周期为兼容目标。
- VHS 家族包括 VHS/S-VHS、Betamax、Video8/Hi8、U-matic、Type C、EIAJ
  以及上游支持的 PAL/NTSC 变体。
- TBC 工具、双击启动的用户 GUI 和开发者绘图窗口明确不在范围内。
- Visual Studio 2026 `.slnx` 包含 **1,482** 项标准 xUnit v3 测试；测试可在
  Test Explorer 中查看，也可用 `dotnet test` 运行。

<!-- SECTION: start -->

## 快速开始

从 [GitHub Releases](https://github.com/JunliangRen/vhs-decode-dotnet/releases)
下载当前仅含二进制文件的 Windows x64 包。发布包以单文件 `decode.exe` 为主。

```powershell
decode.exe vhs [upstream options] input.lds output
decode.exe cvbs [upstream options] input.lds output
decode.exe ld [upstream options] input.lds output
decode.exe hifi [upstream options] input.lds output.wav
```

同时支持 `vhs-decode.exe`、`ld-decode.exe` 等独立命令别名。使用
`decode.exe <command> --help` 查看完整兼容参数。

### 可拖动的 RF 预览服务器

VHS 可运行 `decode.exe vhs --preview-server --pal input.lds`，LaserDisc
可运行 `decode.exe ld --preview-server --pal input.ldf`。命令会输出仅监听
loopback 的网页播放器地址，以及标准 HLS/fMP4 播放列表地址；不需要输出基名，
也不会生成 TBC、JSON、SQLite、EFM、音频或解码日志文件。

这是用于定位内容的低精度模式：通过轻量 4fSC 一维解调保留彩色，默认执行快速
dropout 遮盖，跳过音频以及正式导出使用的高成本 comb/修复阶段；每个 2 秒窗口只
实际解码 4 个源帧。NTSC 固定输出 top-field-first 的 640x480、30000/1001 fps，
PAL 固定输出 top-field-first 的 768x576、25 fps。`--preview-crf` 接受 0 到 51，
默认 31；值越低，画质和码率越高。IPP 可用时会自动选用 `ipp-fast`，否则回退到
可移植的托管后端。系统需要能找到带 `libx264` 的 FFmpeg；也可通过
`VHSDECODE_FFMPEG` 和 `VHSDECODE_FFPROBE` 指定路径。

<!-- SECTION: profiles -->

## 行为配置与后端

`--compat-version` 选择上游行为：

| 值 | 含义 |
| --- | --- |
| `v0.4.0` | 默认值，目标是固定的 Python release 行为。 |
| `current` | 显式启用上游 PR 341 的分阶段行为，包括较新的 VHS 同步和 color-under 处理。 |

严格兼容基准是 Python v0.4.0 commit `g4315520` 的 `--threads 0` 输出。
Python 原版在不同 worker 数下的输出哈希并不稳定，因此 Python 多线程结果只用于
速度比较，不作为逐字节 oracle。

`--dsp-backend` 选择 DSP 实现：

| 值 | 含义 |
| --- | --- |
| `exact` | 默认托管路径，适合兼容性敏感的解码。 |
| `ipp-fast` | 实验性的 Windows x64 VHS 与 LaserDisc real-RF 路径，使用 Intel IPP；可能改变浮点位，并且绝不会静默回退到 `exact`。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

LaserDisc 的视频、EFM 与模拟音频 full-complex FFT 阶段现已接入 IPP。
CVBS 和 HiFi 仍会拒绝 `ipp-fast`；需要 release 兼容行为时应使用 `exact`。
兼容性敏感场景启用 IPP 前请阅读
[详细后端说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: performance -->

## 最新性能

这是同一份私有本地 40 MHz PAL VHS `.ldf` 夹具上使用
`--start 100 --length 160` 的含启动开销快照，且不会公开源文件名。表中保留了
2026-08-12 的 30 次固定 Python 参考测量。全部 60 次 .NET 测量已在 2026-08-15
用基于 main `508b4f6` 的最新候选同时刷新。每个单元格均有三次完整运行，
兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 52.811 s | 54.243 s | 12.625 s / 4.183x | 11.841 s / 4.581x | 11.140 s / 4.741x | 8.530 s / 6.359x |
| `--threads 1` | 57.067 s | 56.762 s | 32.879 s / 1.736x | 36.092 s / 1.573x | 23.205 s / 2.459x | 25.135 s / 2.258x |
| `--threads 5` | 52.920 s | 55.722 s | 12.842 s / 4.121x | 11.720 s / 4.754x | 11.120 s / 4.759x | 8.489 s / 6.564x |
| `--threads 10` | 52.965 s | 54.949 s | 10.233 s / 5.176x | 8.783 s / 6.256x | 9.137 s / 5.797x | 6.473 s / 8.488x |
| `--threads 20` | 53.555 s | 54.842 s | 8.183 s / 6.545x | 7.624 s / 7.194x | 7.890 s / 6.788x | 5.220 s / 10.507x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: performance-snapshot-runs=90 dotnet-matrix-runs=60 dotnet-current-runs=30 python-reference-runs=30 dotnet-repeats=3 python-reference-date=2026-08-12 dotnet-v040-date=2026-08-15 dotnet-current-date=2026-08-15 phase22-200-ab-pairs=20 phase22-long-ab-pairs=8 phase22-thread-backend-runs=60 phase22-gc-traces=2 phase22-tests=1438 phase24-short-ab-pairs=6 phase24-long-ab-pairs=4 phase24-thread-gate-runs=12 phase24-tests=1442 phase25-public-cell-runs=15 phase25-public-ab-pairs=15 phase25-long-ab-pairs=3 phase25-thread-gate-runs=12 phase25-tests=1446 phase26-kernel-ab-pairs=8 phase26-long-ab-pairs=4 phase26-thread-backend-runs=36 phase26-public-cell-runs=30 phase26-tests=1447 phase27-kernel-ab-pairs=8 phase27-long-ab-pairs=8 phase27-thread-backend-runs=24 phase27-public-cell-runs=60 phase27-tests=1448 phase28-kernel-ab-pairs=8 phase28-long-ab-pairs=6 phase28-thread-backend-runs=24 phase28-intrinsic-runs=3 phase28-public-cell-runs=60 phase28-tests=1448 phase30-burst-kernel-runs=14 phase30-long-ab-pairs=3 phase30-thread-gate-runs=6 phase30-memory-runs=2 phase30-public-cell-runs=60 phase30-tests=1448 phase31-interleaved-ab-pairs=9 phase31-long-gate-runs=8 phase31-thread-backend-runs=24 phase31-memory-runs=4 phase31-public-cell-runs=60 phase31-tests=1459 phase32-vblank-short-ab-pairs=6 phase32-vblank-long-ab-pairs=2 phase32-thread-backend-runs=24 phase32-gc-traces=2 phase32-counter-runs=2 phase32-tests=1460 phase33-sync-list-short-ab-pairs=6 phase33-sync-list-long-ab-pairs=2 phase33-thread-backend-runs=24 phase33-gc-traces=1 phase33-memory-runs=4 phase33-public-cell-runs=60 phase33-tests=1463 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；默认实际
使用 **5 个 workers**。三次运行范围见[详细性能说明](docs/README.detailed.zh-CN.md#性能)。
倍数会随作为分子的 Python 时间和作为分母的 .NET 时间一起变化，使用其他夹具或窗口的历史表格
也不能直接横向比较。判断因果回退时使用同一时刻的 .NET 版本配对 A/B，而不是旧表倍数。

最新候选加入有界的两场 VHS wavefront。跨场状态、输出、元数据和诊断仍严格
串行，只让与输入无关的场尾部和下一次 RF 读取重叠。交错 A/B 证明 Exact
`current` 没有收益，因此它保留旧路径；完成渲染与 dropout 后会在 lookahead 前
归还大型 RF span，窗口不会随解码长度增长。

最终 1,000 帧 `--threads 20` 门禁在全部兼容面一致。Exact v0.4.0 从 46.047 秒
降到 42.575 秒，IPP-fast v0.4.0 从 44.980 秒降到 42.084 秒，IPP-fast current
从 31.009 秒降到 25.260 秒。内存采样从 500 帧翻倍到 1,000 帧后，候选峰值
工作集保持在 473/469 MiB，main 为 354/354 MiB，证明额外窗口固定且有界。

后续 Exact-current 审计否决了完整跨场 wavefront：1,000 帧 A/B 的墙钟增加
6.05%，有效核心数没有提升。最终保留的同步分析修改不宣称吞吐提升，但同一份
500 帧计数器对照把托管分配降低 46.0%，Gen0 从 60 次降到 30 次，GC 暂停
从 44.4 ms 降到 24.2 ms；因此公开速度表保持不变。

下一轮 Exact-current 优化复用了场内分类/精修脉冲列表，并保持公共 API 的所有权语义
不变。两组顺序互换的 1000 帧配对把墙钟中位数从 32.95 降到 32.11 秒，CPU 时间
从 298.40 降到 288.02 秒；采样分配降低 8.65%。保守采用反序内存配对时，峰值
工作集从 390.8 降到 360.5 MiB，private bytes 从 409.9 降到 374.4 MiB。

覆盖 `--threads 0`、默认 5 workers 和 20 workers 的 24 次门禁，以及刷新后的
60 次 Exact/IPP-fast 矩阵，都在亮度、色度、原始 JSON、stdout、归一化
stderr/日志和有序 `fileLoc` 上只产生一个 hash。标准 xUnit v3 套件的
**1,482** 项测试全部通过。

刷新后的每个 .NET profile/线程单元格在三轮内都保持确定性。固定参考集中的 Python
PR341 保持确定；Python v0.4.0 的 15 次运行产生了 15 套不同的亮度、色度、JSON 和
归一化日志 hash，因此严格 oracle 仍是 Python v0.4.0 `g4315520 --threads 0`。
命令、范围、二进制 hash、内存边界和历史测量见
[详细性能说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: compatibility -->

## 兼容性状态

主要解码流水线、流式输出、恢复行为和 CLI 表面已经实现。聚焦测试和真实 RF 门禁
覆盖亮度、色度、JSON、有序 `fileLoc`、stdout、归一化 stderr/日志、确定性和
有界内存。罕见采集和少见参数组合仍在持续验证，因此构建成功或文件大小相同都不
等同于兼容性证明。

TBC、色度、JSON 和日志文件允许在解码期间并发读取，兼容的预览工具可以检查
部分输出而不会阻塞写入。

在原生输入路径上，40 kHz、单声道 PCM16 且总样本数不超过 `Int32.MaxValue` 的
直接 raw `fLaC` `.ldf`/`.flac` 使用内置 libsndfile。普通并行 VHS 解码还可对经过
严格门控、无 seek table 的超大 fixed-block raw FLAC 使用 libsndfile；纯整数映射会
复现固定 FFmpeg/PyAV 版本的帧起点与回退/重启边界，任何失败都从同一逻辑样本单向
切回 FFmpeg。`--threads 0/1`、debug-plot 和 GNU Radio AFE 模式、非零 `--sharpness`、
其他命令族、默认 VHS `.flac`、全部 CVBS、Ogg/FLAC、立体声、PCM24、其他采样率以及
未完成或不符合条件的文件头继续使用 FFmpeg。

<!-- SECTION: build -->

## 构建与测试

项目固定使用 .NET SDK `11.0.100-preview.6.26359.118`。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1482
```

在 Visual Studio 2026 中打开 `VHSDecodeDotNet.slnx`，即可构建、调试并通过
Test Explorer 运行 xUnit v3 测试。

<!-- SECTION: detail -->

## 详细资料

- [中文详细说明](docs/README.detailed.zh-CN.md)
- [兼容性证据](docs/COMPATIBILITY_EVIDENCE.md)
- [English overview](README.md)
- [日本語概要](README.ja.md)

<!-- SECTION: license -->

## 许可证

GPL-3.0。参见 [`LICENSE`](LICENSE)。
