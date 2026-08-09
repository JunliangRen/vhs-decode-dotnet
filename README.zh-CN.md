# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-04.03 -->

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
- Visual Studio 2026 `.slnx` 包含 **1,382** 项标准 xUnit v3 测试；测试可在
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

下表固定使用同一份私有本地 40 MHz PAL VHS `.ldf` 夹具和相同的 40 帧窗口，
不会公开源文件名。每个单元格都是本候选三次重新排序的 Release 测量中位数，
候选基于已合并的 main `ae3722d`；没有沿用其他夹具或构建的数据。兼容性结论与
速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 17.401 s | 19.791 s | 4.341 s / 4.008x | 4.302 s / 4.601x | 3.549 s / 4.904x | 3.126 s / 6.331x |
| `--threads 1` | 19.177 s | 21.052 s | 11.626 s / 1.649x | 12.860 s / 1.637x | 8.361 s / 2.294x | 9.232 s / 2.280x |
| `--threads 5` | 17.458 s | 20.209 s | 4.043 s / 4.318x | 4.157 s / 4.861x | 3.654 s / 4.778x | 3.111 s / 6.495x |
| `--threads 10` | 17.230 s | 19.480 s | 3.526 s / 4.886x | 3.745 s / 5.201x | 2.983 s / 5.775x | 2.513 s / 7.751x |
| `--threads 20` | 17.558 s | 19.928 s | 2.772 s / 6.334x | 3.298 s / 6.042x | 2.632 s / 6.672x | 2.075 s / 9.605x |
<!-- LATEST_PERFORMANCE_END -->
<!-- LATEST_PERFORMANCE_RUNS: full-refresh=90 repeats=3 candidate-ab=8 thread-gates=24 candidate-long=2 dotnet-determinism=60 -->

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；默认实际
使用 **5 个 workers**。更早公开的表格使用另一份私有 NTSC Betamax HiFi 夹具，
不能直接横向比较。在这份 PAL 夹具上，之前的 main 还因为超大 raw FLAC 改走 FFmpeg
而承担了真实开销；该问题已在之前的 release 中修复。本候选把确定性 float64 median
的 introselect 阈值从 32,768 降到 4,096 个样本，使约 6K 和 15K 样本的 PAL VSync
MAD 分组不再完整排序，同时保持 NaN、正负零混合和偶数长度中位数表达式不变。

四组交错的 160 帧 IPP-fast `current --threads 20` 配对，把墙钟中位数从 7.253
降到 6.542 秒（缩短 9.80%，吞吐提升 10.87%），CPU 中位数基本不变：39.453 对
39.469 秒。1000 帧门禁从 38.478 降到 35.487 秒（缩短 7.77%），CPU 时间减少
1.21%，峰值工作集保持有界：368.3 对 368.1 MiB。

34 次严格基线/候选运行的亮度、色度、原始 JSON、有序 `fileLoc`、stdout、归一化
stderr 和日志全部一致；60 次 .NET 矩阵运行也保持跨线程确定性。IPP-fast 仍是显式
启用的数值近似后端，因此不声称其产物与 Exact 逐字节一致。已合并的 Python PR341
在本轮保持确定性；Python v0.4.0 的全部 15 次矩阵运行都产生了不同的亮度、色度和
JSON hash，其中 12 次显式非默认 worker 运行也是 12 套。因此严格 oracle 仍是
Python v0.4.0 `g4315520 --threads 0`。完整命令、硬件、hash、内存边界和历史测量请查看
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
  --no-build --no-restore --minimum-expected-tests 1382
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
