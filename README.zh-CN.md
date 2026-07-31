# vhs-decode-dotnet

[English](README.md) | **[简体中文](README.zh-CN.md)** | [日本語](README.ja.md)

<!-- README_SYNC: 2026-08-01.01 -->

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
- Visual Studio 2026 `.slnx` 包含 **1,190** 项标准 xUnit v3 测试；测试可在
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
| `ipp-fast` | 实验性的 Windows x64 VHS real-RF 路径，使用 Intel IPP；可能改变浮点位，并且绝不会静默回退到 `exact`。 |

```powershell
decode.exe vhs --compat-version current --dsp-backend ipp-fast `
  --threads 20 input.lds output
```

CVBS、LaserDisc 和 HiFi 当前会拒绝 `ipp-fast`，这些命令应使用 `exact`。
兼容性敏感场景启用 IPP 前请阅读
[详细后端说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: performance -->

## 最新性能

下表固定使用同一份私有本地 40 MHz NTSC `BETAMAX_HIFI` `.lds` 样本，
所有运行采用相同的有界帧区间，且不会公开样本文件名。v0.4.0 的 .NET profile
与 Python v0.4.0 对比，`current` profile 与已合并的 Python PR341 对比，且使用
相同的请求 worker 数；兼容性结论与速度数据分开判断。

<!-- LATEST_PERFORMANCE_BEGIN -->
| CLI 模式（workers） | Python v0.4.0 | Python PR341 | Exact + v0.4.0 | Exact + current | IPP-fast + v0.4.0 | IPP-fast + current |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 默认（5） | 16.983 s | 14.414 s | 5.623 s / 3.020x | 6.730 s / 2.142x | 5.295 s / 3.207x | 6.688 s / 2.155x |
| `--threads 1` | 21.263 s | 19.881 s | 15.275 s / 1.392x | 17.979 s / 1.106x | 14.625 s / 1.454x | 17.008 s / 1.169x |
| `--threads 5` | 16.880 s | 14.329 s | 5.475 s / 3.083x | 6.514 s / 2.200x | 5.361 s / 3.149x | 6.841 s / 2.094x |
| `--threads 10` | 17.612 s | 15.149 s | 4.640 s / 3.795x | 5.890 s / 2.572x | 4.900 s / 3.594x | 5.631 s / 2.690x |
| `--threads 20` | 18.330 s | 15.447 s | 3.809 s / 4.812x | 4.388 s / 3.520x | 3.722 s / 4.924x | 4.819 s / 3.206x |
<!-- LATEST_PERFORMANCE_END -->

最新一轮 Exact 优化专门化了 PocketFFT radix-8 的方向分派，对逐字节一致的 VHS
色度 UInt16 转换和 `current` burst fit 做了向量化，并用确定性的 radix 筛选缩小
`current` sync 分位数范围。标量回退、数据类型和原始数值运算顺序均保留。

原生与标量线程/profile 门禁以及最终 60 次矩阵运行全部匹配参考结果。6 组平衡顺序的
`current`/20-worker 配对把墙钟中位数从 9.637 降至 8.627 秒（缩短 10.48%，
吞吐提高 11.71%）；4 组单线程配对从 50.377 降至 37.300 秒（缩短 25.96%，
吞吐提高 35.06%）。两组相反顺序的 1000 帧配对中，平均墙钟从 72.405 降至
62.752 秒（缩短 13.33%，吞吐提高 15.38%）。分配量仅变化 0.18%，候选工作集
低于 706 MiB，且没有渐进减速。

新的直接 raw `fLaC` 输入路径不会影响上面的固定 `.lds` 表。在同一个私有本地 RF
窗口上进行的一组 100 帧、20-worker 配对中，Release 1.4.4 的 FFmpeg 基线与内置
libsndfile 候选分别用时 8.319 s 和 7.345 s（墙钟缩短 11.71%，吞吐为 1.133x）；
亮度、色度、原始 JSON、stdout、归一化 stderr/日志以及全部 200 个有序 `fileLoc`
完全一致。这只是范围明确的单组观测，不是对所有解码的通用加速声明。

每个 .NET 单元格依次给出墙钟中位数和相对同 profile Python 列的倍速；低于
`1.000x` 表示更慢。Python PR341 使用合并提交
`2f21e8ed6018b14561396cc95f1f6828054470b8`，它是 `current` 的上游对应版本。
默认实际使用 **5 个 workers**。非零线程的 Python 行只用于吞吐比较，严格兼容
基准仍是 Python v0.4.0 `g4315520 --threads 0`。

默认 worker 的实际数量、完整命令、构建 hash、硬件、重复测量方法、输出 hash
以及旧数据请查看[详细性能说明](docs/README.detailed.zh-CN.md#性能)。

<!-- SECTION: compatibility -->

## 兼容性状态

主要解码流水线、流式输出、恢复行为和 CLI 表面已经实现。聚焦测试和真实 RF 门禁
覆盖亮度、色度、JSON、有序 `fileLoc`、stdout、归一化 stderr/日志、确定性和
有界内存。罕见采集和少见参数组合仍在持续验证，因此构建成功或文件大小相同都不
等同于兼容性证明。

TBC、色度、JSON 和日志文件允许在解码期间并发读取，兼容的预览工具可以检查
部分输出而不会阻塞写入。

40 kHz、单声道 PCM16 的直接 raw `fLaC` `.ldf`/`.flac` 输入使用内置 libsndfile
读取器。Ogg/FLAC、立体声、PCM24、其他采样率、未完成的文件头和其他容器继续使用
与 FFmpeg/PyAV 兼容的路径。

<!-- SECTION: build -->

## 构建与测试

项目固定使用 .NET SDK `11.0.100-preview.6.26359.118`。

```powershell
dotnet restore VHSDecodeDotNet.slnx
dotnet build VHSDecodeDotNet.slnx -c Release --no-restore
dotnet test --solution VHSDecodeDotNet.slnx -c Release `
  --no-build --no-restore --minimum-expected-tests 1190
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
