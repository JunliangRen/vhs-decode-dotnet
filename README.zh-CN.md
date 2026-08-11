# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-11.01 -->

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
- Visual Studio 2026 `.slnx` 包含 **1,425** 项标准 xUnit v3 测试；测试可在
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
`--start 100 --length 160` 的含启动开销快照，且不会公开源文件名。全部 90 次
Python 与 .NET Release 测量使用相同的正序、反序和混排三轮方案；性能候选基于已合并的
main `cefdbaf`，最终所有权原子门在下方单独测量。详细说明固定记录了两种被测二进制和
三次运行范围；兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 45.414 s | 45.060 s | 12.483 s / 3.638x | 12.231 s / 3.684x | 11.235 s / 4.042x | 9.442 s / 4.772x |
| `--threads 1` | 52.323 s | 52.579 s | 32.014 s / 1.634x | 37.531 s / 1.401x | 23.312 s / 2.244x | 25.972 s / 2.024x |
| `--threads 5` | 45.991 s | 44.990 s | 12.606 s / 3.648x | 12.044 s / 3.736x | 11.247 s / 4.089x | 9.407 s / 4.783x |
| `--threads 10` | 47.385 s | 47.713 s | 10.257 s / 4.620x | 9.923 s / 4.808x | 9.499 s / 4.988x | 7.519 s / 6.346x |
| `--threads 20` | 48.459 s | 47.490 s | 8.309 s / 5.832x | 8.284 s / 5.733x | 8.045 s / 6.024x | 5.823 s / 8.155x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-interleaved-matrix-runs=90 dotnet-matrix-runs=60 python-matrix-runs=30 repeats=3 segmented-envelope-release-ab-pairs=4 segmented-envelope-current-1000-ab-pairs=2 segmented-envelope-v040-160-ab-pairs=2 segmented-envelope-safety-ab-pairs=4 segmented-envelope-tests=2 segmented-envelope-intrinsic-modes=2 python-v040-runs=15 python-v040-hashes=15 python-pr341-runs=15 python-pr341-hashes=1 -->

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；默认实际
使用 **5 个 workers**。三次运行范围见[详细性能说明](docs/README.detailed.zh-CN.md#性能)。
倍数会随 .NET 绝对时间和作为分母的 Python 时间一起变化，使用其他夹具或窗口的历史表格
也不能直接横向比较。判断因果回退时使用同一时刻的 .NET 版本配对 A/B，而不是旧表倍数。

在 20 个及以上 workers 时，分阶段 VHS 路径现在直接扫描保留在 block 内的 RF Envelope，
以完全相同的 float32 顺序计算均值和 dropout 范围，不再先复制整段 Envelope。低线程继续
使用连续数组路径；算术顺序、阈值、迟滞、block 生命周期和输出顺序均未改变。一个原子缓存
操作门会阻止普通读取、缓存失效与 staged 获取互相重叠；活动 staged lease 存在期间拒绝缓存
变更操作，避免池化 block 存储被提前复用。

两组正反顺序 1000 帧 Exact `current --threads 20` 配对把墙钟中位数从 43.00 降到
42.88 秒，CPU 时间从 342.50 降到 324.50 秒（低 5.3%），峰值工作集约从 753 降到
721 MiB。另一个正式打包方式的 160 帧四组 A/B 各胜两组，墙钟与 CPU 结论不确定，
因此不宣称短窗口加速；九类兼容表面全部一致。

本地只读审查在发布前发现并修复了这处缓存所有权竞态。四组正反顺序 1000 帧修复前后
配对的九类表面全部一致；墙钟中位数为 39.770/39.932 秒，各组变化为
+0.94%/+1.15%/-1.04%/-0.03%，CPU 中位数降低 1.74%，因此最终原子门归类为性能中性。

已合并的 Python PR341 在本轮保持确定性；Python v0.4.0 的 15 次运行产生了 15 套不同的
亮度、色度、JSON 和归一化日志 hash，因此严格 oracle 仍是 Python v0.4.0
`g4315520 --threads 0`。命令、硬件、hash、内存边界和历史测量见
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
  --no-build --no-restore --minimum-expected-tests 1425
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
